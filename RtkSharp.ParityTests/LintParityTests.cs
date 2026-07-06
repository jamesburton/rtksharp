using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Oracle parity battery for <c>rtk lint</c> (<c>src/cmds/js/lint_cmd.rs</c>) covering the
/// <c>eslint</c>, <c>pylint</c>, and generic-fallback paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synthetic PATH stand-in tools, not real eslint/pylint installs</b> — same convention
/// <see cref="JsStackParityTests"/> established: a tiny <c>{tool}.cmd</c> script that <c>type</c>s a
/// sibling <c>.out</c> file's exact bytes to stdout and exits with a configured code, sidestepping
/// batch-quoting hazards for JSON output containing embedded double-quotes.
/// </para>
/// <para>
/// <b>Distinct counts in every fixture to dodge HashMap/Dictionary iteration-order nondeterminism.</b>
/// Rust's <c>by_rule</c>/<c>by_file</c> grouping uses a <c>HashMap</c> (randomized iteration order per
/// process); ties in count are sorted arbitrarily on the oracle itself, run to run. Every fixture here
/// uses strictly distinct per-rule/per-file counts so the sort order is fully determined and
/// comparable — the same precondition <c>HookAuditParityTests</c>/<c>TelemetryParityTests</c> already
/// established for this project's other count-and-sort groupers.
/// </para>
/// <para>
/// <b>Scope note: <c>ruff</c>/<c>mypy</c> are not exercised here.</b> Per <c>LintCommand.cs</c>'s class
/// remarks, those two linters delegate to unported Python-ecosystem modules in Rust and are
/// deliberately passed through unfiltered on the .NET side rather than faked — a battery entry
/// comparing "unfiltered passthrough" against Rust's real grouped ruff/mypy output would be a known,
/// disclosed mismatch, not a useful parity signal, so it's omitted rather than added to an
/// expected-mismatch allowlist for a feature this port doesn't claim to implement yet.
/// </para>
/// <para>
/// <b>JSON parse-error message text is masked before comparison.</b> The malformed-JSON fallback
/// path's message embeds the underlying parser's own error text (Rust's <c>serde_json::Error</c>
/// <c>Display</c> vs. .NET's <c>JsonException.Message</c>) — legitimately different prose for the
/// same "not valid JSON" condition, the same class of divergence already accepted elsewhere in this
/// project (see the Phase 9a progress-ledger note: "stderr parse-error suffix legitimately differs
/// serde vs System.Text.Json — prefix exact"). <see cref="Normalize"/> replaces the parenthesized
/// error detail with a placeholder so the surrounding structure (prefix, truncated raw content) is
/// still compared byte-exact.
/// </para>
/// </remarks>
public class LintParityTests
{
    private const double ParityThresholdPercent = 100.0;

    private sealed record ToolStub(string ToolName, string Output, int ExitCode = 0);

    private sealed record Entry(string Label, string[] Args, ToolStub Stub);

