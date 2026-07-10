using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using static RtkSharp.Filters.Commands.Git.DiffFilters;

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
