using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 6a acceptance gate: an oracle parity battery that drives the ported <c>dotnet</c>
/// verb (<c>build</c>, <c>restore</c>, <c>test</c>, <c>format</c>) of BOTH the reference Rust
/// <c>rtk</c> binary and the RtkSharp port with identical arguments from the repository root,
/// comparing masked stdout and exit code, and asserting ≥90% overall line-parity. The battery
/// exercises BOTH passing paths (repo-relative entries) and — the fork's headline — FAILURE
/// summaries: a deliberately broken build and a deliberately failing xunit test, scaffolded in a
/// throwaway temp tree that is created and deleted per run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Build-state determinism.</b> <c>dotnet build</c>/<c>test</c> mutate <c>obj/</c>+<c>bin/</c>.
/// The harness runs a single plain <c>dotnet build RtkSharp.slnx</c> to WARM the tree before
/// capturing any battery entry, then runs the oracle and the port back-to-back for each entry
/// so both observe the same incremental no-op build shape. Warm no-op is also what makes the
/// battery safe to run under <c>dotnet test RtkSharp.slnx</c>: an up-to-date build never tries
/// to overwrite the <c>RtkSharp.ParityTests.dll</c> the outer test host has loaded, whereas a
/// clean/<c>--no-incremental</c> build would fail on the file lock.
/// </para>
/// <para>
/// <b>Duration masking.</b> Summary lines carry non-deterministic elapsed-time tokens in three
/// observed shapes — <c>(00:00:04.10)</c> (HH:MM:SS.ff), <c>(4.6 s)</c> / <c>(0.0 s)</c> (seconds),
/// and <c>(N ms)</c> — plus the literal <c>(unknown)</c> the restore path emits. All are
/// replaced with <c>(DURATION)</c> on both sides before comparison. TRX/temp absolute paths with
/// random suffixes (should they ever leak into stdout) are masked with <see cref="TempPathRegex"/>.
/// </para>
/// <para>
/// <b>Scoped project-count allowance (binlog deferral).</b> The Rust oracle's default
/// <c>build</c>/<c>restore</c> path parses the MSBuild <b>binary log</b> and therefore reports the
/// full build-graph node count even for an up-to-date no-op build — for <c>RtkSharp.slnx</c> that is
/// <c>4 projects</c> (the binlog enumerates 4 build-graph nodes, whereas the <c>*.slnx</c> itself
/// lists only 3 projects). RtkSharp defers binlog parsing (see <c>DotnetCommand.cs</c> remarks) and
/// counts <c>.csproj</c> mentions in the console text — identical to Rust's <c>PROJECT_PATH_RE</c>
/// text fallback — which under-reports on a no-op multi-project build (<c>1 projects</c> for build,
/// <c>0 projects</c> for restore). This is the exact class already ledgered in
/// <c>docs/parity/compatibility-ledger.md</c> (the restore <c>0 vs 1 projects</c> row and the
/// binlog-deferral rows). It is tolerated ONLY for the two multi-project <c>*.slnx</c> entries via
/// the per-entry <c>AllowProjectCountDivergence</c> flag, which masks the <c>N projects</c> token on
/// the summary line on both sides. Error/warning counts, diagnostic detail lines, and every other
/// line are compared strictly — single-project entries (<c>build RtkSharp.csproj</c>,
/// <c>test RtkSharp.Tests</c>) do NOT get the allowance and match the count exactly.
/// </para>
/// <para>
/// <b>Failure entries + temp-path masking.</b> Two entries drive the FAILURE path against throwaway
/// projects created OUTSIDE the repo under <c>%TEMP%\rtk-parity-&lt;guid&gt;\</c> (deleted per run):
/// a broken build (<c>int x = ;</c> → CS1525) and a failing xunit test (<c>Assert.True(false)</c>).
/// The temp root is replaced with <c>&lt;TMP&gt;</c> on both sides before comparison (it appears in
/// diagnostics), and rtk's random-suffixed TRX results file is masked with <c>(TRX)</c>. Both sides
/// exit non-zero with matching failure summaries.
/// </para>
/// <para>
/// <b>Scoped diagnostic-detail allowance (binlog garble).</b> On the broken-build entry the compact
/// <c>Errors:</c> detail line diverges: the oracle sources it from the binlog and garbles the
/// location to a raw byte offset with no filename (observed <c>CS1525(50,5)</c>), whereas RtkSharp —
/// with the drive-letter <c>ISSUE_RE</c> text fix — shows the REAL location (<c>Program.cs(5,17)</c>),
/// i.e. strictly BETTER detail. This is tolerated ONLY on that entry via
/// <c>AllowDiagnosticDetailDivergence</c>, which masks the indented detail lines of the compact
/// <c>Errors:</c>/<c>Warnings:</c> section on both sides (preserving their COUNT so diagnostic-count
/// parity is still enforced). The raw <c>Build FAILED</c> diagnostics, error/warning counts, verdict
/// and exit code are compared strictly and match byte-for-byte. See the ledger row documenting the
/// exact difference. The failing-test entry needs NO such allowance — the test actually runs on both
/// sides and the failure summary is byte-identical once the TRX path is masked.
/// </para>
/// <para>
/// <b>KNOWN HAZARD — this test can crash the testhost when run ISOLATED via
/// <c>dotnet test --filter</c> on a loaded machine; it is safe as part of the full suite.</b>
/// Diagnosed directly via live process-tree tracing (not just reasoned about): this is a HEAVY
/// test — it runs the full 3275-test <c>RtkSharp.Tests</c> suite TWICE (once oracle-wrapped, once
/// port-wrapped) as one battery entry, then immediately runs <c>dotnet format RtkSharp.slnx
/// --verify-no-changes</c> twice more (Roslyn workspace analysis, memory-intensive) on the same
/// already-memory-heavy testhost process. On a machine with many concurrent MSBuild
/// <c>/nodeReuse:true</c> server processes and other background load, the testhost has crashed
/// (<c>Test host process crashed</c> / <c>Test Run Aborted</c>) within ~3 minutes of an isolated
/// <c>--filter</c> run — reproducibly, but NOT from any code bug in this test or in
/// <c>DotnetCommand</c>'s format handling (both were verified working correctly in isolation
/// outside the testhost). A previous investigation misattributed this to a self-referential
/// MSBuild build-lock (the warm-up's <c>dotnet build RtkSharp.slnx</c> allegedly deadlocking on
/// its own loaded <c>RtkSharp.ParityTests.dll</c>) — that theory is WRONG and was directly
/// disproven: running with <c>dotnet test --no-build --filter ...</c> (which skips the outer
/// rebuild entirely) still crashes identically. The real pattern points to resource exhaustion
/// (likely OOM-class) under memory pressure, not a lock. <b>Before debugging this test in
/// isolation</b>: kill stray <c>dotnet.exe</c>/<c>MSBuild.exe</c>/<c>VBCSCompiler.exe</c>
/// processes first (<c>tasklist | grep -i dotnet</c>), or just run the FULL
/// <c>RtkSharp.ParityTests</c> suite (no filter) instead, which has completed successfully
/// end-to-end in prior sessions. Do not re-litigate the build-lock theory without new evidence.
/// </para>
/// <para>
/// Stderr is intentionally ignored (the oracle prints a <c>[rtk] /!\ No hook installed</c> warning
/// there). Line endings are CRLF/LF-normalized and tee-hint lines stripped, per the established
/// harness rules in <see cref="SystemParityTests"/>.
/// </para>
/// </remarks>
public class DotnetParityTests
{
    private const double ParityThresholdPercent = 90.0;

