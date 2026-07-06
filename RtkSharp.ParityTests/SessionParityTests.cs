using System.Text;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Oracle parity battery for <c>rtk session</c> (<c>src/analytics/session_cmd.rs</c>), including its
/// <c>discover::provider</c>/<c>discover::registry</c> dependencies (<c>ClaudeProvider</c>,
/// <c>classify_command</c>, <c>split_command_chain</c> — reused via the already-ported rewrite engine
/// on the .NET side, see <c>RtkSharp/Discover/CommandClassifier.cs</c>'s class remarks).
/// </summary>
/// <remarks>
/// <b>Hermeticity via <c>CLAUDE_CONFIG_DIR</c> — a real Rust-honored override, not a .NET-only escape
/// hatch.</b> Unlike <c>dirs::config_dir()</c>/<c>dirs::data_local_dir()</c> (which
/// <c>TelemetryParityTests</c>'s class remarks document as having NO override on the Rust side, the
/// root cause of a real safety incident during that phase), Rust's own
/// <c>resolve_claude_dir()</c> (<c>init.rs</c>:2724-2729) checks <c>CLAUDE_CONFIG_DIR</c> directly —
/// the exact mechanism every other parity battery in this project already relies on for hook-status
/// isolation (pointed at a non-existent path there; here pointed at a real, per-entry synthetic
/// directory seeded with fixture <c>.jsonl</c> files instead). No <c>HostRealDirGuard</c>/junction
/// machinery is required — every entry fully redirects both sides' only real filesystem read.
/// </remarks>
public class SessionParityTests
{
    private const double ParityThresholdPercent = 100.0;

    private sealed record Entry(string Label, (string ProjectDir, string FileName, string[] Lines)[] Sessions);

    [Fact]
    public async Task SessionVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the session parity gate.");
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

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "session-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"session parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
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
        new("no projects directory at all", []),
        new(
            "one session: mixed explicit-rtk, hook-rewritable, and unsupported commands",
            [
                ("-Users-test-rtk", "abcd1234-session.jsonl",
                [
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rtk git status"}}]}}""",
                    """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"On branch main"}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git log -5"}}]}}""",
                    """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"commit abc123\ncommit def456"}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t3","name":"Bash","input":{"command":"echo hello"}}]}}""",
                    """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t3","content":"hello"}]}}""",
                ]),
            ]),
        new(
            "session with only non-Bash tool activity (Read/Grep) -> zero commands found",
            [
                ("-Users-test-other", "noBash.jsonl",
                [
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/tmp/foo"}}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Grep","input":{"pattern":"TODO"}}]}}""",
                ]),
            ]),
        new(
            "chained command (&&) split across two logical commands, one supported one not",
            [
                ("-Users-test-chain", "chain.jsonl",
                [
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"cd ./your/app/path && rtk ls"}}]}}""",
                    """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"file1.rs\nfile2.rs"}]}}""",
                ]),
            ]),
        new(
            "two sessions across two different project directories",
            [
                ("-Users-test-projA", "sessionA.jsonl",
                [
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"a1","name":"Bash","input":{"command":"cargo test"}}]}}""",
                    """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"a1","content":"test result: ok. 5 passed"}]}}""",
                ]),
                ("-Users-test-projB", "sessionB.jsonl",
                [
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"npm install"}}]}}""",
                    """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"b1","content":"added 42 packages"}]}}""",
                ]),
            ]),
        new(
            "adoption percentage lands exactly on a .5 rounding tie (8 total, 1 rtk -> 12.5%)",
            [
                ("-Users-test-round", "session.jsonl",
                [
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rtk git status"}}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"echo a"}}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t3","name":"Bash","input":{"command":"echo b"}}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t4","name":"Bash","input":{"command":"echo c"}}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t5","name":"Bash","input":{"command":"echo d"}}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t6","name":"Bash","input":{"command":"echo e"}}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t7","name":"Bash","input":{"command":"echo f"}}]}}""",
                    """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t8","name":"Bash","input":{"command":"echo g"}}]}}""",
                ]),
            ]),
    ];

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-session-parity-oracle-");
        var portTemp = CreateTempDir("rtk-session-parity-port-");

        try
        {
            var oracleClaudeDir = Path.Combine(oracleTemp, "claude");
            var portClaudeDir = Path.Combine(portTemp, "claude");
            SeedSessions(oracleClaudeDir, entry.Sessions);
            SeedSessions(portClaudeDir, entry.Sessions);

            var oracleEnv = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = oracleClaudeDir };
            var portEnv = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = portClaudeDir };

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                oraclePath, ["session"], oracleTemp, oracleEnv, stdin: "");

            var portArgs = portPrefixArgs.Concat(["session"]).ToArray();
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

    private static void SeedSessions(string claudeDir, (string ProjectDir, string FileName, string[] Lines)[] sessions)
    {
        Directory.CreateDirectory(claudeDir);
        foreach (var (projectDir, fileName, lines) in sessions)
        {
            var dir = Path.Combine(claudeDir, "projects", projectDir);
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, fileName), lines);
        }
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
        sb.AppendLine("# `rtk session` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/SessionParityTests.cs`. " +
                      "Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (no sessions, mixed explicit/hook/unsupported, " +
                      "non-Bash-only session, chained command split, multi-project)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry writes identical synthetic `.jsonl` session-transcript " +
                      "fixtures into a per-side `CLAUDE_CONFIG_DIR/projects/<dir>/<file>.jsonl` tree (a real, " +
                      "Rust-honored override — no junction/guard machinery needed), then runs both binaries " +
                      "with `CLAUDE_CONFIG_DIR` pointed at that tree. Stdout (CRLF/LF normalized, " +
                      "trailing-whitespace trimmed) and exit code are compared; stderr is not.");
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