    [Fact]
    public async Task LintVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the lint parity gate.");
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

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "lint-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"lint parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} stdoutMatch={r.StdoutMatches}");
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    // ===================== battery definition =====================

    private const string EslintNoIssuesJson = """[{"filePath":"/a.ts","messages":[],"errorCount":0,"warningCount":0}]""";

    private const string EslintTwoFilesJson = """
        [
            {"filePath":"/repo/src/utils.ts","messages":[
                {"ruleId":"prefer-const","severity":1,"message":"Use const","line":1,"column":1},
                {"ruleId":"prefer-const","severity":1,"message":"Use const","line":2,"column":1},
                {"ruleId":"prefer-const","severity":1,"message":"Use const","line":3,"column":1}
            ],"errorCount":0,"warningCount":3},
            {"filePath":"/repo/src/api.ts","messages":[
                {"ruleId":"no-unused-vars","severity":2,"message":"x unused","line":10,"column":1}
            ],"errorCount":1,"warningCount":0}
        ]
        """;

    private const string PylintNoIssuesJson = "[]";

    private const string PylintTwoFilesJson = """
        [
            {"type":"warning","module":"m","obj":"","line":1,"column":0,"path":"src/main.py","symbol":"unused-variable","message":"x","message-id":"W0612"},
            {"type":"warning","module":"m","obj":"","line":2,"column":0,"path":"src/main.py","symbol":"unused-variable","message":"y","message-id":"W0612"},
            {"type":"error","module":"u","obj":"","line":3,"column":0,"path":"src/utils.py","symbol":"undefined-variable","message":"z","message-id":"E0602"}
        ]
        """;

    private static IReadOnlyList<Entry> BuildBattery() =>
    [
        new("eslint (default, no args): no issues", [], new ToolStub("eslint", EslintNoIssuesJson)),
        new("eslint explicit, with path: grouped by rule and file", ["eslint", "src/"], new ToolStub("eslint", EslintTwoFilesJson)),
        new("eslint: malformed JSON falls back to truncated raw", ["eslint"], new ToolStub("eslint", "eslint: config error, not JSON")),
        new("eslint exits 1 (lint findings): filtered output still shown, exit code propagated", ["eslint"], new ToolStub("eslint", EslintTwoFilesJson, ExitCode: 1)),
        new("pylint: no issues", ["pylint"], new ToolStub("pylint", PylintNoIssuesJson)),
        new("pylint: grouped by symbol and file", ["pylint"], new ToolStub("pylint", PylintTwoFilesJson)),
        new("generic fallback (biome): naive warning/error line-scan", ["biome", "check"], new ToolStub("biome", "file.js: warning: unused import\nfile.js: error: syntax error\n")),
        new("generic fallback: no issues detected", ["biome", "check"], new ToolStub("biome", "All good, no problems found.\n")),
    ];

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-lint-parity-oracle-");
        var portTemp = CreateTempDir("rtk-lint-parity-port-");

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
                ["RTK_TEE"] = "0",
            };
            var portEnv = new Dictionary<string, string?>
            {
                ["PATH"] = portToolsDir + Path.PathSeparator + realPath,
                ["CLAUDE_CONFIG_DIR"] = portClaudeDir,
                ["RTK_TEE"] = "0",
            };

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                oraclePath, ["lint", .. entry.Args], oracleTemp, oracleEnv, stdin: "");

            var portArgs = portPrefixArgs.Concat(["lint"]).Concat(entry.Args).ToArray();
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
    /// Writes a synthetic PATH stand-in tool as a <c>{tool}.cmd</c>/<c>{tool}.out</c> pair — same
    /// mechanism as <see cref="JsStackParityTests"/>'s own <c>WriteToolStub</c>.
    /// </summary>
    private static void WriteToolStub(string toolsDir, ToolStub stub)
    {
        Directory.CreateDirectory(toolsDir);

        var outPath = Path.Combine(toolsDir, stub.ToolName + ".out");
        var cmdPath = Path.Combine(toolsDir, stub.ToolName + ".cmd");

        File.WriteAllText(outPath, stub.Output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            cmdPath,
            $"@echo off\r\ntype \"%~dp0{stub.ToolName}.out\"\r\nexit /b {stub.ExitCode}\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ===================== shared helpers =====================

    private static readonly Regex JsonParseErrorDetail = new(
        @"(?:ESLint|Pylint) output \(JSON parse failed: [^)]*\)", RegexOptions.Compiled);

    private static string Normalize(string s) =>
        JsonParseErrorDetail.Replace(s.Replace("\r\n", "\n").Replace("\r", "\n").Trim(), m =>
            m.Value.StartsWith("ESLint", StringComparison.Ordinal)
                ? "ESLint output (JSON parse failed: <ERR>)"
                : "Pylint output (JSON parse failed: <ERR>)");

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
        sb.AppendLine("# `rtk lint` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/LintParityTests.cs`. " +
                      "Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (eslint no-issues/grouped/malformed/nonzero-exit, pylint no-issues/grouped, generic fallback with/without issues)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry writes a synthetic PATH stand-in `.cmd` tool (same " +
                      "mechanism as `JsStackParityTests`) returning canned JSON/text output, then runs " +
                      "both binaries with `PATH` prepended with that stand-in and `RTK_TEE=0`. Stdout " +
                      "(CRLF/LF normalized, trailing whitespace trimmed) and exit code are compared; " +
                      "stderr is not. `ruff`/`mypy` are not exercised — see this file's class remarks.");
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
