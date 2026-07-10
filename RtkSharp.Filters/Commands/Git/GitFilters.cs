using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Git;

/// <summary>
/// Pure filtering logic for the <c>git</c> proxy: condensing <c>git log</c>/<c>diff</c>/<c>status</c>/
/// <c>push</c>/<c>pull</c>/<c>branch</c>/<c>fetch</c>/<c>stash</c>/<c>worktree</c> output. Extracted
/// verbatim from <c>RtkSharp.Commands.Git.GitCommand</c> (Phase 7a, Tasks 1-3) — signatures and logic
/// are unchanged, only visibility moved from <c>internal</c> to <c>public</c> and the containing type
/// from <c>GitCommand</c> to <c>GitFilters</c>. See <c>RtkSharp.Commands.Git.GitCommand</c> for the
/// process-execution/dispatch code that calls these methods, and for the original Rust source
/// pointers (<c>src/cmds/git/git.rs</c>) preserved in each method's XML doc.
/// </summary>
/// <remarks>
/// <b>Private helpers duplicated, not shared, with <c>GitCommand.cs</c>.</b> A handful of small pure
/// helpers (<c>ExtractDiffFileName</c>, <c>FormatStatusInner</c>, <c>TrimStartMatches</c>,
/// <c>LeadingCount</c>) are used exclusively by the methods moved here, so they moved wholesale as
/// private members of this class. Three helpers — <c>IsPushNoiseLine</c>, <c>ExtractPushedRef</c>, and
/// <c>GitPushNoisePrefixes</c> — are used by both <see cref="FilterPushOutput"/> here AND by
/// <c>GitCommand.RunPushAsync</c>'s still-live streaming logic (and <c>ExtractPushedRef</c> is directly
/// unit-tested under its original <c>GitCommand.ExtractPushedRef</c> name), so rather than promoting
/// them to a shared cross-assembly surface, small private copies were duplicated here for
/// <see cref="FilterPushOutput"/>'s exclusive use — the originals in <c>GitCommand.cs</c> are
/// untouched. This is a disclosed, deliberate, minimal duplication (a handful of lines), not an
/// oversight.
/// </remarks>
public static class GitFilters
{
    /// <summary>Commit-block separator RTK injects via its <c>--pretty=format:</c> (git.rs:447).</summary>
    private const string LogBlockSeparator = "---END---";

    /// <summary>Truncation width for log headers/bodies when RTK set the default limit (git.rs:555).</summary>
    private const int LogTruncateDefault = 80;

    /// <summary>Wider truncation width when the user set an explicit <c>-N</c> limit (git.rs:555).</summary>
    private const int LogTruncateUserLimit = 120;

    /// <summary>Maximum commit-body lines kept per commit before an omission marker (git.rs:596).</summary>
    private const int LogMaxBodyLines = 3;

    /// <summary>Maximum +/- lines shown per hunk before the remainder is counted as truncated (git.rs:338).</summary>
    private const int CompactDiffMaxHunkLines = 100;

    /// <summary>
    /// Maximum remote-only branches listed before a <c>... +N more</c> overflow marker. Rust binds
    /// this to <c>CAP_WARNINGS</c> (= 10) from <c>src/core/truncate.rs</c> (git.rs:1429).
    /// </summary>
    private const int BranchRemoteCap = 10;

    /// <summary>
    /// Git push progress prefixes (matched after trimming leading whitespace) dropped from the stream.
    /// Ported from <c>GIT_PUSH_NOISE_PREFIXES</c> (git.rs:1063-1070). Duplicated from
    /// <c>GitCommand.GitPushNoisePrefixes</c> — see class remarks.
    /// </summary>
    private static readonly string[] GitPushNoisePrefixes =
    {
        "Enumerating objects:",
        "Counting objects:",
        "Compressing objects:",
        "Writing objects:",
        "Delta compression using",
        "Total ",
    };

