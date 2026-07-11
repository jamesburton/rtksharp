using System.Text;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Oracle parity battery for <c>rtk hook-audit</c> (<c>src/hooks/hook_audit_cmd.rs</c>), the final
/// "Must port for MVP" command per <c>docs/parity/command-inventory.md</c>: no audit log, an empty
/// log, malformed lines mixed with valid ones, <c>--since 0</c> (all time) with distinct
/// skip-reason/top-command counts, a time-windowed <c>--since</c> query using "now"-relative
/// timestamps generated at test time, and the "no entries in window" case.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hermeticity via <c>RTK_AUDIT_DIR</c> + a non-existent <c>CLAUDE_CONFIG_DIR</c> — no
/// <c>HostRealDirGuard</c> needed, following the Phase 6 precedent.</b> <c>RTK_AUDIT_DIR</c> is the
/// oracle's own top-priority override for the audit-log directory (<c>default_log_path()</c>,
/// <c>hook_audit_cmd.rs:8-11</c>), so every entry here fully redirects log-file reads to a per-entry
/// temp directory with no fallback to the real <c>$HOME/.local/share/rtk</c>. <c>hook-audit</c> is
/// <i>not</i> excluded from <c>main.rs</c>'s unconditional pre-dispatch <c>hook_check::maybe_warn()</c>
/// call (only <c>Commands::Gain</c> is, at <c>main.rs</c>:1484) — the same asymmetry already
/// empirically verified harmless for <c>proxy</c>/<c>run</c>/<c>pipe</c>/<c>err</c>/<c>test</c> in
/// <see cref="PipeProxyRunParityTests"/>: redirecting <c>CLAUDE_CONFIG_DIR</c> to a non-existent path
/// makes <c>hook_check::status()</c> resolve to <c>Ok</c> deterministically, and the <c>Ok</c> branch
/// returns before the marker-file path is ever resolved, so no write occurs. This battery reuses that
/// established, already-canary-verified mechanism rather than re-running a fresh canary for a command
/// that reaches the exact same guard the exact same way.
/// </para>
/// <para>
/// <b>Deliberately avoiding tied counts in "distinct counts" entries.</b> Rust's std
/// <c>HashMap</c> has a randomized per-process iteration order; <c>hook_audit_cmd.rs</c>'s skip-reason
/// and top-command breakdowns are sorted by count but never tie-broken by key, so two entries with an
/// equal count can print in either order on the oracle itself, run to run — a non-determinism no port
/// can byte-match. Every battery entry that exercises the skip-reason/top-command output uses
/// distinct, strictly-ordered counts per bucket so the sort order is fully determined and comparable.
/// </para>
/// </remarks>
public class HookAuditParityTests
{
    private const double ParityThresholdPercent = 100.0;

    private sealed record Entry(string Label, string[] Args, string? LogContent, string[]? GlobalArgs = null);

