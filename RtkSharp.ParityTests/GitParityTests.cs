using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 7a acceptance gate: an oracle parity battery for the ported <c>git</c> verb. It drives BOTH
/// the reference Rust <c>rtk</c> binary and the RtkSharp port with identical arguments and compares
/// masked stdout (and, for the failure group, stderr) plus exit code, asserting ≥90% overall
/// line-parity. The battery has three groups:
/// </summary>
/// <remarks>
/// <para>
/// <b>Group A — this repository, committed-history READS only.</b> <c>git log -5</c>,
/// <c>git show &lt;fixed-sha&gt;</c>, <c>git diff &lt;shaA&gt; &lt;shaB&gt;</c>,
/// <c>git diff --stat &lt;shaA&gt; &lt;shaB&gt;</c>, and <c>git branch</c> run from the repo root
/// against fixed, early SHAs (<c>b315fe6</c>, <c>01d5d1f</c>, <c>4e045a7</c>). These NEVER mutate the
/// repo. Because the SHAs are fixed, the output is expected byte-identical and needs no SHA masking;
/// only the non-deterministic relative-date tokens git emits (<c>(9 minutes ago)</c>) are masked to
/// remove a minute-straddle flake between the two back-to-back captures.
/// </para>
/// <para>
/// <b>Group B — throwaway temp repos, MUTATING commands.</b> <c>git status</c> (mixed
/// untracked/modified/staged state), <c>git add .</c>, <c>git commit</c>, <c>git push origin master</c>
/// (to a local bare remote), <c>git pull</c>, <c>git fetch</c>, <c>git stash</c>, <c>git stash list</c>,
/// <c>git stash pop</c>, and <c>git worktree list</c>. For determinism each entry scaffolds TWO
/// identical temp repos — one per binary — under a fresh <c>%TEMP%</c> parent, so the oracle and the
/// port each observe pristine, identical starting state (a re-scaffold-per-side strategy; documented
/// choice). Non-deterministic tokens are masked on both sides: commit/stash/worktree SHAs
/// (7-40 hex → <c>&lt;SHA&gt;</c>), the temp-repo parent path in both slash forms
/// (→ <c>&lt;TMP&gt;</c>), and relative dates. Scaffolding and the measured command run with a fixed
/// git identity + fixed author/committer dates.
/// </para>
/// <para>
/// <b>Group C — failure paths (stderr compared).</b> <c>git add nonexistent-file.txt</c> (exit 128)
/// and <c>git push nonexistent-remote master</c> (exit 128) run in temp repos; the comparison covers
/// stdout AND stderr because that is where the failure output lives. Task 3's review flagged that the
/// port normalizes failure-path stderr trailing newlines whereas Rust's <c>eprintln!("{}", stderr)</c>
/// double-emits a newline and leaves a spurious trailing blank line (observed on the
/// <c>git add nonexistent</c> path). <b>Decision:</b> this is an intentional, documented normalization
/// — the port's output is strictly cleaner, the divergence is trailing-whitespace only, and it is
/// absorbed by the harness's standard trailing-newline trim (the same <c>TrimEnd('\n')</c> the
/// established <see cref="DotnetParityTests"/>/<see cref="SystemParityTests"/> harnesses apply), so no
/// divergence surfaces and both sides compare equal. Ledgered in
/// <c>docs/parity/compatibility-ledger.md</c>. The oracle's <c>[rtk] /!\ No hook installed</c> stderr
/// warning and git's <c>LF will be replaced by CRLF</c> autocrlf warnings are stripped from the
/// failure-group comparison on both sides (neither is part of any verb's contract).
/// </para>
/// <para>
/// Line endings are CRLF/LF-normalized and tee-hint lines stripped throughout, per the established
/// harness rules. The oracle is <c>target/release/rtk.exe</c>; the port is the RtkSharp apphost beside
/// the test assembly (identical bits to a fresh <c>.artifacts/publish</c>).
/// </para>
/// </remarks>
public class GitParityTests
{
    private const double ParityThresholdPercent = 90.0;

    /// <summary>Fixed, early SHAs in THIS repo used by the Group-A read-only entries.</summary>
    private const string ShowSha = "b315fe6";
    private const string DiffShaA = "01d5d1f";
    private const string DiffShaB = "4e045a7";

    private static readonly Regex TeeHintRegex =
        new(@"^\[(full output|see remaining):.*\]$", RegexOptions.Compiled);

    // Git's relative-date tokens — e.g. "(9 minutes ago)". Non-deterministic across the two
    // back-to-back captures (a minute may tick between them), so masked on both sides everywhere.
    private static readonly Regex RelativeDateRegex =
        new(@"\(\d+ (?:second|minute|hour|day|week|month|year)s? ago\)", RegexOptions.Compiled);