    /// <summary>
    /// Filters git log output: truncates long lines and, for RTK's injected format, keeps a commit
    /// header plus up to three body lines with an omission marker. When <paramref name="userSetLimit"/>
    /// is true the caller passed an explicit <c>-N</c>, so line-capping is skipped (git already
    /// returned exactly N commits) and a wider truncation width preserves rebase/squash context.
    /// Ports <c>filter_log_output</c> (git.rs:549).
    /// </summary>
    /// <param name="output">The raw <c>git log</c> stdout.</param>
    /// <param name="limit">The commit/line cap RTK applies when it set the default limit.</param>
    /// <param name="userSetLimit">Whether the user passed an explicit <c>-N</c> limit.</param>
    /// <param name="userFormat">Whether the user supplied their own <c>--oneline</c>/<c>--pretty</c>/<c>--format</c>.</param>
    /// <returns>The filtered log text (no trailing newline).</returns>
    public static string FilterLogOutput(string output, int limit, bool userSetLimit, bool userFormat)
    {
        ArgumentNullException.ThrowIfNull(output);

        var truncateWidth = userSetLimit ? LogTruncateUserLimit : LogTruncateDefault;

        // User-supplied format: RTK injected no ---END--- markers, so truncate line-by-line.
        if (userFormat)
        {
            var lines = SourceFilterLineSplitter.SplitLines(output);
            var maxLines = userSetLimit ? lines.Count : limit;
            return string.Join("\n", lines.Take(maxLines).Select(l => TruncateLine(l, truncateWidth)));
        }

        // RTK-injected format: split into commit blocks separated by ---END---.
        var commits = output.Split(LogBlockSeparator);
        var maxCommits = userSetLimit ? commits.Length : limit;

        var result = new List<string>();
        foreach (var rawBlock in commits.Take(maxCommits))
        {
            var block = rawBlock.Trim();
            if (block.Length == 0)
            {
                continue;
            }

            var blockLines = SourceFilterLineSplitter.SplitLines(block);
            if (blockLines.Count == 0)
            {
                continue;
            }

            // First line is the header: hash subject (date) <author>.
            var header = TruncateLine(blockLines[0].Trim(), truncateWidth);

            // Remaining lines: keep up to 3 non-empty, non-trailer lines.
            var allBodyLines = blockLines
                .Skip(1)
                .Select(l => l.Trim())
                .Where(l =>
                    l.Length != 0
                    && !l.StartsWith("Signed-off-by:", StringComparison.Ordinal)
                    && !l.StartsWith("Co-authored-by:", StringComparison.Ordinal))
                .ToList();

            var bodyOmitted = Math.Max(0, allBodyLines.Count - LogMaxBodyLines);
            var bodyLines = allBodyLines.Take(LogMaxBodyLines).ToList();

            if (bodyLines.Count == 0)
            {
                result.Add(header);
            }
            else
            {
                var entry = new StringBuilder(header);
                foreach (var body in bodyLines)
                {
                    entry.Append($"\n  {TruncateLine(body, truncateWidth)}");
                }

                if (bodyOmitted > 0)
                {
                    entry.Append($"\n  [+{bodyOmitted} lines omitted]");
                }

                result.Add(entry.ToString());
            }
        }

        return string.Join("\n", result).Trim();
    }

    /// <summary>
    /// Truncates a single line to <paramref name="width"/> Unicode scalar values, appending
    /// <c>...</c> when it was longer. Ports <c>truncate_line</c> (git.rs:616), counting <c>char</c>s
    /// the way Rust counts <c>chars()</c> so multibyte input is never split mid-character.
    /// </summary>
    /// <param name="line">The line to truncate.</param>
    /// <param name="width">The maximum width in Unicode scalar values.</param>
    /// <returns>The (possibly truncated) line.</returns>
    public static string TruncateLine(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);

        var runes = line.EnumerateRunes().ToList();
        if (runes.Count <= width)
        {
            return line;
        }