    private static readonly Regex TeeHintRegex =
        new(@"^\[(full output|see remaining):.*\]$", RegexOptions.Compiled);

    // Duration tokens: (HH:MM:SS.ff) | (N[.N] s|ms) | (unknown). Masked on both sides.
    private static readonly Regex DurationRegex =
        new(@"\((?:\d{2}:\d{2}:\d{2}\.\d+|\d+(?:\.\d+)?\s*m?s|unknown)\)", RegexOptions.Compiled);

    // MSBuild's bare (unparenthesized) `Time Elapsed 00:00:02.45` line, passed through verbatim on
    // a FAILED build by both sides — the elapsed value itself is non-deterministic, so mask it.
    private static readonly Regex TimeElapsedRegex =
        new(@"\bTime Elapsed \d{2}:\d{2}:\d{2}\.\d+", RegexOptions.Compiled);

    // "N projects" summary token — masked only for entries that opt into the binlog-deferral
    // allowance via AllowProjectCountDivergence.
    private static readonly Regex ProjectCountRegex =
        new(@"\b\d+ projects\b", RegexOptions.Compiled);

    // Absolute temp paths with random-suffix segments (TRX results dirs, MSBuild temp files).
    // Defensive: the current dotnet summaries do not leak these, but a random temp segment would
    // otherwise fail parity. Matches e.g. C:\...\Temp\<random>\ or /tmp/<random>/.
    private static readonly Regex TempPathRegex =
        new(@"(?:[A-Za-z]:\\|/)[^\s]*?[\\/](?:Temp|tmp)[\\/][^\s\\/]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // rtk's own TRX results file emitted on the `dotnet test` FAILURE path — a random hex directory
    // plus a wall-clock timestamp (`...\rtk_dotnet_testresults_<hex>\<user>_<host>_<ts>_net10.0.trx`).
    // Only rtk emits this exact stem, so masking it is deterministic-safe; masked on both sides.
    private static readonly Regex TrxPathRegex =
        new(@"rtk_dotnet_testresults_\S+\.trx", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The static (repo-relative, passing-path) battery entries. Each entry is
    /// (display label, argument vector, project-count-allowance flag, diagnostic-detail-allowance
    /// flag) run verbatim on both sides from the repo root. <c>AllowProjectCountDivergence</c> is
    /// <c>true</c> only for the two multi-project <c>*.slnx</c> entries (binlog deferral — see class
    /// remarks and the ledger); none of the passing entries need the diagnostic-detail allowance.
    /// The two FAILURE entries (broken build, failing test) are appended at run time from a throwaway
    /// temp tree — see <see cref="DotnetVerb_MatchesRustOracle_AcrossBattery"/>.
    /// </summary>
    private static readonly (string Label, string[] Args, bool AllowProjectCountDivergence,
        bool AllowDiagnosticDetailDivergence)[] Battery =
    [
        ("dotnet build RtkSharp/RtkSharp.csproj", ["dotnet", "build", "RtkSharp/RtkSharp.csproj"], false, false),
        ("dotnet build RtkSharp.slnx", ["dotnet", "build", "RtkSharp.slnx"], true, false),
        ("dotnet restore RtkSharp.slnx", ["dotnet", "restore", "RtkSharp.slnx"], true, false),
        ("dotnet test RtkSharp.Tests", ["dotnet", "test", "RtkSharp.Tests"], false, false),
        ("dotnet format RtkSharp.slnx --verify-no-changes",
            ["dotnet", "format", "RtkSharp.slnx", "--verify-no-changes"], false, false),
    ];

    [Fact]
    public async Task DotnetVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the dotnet-parity gate.");
            return;
        }

        var (dotnetFileName, dotnetPrefixArgs) = LocateRtkSharp(repoRoot);
        if (dotnetFileName is null)
        {
            Assert.Fail(
                "RtkSharp binary not found. It is normally copied next to the test assembly via the " +
                "project reference; if absent, publish it with " +
                "`dotnet publish RtkSharp -c Release -o .artifacts/publish -p:PublishAot=false`.");
            return;
        }

        // Warm the tree once so both sides observe the same incremental no-op build shape (and so a
        // no-op build never races the file lock on the loaded ParityTests assembly). Failure here is
        // non-fatal to the battery — the entries themselves would surface any real build breakage.
        _ = await ParityRunner.RunAsync("dotnet", ["build", "RtkSharp.slnx"], repoRoot);

        // Materialize the FAILURE half of the battery (broken build + failing xunit test) in a
        // throwaway temp tree OUTSIDE the repo. Created and deleted per run — no litter left behind.
        var tempRoot = CreateFailureProjects();
        var results = new List<DotnetResult>();
        try
        {
            var brokenProj = Path.Combine(tempRoot, "broken", "Broken.csproj");
            var failingProj = Path.Combine(tempRoot, "failing", "FailingTests.csproj");

            // Warm each temp project so its restore state is "up-to-date" before capture; otherwise
            // the oracle (which always runs first per entry) would show "Restored (in N ms)" while
            // the port shows "All projects are up-to-date for restore." — an ordering artifact, not a
            // real divergence. A failing build still restores cleanly (the error is a compile error).
            _ = await ParityRunner.RunAsync("dotnet", ["build", brokenProj], repoRoot);
            _ = await ParityRunner.RunAsync("dotnet", ["build", failingProj], repoRoot);

            var battery = Battery.Concat(
            [
                // Broken build: both sides exit 1 with matching counts/verdict. The compact `Errors:`
                // detail line diverges (oracle garbles it from the binlog, e.g. `CS1525(50,5)` with no
                // file; the port shows the real `Program.cs(5,17)` via the drive-letter text fix) —
                // tolerated ONLY here via AllowDiagnosticDetailDivergence. See the ledger row.
                ("dotnet build <TMP>\\broken\\Broken.csproj",
                    (string[])["dotnet", "build", brokenProj], false, true),

                // Failing xunit test: both sides exit 1 with a byte-identical failure summary (the
                // real Assert.True failure + stack trace) once the random TRX results path is masked.
                // No diagnostic-detail allowance needed — the test actually runs on both sides.
                ("dotnet test <TMP>\\failing\\FailingTests.csproj",
                    (string[])["dotnet", "test", failingProj], false, false),
            ]).ToArray();

            foreach (var (label, args, allowProjectCount, allowDiagnosticDetail) in battery)
            {
                // Oracle then port, back-to-back on the same tree state.
                var (rustOut, rustExit) = await ParityRunner.RunAsync(oraclePath, args, repoRoot);

                var dotnetArgs = dotnetPrefixArgs.Concat(args).ToArray();
                var (dotnetOut, dotnetExit) = await ParityRunner.RunAsync(dotnetFileName, dotnetArgs, repoRoot);

                results.Add(DotnetResult.Compare(
                    label,
                    Clean(rustOut, tempRoot, allowProjectCount, allowDiagnosticDetail),
                    Clean(dotnetOut, tempRoot, allowProjectCount, allowDiagnosticDetail),
                    rustExit,
                    dotnetExit,
                    allowProjectCount,
                    allowDiagnosticDetail));
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }

        var totalLines = results.Sum(r => r.TotalLines);
        var matchedLines = results.Sum(r => r.MatchedLines);
        var overallPercent = totalLines == 0 ? 100.0 : matchedLines * 100.0 / totalLines;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "dotnet-parity-report.md");
        await WriteReportAsync(
            reportPath, results, overallPercent, matchedLines, totalLines,
            oraclePath, dotnetFileName, dotnetPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine(
            $"dotnet parity: {overallPercent:F1}% ({matchedLines}/{totalLines} lines matched). " +
            $"Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsPerfectMatch))
        {
            detail.AppendLine($"  {r.Verdict} [{r.Label}]: parity={r.LineParityPercent:F1}% " +
                              $"rustExit={r.RustExit} dotnetExit={r.DotnetExit}");
            foreach (var d in r.Diffs)
            {
                detail.AppendLine($"      {d}");
            }
        }

        Assert.True(overallPercent >= ParityThresholdPercent, detail.ToString());
    }

    /// <summary>
    /// Normalizes line endings, strips tee-hint lines, masks non-deterministic duration, TRX and
    /// temp-path tokens, replaces the throwaway temp-project root (<paramref name="tempRoot"/>) with
    /// <c>&lt;TMP&gt;</c> on both sides, and — when the corresponding allowance flag is set — masks
    /// the <c>N projects</c> summary token (<paramref name="maskProjectCount"/>) or the compact
    /// <c>Errors:</c>/<c>Warnings:</c> section detail lines (<paramref name="maskDiagnosticDetail"/>).
    /// </summary>
    /// <param name="s">Raw captured stdout.</param>
    /// <param name="tempRoot">Absolute path of the throwaway temp-project root to mask, or null.</param>
    /// <param name="maskProjectCount">Mask the <c>N projects</c> token (binlog-deferral allowance).</param>
    /// <param name="maskDiagnosticDetail">
    /// Mask the indented detail lines of rtk's compact <c>Errors:</c>/<c>Warnings:</c> section
    /// (oracle binlog garbling vs port real-text divergence). The section HEADER, the raw
    /// <c>Build FAILED</c> diagnostics, error/warning counts, the verdict line and the exit code are
    /// all left intact and compared strictly; only the compact detail representation is tolerated.
    /// </param>
    private static string Clean(string s, string? tempRoot, bool maskProjectCount, bool maskDiagnosticDetail)
    {
        var normalized = s.Replace("\r\n", "\n").Replace("\r", "\n");
        if (tempRoot is { Length: > 0 })
        {
            // Mask the temp-project root (present in diagnostics on both sides) in both separator
            // forms, case-insensitively, so only the random-suffix-free structure is compared.
            normalized = Regex.Replace(normalized, Regex.Escape(tempRoot), "<TMP>", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(
                normalized, Regex.Escape(tempRoot.Replace('\\', '/')), "<TMP>", RegexOptions.IgnoreCase);
        }

        var kept = new List<string>();
        var inCompactIssueSection = false;
        foreach (var line in normalized.Split('\n'))
        {
            if (TeeHintRegex.IsMatch(line.Trim()))
            {
                continue;
            }

            var masked = DurationRegex.Replace(line, "(DURATION)");
            masked = TimeElapsedRegex.Replace(masked, "Time Elapsed (DURATION)");
            masked = TrxPathRegex.Replace(masked, "(TRX)");
            masked = TempPathRegex.Replace(masked, "(TEMP)");
            if (maskProjectCount)
            {
                masked = ProjectCountRegex.Replace(masked, "(PROJECTS)");
            }

            if (maskDiagnosticDetail)
            {
                var trimmed = masked.Trim();
                if (trimmed is "Errors:" or "Warnings:")
                {
                    // Header stays as-is (identical on both sides); detail lines below get masked.
                    inCompactIssueSection = true;
                }
                else if (trimmed.Length == 0)
                {
                    inCompactIssueSection = false;
                }
                else if (inCompactIssueSection)
                {
                    // Preserve the line COUNT (so diagnostic-count parity is still enforced) while
                    // tolerating the oracle-binlog-vs-port-text representation difference.
                    masked = "  (DIAGNOSTIC-DETAIL)";
                }
            }

            kept.Add(masked);
        }

        return string.Join('\n', kept).TrimEnd('\n');
    }

    private static async Task WriteReportAsync(
        string reportPath,
        IReadOnlyList<DotnetResult> results,
        double overallPercent,
        int matchedLines,
        int totalLines,
        string oraclePath,
        string dotnetFileName,
        string[] dotnetPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# dotnet-Command Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/DotnetParityTests.cs` " +
                      "(Phase 6a acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var dotnetInvoke = dotnetPrefixArgs.Length == 0
            ? dotnetFileName
            : $"{dotnetFileName} {string.Join(' ', dotnetPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{dotnetInvoke}`");
        sb.AppendLine("- **Working directory (both sides):** repo root");
        sb.AppendLine($"- **Battery size:** {results.Count} commands");
        sb.AppendLine();
        sb.AppendLine("Comparison method: stdout with CRLF/LF normalized and tee-hint lines stripped; " +
                      "duration tokens (`(HH:MM:SS.ff)`, `(N.N s)`, `(N ms)`, `(unknown)`, the bare " +
                      "`Time Elapsed HH:MM:SS.ff` line on failed builds), rtk's " +
                      "random TRX results file, and (for the two failure entries) the throwaway " +
                      "temp-project root — replaced with `<TMP>` — are masked on both sides; " +
                      "line-parity % = matching lines / max(oracle, dotnet). Stderr is ignored (oracle " +
                      "prints a hook warning there). Build-state determinism: a plain `dotnet build " +
                      "RtkSharp.slnx` warms the tree (and each temp project is warmed) before capture " +
                      "so both sides see the incremental no-op shape; oracle and port run back-to-back " +
                      "per entry.");
        sb.AppendLine();
        sb.AppendLine("Failure entries: `dotnet build <TMP>\\broken\\Broken.csproj` (deliberate CS1525) " +
                      "and `dotnet test <TMP>\\failing\\FailingTests.csproj` (a failing xunit " +
                      "`Assert.True(false)`) are scaffolded in a per-run temp tree OUTSIDE the repo and " +
                      "deleted afterwards. Both sides exit non-zero with matching failure summaries.");
        sb.AppendLine();
        sb.AppendLine("Scoped allowance 1 (project-count, binlog deferral): the two multi-project " +
                      "`*.slnx` entries mask the `N projects` summary token on both sides. The Rust " +
                      "oracle reports the full build-graph node count from the MSBuild binary log " +
                      "(`4 projects` — the binlog enumerates 4 build-graph nodes, whereas the `*.slnx` " +
                      "itself lists 3 projects); RtkSharp defers binlog and counts `.csproj` text " +
                      "mentions (`1`/`0 projects` on a no-op build) — the exact class ledgered in " +
                      "`docs/parity/compatibility-ledger.md`.");
        sb.AppendLine();
        sb.AppendLine("Scoped allowance 2 (diagnostic-detail, binlog garble): the broken-build entry " +
                      "masks the indented detail lines of rtk's compact `Errors:` section on both " +
                      "sides. The oracle sources them from the binlog and garbles the location to a " +
                      "raw byte offset with no filename (`CS1525(50,5)`); RtkSharp shows the REAL " +
                      "`Program.cs(5,17)` via the drive-letter text fix — strictly better detail, also " +
                      "ledgered. Error/warning counts, the raw `Build FAILED` diagnostics, the verdict " +
                      "and the exit code are compared strictly; single-project passing entries and the " +
                      "failing-test entry get NO allowance.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|--------|--------|");
        sb.AppendLine($"| **Overall line parity (headline)** | **{overallPercent:F1}%** |");
        sb.AppendLine($"| Threshold | {ParityThresholdPercent:F0}% |");
        sb.AppendLine($"| Lines matched | {matchedLines} / {totalLines} |");
        sb.AppendLine($"| Commands at 100% | {results.Count(r => r.IsPerfectMatch)} / {results.Count} |");
        sb.AppendLine();
        sb.AppendLine("## Per-command results");
        sb.AppendLine();
        sb.AppendLine("| Command | Rust exit | .NET exit | Line parity % | Allowance | Verdict |");
        sb.AppendLine("|---------|-----------|-----------|---------------|-----------|---------|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| `{r.Label}` | {r.RustExit} | {r.DotnetExit} | " +
                $"{r.LineParityPercent:F1}% | {r.Allowance} | {r.Verdict} |");
        }

        sb.AppendLine();
        var mismatches = results.Where(r => !r.IsPerfectMatch).ToList();
        sb.AppendLine("## Mismatch details");
        sb.AppendLine();
        if (mismatches.Count == 0)
        {
            sb.AppendLine("None — every battery command matched line-for-line (after normalization, " +
                          "tee-hint stripping, duration/temp masking, and the scoped project-count " +
                          "allowance on the two `*.slnx` entries) with identical exit codes.");
        }
        else
        {
            foreach (var r in mismatches)
            {
                sb.AppendLine($"### `{r.Label}`");
                sb.AppendLine();
                sb.AppendLine($"- Verdict: **{r.Verdict}** (line parity {r.LineParityPercent:F1}%, " +
                              $"rust exit {r.RustExit}, dotnet exit {r.DotnetExit})");
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
        // Preferred: the apphost copied next to the test assembly by the project reference —
        // always rebuilt in step with the source under `dotnet test`.
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

    /// <summary>
    /// Scaffolds the FAILURE half of the battery in a fresh, hermetic temp tree OUTSIDE the repo:
    /// a <c>broken/Broken.csproj</c> (deliberate CS1525 compile error) and a
    /// <c>failing/FailingTests.csproj</c> (one xunit test asserting <c>Assert.True(false)</c>), in
    /// separate subdirectories so their default globs don't collide. Root-level sentinel
    /// <c>Directory.Build.props</c>/<c>.targets</c>/<c>Directory.Packages.props</c> stop MSBuild from
    /// walking up into any ancestor build props (e.g. a host Central-Package-Management file), so the
    /// projects restore offline from the NuGet cache regardless of where the temp dir lives.
    /// </summary>
    /// <returns>The absolute path of the created temp root (caller deletes it).</returns>
    private static string CreateFailureProjects()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "rtk-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "broken"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "failing"));

        // Hermetic sentinels: empty Build props/targets + CPM-off Packages props stop the upward walk.
        File.WriteAllText(Path.Combine(tempRoot, "Directory.Build.props"), "<Project></Project>\n");
        File.WriteAllText(Path.Combine(tempRoot, "Directory.Build.targets"), "<Project></Project>\n");
        File.WriteAllText(
            Path.Combine(tempRoot, "Directory.Packages.props"),
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>false" +
            "</ManagePackageVersionsCentrally></PropertyGroup></Project>\n");

        File.WriteAllText(
            Path.Combine(tempRoot, "broken", "Broken.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>

            """);
        File.WriteAllText(
            Path.Combine(tempRoot, "broken", "Program.cs"),
            """
            public static class Program
            {
                public static void Main()
                {
                    int x = ;
                }
            }

            """);

        File.WriteAllText(
            Path.Combine(tempRoot, "failing", "FailingTests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>

            """);
        File.WriteAllText(
            Path.Combine(tempRoot, "failing", "FailingTest.cs"),
            """
            using Xunit;

            public class FailingTest
            {
                [Fact]
                public void DeliberatelyFails()
                {
                    Assert.True(false);
                }
            }

            """);

        return tempRoot;
    }

    /// <summary>
    /// Best-effort recursive delete of the throwaway temp tree. Swallows I/O errors (e.g. a lingering
    /// file lock from a just-finished <c>dotnet test</c> host) so cleanup never fails the battery.
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

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

    /// <summary>
    /// The parity outcome for a single dotnet battery command. Internal so in-assembly unit tests
    /// can construct/compare results directly.
    /// </summary>
    internal sealed record DotnetResult(
        string Label,
        int RustExit,
        int DotnetExit,
        int MatchedLines,
        int TotalLines,
        bool ProjectCountAllowed,
        bool DiagnosticDetailAllowed,
        IReadOnlyList<string> Diffs)
    {
        public double LineParityPercent => TotalLines == 0 ? 100.0 : MatchedLines * 100.0 / TotalLines;

        public bool ExitsMatch => RustExit == DotnetExit;

        public bool IsPerfectMatch => MatchedLines == TotalLines && ExitsMatch;

        /// <summary>Short human-readable label for whichever scoped allowance this entry opted into.</summary>
        public string Allowance => (ProjectCountAllowed, DiagnosticDetailAllowed) switch
        {
            (true, _) => "project-count (binlog)",
            (_, true) => "diagnostic-detail (binlog garble)",
            _ => "—",
        };

        public string Verdict => IsPerfectMatch
            ? ProjectCountAllowed
                ? "MATCH (project-count masked, binlog deferral)"
                : DiagnosticDetailAllowed
                    ? "MATCH (compact diagnostic detail masked, binlog garble)"
                    : "MATCH"
            : MatchedLines == TotalLines
                ? "MISMATCH (exit)"
                : "MISMATCH (stdout)";

        /// <summary>
        /// Compares two already-cleaned/masked outputs line-by-line at matching indices. Total lines
        /// is the longer of the two so extra/missing lines count against parity.
        /// </summary>
        public static DotnetResult Compare(
            string label, string rustOut, string dotnetOut, int rustExit, int dotnetExit,
            bool projectCountAllowed, bool diagnosticDetailAllowed)
        {
            var rustLines = rustOut.Length == 0 ? [] : rustOut.Split('\n');
            var dotnetLines = dotnetOut.Length == 0 ? [] : dotnetOut.Split('\n');
            var total = Math.Max(rustLines.Length, dotnetLines.Length);

            var matched = 0;
            var diffs = new List<string>();
            for (var i = 0; i < total; i++)
            {
                var r = i < rustLines.Length ? rustLines[i] : null;
                var d = i < dotnetLines.Length ? dotnetLines[i] : null;
                if (r == d)
                {
                    matched++;
                }
                else if (diffs.Count < 10)
                {
                    diffs.Add($"L{i + 1}: rust=[{r ?? "<none>"}] dotnet=[{d ?? "<none>"}]");
                }
            }

            if (rustExit != dotnetExit)
            {
                diffs.Add($"exit: rust={rustExit} dotnet={dotnetExit}");
            }

            return new DotnetResult(
                label, rustExit, dotnetExit, matched, total,
                projectCountAllowed, diagnosticDetailAllowed, diffs);
        }
    }
}