    [Fact]
    public async Task HookAuditVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cd rust-original && cargo build --release` " +
                "(from PowerShell) before running the hook-audit parity gate.");
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

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "hook-audit-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"hook-audit parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} stdoutMatch={r.StdoutMatches}");
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    // ===================== battery definition =====================

    private static IReadOnlyList<Entry> BuildBattery()
    {
        var nowWindow = BuildNowWindowLog();

        return
        [
            new("no audit log at all", ["--since", "0"], LogContent: null),
            new("empty log file", ["--since", "0"], LogContent: ""),
            new("only malformed lines", ["--since", "0"], LogContent: "garbage\nmore garbage\n"),
            new(
                "mixed valid+malformed, distinct skip/top-command counts, all time",
                ["--since", "0"],
                LogContent:
                    "2026-02-16T14:30:01Z | rewrite | git status | rtk git status\n" +
                    "not a valid line at all\n" +
                    "2026-02-16T14:30:02Z | skip:no_match | echo hello | -\n" +
                    "2026-02-16T14:30:03Z | rewrite | git status | rtk git status\n" +
                    "2026-02-16T14:30:04Z | skip:already_rtk | rtk git log | -\n" +
                    "2026-02-16T14:30:05Z | rewrite | git status | rtk git status\n" +
                    "2026-02-16T14:30:06Z | skip:no_match | mkdir -p foo | -\n" +
                    "2026-02-16T14:30:07Z | rewrite | gh pr view 42 | rtk gh pr view 42\n" +
                    "2026-02-16T14:30:08Z | skip:no_match | mkdir -p bar | -\n"),
            new(
                "--since default (7, via -s short flag), all entries far in the past -> empty window",
                ["-s", "7"],
                LogContent: "2020-01-01T00:00:00Z | rewrite | git status | rtk git status\n"),
            new(
                "--since=3 (equals form), 'now'-relative timestamps inside and outside the window",
                ["--since=3"],
                LogContent: nowWindow),
            new(
                "env-var-prefixed original commands feed base_command grouping",
                ["--since", "0"],
                LogContent:
                    "2026-02-16T14:30:01Z | rewrite | GIT_PAGER=cat git status | rtk git status\n" +
                    "2026-02-16T14:30:02Z | rewrite | GIT_PAGER=cat git status | rtk git status\n" +
                    "2026-02-16T14:30:03Z | rewrite | NODE_ENV=test CI=1 npx vitest | rtk npx vitest\n"),
            new(
                "skip-reason padding clamp at/over the 13-char threshold",
                ["--since", "0"],
                LogContent:
                    "2026-02-16T14:30:01Z | skip:exactly13chars | echo hi | -\n" +
                    "2026-02-16T14:30:02Z | skip:a | echo bye | -\n" +
                    "2026-02-16T14:30:03Z | skip:a | echo bye | -\n"),
            new(
                "-v (verbose) before the verb appends the trailing Log: path line",
                ["--since", "0"],
                LogContent: "2026-02-16T14:30:01Z | rewrite | git status | rtk git status\n",
                GlobalArgs: ["-v"]),
        ];
    }

    /// <summary>
    /// Builds a log with one entry ~1 day ago (inside a <c>--since 3</c> window) and one entry ~10
    /// days ago (outside it), both timestamped relative to the real current UTC instant at test-run
    /// time so the <c>--since</c> cutoff comparison is genuinely exercised rather than using fixed
    /// dates that could drift in or out of the window as the calendar advances.
    /// </summary>
    private static string BuildNowWindowLog()
    {
        var now = DateTime.UtcNow;
        var inside = now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var outside = now.AddDays(-10).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        return
            $"{outside} | rewrite | old command | rtk old command\n" +
            $"{inside} | rewrite | recent command | rtk recent command\n" +
            $"{inside} | skip:no_match | another recent | -\n";
    }

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-hookaudit-parity-oracle-");
        var portTemp = CreateTempDir("rtk-hookaudit-parity-port-");

        try
        {
            var oracleAuditDir = Path.Combine(oracleTemp, "audit");
            var portAuditDir = Path.Combine(portTemp, "audit");
            Directory.CreateDirectory(oracleAuditDir);
            Directory.CreateDirectory(portAuditDir);

            if (entry.LogContent is not null)
            {
                var content = entry.LogContent.Replace("\r\n", "\n");
                await File.WriteAllTextAsync(Path.Combine(oracleAuditDir, "hook-audit.log"), content);
                await File.WriteAllTextAsync(Path.Combine(portAuditDir, "hook-audit.log"), content);
            }

            var oracleClaudeDir = Path.Combine(oracleTemp, "no-claude-dir");
            var portClaudeDir = Path.Combine(portTemp, "no-claude-dir");

            var oracleEnv = new Dictionary<string, string?>
            {
                ["RTK_AUDIT_DIR"] = oracleAuditDir,
                ["CLAUDE_CONFIG_DIR"] = oracleClaudeDir,
            };
            var portEnv = new Dictionary<string, string?>
            {
                ["RTK_AUDIT_DIR"] = portAuditDir,
                ["CLAUDE_CONFIG_DIR"] = portClaudeDir,
            };

            var globalArgs = entry.GlobalArgs ?? [];

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                oraclePath, [.. globalArgs, "hook-audit", .. entry.Args], oracleTemp, oracleEnv, stdin: "");

            var portArgs = portPrefixArgs.Concat(globalArgs).Concat(["hook-audit"]).Concat(entry.Args).ToArray();
            var (portStdout, portExit) = await ParityRunner.RunAsync(
                portFileName, portArgs, portTemp, portEnv, stdin: "");

            var normalizedOracle = Normalize(oracleStdout).Replace(oracleAuditDir, "<AUDIT_DIR>");
            var normalizedPort = Normalize(portStdout).Replace(portAuditDir, "<AUDIT_DIR>");

            return new Result(entry.Label, normalizedOracle, normalizedPort, oracleExit, portExit);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RtkSharp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate repo root (no RtkSharp.slnx found in any parent directory).");
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
        sb.AppendLine("# `rtk hook-audit` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/HookAuditParityTests.cs`. " +
                      "Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (no log, empty log, malformed-only, " +
                      "mixed valid+malformed with distinct skip/top-command counts, empty-window --since, " +
                      "now-relative --since window, env-var-prefixed base-command grouping)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry writes an identical (or absent) `hook-audit.log` into " +
                      "its own per-side temp directory, then runs both binaries with `RTK_AUDIT_DIR` pointed " +
                      "at that directory and `CLAUDE_CONFIG_DIR` redirected to a non-existent per-entry path " +
                      "(so `hook_check::status()` resolves deterministically to `Ok` on both sides, matching " +
                      "the established `PipeProxyRunParityTests` convention). Stdout (CRLF/LF normalized, " +
                      "trailing-whitespace trimmed, temp audit-dir path substituted with a placeholder) and " +
                      "exit code are compared; stderr is not (matches this project's established " +
                      "parity-harness convention).");
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
