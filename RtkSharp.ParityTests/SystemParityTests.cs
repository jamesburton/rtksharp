using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 5a acceptance gate: an oracle parity battery that drives the six ported
/// system verbs (<c>ls</c>, <c>read</c>, <c>wc</c>, <c>tree</c>, <c>find</c>, <c>grep</c>)
/// of BOTH the reference Rust <c>rtk</c> binary and the RtkSharp port with identical
/// arguments from the repository root, comparing stdout and exit code, and asserting
/// ≥90% overall line-parity.
/// </summary>
/// <remarks>
/// <para>
/// The comparison is CLI-to-CLI: each battery entry is run as
/// <c>&lt;binary&gt; &lt;verb&gt; &lt;args…&gt;</c> on both sides with the working directory set to
/// the repo root (all probe targets use repo-relative paths). Stdout is compared after
/// (a) CRLF/LF normalization and (b) stripping non-deterministic tee-hint lines
/// (<c>[full output: …]</c> / <c>[see remaining: …]</c>) from both sides.
/// </para>
/// <para>
/// Stderr is intentionally ignored: the oracle emits a <c>[rtk] /!\ No hook installed</c>
/// warning there that is not part of any verb's contract. These verbs do not consult the
/// permission system, so the host <c>~/.claude/settings.json</c> exit-code deviation that
/// affects <c>rewrite</c> does not apply here.
/// </para>
/// <para>
/// <b>The <c>tree</c> case.</b> Windows ships a native <c>tree.com</c> in System32, so both
/// binaries shell out to IT. Invoked with rtk's default arguments it emits a
/// "Too many parameters" usage error with exit 0 on both sides — that identical failure
/// shape IS the parity case and is compared as-is.
/// </para>
/// </remarks>
public class SystemParityTests
{
    private const double ParityThresholdPercent = 90.0;

    private static readonly Regex TeeHintRegex =
        new(@"^\[(full output|see remaining):.*\]$", RegexOptions.Compiled);

    /// <summary>
    /// The battery: at least three variants per verb, plus the <c>tree</c> missing-tool
    /// parity case. Each entry is (display label, argument vector, ordering-tolerance flag)
    /// run verbatim on both sides. <c>AllowUnorderedLines</c> is <c>true</c> only for the
    /// <c>grep -rln</c> files-with-matches entry — see <see cref="CommandResult.Compare"/>.
    /// </summary>
    private static readonly (string Command, string[] Args, bool AllowUnorderedLines)[] Battery =
    [
        ("ls RtkSharp", ["ls", "RtkSharp"], false),
        ("ls -la rust-original/src/cmds/system", ["ls", "-la", "rust-original/src/cmds/system"], false),
        ("ls .", ["ls", "."], false),
        ("read .rtk/filters.toml", ["read", ".rtk/filters.toml"], false),
        ("read --max-lines 10 README.md", ["read", "--max-lines", "10", "README.md"], false),
        ("read -n RtkSharp.slnx", ["read", "-n", "RtkSharp.slnx"], false),
        ("read --level minimal rust-original/src/core/filter.rs",
            ["read", "--level", "minimal", "rust-original/src/core/filter.rs"], false),
        ("read --level aggressive rust-original/src/core/filter.rs",
            ["read", "--level", "aggressive", "rust-original/src/core/filter.rs"], false),
        ("read --level minimal .rtk/filters.toml", ["read", "--level", "minimal", ".rtk/filters.toml"], false),
        ("wc -l .rtk/filters.toml", ["wc", "-l", ".rtk/filters.toml"], false),
        ("wc README.md", ["wc", "README.md"], false),
        ("find rust-original/src/cmds/system -name \"*.rs\"", ["find", "rust-original/src/cmds/system", "-name", "*.rs"], false),
        ("find RtkSharp -type d", ["find", "RtkSharp", "-type", "d"], false),
        ("grep \"fn main\" rust-original/src/main.rs", ["grep", "fn main", "rust-original/src/main.rs"], false),
        ("grep -rln \"TokenKind\" RtkSharp/Rewrite", ["grep", "-rln", "TokenKind", "RtkSharp/Rewrite"], true),
        ("grep -C 2 \"PackAsTool\" RtkSharp/RtkSharp.csproj", ["grep", "-C", "2", "PackAsTool", "RtkSharp/RtkSharp.csproj"], false),
        ("tree", ["tree"], false),
    ];

