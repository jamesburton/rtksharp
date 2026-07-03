using System.Text;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 9a Task 3 acceptance gate: an oracle parity battery for the ported <c>hook</c> verb
/// (the four native AI-agent hook processors — <c>claude</c>, <c>cursor</c>, <c>gemini</c>,
/// <c>copilot</c> — plus the <c>hook check</c> dry-run). Pipes a fixed set of stdin JSON
/// payloads into both binaries and compares stdout byte-exact plus exit codes, asserting
/// &gt;=95% parity (expected 100%, since the protocol is fully deterministic — JSON in, JSON
/// out, no network, no filesystem beyond the shared <c>~/.claude/settings.json</c> rule
/// loading closed in Task 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stdin piping.</b> Rust's oracle reads the payload from real stdin; the port's
/// <c>HookCommand</c> processors do too. <see cref="ExecutionRequest.StdinContent"/> (added by
/// this task) redirects the child's standard input, writes the payload, and closes the stream
/// so both binaries observe EOF exactly as they would under a real <c>echo ... | rtk hook ...</c>
/// pipe.
/// </para>
/// <para>
/// <b>Shared settings.json (Task 1).</b> Both binaries load the same real
/// <c>~/.claude/settings.json</c> allow/ask rules — no isolation env vars are applied, unlike
/// <c>RewriteParityTests</c> — because the hook contract is symmetric now: whatever the host
/// configures, both sides see it identically. This host has <c>Bash(git:*)</c> in its allow
/// list and no deny rules, so <c>git</c> commands exercise the Allow branch and everything
/// else (e.g. <c>cargo test</c>, bare <c>ls</c>) exercises the Ask branch. The Deny branch has
/// no live trigger on this host (no configured deny rule, and RTK has no hardcoded deny list —
/// deny is purely settings-driven); it is covered deterministically by
/// <c>RtkSharp.Tests/Hooks/HookCommandTests.cs</c> via explicit rule injection instead.
/// </para>
/// <para>
/// <b>Stdout only.</b> Like <c>GhParityTests</c>/<c>GitParityTests</c>, only stdout and exit
/// code are compared. Stderr is intentionally excluded: the malformed-JSON diagnostic
/// (<c>[rtk hook] Failed to parse JSON input: {message}</c>) has a byte-exact prefix on both
/// sides but a message suffix that differs between serde_json and System.Text.Json — a
/// documented, expected divergence (see Task 2's fix-round report), not a parity defect.
/// </para>
/// <para>
/// <b>Deterministic — no masking, no retries.</b> Every entry is pure JSON-in/JSON-out with no
/// network or live data, so (unlike the gh/rewrite batteries) no live-drift retry or output
/// masking is applied.
/// </para>
/// <para>
/// The oracle is <c>target/release/rtk.exe</c>; the port is the RtkSharp apphost beside the
/// test assembly — a fresh build via the project reference (never a stale
/// <c>.artifacts/publish</c> copy), matching the established batteries' pattern.
/// </para>
/// </remarks>
public class HookParityTests
{
    private const double ParityThresholdPercent = 95.0;

    /// <summary>A single battery entry: a label, the argv after the exe, and optional stdin payload.</summary>
    private sealed record Entry(string Label, string[] Args, string? Stdin);

    [Fact]
    public async Task HookVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the hook-parity gate.");
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
        var results = new List<HookResult>();

        foreach (var entry in battery)
        {
            var (rustOut, rustExit) = await RunCaptureAsync(oraclePath, entry.Args, entry.Stdin, repoRoot);
            var (portOut, portExit) = await RunCaptureAsync(
                portFileName, portPrefixArgs.Concat(entry.Args).ToArray(), entry.Stdin, repoRoot);

            results.Add(new HookResult(entry.Label, Normalize(rustOut), Normalize(portOut), rustExit, portExit));
        }

