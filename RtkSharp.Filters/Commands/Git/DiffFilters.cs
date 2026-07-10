using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Git;

/// <summary>
/// Pure filtering/diffing logic for the standalone <c>rtk diff</c> verb (distinct from <c>git diff</c>
/// — see <c>RtkSharp.Commands.Git.GitCommand</c>'s own diff filter for that one). Extracted verbatim
/// from <c>RtkSharp.Commands.Git.DiffCommand</c> — signatures and logic are unchanged, only visibility
/// moved from <c>internal</c> to <c>public</c> and the containing type from <c>DiffCommand</c> to
/// <c>DiffFilters</c>. The three supporting types (<see cref="DiffChangeKind"/>,
/// <see cref="DiffChange"/>, <see cref="DiffResult"/>) moved alongside the methods that use them as
/// parameter/return types, even though they weren't separately named in the extraction's public
/// interface list — a method can't compile without its own signature's types. See
/// <c>RtkSharp.Commands.Git.DiffCommand</c> for the file I/O/CLI-argument-parsing code that calls
/// these methods, and for the original Rust source pointers (<c>src/cmds/git/diff_cmd.rs</c>)
/// preserved in each method's XML doc.
/// </summary>
public static class DiffFilters
{
    /// <summary>The kind of line-level change recorded by <see cref="DiffChange"/>.</summary>
    public enum DiffChangeKind
    {
        /// <summary>A line present only in the second file.</summary>
        Added,

        /// <summary>A line present only in the first file.</summary>
        Removed,

        /// <summary>A line whose text changed between the two files (similarity above the threshold).</summary>
        Modified,
    }

    /// <summary>A single recorded line-level change.</summary>
    /// <param name="Kind">The kind of change.</param>
    /// <param name="LineNumber">The 1-based line number the change occurred at.</param>
    /// <param name="Text">The changed line's text (the "before" text for a modification).</param>
    /// <param name="NewText">The "after" text for a modification, or null otherwise.</param>
    public readonly record struct DiffChange(DiffChangeKind Kind, int LineNumber, string Text, string? NewText = null);

    /// <summary>The computed diff between two line sequences.</summary>
    /// <param name="Added">The count of added lines.</param>
    /// <param name="Removed">The count of removed lines.</param>
    /// <param name="Modified">The count of modified lines.</param>
    /// <param name="Changes">Every recorded change, in encounter order.</param>
    public sealed record DiffResult(int Added, int Removed, int Modified, List<DiffChange> Changes);

    /// <summary>
    /// Renders the condensed file comparison and returns it with the diff-convention exit code
    /// (0 = identical, 1 = differences found). Faithful port of <c>render_file_diff</c>
    /// (<c>diff_cmd.rs</c>:35-52).
    /// </summary>
    /// <param name="file1">The first file's display path.</param>
    /// <param name="file2">The second file's display path.</param>
    /// <param name="content1">The first file's content.</param>
    /// <param name="content2">The second file's content.</param>
    /// <returns>The rendered diff text and the diff-convention exit code.</returns>
    public static (string Output, int ExitCode) RenderFileDiff(string file1, string file2, string content1, string content2)
    {
        ArgumentNullException.ThrowIfNull(file1);
        ArgumentNullException.ThrowIfNull(file2);
        ArgumentNullException.ThrowIfNull(content1);
        ArgumentNullException.ThrowIfNull(content2);

        var lines1 = SourceFilterLineSplitter.SplitLines(content1);
        var lines2 = SourceFilterLineSplitter.SplitLines(content2);
        var diff = ComputeDiff(lines1, lines2);

        if (diff.Changes.Count == 0)
        {
            return ("[ok] Files are identical\n", 0);
        }

        var rtk = new StringBuilder();
        rtk.Append($"{file1} → {file2}\n");
        rtk.Append($"   +{diff.Added} added, -{diff.Removed} removed, ~{diff.Modified} modified\n\n");
        rtk.Append(FormatDiffChanges(diff));

        return (rtk.ToString(), 1);
    }

    /// <summary>
    /// Formats the changes recorded in <paramref name="diff"/> into their compact
    /// <c>+</c>/<c>-</c>/<c>~</c>-prefixed lines. Faithful port of <c>format_diff_changes</c>
    /// (<c>diff_cmd.rs</c>:85-97).
    /// </summary>
    /// <param name="diff">The computed diff.</param>
    /// <returns>The formatted change lines.</returns>
    public static string FormatDiffChanges(DiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var outBuilder = new StringBuilder();
        foreach (var change in diff.Changes)
        {
            switch (change.Kind)
            {
                case DiffChangeKind.Added:
                    outBuilder.Append($"+{change.LineNumber,4} {change.Text}\n");
                    break;
                case DiffChangeKind.Removed:
                    outBuilder.Append($"-{change.LineNumber,4} {change.Text}\n");
                    break;
                case DiffChangeKind.Modified:
                    outBuilder.Append($"~{change.LineNumber,4} {change.Text} → {change.NewText}\n");
                    break;
            }
        }

        return outBuilder.ToString();
    }

