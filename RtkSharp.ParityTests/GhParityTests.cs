using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 7b acceptance gate: an oracle parity battery for the ported <c>gh</c> (GitHub CLI) verb. It
/// drives BOTH the reference Rust <c>rtk</c> binary and the RtkSharp port with identical arguments
/// against this repository's LIVE GitHub state (gh CLI authenticated on the host) and compares masked
/// stdout plus exit code, asserting ≥90% overall line-parity.
/// </summary>
/// <remarks>
/// <para>
/// <b>All entries are READ-ONLY gh commands</b> (never <c>gh pr create/merge/comment</c> or any write).
/// The battery mixes three shapes:
/// </para>
/// <para>
/// <b>Filtered summaries</b> — <c>gh pr list --limit 5</c>, <c>gh pr view 2775</c> (a fixed MERGED PR,
/// so its body/checks are frozen and byte-stable), <c>gh issue list --limit 5</c>, <c>gh repo view</c>,
/// <c>gh run list --limit 3</c>, and <c>--ultra-compact gh pr list --limit 5</c> (exercises the new
/// ultra-compact threading). RTK filters these into terse agent summaries; the port must reproduce the
/// oracle's summary line-for-line.
/// </para>
/// <para>
/// <b>Structured-output guards</b> — <c>gh pr list --json number</c> and
/// <c>gh api repos/{owner}/{repo} --jq .name</c>. When the user asks gh for structured output RTK MUST
/// pass raw gh through untouched (the core "never corrupt structured output" contract); both sides emit
/// identical raw gh bytes.
/// </para>
/// <para>
/// <b>Passthrough parity</b> — <c>gh release list --limit 3</c>. Neither binary has a <c>release</c>
/// filter (no <c>release</c> arm in Rust's <c>gh_cmd::run</c>), so both pass gh's native table through
/// unchanged.
/// </para>
/// <para>
/// <b>Live-data masking.</b> The repo's <c>N stars | N forks</c> counts drift second-to-second and
/// <c>gh run list</c> database IDs / statuses can change as CI fires, so star/fork counts and bracketed
/// run IDs are masked on both sides. Git-style relative-date tokens (<c>(N units ago)</c>) are also
/// masked defensively though RTK's filtered summaries don't currently surface them. Line endings are
/// CRLF/LF-normalized and tee-hint lines stripped throughout, per the established harness rules.
/// </para>
/// <para>
/// <b>Live-data drift retry.</b> Oracle and port run back-to-back per entry to minimize drift. If an
/// entry does not reach 100% line-parity on the first pass (a PR/issue/run may have changed between the
/// two captures), that ONE entry is re-run once and the second result kept — matching the plan's
/// documented drift rule.
/// </para>
/// <para>
/// <b>Network required.</b> The battery needs GitHub API access. If <c>gh auth status</c> fails
/// (unauthenticated / rate-limited), the test <see cref="Assert.Fail(string)"/>s with a clear message
/// rather than reporting a spurious mismatch — mirroring the binaries-missing guard.
/// </para>
/// <para>
/// The oracle is <c>target/release/rtk.exe</c>; the port is the RtkSharp apphost beside the test
/// assembly (identical bits to a fresh <c>.artifacts/publish</c>).
/// </para>
/// </remarks>
public class GhParityTests
{
    private const double ParityThresholdPercent = 90.0;

    /// <summary>A fixed MERGED PR in this repo — its body, checks, and metadata are frozen and stable.</summary>
    private const string ClosedPrNumber = "2775";

    private static readonly Regex TeeHintRegex =
        new(@"^\[(full output|see remaining):.*\]$", RegexOptions.Compiled);

    // "68077 stars | 4211 forks" — both counts drift live, so mask the numbers on both sides.
    private static readonly Regex StarForkRegex =
        new(@"\d+ stars \| \d+ forks", RegexOptions.Compiled);

    // "[28642068840]" — gh run list database IDs (and the runs themselves) churn as CI fires; mask them.
    private static readonly Regex RunIdRegex =
        new(@"\[\d{5,}\]", RegexOptions.Compiled);

