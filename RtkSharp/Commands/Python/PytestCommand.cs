using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Python;

namespace RtkSharp.Commands.Python;

/// <summary>
/// Implements the <c>rtk pytest</c> CLI verb: runs pytest with compact-friendly defaults
/// (<c>--tb=short -q -rxX</c>, unless the caller already supplied an equivalent flag) and reduces
/// the output to the pass/fail/skip/xfail/xpass counts plus a capped list of failures and
/// expected-failure outcomes. Faithful port of <c>src/cmds/python/pytest_cmd.rs</c> plus its
/// dispatch arm in <c>src/main.rs</c> (<c>Commands::Pytest</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat argument surface, no subcommand enum.</b> Rust declares <c>Pytest { #[arg(trailing_var_arg
/// = true, allow_hyphen_values = true)] args: Vec&lt;String&gt; }</c> (<c>main.rs</c>:667-672) — clap
/// accepts any token stream, so there is no zero-args or unknown-flag failure mode to reproduce; this
/// port never throws <see cref="Cli.CommandArgumentParseException"/>.
/// </para>
/// <para>
/// <b><c>pytest</c>-or-<c>python -m pytest</c> resolution.</b> Mirrors Rust's <c>tool_exists("pytest")</c>
/// check exactly: when a <c>pytest</c> executable is resolvable, it is invoked directly; otherwise the
/// invocation falls back to <c>python -m pytest</c> (not <c>python3</c> — pytest_cmd.rs:23 uses
/// <c>"python"</c> specifically, unlike <see cref="MypyCommand"/>'s <c>python3</c> fallback).
/// </para>
/// <para>
/// <b>Buffered, stdout-only filter with a tee recovery hint.</b> Rust invokes
/// <c>runner::run_filtered</c> with <c>RunOptions::stdout_only().tee("pytest")</c> — filtering sees
/// only stdout, and the raw output is teed to disk (subject to <see cref="Tee"/>'s own thresholds) so
/// a truncated failures/xfail section can be recovered. This port uses
/// <see cref="CommandRunner.RunFilteredAsync"/> with <c>FilterStdoutOnly: true, TeeLabel: "pytest"</c>.
/// </para>
/// </remarks>
public static class PytestCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk pytest</c> with the given arguments (the remainder after the
    /// <c>pytest</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>pytest</c>.</param>
    /// <returns>Pytest's real exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, RuntimeOptions.Verbosity, static name => PathResolver.Resolve(name) != name);

    /// <summary>
    /// Testable core of <see cref="RunAsync(string[])"/>: the tool-existence check is injected so the
    /// <c>python -m pytest</c> fallback branch can be exercised deterministically regardless of the
    /// host's installed tools.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>pytest</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="toolExists">Predicate reporting whether a named tool is resolvable on the host.</param>
    /// <returns>Pytest's real exit code.</returns>
    internal static Task<int> RunAsync(string[] args, int verbose, Func<string, bool> toolExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(toolExists);

        var usePytest = toolExists("pytest");
        var fileName = PathResolver.Resolve(usePytest ? "pytest" : "python");

        var hasTbFlag = args.Any(a => a.StartsWith("--tb", StringComparison.Ordinal));
        var hasQuietFlag = args.Any(a => a is "-q" or "--quiet");
        // Only treat a short `-r…` as pytest's report flag (not `--randomly-seed` etc.)
        var hasReportFlag = args.Any(a => a.StartsWith("-r", StringComparison.Ordinal) && !a.StartsWith("--", StringComparison.Ordinal));

        var invocation = new List<string>();
        if (!usePytest)
        {
            invocation.Add("-m");
            invocation.Add("pytest");
        }

        if (!hasTbFlag)
        {
            invocation.Add("--tb=short");
        }

        if (!hasQuietFlag)
        {
            invocation.Add("-q");
        }

        // Surface xfailed/xpassed (and their reasons) in the short summary section so the compact
        // output can report expected failures and — crucially — unexpected passes (XPASS), which
        // signal a behavior change.
        if (!hasReportFlag)
        {
            invocation.Add("-rxX");
        }

        invocation.AddRange(args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: pytest --tb=short -q {string.Join(' ', args)}\n");
        }

        var argsDisplay = string.Join(' ', args);

        return CommandRunner.RunFilteredAsync(
            fileName,
            invocation,
            "pytest",
            argsDisplay,
            PytestFilters.FilterPytestOutput,
            new RunOptions(FilterStdoutOnly: true, TeeLabel: "pytest")
        );
    }
}
