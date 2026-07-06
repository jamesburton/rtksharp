using System.Text;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Oracle parity battery for <c>rtk docker</c> (the docker-specific portions of
/// <c>src/cmds/cloud/container.rs</c>): <c>ps</c>/<c>ps -a</c>/<c>images</c>/<c>logs</c>/
/// <c>compose ps</c>/<c>compose logs</c>/<c>compose build</c>, plus the generic passthrough fallback.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synthetic PATH stand-in <c>docker</c>, not a real daemon</b> — same convention
/// <see cref="JsStackParityTests"/>/<see cref="LintParityTests"/> already established: a
/// <c>docker.cmd</c> script that <c>type</c>s a canned <c>.out</c> file to stdout regardless of
/// arguments and exits with a configured code. Rust's own <c>container.rs</c> test suite never
/// invokes a real docker binary or daemon either (100% pure-function tests) — this project has no
/// daemon-dependent parity precedent to deviate from.
/// </para>
/// <para>
/// <b><c>docker ps</c>/<c>ps -a</c>/<c>images</c>/<c>compose ps</c> each spawn the stand-in TWICE</b>
/// (once for the plain invocation used only for tracking, once for the <c>--format</c>-flagged one
/// that's actually parsed) — since the stand-in returns the identical canned output regardless of
/// arguments, this is harmless for stdout comparison but is exercised deliberately (see
/// <see cref="DockerCommand"/>'s own class remarks on why this double-invocation is faithfully
/// preserved, not "optimized" away).
/// </para>
/// </remarks>
public class DockerParityTests
{
    private const double ParityThresholdPercent = 100.0;

    private sealed record ToolStub(string Output, int ExitCode = 0);

    private sealed record Entry(string Label, string[] Args, ToolStub Stub);

    [Fact]
    public async Task DockerVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the docker parity gate.");
            return;
        }

        var (portFileName, portPrefixArgs) = LocateRtkSharp(repoRoot);
        if (portFileName is null)
        {
            Assert.Fail(
                "RtkSharp binary not found. It is normally copied next to the test assembly via the " +
                "project reference; if absent, publish it with " +
                "`dotnet publish RtkSharp -c Release -o .artifacts/publish -p:PublishAot=false`.");
            return;
        }

        var battery = BuildBattery();
        var results = new List<Result>();

        foreach (var entry in battery)
        {
            results.Add(await RunEntryAsync(entry, oraclePath, portFileName, portPrefixArgs));
        }

        var matched = results.Count(r => r.IsMatch);
        var total = results.Count;
        var percent = total == 0 ? 100.0 : matched * 100.0 / total;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "docker-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"docker parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} stdoutMatch={r.StdoutMatches}");
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    // ===================== battery definition =====================

    private static IReadOnlyList<Entry> BuildBattery() =>
    [
        new("ps: no containers", ["ps"], new ToolStub("")),
        new(
            "ps: one container with ports",
            ["ps"],
            new ToolStub("abcdef012345\tweb\tUp 2 hours\tmyrepo/nginx:latest\t0.0.0.0:80->80/tcp\n")),
        new(
            "ps -a: running and stopped",
            ["ps", "-a"],
            new ToolStub(
                "running\tabc123456789\tweb\tUp 2 hours\tnginx:latest\t\n" +
                "exited\tdef123456789\tworker\tExited (0) 5 minutes ago\tpython:3.12\t\n")),
        new("images: none", ["images"], new ToolStub("")),
        new(
            "images: two images with sizes",
            ["images"],
            new ToolStub("myapp:latest\t1.5GB\nother:tag\t500MB\n")),
        new(
            "logs: dedups repeated errors",
            ["logs", "my-container"],
            new ToolStub(
                "2024-01-01 10:00:00 ERROR: connection refused\n" +
                "2024-01-01 10:00:01 ERROR: connection refused\n" +
                "2024-01-01 10:00:02 INFO: retrying\n")),
        new(
            "compose ps: one service",
            ["compose", "ps"],
            new ToolStub("web-1\tnginx:latest\tUp 2 hours\t0.0.0.0:80->80/tcp\n")),
        new(
            "compose logs: dedups",
            ["compose", "logs"],
            new ToolStub("web-1  | 192.168.1.1 - GET / 200\nweb-1  | 192.168.1.1 - GET / 200\n")),
        new(
            "compose build: finished summary with services",
            ["compose", "build"],
            new ToolStub(
                "[+] Building 12.3s (8/8) FINISHED\n" +
                " => [web 1/4] FROM node:20                                          0.0s\n" +
                " => [web 2/4] WORKDIR /app                                          0.1s\n")),
        new("passthrough: unrecognized docker subcommand", ["build", "-t", "myimage", "."], new ToolStub("Successfully built myimage\n")),
        new("passthrough: unrecognized compose subcommand", ["compose", "down"], new ToolStub("Stopping web-1 ... done\n")),
    ];

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-docker-parity-oracle-");
        var portTemp = CreateTempDir("rtk-docker-parity-port-");

        try
        {
            var oracleToolsDir = Path.Combine(oracleTemp, "tools");
            var portToolsDir = Path.Combine(portTemp, "tools");
            WriteToolStub(oracleToolsDir, entry.Stub);
            WriteToolStub(portToolsDir, entry.Stub);

            var realPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var oracleClaudeDir = Path.Combine(oracleTemp, "no-claude-dir");
            var portClaudeDir = Path.Combine(portTemp, "no-claude-dir");

            var oracleEnv = new Dictionary<string, string?>
            {
                ["PATH"] = oracleToolsDir + Path.PathSeparator + realPath,
                ["CLAUDE_CONFIG_DIR"] = oracleClaudeDir,
            };
            var portEnv = new Dictionary<string, string?>
            {
                ["PATH"] = portToolsDir + Path.PathSeparator + realPath,
                ["CLAUDE_CONFIG_DIR"] = portClaudeDir,
            };

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                oraclePath, ["docker", .. entry.Args], oracleTemp, oracleEnv, stdin: "");

            var portArgs = portPrefixArgs.Concat(["docker"]).Concat(entry.Args).ToArray();
            var (portStdout, portExit) = await ParityRunner.RunAsync(
                portFileName, portArgs, portTemp, portEnv, stdin: "");

            return new Result(entry.Label, Normalize(oracleStdout), Normalize(portStdout), oracleExit, portExit);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    /// <summary>
    /// Writes a synthetic PATH stand-in <c>docker</c> tool as a <c>docker.cmd</c>/<c>docker.out</c>
    /// pair — same mechanism as <see cref="JsStackParityTests"/>/<see cref="LintParityTests"/>.
    /// </summary>
    private static void WriteToolStub(string toolsDir, ToolStub stub)
    {
        Directory.CreateDirectory(toolsDir);

        var outPath = Path.Combine(toolsDir, "docker.out");
        var cmdPath = Path.Combine(toolsDir, "docker.cmd");

        File.WriteAllText(outPath, stub.Output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            cmdPath,
            $"@echo off\r\ntype \"%~dp0docker.out\"\r\nexit /b {stub.ExitCode}\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ===================== shared helpers =====================

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static (string? FileName, string[] PrefixArgs) LocateRtkSharp(string repoRoot)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, ExeName("RtkSharp"));
        if (File.Exists(beside))
        {
            return (beside, []);
        }

        var candidates = new[]
        {
            Path.Combine(repoRoot, ".artifacts", "publish", ExeName("RtkSharp")),
            Path.Combine(repoRoot, "RtkSharp", "bin", "Release", "net10.0", ExeName("RtkSharp")),
            Path.Combine(repoRoot, "RtkSharp", "bin", "Debug", "net10.0", ExeName("RtkSharp")),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return (c, []);
            }
        }

        var dll = Path.Combine(AppContext.BaseDirectory, "RtkSharp.dll");
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "RtkSharp.runtimeconfig.json");
        if (File.Exists(dll) && File.Exists(runtimeConfig))
        {
            return ("dotnet", [dll]);
        }

        return (null, []);
    }

    private static string ExeName(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate repo root (no Cargo.toml found in any parent directory).");
    }

    private static async Task WriteReportAsync(
        string reportPath,
        IReadOnlyList<Result> results,
        double percent,
        int matched,
        int total,
        string oraclePath,
        string portFileName,
        string[] portPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# `rtk docker` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/DockerParityTests.cs`. " +
                      "Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (ps/ps-a/images/logs, compose ps/logs/build, two passthrough)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry writes a synthetic PATH stand-in `docker.cmd` tool " +
                      "(same mechanism as `JsStackParityTests`/`LintParityTests`) returning canned output, " +
                      "then runs both binaries with `PATH` prepended with that stand-in. Stdout " +
                      "(CRLF/LF normalized, trailing whitespace trimmed) and exit code are compared; " +
                      "stderr is not. `kubectl`/`oc` are out of scope for this port — see " +
                      "`DockerCommand.cs`'s class remarks.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|--------|--------|");
        sb.AppendLine($"| **Overall parity (headline)** | **{percent:F1}%** |");
        sb.AppendLine($"| Threshold | {ParityThresholdPercent:F0}% |");
        sb.AppendLine($"| Entries matched | {matched} / {total} |");
        sb.AppendLine();
        sb.AppendLine("## Per-entry results");
        sb.AppendLine();
        sb.AppendLine("| Entry | Rust exit | .NET exit | Stdout match | Verdict |");
        sb.AppendLine("|-------|-----------|-----------|--------------|---------|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| `{r.Label}` | {r.RustExit} | {r.PortExit} | {(r.StdoutMatches ? "yes" : "no")} | {r.Verdict} |");
        }

        sb.AppendLine();
        var mismatches = results.Where(r => !r.IsMatch).ToList();
        sb.AppendLine("## Mismatch details");
        sb.AppendLine();
        if (mismatches.Count == 0)
        {
            sb.AppendLine("None — every battery entry matched byte-exact stdout and an identical exit code.");
        }
        else
        {
            foreach (var r in mismatches)
            {
                sb.AppendLine($"### `{r.Label}`");
                sb.AppendLine();
                sb.AppendLine($"- Verdict: **{r.Verdict}**");
                sb.AppendLine($"- Rust exit: `{r.RustExit}`, .NET exit: `{r.PortExit}`");
                sb.AppendLine($"- Rust stdout: {Md(r.RustStdout)}");
                sb.AppendLine($"- .NET stdout: {Md(r.PortStdout)}");
                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(reportPath, sb.ToString());
    }

    private static string Md(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "*(empty)*";
        }

        return "`" + s.Replace("\n", " ").Replace("|", "\\|") + "`";
    }

    /// <summary>The parity outcome for a single battery entry.</summary>
    private sealed record Result(string Label, string RustStdout, string PortStdout, int RustExit, int PortExit)
    {
        public bool StdoutMatches => RustStdout == PortStdout;

        public bool ExitsMatch => RustExit == PortExit;

        public bool IsMatch => StdoutMatches && ExitsMatch;

        public string Verdict => IsMatch
            ? "MATCH"
            : !StdoutMatches
                ? "MISMATCH (stdout)"
                : "MISMATCH (exit)";
    }
}