        var matched = results.Count(r => r.IsMatch);
        var total = results.Count;
        var percent = total == 0 ? 100.0 : matched * 100.0 / total;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "hook-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"hook parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} " +
                $"rustOut=[{r.RustStdout}] portOut=[{r.PortStdout}]");
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    /// <summary>Builds the battery: 8 claude + 6 cursor + 6 gemini + 6 copilot + 3 check entries.</summary>
    private static IReadOnlyList<Entry> BuildBattery()
    {
        var entries = new List<Entry>();

        // ── claude ───────────────────────────────────────────────────────
        entries.Add(new("claude: rewritable git (allow)", ["hook", "claude"],
            """{"tool_name":"Bash","tool_input":{"command":"git status"}}"""));
        entries.Add(new("claude: rewritable cargo (ask)", ["hook", "claude"],
            """{"tool_name":"Bash","tool_input":{"command":"cargo test"}}"""));
        entries.Add(new("claude: rewritable ls (ask)", ["hook", "claude"],
            """{"tool_name":"Bash","tool_input":{"command":"ls -la"}}"""));
        entries.Add(new("claude: non-rewritable command", ["hook", "claude"],
            """{"tool_name":"Bash","tool_input":{"command":"htop"}}"""));
        entries.Add(new("claude: non-Bash tool", ["hook", "claude"],
            """{"tool_name":"Read","tool_input":{"file_path":"/x"}}"""));
        entries.Add(new("claude: malformed JSON", ["hook", "claude"], "not valid json {{{"));
        entries.Add(new("claude: empty stdin", ["hook", "claude"], ""));
        entries.Add(new("claude: compound command", ["hook", "claude"],
            """{"tool_name":"Bash","tool_input":{"command":"git add . && cargo test"}}"""));

        // ── cursor ───────────────────────────────────────────────────────
        entries.Add(new("cursor: rewritable git (allow)", ["hook", "cursor"],
            """{"tool_name":"Bash","tool_input":{"command":"git status"}}"""));
        entries.Add(new("cursor: rewritable cargo (ask, defers)", ["hook", "cursor"],
            """{"tool_name":"Bash","tool_input":{"command":"cargo test"}}"""));
        entries.Add(new("cursor: non-rewritable command", ["hook", "cursor"],
            """{"tool_name":"Bash","tool_input":{"command":"htop"}}"""));
        entries.Add(new("cursor: malformed JSON", ["hook", "cursor"], "xx{{"));
        entries.Add(new("cursor: empty stdin", ["hook", "cursor"], ""));
        entries.Add(new("cursor: compound with leading cd", ["hook", "cursor"],
            """{"tool_name":"Bash","tool_input":{"command":"cd \"/tmp/proj\" && git status"}}"""));

        // ── gemini ───────────────────────────────────────────────────────
        entries.Add(new("gemini: rewritable git (allow)", ["hook", "gemini"],
            """{"tool_name":"run_shell_command","tool_input":{"command":"git status"}}"""));
        entries.Add(new("gemini: rewritable cargo (ask_user)", ["hook", "gemini"],
            """{"tool_name":"run_shell_command","tool_input":{"command":"cargo test"}}"""));
        entries.Add(new("gemini: non-rewritable command", ["hook", "gemini"],
            """{"tool_name":"run_shell_command","tool_input":{"command":"htop"}}"""));
        entries.Add(new("gemini: non-shell tool", ["hook", "gemini"],
            """{"tool_name":"read_file","tool_input":{}}"""));
        entries.Add(new("gemini: malformed JSON (exit 1)", ["hook", "gemini"], "xx{{"));
        entries.Add(new("gemini: empty stdin (exit 1)", ["hook", "gemini"], ""));

        // ── copilot ──────────────────────────────────────────────────────
        entries.Add(new("copilot VS Code: rewritable git (allow)", ["hook", "copilot"],
            """{"tool_name":"Bash","tool_input":{"command":"git status"}}"""));
        entries.Add(new("copilot VS Code: rewritable cargo (ask)", ["hook", "copilot"],
            """{"tool_name":"Bash","tool_input":{"command":"cargo test"}}"""));
        entries.Add(new("copilot VS Code: non-Bash tool", ["hook", "copilot"],
            """{"tool_name":"editFiles"}"""));
        entries.Add(new("copilot CLI: rewritable git (allow)", ["hook", "copilot"],
            """{"toolName":"bash","toolArgs":"{\"command\":\"git status\"}"}"""));
        entries.Add(new("copilot CLI: rewritable cargo (ask)", ["hook", "copilot"],
            """{"toolName":"bash","toolArgs":"{\"command\":\"cargo test\",\"description\":\"run\"}"}"""));
        entries.Add(new("copilot: malformed JSON", ["hook", "copilot"], "not json {{{"));

        // ── hook check ───────────────────────────────────────────────────
        entries.Add(new("check: rewritable (default agent)", ["hook", "check", "git", "status"], null));
        entries.Add(new("check: --agent gemini rewritable", ["hook", "check", "--agent", "gemini", "cargo", "test"], null));
        entries.Add(new("check: non-rewritable", ["hook", "check", "htop"], null));

        return entries;
    }

    // ===================== helpers =====================

    /// <summary>Runs one binary once, piping <paramref name="stdin"/> if provided, and returns stdout + exit code.</summary>
    private static async Task<(string Stdout, int ExitCode)> RunCaptureAsync(
        string fileName, string[] args, string? stdin, string cwd)
    {
        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(
            new ExecutionRequest(fileName, args, cwd, null, CaptureMode: ExecutionCaptureMode.Separate, StdinContent: stdin));
        return (result.Stdout, result.ExitCode);
    }

    /// <summary>Normalizes CRLF/LF line endings so a platform newline difference never counts as a mismatch.</summary>
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

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
        IReadOnlyList<HookResult> results,
        double percent,
        int matched,
        int total,
        string oraclePath,
        string portFileName,
        string[] portPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# Hook-Processor Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/HookParityTests.cs` " +
                      "(Phase 9a Task 3 acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries " +
                      "(8 claude + 6 cursor + 6 gemini + 6 copilot + 3 hook check)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: stdout with CRLF/LF normalized, compared byte-exact, plus exit " +
                      "code. Stdin payloads are piped to both binaries via `ExecutionRequest.StdinContent` " +
                      "(added by this task). Both sides read the same real `~/.claude/settings.json` " +
                      "allow/ask rules (symmetric since Task 1) — no environment isolation is applied, " +
                      "unlike the rewrite-parity battery. Stderr is intentionally not compared (see class " +
                      "remarks: the malformed-JSON diagnostic prefix is byte-exact but the serde_json vs " +
                      "System.Text.Json message suffix differs). Fully deterministic protocol — no live " +
                      "data, no masking, no drift retries.");
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
        sb.AppendLine("|-------|-----------|-----------|---------------|---------|");
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
            sb.AppendLine("None — every battery entry matched byte-exact stdout with an identical exit code.");
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

    /// <summary>Escapes a cell value for a Markdown table (pipes, newlines, empties).</summary>
    private static string Md(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "*(empty)*";
        }

        return "`" + s.Replace("\n", " ").Replace("|", "\\|") + "`";
    }

    /// <summary>The parity outcome for a single hook battery entry.</summary>
    private sealed record HookResult(string Label, string RustStdout, string PortStdout, int RustExit, int PortExit)
    {
        public bool StdoutMatches => RustStdout == PortStdout;

        public bool ExitsMatch => RustExit == PortExit;

        public bool IsMatch => StdoutMatches && ExitsMatch;

        public string Verdict => IsMatch
            ? "MATCH"
            : StdoutMatches
                ? "MISMATCH (exit)"
                : "MISMATCH (stdout)";
    }
}