    // Commit/stash/worktree short-or-long hashes in temp-repo output. Git emits lowercase hex; the
    // 7-40 length window matches short and full object names without catching ordinary words.
    private static readonly Regex ShaRegex =
        new(@"\b[0-9a-f]{7,40}\b", RegexOptions.Compiled);

    // The oracle's hook warning and git's autocrlf warning — stripped from the failure-group stderr
    // comparison on both sides (neither is part of the git verb's contract).
    private static readonly Regex NoiseStderrRegex =
        new(@"(No hook installed|LF will be replaced by CRLF|CRLF will be replaced by LF)",
            RegexOptions.Compiled);

    /// <summary>The battery group a given entry belongs to.</summary>
    internal enum BatteryGroup
    {
        /// <summary>Group A: read-only against this repo (fixed SHAs), no scaffold, no masking beyond dates.</summary>
        ReadOnly,

        /// <summary>Group B: mutating command in a throwaway temp repo (SHA + temp-path + date masking).</summary>
        Mutating,

        /// <summary>Group C: failure path in a throwaway temp repo (stderr compared, noise stripped).</summary>
        Failure,
    }

    /// <summary>
    /// A single battery entry. <see cref="Scaffold"/> is null for Group A; for Groups B/C it prepares
    /// <c>&lt;parent&gt;/repo</c> (and any bare remote) and the measured command runs with cwd set to
    /// that repo. <see cref="CompareStderr"/> is true only for Group C, folding stderr into the
    /// compared text.
    /// </summary>
    private sealed record Entry(
        string Label,
        string[] Args,
        BatteryGroup Group,
        Func<string, Task>? Scaffold,
        bool CompareStderr);