    // Git-style relative dates — masked defensively (RTK's gh summaries don't currently emit them).
    private static readonly Regex RelativeDateRegex =
        new(@"\(\d+ (?:second|minute|hour|day|week|month|year)s? ago\)", RegexOptions.Compiled);

    /// <summary>The comparison shape a battery entry belongs to (drives the report grouping only).</summary>
    internal enum EntryKind
    {
        /// <summary>RTK filters gh output into a terse summary.</summary>
        Filtered,

        /// <summary>User requested structured output (<c>--json</c>/<c>--jq</c>); both sides pass raw gh through.</summary>
        Guard,

        /// <summary>No RTK filter exists; both sides pass gh's native output through.</summary>
        Passthrough,
    }

    /// <summary>A single battery entry: a label, the argv after the port/oracle exe, and its kind.</summary>
    private sealed record Entry(string Label, string[] Args, EntryKind Kind);

    [Fact]
    public async Task GhVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cd rust-original && cargo build --release` " +
                "(from PowerShell) before running the gh-parity gate.");
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

        await AssertGitHubReachableAsync(repoRoot);

        var battery = BuildBattery();
        var results = new List<GhResult>();

        foreach (var entry in battery)
        {
            var result = await RunEntryAsync(entry, oraclePath, portFileName, portPrefixArgs, repoRoot);

            // Live-data drift rule: if the two back-to-back captures disagree, re-run the entry ONCE
            // (a PR/issue/run may have changed between the oracle and port captures) before trusting it.
            if (!result.IsPerfectMatch)
            {
                var retry = await RunEntryAsync(entry, oraclePath, portFileName, portPrefixArgs, repoRoot);
                retry = retry with { Retried = true };
                result = retry;
            }

            results.Add(result);
        }

        var totalLines = results.Sum(r => r.TotalLines);
        var matchedLines = results.Sum(r => r.MatchedLines);
        var overallPercent = totalLines == 0 ? 100.0 : matchedLines * 100.0 / totalLines;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "gh-parity-report.md");
        await WriteReportAsync(
            reportPath, results, overallPercent, matchedLines, totalLines,
            oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine(
            $"gh parity: {overallPercent:F1}% ({matchedLines}/{totalLines} lines matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsPerfectMatch))
        {
            detail.AppendLine($"  {r.Verdict} [{r.Label}]: parity={r.LineParityPercent:F1}% " +
                              $"rustExit={r.RustExit} portExit={r.PortExit} retried={r.Retried}");
            foreach (var d in r.Diffs)
            {
                detail.AppendLine($"      {d}");
            }
        }

        Assert.True(overallPercent >= ParityThresholdPercent, detail.ToString());
    }

    /// <summary>Runs one entry against both binaries back-to-back and returns a compared result.</summary>
    private static async Task<GhResult> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs, string repoRoot)
    {
        var (rustOut, _, rustExit) = await RunCaptureAsync(oraclePath, entry.Args, repoRoot);
        var (portOut, _, portExit) = await RunCaptureAsync(
            portFileName, portPrefixArgs.Concat(entry.Args).ToArray(), repoRoot);

        return GhResult.Compare(
            entry.Label, entry.Kind, Clean(rustOut), Clean(portOut), rustExit, portExit);
    }

    /// <summary>Builds the 9-entry battery (6 filtered + 2 guard + 1 passthrough).</summary>
    private static IReadOnlyList<Entry> BuildBattery() =>
    [
        // ---- filtered summaries ----
        new("gh pr list --limit 5", ["gh", "pr", "list", "--limit", "5"], EntryKind.Filtered),
        new($"gh pr view {ClosedPrNumber}", ["gh", "pr", "view", ClosedPrNumber], EntryKind.Filtered),
        new("gh issue list --limit 5", ["gh", "issue", "list", "--limit", "5"], EntryKind.Filtered),
        new("gh repo view", ["gh", "repo", "view"], EntryKind.Filtered),
        new("gh run list --limit 3", ["gh", "run", "list", "--limit", "3"], EntryKind.Filtered),
        new("--ultra-compact gh pr list --limit 5",
            ["--ultra-compact", "gh", "pr", "list", "--limit", "5"], EntryKind.Filtered),

        // ---- structured-output guards: both sides must emit identical RAW gh bytes ----
        new("gh pr list --json number", ["gh", "pr", "list", "--json", "number"], EntryKind.Guard),
        new("gh api repos/{owner}/{repo} --jq .name",
            ["gh", "api", "repos/rtk-ai/rtk", "--jq", ".name"], EntryKind.Guard),

        // ---- passthrough parity: no release filter on either side ----
        new("gh release list --limit 3", ["gh", "release", "list", "--limit", "3"], EntryKind.Passthrough),
    ];

    // ===================== helpers =====================

    /// <summary>
    /// Fails the battery early (with the binaries-missing guard's tone) when gh cannot reach GitHub,
    /// so an auth/rate-limit outage reads as an infrastructure problem rather than a parity regression.
    /// </summary>
    private static async Task AssertGitHubReachableAsync(string repoRoot)
    {
        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(
            new ExecutionRequest("gh", ["auth", "status"], repoRoot, null, CaptureMode: ExecutionCaptureMode.Separate));

        if (result.ExitCode != 0)
        {
            Assert.Fail(
                "gh CLI is not authenticated / cannot reach GitHub (`gh auth status` exited " +
                $"{result.ExitCode}). This battery needs live GitHub API access — run `gh auth login` " +
                "and retry. stderr: " + result.Stderr.Trim());
        }
    }

    /// <summary>Runs one binary in the repo root and returns its captured stdout, stderr, and exit code.</summary>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunCaptureAsync(
        string fileName, string[] args, string cwd)
    {
        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(
            new ExecutionRequest(fileName, args, cwd, null, CaptureMode: ExecutionCaptureMode.Separate));
        return (result.Stdout, result.Stderr, result.ExitCode);
    }

    /// <summary>
    /// Normalizes line endings, strips tee-hint lines, masks live-drifting star/fork counts and run
    /// IDs, masks relative-date tokens, and trims trailing newlines (mirroring the established
    /// harnesses so a trailing-newline divergence never surfaces as a mismatch).
    /// </summary>
    private static string Clean(string s)
    {
        var normalized = s.Replace("\r\n", "\n").Replace("\r", "\n");

        var kept = new List<string>();
        foreach (var line in normalized.Split('\n'))
        {
            if (TeeHintRegex.IsMatch(line.Trim()))
            {
                continue;
            }

            var masked = StarForkRegex.Replace(line, "<STARS> stars | <FORKS> forks");
            masked = RunIdRegex.Replace(masked, "[<RUNID>]");
            masked = RelativeDateRegex.Replace(masked, "(DATE)");
            kept.Add(masked);
        }

        return string.Join('\n', kept).TrimEnd('\n');
    }

    private static async Task WriteReportAsync(
        string reportPath,
        IReadOnlyList<GhResult> results,
        double overallPercent,
        int matchedLines,
        int totalLines,
        string oraclePath,
        string portFileName,
        string[] portPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# gh-Command Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/GhParityTests.cs` " +
                      "(Phase 7b acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine("- **Battery size:** " + results.Count + " commands " +
                      "(6 filtered + 2 structured-output guard + 1 passthrough)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: stdout with CRLF/LF normalized and tee-hint lines stripped; " +
                      "live-drifting `N stars | N forks` counts and bracketed `gh run list` database IDs " +
                      "masked on both sides, plus relative-date tokens (`(N units ago)`) defensively. " +
                      "Line-parity % = matching lines / max(oracle, port). Oracle and port run " +
                      "back-to-back per entry against live GitHub state; any entry that does not reach " +
                      "100% on the first pass is re-run once (drift rule) and the retry kept.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|--------|--------|");
        sb.AppendLine($"| **Overall line parity (headline)** | **{overallPercent:F1}%** |");
        sb.AppendLine($"| Threshold | {ParityThresholdPercent:F0}% |");
        sb.AppendLine($"| Lines matched | {matchedLines} / {totalLines} |");
        sb.AppendLine($"| Commands at 100% | {results.Count(r => r.IsPerfectMatch)} / {results.Count} |");
        sb.AppendLine($"| Entries re-run (drift) | {results.Count(r => r.Retried)} |");
        sb.AppendLine();
        sb.AppendLine("## Per-command results");
        sb.AppendLine();
        sb.AppendLine("| Command | Kind | Rust exit | .NET exit | Line parity % | Retried | Verdict |");
        sb.AppendLine("|---------|------|-----------|-----------|---------------|---------|---------|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| `{r.Label}` | {r.KindLabel} | {r.RustExit} | {r.PortExit} | " +
                $"{r.LineParityPercent:F1}% | {(r.Retried ? "yes" : "no")} | {r.Verdict} |");
        }

        sb.AppendLine();
        var mismatches = results.Where(r => !r.IsPerfectMatch).ToList();
        sb.AppendLine("## Mismatch details");
        sb.AppendLine();
        if (mismatches.Count == 0)
        {
            sb.AppendLine("None — every battery command matched line-for-line (after normalization, " +
                          "tee-hint stripping, and the documented star/fork/run-ID/date masking) with " +
                          "identical exit codes.");
        }
        else
        {
            foreach (var r in mismatches)
            {
                sb.AppendLine($"### `{r.Label}`");
                sb.AppendLine();
                sb.AppendLine($"- Verdict: **{r.Verdict}** (line parity {r.LineParityPercent:F1}%, " +
                              $"rust exit {r.RustExit}, .NET exit {r.PortExit}, retried {r.Retried})");
                foreach (var d in r.Diffs)
                {
                    sb.AppendLine($"- {d}");
                }

                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(reportPath, sb.ToString());
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

    /// <summary>
    /// The parity outcome for a single gh battery command. Internal so in-assembly unit tests can
    /// construct/compare results directly.
    /// </summary>
    internal sealed record GhResult(
        string Label,
        string KindLabel,
        int RustExit,
        int PortExit,
        int MatchedLines,
        int TotalLines,
        IReadOnlyList<string> Diffs)
    {
        /// <summary>Whether the entry was re-run once under the live-data drift rule.</summary>
        public bool Retried { get; init; }

        public double LineParityPercent => TotalLines == 0 ? 100.0 : MatchedLines * 100.0 / TotalLines;

        public bool ExitsMatch => RustExit == PortExit;

        public bool IsPerfectMatch => MatchedLines == TotalLines && ExitsMatch;

        public string Verdict => IsPerfectMatch
            ? "MATCH"
            : MatchedLines == TotalLines
                ? "MISMATCH (exit)"
                : "MISMATCH (stdout)";

        /// <summary>Compares two already-cleaned/masked outputs line-by-line at matching indices.</summary>
        public static GhResult Compare(
            string label, EntryKind kind, string rustOut, string portOut, int rustExit, int portExit)
        {
            var rustLines = rustOut.Length == 0 ? [] : rustOut.Split('\n');
            var portLines = portOut.Length == 0 ? [] : portOut.Split('\n');
            var total = Math.Max(rustLines.Length, portLines.Length);

            var matched = 0;
            var diffs = new List<string>();
            for (var i = 0; i < total; i++)
            {
                var r = i < rustLines.Length ? rustLines[i] : null;
                var d = i < portLines.Length ? portLines[i] : null;
                if (r == d)
                {
                    matched++;
                }
                else if (diffs.Count < 10)
                {
                    diffs.Add($"L{i + 1}: rust=[{r ?? "<none>"}] port=[{d ?? "<none>"}]");
                }
            }

            if (rustExit != portExit)
            {
                diffs.Add($"exit: rust={rustExit} port={portExit}");
            }

            var kindLabel = kind switch
            {
                EntryKind.Filtered => "filtered",
                EntryKind.Guard => "guard",
                EntryKind.Passthrough => "passthrough",
                _ => "?",
            };

            return new GhResult(label, kindLabel, rustExit, portExit, matched, total, diffs);
        }
    }
}
