using RtkSharp.Execution;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Compact filter for <c>wc</c> — runs the real <c>wc</c> binary and strips redundant paths
/// and alignment padding from its output. Single-file counts collapse to a terse form
/// (<c>wc file</c> → <c>30L 96W 978B</c>, <c>wc -l file</c> → <c>30</c>); multi-file listings
/// become a compact table with the common path prefix removed and the <c>total</c> row shown
/// as <c>Σ</c>.
/// </summary>
/// <remarks>
/// Ported faithfully from <c>src/cmds/system/wc_cmd.rs</c>. Execution goes through
/// <see cref="CommandRunner.RunFilteredAsync"/> with <c>FilterStdoutOnly</c> (matching
/// <c>RunOptions::stdout_only()</c>); no tee, no early-exit-on-failure, and the default
/// trailing newline (wc_cmd.rs uses <c>println!</c>). <see cref="RunOptions.InheritStdin"/> is
/// set when <c>wc</c> has no file operands (it would read a pipe), mirroring
/// <c>.inherit_stdin()</c>. Known limitation: the current <see cref="ProcessExecutor"/> does
/// not redirect standard input, so this flag is inert — piped stdin
/// (<c>echo hi | rtk wc</c>) relies on the child inheriting the parent's stdin handle rather
/// than on an explicit forward. File-argument mode (<c>rtk wc -l file</c>) is unaffected and
/// oracle-exact. The pure filtering logic lives in <see cref="WcFilters"/>.
/// </remarks>
public static class WcCommand
{
    /// <summary>
    /// Runs the underlying <c>wc</c> with the given arguments and prints the compacted output.
    /// </summary>
    /// <param name="args">The arguments following the <c>wc</c> verb (flags and file operands).</param>
    /// <returns>The underlying <c>wc</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var mode = WcFilters.DetectMode(args);

        // No file operands → wc reads from stdin; forward rtk's stdin to the child.
        var readsStdin = !args.Any(a => !a.StartsWith('-'));

        var options = new RunOptions(FilterStdoutOnly: true, InheritStdin: readsStdin);

        return CommandRunner.RunFilteredAsync(
            "wc",
            args,
            "wc",
            string.Join(' ', args),
            raw => WcFilters.FilterWcOutput(raw, mode),
            options
        );
    }
}