    [Fact]
    public async Task SystemVerbs_MatchRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cd rust-original && cargo build --release` " +
                "(from PowerShell) before running the system-parity gate.");
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

        // Both rtk binaries shell out to the real Unix coreutils (`ls`, `wc`) for the
        // ls/wc verbs. Those binaries ship with Git for Windows but its `usr\bin` is not on
        // the PATH inherited from a PowerShell-launched `dotnet test`. Prepend it (when found)
        // for BOTH sides so the filters actually run against real output rather than an empty
        // spawn-failure — the rtk deployment premise is that these tools are present. `rg`
        // (used by grep) is already resolvable via the machine PATH.
        var childEnv = BuildChildEnvironment();

        var results = new List<CommandResult>();

        foreach (var (command, args, allowUnorderedLines) in Battery)
        {
            var (rustOut, rustExit) = await ParityRunner.RunAsync(oraclePath, args, repoRoot, childEnv);

            var dotnetArgs = dotnetPrefixArgs.Concat(args).ToArray();
            var (dotnetOut, dotnetExit) = await ParityRunner.RunAsync(dotnetFileName, dotnetArgs, repoRoot, childEnv);

            results.Add(CommandResult.Compare(
                command, Clean(rustOut), Clean(dotnetOut), rustExit, dotnetExit, allowUnorderedLines));
        }

        var totalLines = results.Sum(r => r.TotalLines);
        var matchedLines = results.Sum(r => r.MatchedLines);
        var overallPercent = totalLines == 0 ? 100.0 : matchedLines * 100.0 / totalLines;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "system-parity-report.md");
        await WriteReportAsync(
            reportPath, results, overallPercent, matchedLines, totalLines,
            oraclePath, dotnetFileName, dotnetPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine(
            $"System parity: {overallPercent:F1}% ({matchedLines}/{totalLines} lines matched). " +
            $"Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsPerfectMatch))
        {
            detail.AppendLine($"  {r.Verdict} [{r.Command}]: parity={r.LineParityPercent:F1}% " +
                              $"rustExit={r.RustExit} dotnetExit={r.DotnetExit}");
            foreach (var d in r.Diffs)
            {
                detail.AppendLine($"      {d}");
            }
        }

        Assert.True(overallPercent >= ParityThresholdPercent, detail.ToString());
    }

    /// <summary>
    /// Normalizes line endings (so CRLF/LF differences are not mismatches) and strips
    /// non-deterministic tee-hint lines from output before comparison.
    /// </summary>
    private static string Clean(string s)
    {
        var normalized = s.Replace("\r\n", "\n").Replace("\r", "\n");
        var kept = normalized
            .Split('\n')
            .Where(line => !TeeHintRegex.IsMatch(line.Trim()));
        return string.Join('\n', kept).TrimEnd('\n');
    }

    private static async Task WriteReportAsync(
        string reportPath,
        IReadOnlyList<CommandResult> results,
        double overallPercent,
        int matchedLines,
        int totalLines,
        string oraclePath,
        string dotnetFileName,
        string[] dotnetPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# System-Command Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/SystemParityTests.cs` " +
                      "(Phase 5a acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var dotnetInvoke = dotnetPrefixArgs.Length == 0
            ? dotnetFileName
            : $"{dotnetFileName} {string.Join(' ', dotnetPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{dotnetInvoke}`");
        sb.AppendLine($"- **Working directory (both sides):** repo root");
        sb.AppendLine($"- **Battery size:** {results.Count} commands");
        sb.AppendLine();
        sb.AppendLine("Comparison method: stdout with CRLF/LF normalized and tee-hint lines " +
                      "(`[full output: …]` / `[see remaining: …]`) stripped from both sides; " +
                      "line-parity % = matching lines / max(oracle lines, dotnet lines). " +
                      "Stderr is ignored (oracle prints a hook warning there).");
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
        sb.AppendLine("| Command | Rust exit | .NET exit | Line parity % | Verdict |");
        sb.AppendLine("|---------|-----------|-----------|---------------|---------|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| `{r.Command}` | {r.RustExit} | {r.DotnetExit} | " +
                $"{r.LineParityPercent:F1}% | {r.Verdict} |");
        }
        sb.AppendLine();

        var mismatches = results.Where(r => !r.IsPerfectMatch).ToList();
        sb.AppendLine("## Mismatch details");
        sb.AppendLine();
        if (mismatches.Count == 0)
        {
            sb.AppendLine("None — every battery command matched byte-for-byte (after CRLF/LF " +
                          "normalization and tee-hint stripping) with identical exit codes.");
        }
        else
        {
            foreach (var r in mismatches)
            {
                sb.AppendLine($"### `{r.Command}`");
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

    /// <summary>
    /// Builds the environment overrides passed to BOTH child binaries: a PATH with the Git
    /// for Windows <c>usr\bin</c> coreutils directory prepended, when it can be located.
    /// Returns null on non-Windows (coreutils are already on PATH) or when no such directory
    /// is found (the test then runs with the inherited PATH, and any resulting spawn-failure
    /// parity delta is surfaced by the assertion rather than masked).
    /// </summary>
    private static IReadOnlyDictionary<string, string?>? BuildChildEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var coreutilsDir = FindCoreutilsDir();
        if (coreutilsDir is null)
        {
            return null;
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = coreutilsDir + Path.PathSeparator + currentPath,
        };
    }

    /// <summary>
    /// Locates the Git for Windows <c>usr\bin</c> directory that contains <c>ls.exe</c>, trying
    /// the standard install locations and the directory two levels up from a <c>git.exe</c> on PATH.
    /// </summary>
    private static string? FindCoreutilsDir()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files\Git\usr\bin",
            @"C:\Program Files (x86)\Git\usr\bin",
        };

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Programs", "Git", "usr", "bin"));
        }

        // Derive from a git.exe on PATH: <GitRoot>\cmd\git.exe -> <GitRoot>\usr\bin.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "git.exe")))
                {
                    var gitRoot = Directory.GetParent(dir)?.FullName;
                    if (gitRoot is not null)
                    {
                        candidates.Add(Path.Combine(gitRoot, "usr", "bin"));
                    }
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry — skip it.
            }
        }

        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "ls.exe")));
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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RtkSharp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (no RtkSharp.slnx found in any parent directory).");
    }

    /// <summary>
    /// The parity outcome for a single battery command. Internal (not private) so
    /// <c>RtkSharp.ParityTests</c> unit tests in this assembly can construct/compare results
    /// directly (e.g. exercising the ordering-tolerance branch synthetically).
    /// </summary>
    internal sealed record CommandResult(
        string Command,
        int RustExit,
        int DotnetExit,
        int MatchedLines,
        int TotalLines,
        bool OrderingOnlyDeviation,
        IReadOnlyList<string> Diffs)
    {
        public double LineParityPercent => TotalLines == 0 ? 100.0 : MatchedLines * 100.0 / TotalLines;

        public bool ExitsMatch => RustExit == DotnetExit;

        public bool IsPerfectMatch => MatchedLines == TotalLines && ExitsMatch;

        public string Verdict => IsPerfectMatch
            ? OrderingOnlyDeviation ? "MATCH (rg ordering, nondeterministic)" : "MATCH"
            : MatchedLines == TotalLines
                ? "MISMATCH (exit)"
                : "MISMATCH (stdout)";

        /// <summary>
        /// Compares two already-cleaned outputs line-by-line at matching indices. Total lines
        /// is the longer of the two so extra/missing lines count against parity.
        /// </summary>
        /// <remarks>
        /// When <paramref name="allowUnorderedLines"/> is set AND the two outputs are unequal in
        /// index order but equal as sorted line multisets, the divergence is pure line reordering
        /// with no missing/extra/changed content. The only battery command this is enabled for is
        /// <c>grep -rln</c> (see <c>Battery</c>'s <c>AllowUnorderedLines</c> flag), whose
        /// files-with-matches list is emitted by ripgrep's parallel directory walker in a
        /// run-to-run nondeterministic order (verified: the SAME oracle binary flips the order
        /// across repeated runs). Line order is not part of the files-with-matches contract, so
        /// this is recorded as an ordering-only deviation and its lines are counted as matched;
        /// a missing, extra, or altered line would change the multiset and still fail. Every
        /// other battery entry compares strictly in-order — the tolerance does not leak to
        /// commands whose output order IS part of the contract (e.g. <c>ls</c>, <c>grep -C</c>
        /// context blocks). See <c>docs/parity/compatibility-ledger.md</c> → Known Acceptable
        /// Differences.
        /// </remarks>
        public static CommandResult Compare(
            string command, string rustOut, string dotnetOut, int rustExit, int dotnetExit,
            bool allowUnorderedLines)
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

            // Ordering-only deviation: identical line multisets, differing only in order — only
            // tolerated for battery entries that opt in via allowUnorderedLines.
            var orderingOnly = allowUnorderedLines && matched != total && SortedEqual(rustLines, dotnetLines);
            if (orderingOnly)
            {
                matched = total;
                diffs.Add("ordering-only: identical line sets, differing order (ripgrep parallel-walker nondeterminism)");
            }

            if (rustExit != dotnetExit)
            {
                diffs.Add($"exit: rust={rustExit} dotnet={dotnetExit}");
            }

            return new CommandResult(command, rustExit, dotnetExit, matched, total, orderingOnly, diffs);
        }

        /// <summary>
        /// True when <paramref name="a"/> and <paramref name="b"/> contain the same lines as
        /// multisets (same length, same elements after sorting), regardless of order. Internal
        /// (not private) so <c>RtkSharp.ParityTests</c> unit tests can exercise the ordering-
        /// tolerance branch directly without spawning the oracle or RtkSharp binaries.
        /// </summary>
        internal static bool SortedEqual(string[] a, string[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            var sa = a.OrderBy(x => x, StringComparer.Ordinal);
            var sb = b.OrderBy(x => x, StringComparer.Ordinal);
            return sa.SequenceEqual(sb, StringComparer.Ordinal);
        }
    }
}