    /// <summary>Fixed git identity + dates so scaffolded commits are deterministic and hashable.</summary>
    private static readonly IReadOnlyDictionary<string, string?> GitEnv =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = "Tester",
            ["GIT_AUTHOR_EMAIL"] = "tester@example.test",
            ["GIT_COMMITTER_NAME"] = "Tester",
            ["GIT_COMMITTER_EMAIL"] = "tester@example.test",
            ["GIT_AUTHOR_DATE"] = "2020-01-01T00:00:00 +0000",
            ["GIT_COMMITTER_DATE"] = "2020-01-01T00:00:00 +0000",
        };

    [Fact]
    public async Task GitVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the git-parity gate.");
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
        var results = new List<GitResult>();

        foreach (var entry in battery)
        {
            if (entry.Group == BatteryGroup.ReadOnly)
            {
                var (rustOut, _, rustExit) = await RunCaptureAsync(oraclePath, entry.Args, repoRoot, null);
                var (portOut, _, portExit) =
                    await RunCaptureAsync(portFileName, portPrefixArgs.Concat(entry.Args).ToArray(), repoRoot, null);

                results.Add(GitResult.Compare(
                    entry.Label, entry.Group,
                    Clean(rustOut, entry.Group, null, false),
                    Clean(portOut, entry.Group, null, false),
                    rustExit, portExit));
                continue;
            }

            // Groups B/C: scaffold two identical repos, one per binary, so both observe pristine state.
            var rustParent = NewTempParent();
            var portParent = NewTempParent();
            try
            {
                await entry.Scaffold!(rustParent);
                await entry.Scaffold!(portParent);
                var rustCwd = Path.Combine(rustParent, "repo");
                var portCwd = Path.Combine(portParent, "repo");

                var (rustOut, rustErr, rustExit) = await RunCaptureAsync(oraclePath, entry.Args, rustCwd, GitEnv);
                var (portOut, portErr, portExit) = await RunCaptureAsync(
                    portFileName, portPrefixArgs.Concat(entry.Args).ToArray(), portCwd, GitEnv);

                var rustText = entry.CompareStderr ? Combine(rustOut, rustErr) : rustOut;
                var portText = entry.CompareStderr ? Combine(portOut, portErr) : portOut;

                results.Add(GitResult.Compare(
                    entry.Label, entry.Group,
                    Clean(rustText, entry.Group, rustParent, entry.CompareStderr),
                    Clean(portText, entry.Group, portParent, entry.CompareStderr),
                    rustExit, portExit));
            }
            finally
            {
                TryDeleteDirectory(rustParent);
                TryDeleteDirectory(portParent);
            }
        }

        var totalLines = results.Sum(r => r.TotalLines);
        var matchedLines = results.Sum(r => r.MatchedLines);
        var overallPercent = totalLines == 0 ? 100.0 : matchedLines * 100.0 / totalLines;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "git-parity-report.md");
        await WriteReportAsync(
            reportPath, results, overallPercent, matchedLines, totalLines,
            oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine(
            $"git parity: {overallPercent:F1}% ({matchedLines}/{totalLines} lines matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsPerfectMatch))
        {
            detail.AppendLine($"  {r.Verdict} [{r.Label}]: parity={r.LineParityPercent:F1}% " +
                              $"rustExit={r.RustExit} portExit={r.PortExit}");
            foreach (var d in r.Diffs)
            {
                detail.AppendLine($"      {d}");
            }
        }

        Assert.True(overallPercent >= ParityThresholdPercent, detail.ToString());
    }

    /// <summary>Builds the 17-entry battery (5 Group A + 10 Group B + 2 Group C).</summary>
    private static IReadOnlyList<Entry> BuildBattery() =>
    [
        // ---- Group A: read-only reads against THIS repo (fixed SHAs) ----
        new("git log -5", ["git", "log", "-5"], BatteryGroup.ReadOnly, null, false),
        new($"git show {ShowSha}", ["git", "show", ShowSha], BatteryGroup.ReadOnly, null, false),
        new($"git diff {DiffShaA} {DiffShaB}", ["git", "diff", DiffShaA, DiffShaB],
            BatteryGroup.ReadOnly, null, false),
        new($"git diff --stat {DiffShaA} {DiffShaB}", ["git", "diff", "--stat", DiffShaA, DiffShaB],
            BatteryGroup.ReadOnly, null, false),
        new("git branch", ["git", "branch"], BatteryGroup.ReadOnly, null, false),

        // ---- Group B: mutating commands in throwaway temp repos ----
        new("git status", ["git", "status"], BatteryGroup.Mutating, ScaffoldStatusAsync, false),
        new("git add .", ["git", "add", "."], BatteryGroup.Mutating, ScaffoldAddAsync, false),
        new("git commit -m msg", ["git", "commit", "-m", "second commit"],
            BatteryGroup.Mutating, ScaffoldCommitAsync, false),
        new("git push origin master", ["git", "push", "origin", "master"],
            BatteryGroup.Mutating, ScaffoldRemoteAsync, false),
        new("git pull", ["git", "pull"], BatteryGroup.Mutating, ScaffoldClonedAsync, false),
        new("git fetch", ["git", "fetch"], BatteryGroup.Mutating, ScaffoldClonedAsync, false),
        new("git stash", ["git", "stash"], BatteryGroup.Mutating, ScaffoldDirtyAsync, false),
        new("git stash list", ["git", "stash", "list"], BatteryGroup.Mutating, ScaffoldStashedAsync, false),
        new("git stash pop", ["git", "stash", "pop"], BatteryGroup.Mutating, ScaffoldStashedAsync, false),
        new("git worktree list", ["git", "worktree", "list"], BatteryGroup.Mutating, ScaffoldBaseAsync, false),

        // ---- Group C: failure paths (stderr compared) ----
        new("git add nonexistent-file.txt", ["git", "add", "nonexistent-file.txt"],
            BatteryGroup.Failure, ScaffoldBaseAsync, true),
        new("git push nonexistent-remote master", ["git", "push", "nonexistent-remote", "master"],
            BatteryGroup.Failure, ScaffoldBaseAsync, true),
    ];

    // ===================== scaffolds =====================

    /// <summary>Creates <c>&lt;parent&gt;/repo</c> as a fresh master-branch repo with one initial commit.</summary>
    private static async Task ScaffoldBaseAsync(string parent)
    {
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        await GitAsync(parent, "init", "-q", "-b", "master", repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "mod.txt"), "other\n");
        await GitAsync(repo, "add", ".");
        await GitAsync(repo, "commit", "-qm", "initial");
    }

    /// <summary>Base repo + a modified tracked file, a staged new file, and an untracked file.</summary>
    private static async Task ScaffoldStatusAsync(string parent)
    {
        await ScaffoldBaseAsync(parent);
        var repo = Path.Combine(parent, "repo");
        await File.AppendAllTextAsync(Path.Combine(repo, "mod.txt"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged\n");
        await GitAsync(repo, "add", "staged.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "untracked\n");
    }

    /// <summary>Base repo + an unstaged modification and a new untracked file (for <c>git add .</c>).</summary>
    private static async Task ScaffoldAddAsync(string parent)
    {
        await ScaffoldBaseAsync(parent);
        var repo = Path.Combine(parent, "repo");
        await File.AppendAllTextAsync(Path.Combine(repo, "mod.txt"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "new\n");
    }

    /// <summary>Base repo + a staged modification, ready to commit.</summary>
    private static async Task ScaffoldCommitAsync(string parent)
    {
        await ScaffoldBaseAsync(parent);
        var repo = Path.Combine(parent, "repo");
        await File.AppendAllTextAsync(Path.Combine(repo, "mod.txt"), "changed\n");
        await GitAsync(repo, "add", ".");
    }

    /// <summary>Base repo + an unstaged modification (a dirty tree, for <c>git stash</c>).</summary>
    private static async Task ScaffoldDirtyAsync(string parent)
    {
        await ScaffoldBaseAsync(parent);
        var repo = Path.Combine(parent, "repo");
        await File.AppendAllTextAsync(Path.Combine(repo, "mod.txt"), "changed\n");
    }

    /// <summary>Dirty repo whose change has already been stashed (for <c>stash list</c> / <c>stash pop</c>).</summary>
    private static async Task ScaffoldStashedAsync(string parent)
    {
        await ScaffoldDirtyAsync(parent);
        await GitAsync(Path.Combine(parent, "repo"), "stash");
    }

    /// <summary>Repo with a commit and an <c>origin</c> remote pointing at a fresh local bare repo (for <c>push</c>).</summary>
    private static async Task ScaffoldRemoteAsync(string parent)
    {
        var repo = Path.Combine(parent, "repo");
        var remote = Path.Combine(parent, "remote.git");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(remote);
        await GitAsync(parent, "init", "-q", "--bare", remote);
        await GitAsync(parent, "init", "-q", "-b", "master", repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "f.txt"), "base\n");
        await GitAsync(repo, "add", ".");
        await GitAsync(repo, "commit", "-qm", "initial");
        await GitAsync(repo, "remote", "add", "origin", remote);
    }

    /// <summary>Repo pushed to its bare remote with an upstream set (for <c>pull</c> / <c>fetch</c> — up to date).</summary>
    private static async Task ScaffoldClonedAsync(string parent)
    {
        await ScaffoldRemoteAsync(parent);
        var repo = Path.Combine(parent, "repo");
        await GitAsync(repo, "push", "-q", "origin", "master");
        await GitAsync(repo, "branch", "--set-upstream-to=origin/master", "master");
    }

    // ===================== helpers =====================

    /// <summary>Runs <c>git</c> with the fixed identity env in <paramref name="cwd"/> (scaffold-only; ignores exit).</summary>
    private static async Task GitAsync(string cwd, params string[] args)
    {
        var executor = new ProcessExecutor();
        _ = await executor.ExecuteAsync(
            new ExecutionRequest("git", args, cwd, GitEnv, CaptureMode: ExecutionCaptureMode.Separate));
    }

    /// <summary>Runs one binary and returns its captured stdout, stderr, and exit code.</summary>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunCaptureAsync(
        string fileName, string[] args, string cwd, IReadOnlyDictionary<string, string?>? env)
    {
        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(
            new ExecutionRequest(fileName, args, cwd, env, CaptureMode: ExecutionCaptureMode.Separate));
        return (result.Stdout, result.Stderr, result.ExitCode);
    }

    private static string NewTempParent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "rtk-gitparity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        return parent;
    }

    /// <summary>Joins stdout then stderr for the failure-group comparison (either may be empty).</summary>
    private static string Combine(string stdout, string stderr) =>
        (stdout.Length, stderr.Length) switch
        {
            (0, _) => stderr,
            (_, 0) => stdout,
            _ => stdout + "\n" + stderr,
        };

    /// <summary>
    /// Normalizes line endings, strips tee-hint lines, masks relative dates always, masks the temp
    /// parent path (both slash forms) and SHA tokens for Groups B/C, strips oracle-only hook and git
    /// autocrlf warnings for the failure group, and trims trailing newlines (which absorbs the
    /// documented Group-C spurious-trailing-blank-line normalization).
    /// </summary>
    private static string Clean(string s, BatteryGroup group, string? tempParent, bool stripNoise)
    {
        var normalized = s.Replace("\r\n", "\n").Replace("\r", "\n");

        if (tempParent is { Length: > 0 })
        {
            normalized = Regex.Replace(normalized, Regex.Escape(tempParent), "<TMP>", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(
                normalized, Regex.Escape(tempParent.Replace('\\', '/')), "<TMP>", RegexOptions.IgnoreCase);
        }

        var kept = new List<string>();
        foreach (var line in normalized.Split('\n'))
        {
            if (TeeHintRegex.IsMatch(line.Trim()))
            {
                continue;
            }

            if (stripNoise && NoiseStderrRegex.IsMatch(line))
            {
                continue;
            }

            var masked = RelativeDateRegex.Replace(line, "(DATE)");
            if (group != BatteryGroup.ReadOnly)
            {
                masked = ShaRegex.Replace(masked, "<SHA>");
            }

            kept.Add(masked);
        }

        return string.Join('\n', kept).TrimEnd('\n');
    }

    private static async Task WriteReportAsync(
        string reportPath,
        IReadOnlyList<GitResult> results,
        double overallPercent,
        int matchedLines,
        int totalLines,
        string oraclePath,
        string portFileName,
        string[] portPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# git-Command Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/GitParityTests.cs` " +
                      "(Phase 7a acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine("- **Battery size:** " + results.Count + " commands " +
                      "(5 Group A read-only + 10 Group B mutating + 2 Group C failure)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: stdout (plus stderr for Group C) with CRLF/LF normalized and " +
                      "tee-hint lines stripped; relative-date tokens (`(N units ago)`) masked on both " +
                      "sides everywhere; for Groups B/C the throwaway temp-repo parent path (both slash " +
                      "forms → `<TMP>`) and commit/stash/worktree SHAs (7-40 hex → `<SHA>`) masked; for " +
                      "Group C the oracle's `[rtk] No hook installed` warning and git's `LF will be " +
                      "replaced by CRLF` autocrlf warnings stripped. Trailing newlines trimmed on both " +
                      "sides (absorbing the documented Group-C spurious-trailing-blank-line " +
                      "normalization). Line-parity % = matching lines / max(oracle, port). Oracle and " +
                      "port run back-to-back per entry; Groups B/C scaffold two identical temp repos " +
                      "(one per binary) so each observes pristine, identical starting state.");
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
        sb.AppendLine("| Command | Group | Rust exit | .NET exit | Line parity % | Verdict |");
        sb.AppendLine("|---------|-------|-----------|-----------|---------------|---------|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| `{r.Label}` | {r.GroupLabel} | {r.RustExit} | {r.PortExit} | " +
                $"{r.LineParityPercent:F1}% | {r.Verdict} |");
        }

        sb.AppendLine();
        var mismatches = results.Where(r => !r.IsPerfectMatch).ToList();
        sb.AppendLine("## Mismatch details");
        sb.AppendLine();
        if (mismatches.Count == 0)
        {
            sb.AppendLine("None — every battery command matched line-for-line (after normalization, " +
                          "tee-hint stripping, and the documented date/SHA/temp-path masking) with " +
                          "identical exit codes.");
        }
        else
        {
            foreach (var r in mismatches)
            {
                sb.AppendLine($"### `{r.Label}`");
                sb.AppendLine();
                sb.AppendLine($"- Verdict: **{r.Verdict}** (line parity {r.LineParityPercent:F1}%, " +
                              $"rust exit {r.RustExit}, .NET exit {r.PortExit})");
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

    /// <summary>
    /// Best-effort recursive delete of a throwaway temp repo. Git marks its object/pack files
    /// read-only, which makes a plain recursive <see cref="Directory.Delete(string, bool)"/> throw
    /// <see cref="UnauthorizedAccessException"/> on Windows — so the read-only attribute is cleared
    /// on every entry first. Remaining I/O errors are swallowed so cleanup never fails the battery.
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Directory.Delete(path, recursive: true);
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
    /// The parity outcome for a single git battery command. Internal so in-assembly unit tests can
    /// construct/compare results directly.
    /// </summary>
    internal sealed record GitResult(
        string Label,
        string GroupLabel,
        int RustExit,
        int PortExit,
        int MatchedLines,
        int TotalLines,
        IReadOnlyList<string> Diffs)
    {
        public double LineParityPercent => TotalLines == 0 ? 100.0 : MatchedLines * 100.0 / TotalLines;

        public bool ExitsMatch => RustExit == PortExit;

        public bool IsPerfectMatch => MatchedLines == TotalLines && ExitsMatch;

        public string Verdict => IsPerfectMatch
            ? "MATCH"
            : MatchedLines == TotalLines
                ? "MISMATCH (exit)"
                : "MISMATCH (stdout)";

        /// <summary>Compares two already-cleaned/masked outputs line-by-line at matching indices.</summary>
        public static GitResult Compare(
            string label, BatteryGroup group, string rustOut, string portOut, int rustExit, int portExit)
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

            var groupLabel = group switch
            {
                BatteryGroup.ReadOnly => "A (read-only)",
                BatteryGroup.Mutating => "B (mutating)",
                BatteryGroup.Failure => "C (failure)",
                _ => "?",
            };

            return new GitResult(label, groupLabel, rustExit, portExit, matched, total, diffs);
        }
    }
}