        var kept = string.Concat(runes.Take(width - 3).Select(r => r.ToString()));
        return $"{kept}...";
    }

    /// <summary>
    /// Condenses a unified diff to the changed lines, keeping hunk headers and per-file <c>+A -R</c>
    /// counts while dropping index/mode metadata and unchanged context that precedes the first change
    /// in a hunk. Caps each hunk at <see cref="CompactDiffMaxHunkLines"/> shown lines and the whole
    /// output at <paramref name="maxLines"/> entries, appending truncation markers when either limit
    /// is hit. Ports <c>compact_diff</c> (git.rs:330).
    /// </summary>
    /// <param name="diff">The raw unified diff text.</param>
    /// <param name="maxLines">The overall entry budget before the diff is truncated.</param>
    /// <returns>The compacted diff (no trailing newline).</returns>
    public static string CompactDiff(string diff, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var result = new List<string>();
        var currentFile = string.Empty;
        var added = 0;
        var removed = 0;
        var inHunk = false;
        var hunkShown = 0;
        var hunkSkipped = 0;
        var wasTruncated = false;

        foreach (var line in SourceFilterLineSplitter.SplitLines(diff))
        {
            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                // Flush hunk truncation before starting a new file.
                if (hunkSkipped > 0)
                {
                    result.Add($"  ... ({hunkSkipped} lines truncated)");
                    wasTruncated = true;
                    hunkSkipped = 0;
                }

                if (currentFile.Length != 0 && (added > 0 || removed > 0))
                {
                    result.Add($"  +{added} -{removed}");
                }

                currentFile = ExtractDiffFileName(line);
                result.Add($"\n{currentFile}");
                added = 0;
                removed = 0;
                inHunk = false;
                hunkShown = 0;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                // Flush hunk truncation before starting a new hunk.
                if (hunkSkipped > 0)
                {
                    result.Add($"  ... ({hunkSkipped} lines truncated)");
                    wasTruncated = true;
                    hunkSkipped = 0;
                }

                inHunk = true;
                hunkShown = 0;

                // Preserve the full unified diff hunk header, including trailing function/symbol context.
                result.Add($"  {line}");
            }
            else if (inHunk)
            {
                if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    added++;
                    if (hunkShown < CompactDiffMaxHunkLines)
                    {
                        result.Add($"  {line}");
                        hunkShown++;
                    }
                    else
                    {
                        hunkSkipped++;
                    }
                }
                else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
                {
                    removed++;
                    if (hunkShown < CompactDiffMaxHunkLines)
                    {
                        result.Add($"  {line}");
                        hunkShown++;
                    }
                    else
                    {
                        hunkSkipped++;
                    }
                }
                else if (hunkShown < CompactDiffMaxHunkLines && !line.StartsWith('\\'))
                {
                    // Context line: only kept once at least one +/- line has been shown in this hunk.
                    if (hunkShown > 0)
                    {
                        result.Add($"  {line}");
                        hunkShown++;
                    }
                }
            }

            if (result.Count >= maxLines)
            {
                result.Add("\n... (more changes truncated)");
                wasTruncated = true;
                break;
            }
        }

        // Flush the final hunk.
        if (hunkSkipped > 0)
        {
            result.Add($"  ... ({hunkSkipped} lines truncated)");
            wasTruncated = true;
        }

        if (currentFile.Length != 0 && (added > 0 || removed > 0))
        {
            result.Add($"  +{added} -{removed}");
        }

        if (wasTruncated)
        {
            result.Add("[full diff: rtk git diff --no-compact]");
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Extracts the post-image path from a <c>diff --git a/… b/…</c> header, matching Rust's
    /// <c>line.split(" b/").nth(1).unwrap_or("unknown")</c> (git.rs:352). Used only by
    /// <see cref="CompactDiff"/> — moved wholesale from <c>GitCommand.ExtractDiffFileName</c>.
    /// </summary>
    private static string ExtractDiffFileName(string line)
    {
        var parts = line.Split(" b/");
        return parts.Length > 1 ? parts[1] : "unknown";
    }

    /// <summary>
    /// Formats porcelain <c>-b</c> status output into RTK's compact view. Ports
    /// <c>format_status_output</c> (git.rs:625).
    /// </summary>
    /// <param name="porcelain">The <c>git status --porcelain -b</c> stdout.</param>
    /// <returns>The formatted status text.</returns>
    public static string FormatStatusOutput(string porcelain) => FormatStatusInner(porcelain, null);

    /// <summary>
    /// Formats porcelain <c>-b</c> status output, substituting an explicit detached-HEAD reference
    /// for the opaque <c>## HEAD (no branch)</c> branch line. Ports
    /// <c>format_status_output_detached</c> (git.rs:629).
    /// </summary>
    /// <param name="porcelain">The <c>git status --porcelain -b</c> stdout.</param>
    /// <param name="detachedRef">The explicit "HEAD detached at/from &lt;ref&gt;" line to display.</param>
    /// <returns>The formatted status text.</returns>
    public static string FormatStatusOutputDetached(string porcelain, string detachedRef) =>
        FormatStatusInner(porcelain, detachedRef);

    /// <summary>
    /// Shared status formatter. Ports <c>format_status_inner</c> (git.rs:633): rewrites the porcelain
    /// branch header <c>## &lt;branch&gt;</c> to <c>* &lt;branch&gt;</c> (or the detached ref), keeps the
    /// remaining change lines verbatim, and appends a clean marker when nothing but the branch line
    /// is present. Used only by <see cref="FormatStatusOutput"/>/<see cref="FormatStatusOutputDetached"/>
    /// — moved wholesale from <c>GitCommand.FormatStatusInner</c>.
    /// </summary>
    private static string FormatStatusInner(string porcelain, string? detached)
    {
        ArgumentNullException.ThrowIfNull(porcelain);

        var lines = SourceFilterLineSplitter.SplitLines(porcelain).Where(l => l.Trim().Length != 0).ToList();

        if (lines.Count == 0)
        {
            return "Clean working tree";
        }

        var output = new List<string>();

        var branchLine = lines[0];
        if (branchLine.StartsWith("##", StringComparison.Ordinal))
        {
            var branch = TrimStartMatches(branchLine, "## ");
            output.Add($"* {detached ?? branch}");
        }
        else
        {
            output.Add(branchLine);
        }

        for (var i = 1; i < lines.Count; i++)
        {
            output.Add(lines[i]);
        }

        if (lines.Count == 1 && lines[0].StartsWith("##", StringComparison.Ordinal))
        {
            output.Add("clean — nothing to commit");
        }

        return string.Join("\n", output);
    }

    /// <summary>
    /// Removes every leading repetition of <paramref name="prefix"/> from <paramref name="value"/>,
    /// matching Rust's <c>str::trim_start_matches</c> (which strips repeated whole-pattern prefixes).
    /// Used only by <see cref="FormatStatusInner"/> — moved wholesale from
    /// <c>GitCommand.TrimStartMatches</c>.
    /// </summary>
    private static string TrimStartMatches(string value, string prefix)
    {
        var span = value.AsSpan();
        while (span.StartsWith(prefix, StringComparison.Ordinal))
        {
            span = span[prefix.Length..];
        }

        return span.ToString();
    }

    /// <summary>
    /// Minimal filtering for <c>git status</c> with user-provided args: drops blank lines and git
    /// hints, short-circuits on a clean working tree, and returns <c>ok</c> when nothing remains.
    /// Ports <c>filter_status_with_args</c> (git.rs:779).
    /// </summary>
    /// <param name="output">The raw <c>git status</c> stdout.</param>
    /// <returns>The filtered status text (no trailing newline).</returns>
    public static string FilterStatusWithArgs(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            // Skip git hints — can appear at the start of or within a line.
            if (trimmed.StartsWith("(use \"git", StringComparison.Ordinal)
                || trimmed.StartsWith("(create/copy files", StringComparison.Ordinal)
                || trimmed.Contains("(use \"git add", StringComparison.Ordinal)
                || trimmed.Contains("(use \"git restore", StringComparison.Ordinal))
            {
                continue;
            }

            // Special case: clean working tree.
            if (trimmed.Contains("nothing to commit", StringComparison.Ordinal)
                && trimmed.Contains("working tree clean", StringComparison.Ordinal))
            {
                result.Add(trimmed);
                break;
            }

            result.Add(line);
        }

        return result.Count == 0 ? "ok" : string.Join("\n", result);
    }

    /// <summary>
    /// Builds the compact <c>git add</c> summary from <c>git diff --cached --stat --shortstat</c>
    /// output: empty when nothing is staged (git stays silent), otherwise <c>ok &lt;last line&gt;</c>
    /// (the shortstat tally). Ports the inline formatting in <c>run_add</c> (git.rs:945-955).
    /// </summary>
    /// <param name="cachedStat">The <c>git diff --cached --stat --shortstat</c> stdout.</param>
    /// <returns>The compact summary, or an empty string when nothing was staged.</returns>
    public static string FormatAddSummary(string cachedStat)
    {
        ArgumentNullException.ThrowIfNull(cachedStat);

        if (cachedStat.Trim().Length == 0)
        {
            return string.Empty;
        }

        var lines = SourceFilterLineSplitter.SplitLines(cachedStat);
        var last = lines.Count == 0 ? string.Empty : lines[^1].Trim();
        return last.Length == 0 ? "ok" : $"ok {last}";
    }

    /// <summary>
    /// Parses the first line of <c>git commit</c> success output into a compact token. Handles
    /// <c>[main abc1234def] message</c>, <c>[main (root-commit) abc1234def] msg</c>, localized
    /// variants, and multibyte branch names: the hash is the last whitespace-separated token before
    /// <c>]</c>, shortened to seven characters. Ports <c>parse_commit_output</c> (git.rs:993).
    /// </summary>
    /// <param name="line">The first line of <c>git commit</c> stdout.</param>
    /// <returns><c>ok &lt;short-hash&gt;</c>, or <c>ok</c> when no hash could be parsed.</returns>
    public static string ParseCommitOutput(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var bracketEnd = line.IndexOf(']', StringComparison.Ordinal);
        if (bracketEnd <= 0)
        {
            return "ok";
        }

        var bracketContent = line[1..bracketEnd];
        var tokens = bracketContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hash = tokens.Length == 0 ? string.Empty : tokens[^1];
        if (hash.Length >= 7)
        {
            var shortHash = string.Concat(hash.EnumerateRunes().Take(7).Select(r => r.ToString()));
            return $"ok {shortHash}";
        }

        return "ok";
    }

    /// <summary>
    /// Pure-function view of the <c>git push</c> filter for testing: strips progress noise from the
    /// replayed line stream (stderr lines then the stdout trailer, matching git push's natural
    /// emission order) and appends the success summary, matching the <c>filtered</c> text Rust's
    /// streaming filter accumulates. Ports the combined effect of <c>GitPushLineHandler</c>
    /// (git.rs:1072-1116).
    /// </summary>
    /// <param name="stdout">The captured <c>git push</c> stdout.</param>
    /// <param name="stderr">The captured <c>git push</c> stderr.</param>
    /// <param name="exitCode">The child exit code (the summary is emitted only on zero).</param>
    /// <returns>The filtered push output (kept lines plus the summary), each line newline-terminated.</returns>
    public static string FilterPushOutput(string stdout, string stderr, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var sb = new StringBuilder();
        var upToDate = false;
        string? pushedRef = null;

        foreach (var line in SourceFilterLineSplitter.SplitLines(stderr).Concat(SourceFilterLineSplitter.SplitLines(stdout)))
        {
            if (IsPushNoiseLine(line))
            {
                continue;
            }

            if (line.Contains("Everything up-to-date", StringComparison.Ordinal))
            {
                upToDate = true;
            }

            pushedRef ??= ExtractPushedRef(line);
            sb.Append(line).Append('\n');
        }

        if (exitCode == 0)
        {
            var summary = upToDate ? "ok (up-to-date)" : pushedRef is not null ? $"ok {pushedRef}" : "ok";
            sb.Append(summary).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reports whether a <c>git push</c> line is progress noise to drop: a blank line, or a line whose
    /// leading-whitespace-trimmed text starts with one of <see cref="GitPushNoisePrefixes"/>. Ports
    /// <c>GitPushLineHandler::should_skip</c> (git.rs:1079-1087). Duplicated from
    /// <c>GitCommand.IsPushNoiseLine</c> — see class remarks.
    /// </summary>
    private static bool IsPushNoiseLine(string line)
    {
        if (line.Length == 0)
        {
            return true;
        }

        var trimmed = line.TrimStart();
        return GitPushNoisePrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Extracts the destination ref from a <c>git push</c> line containing <c> -&gt; </c> (e.g.
    /// <c> * [new branch]      main -&gt; main</c> yields <c>main</c>), or null when the line has no
    /// such marker. Ports the <c>observe_line</c> ref parse (git.rs:1093-1100). Duplicated from
    /// <c>GitCommand.ExtractPushedRef</c> — see class remarks.
    /// </summary>
    /// <param name="line">A <c>git push</c> output line.</param>
    /// <returns>The pushed destination ref, or null.</returns>
    private static string? ExtractPushedRef(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var idx = line.IndexOf(" -> ", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var after = line[(idx + 4)..];
        var dest = after.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(dest) ? null : dest;
    }

    /// <summary>
    /// Builds the compact <c>git pull</c> summary from stdout: <c>ok (up-to-date)</c> for an
    /// already-current pull, <c>ok N files +A -R</c> when git printed a
    /// <c>N files changed, A insertions(+), R deletions(-)</c> tally, else <c>ok</c>. Ports the inline
    /// parsing in <c>run_pull</c> (git.rs:1168-1211).
    /// </summary>
    /// <param name="stdout">The <c>git pull</c> stdout.</param>
    /// <returns>The compact pull summary.</returns>
    public static string FormatPullSummary(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);

        if (stdout.Contains("Already up to date", StringComparison.Ordinal)
            || stdout.Contains("Already up-to-date", StringComparison.Ordinal))
        {
            return "ok (up-to-date)";
        }

        var files = 0;
        var insertions = 0;
        var deletions = 0;

        foreach (var line in SourceFilterLineSplitter.SplitLines(stdout))
        {
            if (!line.Contains("file", StringComparison.Ordinal) || !line.Contains("changed", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var rawPart in line.Split(','))
            {
                var part = rawPart.Trim();
                if (part.Contains("file", StringComparison.Ordinal))
                {
                    files = LeadingCount(part);
                }
                else if (part.Contains("insertion", StringComparison.Ordinal))
                {
                    insertions = LeadingCount(part);
                }
                else if (part.Contains("deletion", StringComparison.Ordinal))
                {
                    deletions = LeadingCount(part);
                }
            }
        }

        return files > 0 ? $"ok {files} files +{insertions} -{deletions}" : "ok";
    }

    /// <summary>
    /// Parses the leading whitespace-delimited integer of a stat fragment, or zero. Matches Rust's
    /// <c>split_whitespace().next().and_then(parse).unwrap_or(0)</c>. Used only by
    /// <see cref="FormatPullSummary"/> — moved wholesale from <c>GitCommand.LeadingCount</c>.
    /// </summary>
    private static int LeadingCount(string part)
    {
        var first = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out var n) ? n : 0;
    }

    /// <summary>
    /// Compacts <c>git branch -a --no-color</c> output into a current/local/remote-only view: the
    /// current branch (<c>* &lt;name&gt;</c>) first, then local branches, then a de-duplicated
    /// remote-only section capped at <see cref="BranchRemoteCap"/> entries with a <c>... +N more</c>
    /// overflow marker. Ports <c>filter_branch_output</c> (git.rs:1385).
    /// </summary>
    /// <param name="output">The raw <c>git branch -a --no-color</c> stdout.</param>
    /// <returns>The compacted branch listing (no trailing newline).</returns>
    public static string FilterBranchOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var current = string.Empty;
        var local = new List<string>();
        var remote = new List<string>();
        var seenRemote = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in SourceFilterLineSplitter.SplitLines(output))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("* ", StringComparison.Ordinal))
            {
                current = line["* ".Length..];
            }
            else if (line.StartsWith("remotes/", StringComparison.Ordinal))
            {
                var rest = line["remotes/".Length..];
                var slashPos = rest.IndexOf('/', StringComparison.Ordinal);
                if (slashPos < 0)
                {
                    continue;
                }

                var branch = rest[(slashPos + 1)..];
                if (branch.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (seenRemote.Add(branch))
                {
                    remote.Add(branch);
                }
            }
            else
            {
                local.Add(line);
            }
        }

        var result = new List<string> { $"* {current}" };

        foreach (var b in local)
        {
            result.Add($"  {b}");
        }

        var remoteOnly = remote.Where(r => r != current && !local.Contains(r)).ToList();
        if (remoteOnly.Count != 0)
        {
            result.Add($"  remote-only ({remoteOnly.Count}):");
            foreach (var b in remoteOnly.Take(BranchRemoteCap))
            {
                result.Add($"    {b}");
            }

            if (remoteOnly.Count > BranchRemoteCap)
            {
                result.Add($"    ... +{remoteOnly.Count - BranchRemoteCap} more");
            }
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Builds the compact <c>git fetch</c> summary by counting new-ref lines (containing <c>-&gt;</c>
    /// or <c>[new</c>) in git's stderr. Ports the inline count in <c>run_fetch</c> (git.rs:1471-1482).
    /// </summary>
    /// <param name="stderr">The <c>git fetch</c> stderr (git writes ref updates there).</param>
    /// <returns><c>ok fetched (N new refs)</c> when refs arrived, else <c>ok fetched</c>.</returns>
    public static string FormatFetchSummary(string stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        var newRefs = SourceFilterLineSplitter.SplitLines(stderr).Count(l =>
            l.Contains("->", StringComparison.Ordinal) || l.Contains("[new", StringComparison.Ordinal));

        return newRefs > 0 ? $"ok fetched ({newRefs} new refs)" : "ok fetched";
    }

    /// <summary>
    /// Formats the success message for a stash operation. Create operations (bare/<c>push</c>/
    /// <c>save</c>) collapse to <c>ok stashed</c> but surface <c>No local changes to save</c> for a
    /// no-op so it does not read as a successful stash; every other operation reports
    /// <c>ok stash &lt;sub&gt;</c>. Ports <c>format_stash_message</c> (git.rs:1492).
    /// </summary>
    /// <param name="subcommand">The stash subcommand (null for a bare <c>git stash</c>).</param>
    /// <param name="stdout">The operation's stdout.</param>
    /// <param name="stderr">The operation's stderr.</param>
    /// <returns>The compact stash message.</returns>
    public static string FormatStashMessage(string? subcommand, string stdout, string stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (subcommand is null or "push" or "save")
        {
            return (stdout + stderr).Contains("No local changes", StringComparison.Ordinal)
                ? "No local changes to save"
                : "ok stashed";
        }

        return $"ok stash {subcommand}";
    }

    /// <summary>
    /// Compacts <c>git stash list</c> output, stripping the <c>WIP on &lt;branch&gt;:</c> prefix from
    /// each entry so only <c>stash@{N}: &lt;message&gt;</c> remains. Ports <c>filter_stash_list</c>
    /// (git.rs:1649).
    /// </summary>
    /// <param name="output">The raw <c>git stash list</c> stdout.</param>
    /// <returns>The compacted stash list (no trailing newline).</returns>
    public static string FilterStashList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new List<string>();
        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            var colonPos = line.IndexOf(": ", StringComparison.Ordinal);
            if (colonPos < 0)
            {
                result.Add(line);
                continue;
            }

            var index = line[..colonPos];
            var rest = line[(colonPos + 2)..];
            var secondColon = rest.IndexOf(": ", StringComparison.Ordinal);
            var message = secondColon >= 0 ? rest[(secondColon + 2)..].Trim() : rest.Trim();
            result.Add($"{index}: {message}");
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Compacts <c>git worktree list</c> output, rewriting a leading home-directory prefix to <c>~</c>
    /// and normalizing each <c>&lt;path&gt; &lt;hash&gt; &lt;branch&gt;</c> row to single-space
    /// separation. Ports <c>filter_worktree_list</c> (git.rs:1729).
    /// </summary>
    /// <param name="output">The raw <c>git worktree list</c> stdout.</param>
    /// <returns>The compacted worktree listing (no trailing newline).</returns>
    public static string FilterWorktreeList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Rust uses dirs::home_dir(); on Windows that resolves to the same USERPROFILE path. Git
        // prints forward-slash paths, so the ordinal prefix test only fires when they align.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var result = new List<string>();
        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var path = parts[0];
                if (home.Length != 0 && path.StartsWith(home, StringComparison.Ordinal))
                {
                    path = "~" + path[home.Length..];
                }

                var hash = parts[1];
                var branch = string.Join(" ", parts.Skip(2));
                result.Add($"{path} {hash} {branch}");
            }
            else
            {
                result.Add(line);
            }
        }

        return string.Join("\n", result);
    }
}
