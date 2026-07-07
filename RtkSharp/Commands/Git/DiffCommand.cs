using System.Text;
using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;

namespace RtkSharp.Commands.Git;

/// <summary>
/// Implements the top-level <c>rtk diff</c> CLI verb: an ultra-condensed diff between two files
/// (only changed lines, no context), or — when only one positional argument is given — a
/// condensed reading of a unified diff piped in on stdin. Faithful port of Rust
/// <c>src/cmds/git/diff_cmd.rs</c> (including its inline test module).
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from <c>git diff</c>.</b> This is the standalone <c>Commands::Diff</c> clap variant
/// (<c>main.rs</c>:264-270), unrelated to <see cref="GitCommand"/>'s <c>git diff</c> subcommand
/// filter (<c>src/cmds/git/git.rs</c>) despite the shared name and folder.
/// </para>
/// <para>
/// <b>PASSTHROUGH classification, not RTK_META_COMMANDS.</b> Rust's <c>RTK_META_COMMANDS</c>
/// (<c>main.rs</c>:1170-1191) does NOT include <c>"diff"</c> — it is listed in the <c>PASSTHROUGH</c>
/// const instead (<c>main.rs</c>:2939). A clap-layer parse failure (missing the required <c>file1</c>
/// positional, or a third positional) therefore falls back to a raw PATH-exec of the original argv
/// (typically the real <c>diff</c> binary, if present), NOT a clean clap usage error. Matching the
/// established <see cref="Commands.System.EnvCommand"/> convention for ported PASSTHROUGH commands,
/// <see cref="ParseArgs"/> throws <see cref="CommandArgumentParseException"/> for these cases so
/// <c>RtkProgram</c>'s dispatch layer can re-route to that fallback.
/// </para>
/// <para>
/// <b>Quirk: a lone positional argument is treated as stdin mode, and its value is discarded.</b>
/// Rust's dispatch (<c>main.rs</c>:1766-1773) only branches on whether <c>file2</c> (the second,
/// <i>optional</i>, positional) was supplied — <b>not</b> on the value of <c>file1</c> (e.g. not on
/// whether it equals <c>"-"</c>). So <c>rtk diff foo.txt</c> (exactly one positional) invokes
/// <see cref="RunStdin"/>, silently ignoring <c>foo.txt</c> entirely and reading a unified diff from
/// stdin instead — a genuine, disclosed Rust-source quirk, preserved verbatim here rather than
/// "fixed" to the more intuitive two-file comparison. <c>file1</c> is still a clap-required
/// positional, though: <c>rtk diff</c> with zero positionals is a parse failure (falls back to raw
/// exec, per the PASSTHROUGH classification above), even though a supplied <c>file1</c> value would
/// go unused whenever <c>file2</c> is absent.
/// </para>
/// <para>
/// <b><c>run()</c> (two-file mode) uses <c>print!</c>; <c>run_stdin()</c> uses <c>println!</c>.</b>
/// <c>diff_cmd.rs</c>:23 prints the rendered diff with no extra appended newline (the rendered string
/// already ends in <c>"\n"</c> via its own formatting); <c>diff_cmd.rs</c>:64 appends one more
/// <c>"\n"</c> on top of the condensed unified-diff text. This port matches both exactly.
/// </para>
/// <para>
/// <b><c>condense_unified_diff</c> never truncates content, but still emits a stale overflow
/// count.</b> Every <c>+</c>/<c>-</c> line is collected into an unbounded <c>changes</c> list and ALL
/// of them are printed for the current file — matching the "Never truncate diff content" comment in
/// the Rust source (<c>diff_cmd.rs</c>:166-167). However, the function still appends a
/// <c>"... +{total - 10} more"</c> line whenever a file's total +/- count exceeds 10, even though
/// every line was, in fact, already printed in full — a disclosed, oracle-verified inconsistency
/// (see <c>test_condense_unified_diff_overflow_count_accuracy</c>) preserved verbatim rather than
/// "fixed" to only appear when truncation genuinely occurred.
/// </para>
/// </remarks>
public static class DiffCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk diff</c> with the given arguments (the remainder after the
    /// <c>diff</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>diff</c>.</param>
    /// <returns>
    /// 0 if the two files are identical (or in stdin mode), 1 if they differ (diff convention),
    /// matching <c>diff_cmd::run</c>'s return value.
    /// </returns>
    /// <exception cref="CommandArgumentParseException">The arguments failed to parse (falls back to raw exec, per PASSTHROUGH classification).</exception>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var (file1, file2) = ParseArgs(args);
        var verbose = RuntimeOptions.Verbosity;

        if (file2 is null)
        {
            return Task.FromResult(RunStdin(verbose, Console.In, Console.Out));
        }

        return Task.FromResult(Run(file1, file2, verbose, Console.Out));
    }

    /// <summary>
    /// Compares two files and prints an ultra-condensed diff (only changed lines, no context).
    /// Faithful port of <c>diff_cmd::run</c> (<c>diff_cmd.rs</c>:10-31).
    /// </summary>
    /// <param name="file1">The first file path.</param>
    /// <param name="file2">The second file path.</param>
    /// <param name="verbose">The global verbosity level (mirrors Rust's <c>cli.verbose: u8</c>).</param>
    /// <param name="stdout">The destination for the rendered diff.</param>
    /// <returns>The diff-convention exit code: 0 if identical, 1 if the files differ.</returns>
    internal static int Run(string file1, string file2, int verbose, TextWriter stdout)
    {
        if (verbose > 0)
        {
            Console.Error.Write($"Comparing: {file1} vs {file2}\n");
        }

        var timer = TimedExecution.Start();
        var content1 = File.ReadAllText(file1);
        var content2 = File.ReadAllText(file2);
        var raw = $"{content1}\n---\n{content2}";

        var (rtk, exitCode) = RenderFileDiff(file1, file2, content1, content2);

        stdout.Write(rtk);
        timer.Track($"diff {file1} {file2}", "rtk diff", raw, rtk);

        return exitCode;
    }

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
    internal static (string Output, int ExitCode) RenderFileDiff(string file1, string file2, string content1, string content2)
    {
        ArgumentNullException.ThrowIfNull(file1);
        ArgumentNullException.ThrowIfNull(file2);
        ArgumentNullException.ThrowIfNull(content1);
        ArgumentNullException.ThrowIfNull(content2);

        var lines1 = ReadCommand.SplitLines(content1);
        var lines2 = ReadCommand.SplitLines(content2);
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
    /// Runs the stdin path: reads a unified diff from stdin and prints a condensed reading of it.
    /// Faithful port of <c>diff_cmd::run_stdin</c> (<c>diff_cmd.rs</c>:55-69).
    /// </summary>
    /// <param name="verbose">The global verbosity level (unused by this path, matching the Rust
    /// signature's unused <c>_verbose</c> parameter).</param>
    /// <param name="stdin">The source to read the unified diff from.</param>
    /// <param name="stdout">The destination for the condensed reading.</param>
    /// <returns>0 (this path never fails once stdin has been read).</returns>
    internal static int RunStdin(int verbose, TextReader stdin, TextWriter stdout)
    {
        _ = verbose;
        var timer = TimedExecution.Start();

        var input = stdin.ReadToEnd();
        var condensed = CondenseUnifiedDiff(input);
        stdout.Write(condensed + "\n");

        timer.Track("diff (stdin)", "rtk diff (stdin)", input, condensed);

        return 0;
    }

    internal enum DiffChangeKind
    {
        Added,
        Removed,
        Modified,
    }

    internal readonly record struct DiffChange(DiffChangeKind Kind, int LineNumber, string Text, string? NewText = null);

    internal sealed record DiffResult(int Added, int Removed, int Modified, List<DiffChange> Changes);

    /// <summary>
    /// Formats the changes recorded in <paramref name="diff"/> into their compact
    /// <c>+</c>/<c>-</c>/<c>~</c>-prefixed lines. Faithful port of <c>format_diff_changes</c>
    /// (<c>diff_cmd.rs</c>:85-97).
    /// </summary>
    /// <param name="diff">The computed diff.</param>
    /// <returns>The formatted change lines.</returns>
    internal static string FormatDiffChanges(DiffResult diff)
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
    internal static DiffResult ComputeDiff(IReadOnlyList<string> lines1, IReadOnlyList<string> lines2)
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
    internal static double Similarity(string a, string b)
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
    /// <c>condense_unified_diff</c> (<c>diff_cmd.rs</c>:159-211) — see the class remarks for the
    /// preserved "prints everything but still reports a stale overflow count" quirk.
    /// </summary>
    /// <param name="diff">The raw unified diff text (as piped from e.g. <c>git diff</c>).</param>
    /// <returns>The condensed reading.</returns>
    internal static string CondenseUnifiedDiff(string diff)
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

        foreach (var line in ReadCommand.SplitLines(diff))
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

    /// <summary>
    /// Parses <c>rtk diff</c>'s arguments: a required <c>file1</c> positional and an optional
    /// <c>file2</c> positional. Faithful port of the clap <c>Commands::Diff</c> variant
    /// (<c>main.rs</c>:265-270).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>diff</c>.</param>
    /// <returns><c>file1</c> and, if supplied, <c>file2</c>.</returns>
    /// <exception cref="CommandArgumentParseException">
    /// Zero positionals were given (missing the required <c>file1</c>), or more than two positionals
    /// were given.
    /// </exception>
    internal static (string File1, string? File2) ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            throw new CommandArgumentParseException("the following required arguments were not provided: <FILE1>");
        }

        if (args.Count > 2)
        {
            throw new CommandArgumentParseException($"unexpected argument '{args[2]}' found");
        }

        return (args[0], args.Count == 2 ? args[1] : null);
    }
}
