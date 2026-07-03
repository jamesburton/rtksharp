using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 6a acceptance gate: an oracle parity battery that drives the ported <c>dotnet</c>
/// verb (<c>build</c>, <c>restore</c>, <c>test</c>, <c>format</c>) of BOTH the reference Rust
/// <c>rtk</c> binary and the RtkSharp port with identical arguments from the repository root,
/// comparing masked stdout and exit code, and asserting ≥90% overall line-parity.
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
/// full project count of a multi-project solution (e.g. <c>4 projects</c>) even for an up-to-date
/// no-op build. RtkSharp defers binlog parsing (see <c>DotnetCommand.cs</c> remarks) and counts
/// <c>.csproj</c> mentions in the console text — identical to Rust's <c>PROJECT_PATH_RE</c> text
/// fallback — which under-reports on a no-op multi-project build (<c>1 projects</c> for build,
/// <c>0 projects</c> for restore). This is the exact class already ledgered in
/// <c>docs/parity/compatibility-ledger.md</c> (the restore <c>0 vs 1 projects</c> row and the
/// binlog-deferral rows). It is tolerated ONLY for the two multi-project <c>*.slnx</c> entries via
/// the per-entry <c>AllowProjectCountDivergence</c> flag, which masks the <c>N projects</c> token on
/// the summary line on both sides. Error/warning counts, diagnostic detail lines, and every other
/// line are compared strictly — single-project entries (<c>build RtkSharp.csproj</c>,
/// <c>test RtkSharp.Tests</c>) do NOT get the allowance and match the count exactly.
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

    // "N projects" summary token — masked only for entries that opt into the binlog-deferral
    // allowance via AllowProjectCountDivergence.
    private static readonly Regex ProjectCountRegex =
        new(@"\b\d+ projects\b", RegexOptions.Compiled);

    // Absolute temp paths with random-suffix segments (TRX results dirs, MSBuild temp files).
    // Defensive: the current dotnet summaries do not leak these, but a random temp segment would
    // otherwise fail parity. Matches e.g. C:\...\Temp\<random>\ or /tmp/<random>/.
    private static readonly Regex TempPathRegex =
        new(@"(?:[A-Za-z]:\\|/)[^\s]*?[\\/](?:Temp|tmp)[\\/][^\s\\/]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The battery: the five phase-gate <c>dotnet</c> invocations. Each entry is
    /// (display label, argument vector, project-count-allowance flag) run verbatim on both sides
    /// from the repo root. <c>AllowProjectCountDivergence</c> is <c>true</c> only for the two
    /// multi-project <c>*.slnx</c> entries (binlog deferral — see class remarks and the ledger).
    /// </summary>
    private static readonly (string Label, string[] Args, bool AllowProjectCountDivergence)[] Battery =
    [
        ("dotnet build RtkSharp/RtkSharp.csproj", ["dotnet", "build", "RtkSharp/RtkSharp.csproj"], false),
        ("dotnet build RtkSharp.slnx", ["dotnet", "build", "RtkSharp.slnx"], true),
        ("dotnet restore RtkSharp.slnx", ["dotnet", "restore", "RtkSharp.slnx"], true),
        ("dotnet test RtkSharp.Tests", ["dotnet", "test", "RtkSharp.Tests"], false),
        ("dotnet format RtkSharp.slnx --verify-no-changes",
            ["dotnet", "format", "RtkSharp.slnx", "--verify-no-changes"], false),
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

        var results = new List<DotnetResult>();

        foreach (var (label, args, allowProjectCount) in Battery)
        {
            // Oracle then port, back-to-back on the same tree state.
            var (rustOut, rustExit) = await ParityRunner.RunAsync(oraclePath, args, repoRoot);

            var dotnetArgs = dotnetPrefixArgs.Concat(args).ToArray();
            var (dotnetOut, dotnetExit) = await ParityRunner.RunAsync(dotnetFileName, dotnetArgs, repoRoot);

            results.Add(DotnetResult.Compare(
                label,
                Clean(rustOut, allowProjectCount),
                Clean(dotnetOut, allowProjectCount),
                rustExit,
                dotnetExit,
                allowProjectCount));
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
    /// Normalizes line endings, strips tee-hint lines, masks non-deterministic duration and temp-path
    /// tokens, and — when <paramref name="maskProjectCount"/> is set — masks the <c>N projects</c>
    /// summary token (scoped binlog-deferral allowance).
    /// </summary>
    private static string Clean(string s, bool maskProjectCount)
    {
        var normalized = s.Replace("\r\n", "\n").Replace("\r", "\n");
        var kept = normalized
            .Split('\n')
            .Where(line => !TeeHintRegex.IsMatch(line.Trim()))
            .Select(line =>
            {
                var masked = DurationRegex.Replace(line, "(DURATION)");
                masked = TempPathRegex.Replace(masked, "(TEMP)");
                if (maskProjectCount)
                {
                    masked = ProjectCountRegex.Replace(masked, "(PROJECTS)");
                }

                return masked;
            });
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
                      "duration tokens (`(HH:MM:SS.ff)`, `(N.N s)`, `(N ms)`, `(unknown)`) and random " +
                      "temp paths masked on both sides; line-parity % = matching lines / max(oracle, " +
                      "dotnet). Stderr is ignored (oracle prints a hook warning there). " +
                      "Build-state determinism: a plain `dotnet build RtkSharp.slnx` warms the tree " +
                      "before capture so both sides see the incremental no-op shape; oracle and port " +
                      "run back-to-back per entry.");
        sb.AppendLine();
        sb.AppendLine("Scoped allowance (binlog deferral): the two multi-project `*.slnx` entries mask " +
                      "the `N projects` summary token on both sides. The Rust oracle reports the full " +
                      "count from the MSBuild binary log (`4 projects`); RtkSharp defers binlog and " +
                      "counts `.csproj` text mentions (`1`/`0 projects` on a no-op build) — the exact " +
                      "class ledgered in `docs/parity/compatibility-ledger.md`. Error/warning counts " +
                      "and diagnostic detail lines are compared strictly; single-project entries do " +
                      "NOT get the allowance.");
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
                $"{r.LineParityPercent:F1}% | {(r.ProjectCountAllowed ? "project-count (binlog)" : "—")} | {r.Verdict} |");
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
        IReadOnlyList<string> Diffs)
    {
        public double LineParityPercent => TotalLines == 0 ? 100.0 : MatchedLines * 100.0 / TotalLines;

        public bool ExitsMatch => RustExit == DotnetExit;

        public bool IsPerfectMatch => MatchedLines == TotalLines && ExitsMatch;

        public string Verdict => IsPerfectMatch
            ? ProjectCountAllowed ? "MATCH (project-count masked, binlog deferral)" : "MATCH"
            : MatchedLines == TotalLines
                ? "MISMATCH (exit)"
                : "MISMATCH (stdout)";

        /// <summary>
        /// Compares two already-cleaned/masked outputs line-by-line at matching indices. Total lines
        /// is the longer of the two so extra/missing lines count against parity.
        /// </summary>
        public static DotnetResult Compare(
            string label, string rustOut, string dotnetOut, int rustExit, int dotnetExit,
            bool projectCountAllowed)
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

            return new DotnetResult(label, rustExit, dotnetExit, matched, total, projectCountAllowed, diffs);
        }
    }
}