    /// <summary>
    /// Computes a simple (non-optimal, but fast) line-by-line diff between two line sequences.
    /// Faithful port of <c>compute_diff</c> (<c>diff_cmd.rs</c>:99-143): differing lines at the same
    /// index are classified as a modification when their Jaccard character similarity exceeds 0.5,
    /// otherwise as a removal+addition pair.
    /// </summary>
    /// <param name="lines1">The first file's lines.</param>
    /// <param name="lines2">The second file's lines.</param>
    /// <returns>The computed diff, with every change recorded (never truncated).</returns>
    public static DiffResult ComputeDiff(IReadOnlyList<string> lines1, IReadOnlyList<string> lines2)
    {
        ArgumentNullException.ThrowIfNull(lines1);
        ArgumentNullException.ThrowIfNull(lines2);

        var changes = new List<DiffChange>();
        var added = 0;
        var removed = 0;
        var modified = 0;

        var maxLen = Math.Max(lines1.Count, lines2.Count);

        for (var i = 0; i < maxLen; i++)
        {
            var l1 = i < lines1.Count ? lines1[i] : null;
            var l2 = i < lines2.Count ? lines2[i] : null;

            if (l1 is not null && l2 is not null)
            {
                if (l1 != l2)
                {
                    if (Similarity(l1, l2) > 0.5)
                    {
                        changes.Add(new DiffChange(DiffChangeKind.Modified, i + 1, l1, l2));
                        modified++;
                    }
                    else
                    {
                        changes.Add(new DiffChange(DiffChangeKind.Removed, i + 1, l1));
                        changes.Add(new DiffChange(DiffChangeKind.Added, i + 1, l2));
                        removed++;
                        added++;
                    }
                }

                // else: identical lines at this index — no change recorded.
            }
            else if (l1 is not null)
            {
                changes.Add(new DiffChange(DiffChangeKind.Removed, i + 1, l1));
                removed++;
            }
            else if (l2 is not null)
            {
                changes.Add(new DiffChange(DiffChangeKind.Added, i + 1, l2));
                added++;
            }
        }

        return new DiffResult(added, removed, modified, changes);
    }

    /// <summary>
    /// Computes the Jaccard similarity of two strings' Unicode-scalar-value sets: the size of their
    /// character-set intersection divided by the size of their union. Faithful port of
    /// <c>similarity</c> (<c>diff_cmd.rs</c>:145-157); returns 1.0 by convention when both sets are
    /// empty (both strings empty).
    /// </summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>The Jaccard similarity in <c>[0.0, 1.0]</c>.</returns>
    public static double Similarity(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var aRunes = new HashSet<int>(a.EnumerateRunes().Select(r => r.Value));
        var bRunes = new HashSet<int>(b.EnumerateRunes().Select(r => r.Value));

        var intersection = aRunes.Count(bRunes.Contains);
        var union = aRunes.Union(bRunes).Count();

        return union == 0 ? 1.0 : intersection / (double)union;
    }

    /// <summary>
    /// Condenses a piped-in unified diff to per-file <c>+A -R</c> counts followed by every changed
    /// line (diff metadata such as headers and <c>@@</c> hunk markers are stripped). Faithful port of
    /// <c>condense_unified_diff</c> (<c>diff_cmd.rs</c>:159-211) — see
    /// <c>RtkSharp.Commands.Git.DiffCommand</c>'s class remarks for the preserved "prints everything
    /// but still reports a stale overflow count" quirk.
    /// </summary>
    /// <param name="diff">The raw unified diff text (as piped from e.g. <c>git diff</c>).</param>
    /// <returns>The condensed reading.</returns>
    public static string CondenseUnifiedDiff(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var result = new List<string>();
        var currentFile = string.Empty;
        var added = 0;
        var removed = 0;
        var changes = new List<string>();

        void FlushCurrentFile()
        {
            if (currentFile.Length != 0 && (added > 0 || removed > 0))
            {
                result.Add($"[file] {currentFile} (+{added} -{removed})");
                foreach (var c in changes)
                {
                    result.Add($"  {c}");
                }

                var total = added + removed;
                if (total > 10)
                {
                    result.Add($"  ... +{total - 10} more");
                }
            }
        }

        foreach (var line in SourceFilterLineSplitter.SplitLines(diff))
        {
            if (line.StartsWith("diff --git", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    FlushCurrentFile();

                    var afterPrefix = line["+++ ".Length..];
                    currentFile = afterPrefix.StartsWith("b/", StringComparison.Ordinal)
                        ? afterPrefix["b/".Length..]
                        : afterPrefix;
                    added = 0;
                    removed = 0;
                    changes = [];
                }
            }
            else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                added++;
                changes.Add(line);
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                removed++;
                changes.Add(line);
            }
        }

        FlushCurrentFile();

        return string.Join("\n", result);
    }
}
