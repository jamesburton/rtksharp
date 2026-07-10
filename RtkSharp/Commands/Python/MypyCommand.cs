using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Python;

namespace RtkSharp.Commands.Python;

/// <summary>
/// Implements the <c>rtk mypy</c> CLI verb: runs mypy and groups its diagnostics by file, with a
/// top-error-codes summary when 2+ distinct codes are present. Faithful port of
/// <c>src/cmds/python/mypy_cmd.rs</c> plus its dispatch arm in <c>src/main.rs</c>
/// (<c>Commands::Mypy</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat argument surface, no subcommand enum.</b> Rust declares <c>Mypy { #[arg(trailing_var_arg =
/// true, allow_hyphen_values = true)] args: Vec&lt;String&gt; }</c> (<c>main.rs</c>:674-679) — clap
/// accepts any token stream, so there is no zero-args or unknown-flag failure mode to reproduce; this
/// port never throws <see cref="Cli.CommandArgumentParseException"/>.
/// </para>
/// <para>
/// <b><c>mypy</c>-or-<c>python3 -m mypy</c> resolution.</b> Mirrors Rust's <c>tool_exists("mypy")</c>
/// check exactly: when a <c>mypy</c> executable is resolvable, it is invoked directly; otherwise the
/// invocation falls back to <c>python3 -m mypy</c> (not plain <c>python</c> — mypy_cmd.rs:13 uses
/// <c>"python3"</c> specifically, unlike <see cref="PytestCommand"/>'s <c>python</c> fallback).
/// </para>
/// <para>
/// <b>Buffered filter over combined stdout+stderr, no tee.</b> Rust invokes <c>runner::run_filtered</c>
/// with <c>RunOptions::default()</c> — <c>filter_stdout_only</c> is false (so the filter receives
/// stdout+stderr concatenated) and no tee label is set. This port uses
/// <see cref="CommandRunner.RunFilteredAsync"/> with a bare <c>new RunOptions()</c>, matching that
/// default exactly. The captured text is passed through <see cref="Utils.StripAnsi"/> before
/// filtering, mirroring Rust's <c>strip_ansi(raw)</c> call at the filter closure's entry.
/// </para>
/// </remarks>
public static class MypyCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk mypy</c> with the given arguments (the remainder after the
    /// <c>mypy</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>mypy</c>.</param>
    /// <returns>Mypy's real exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, RuntimeOptions.Verbosity, static name => PathResolver.Resolve(name) != name);

    /// <summary>
    /// Testable core of <see cref="RunAsync(string[])"/>: the tool-existence check is injected so the
    /// <c>python3 -m mypy</c> fallback branch can be exercised deterministically regardless of the
    /// host's installed tools.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>mypy</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="toolExists">Predicate reporting whether a named tool is resolvable on the host.</param>
    /// <returns>Mypy's real exit code.</returns>
    internal static Task<int> RunAsync(string[] args, int verbose, Func<string, bool> toolExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(toolExists);

        var useMypy = toolExists("mypy");
        var fileName = PathResolver.Resolve(useMypy ? "mypy" : "python3");

        var invocation = new List<string>();
        if (!useMypy)
        {
            invocation.Add("-m");
            invocation.Add("mypy");
        }

        invocation.AddRange(args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: mypy {string.Join(' ', args)}\n");
        }

        var argsDisplay = string.Join(' ', args);

        return CommandRunner.RunFilteredAsync(
            fileName,
            invocation,
            "mypy",
            argsDisplay,
            raw => MypyFilters.FilterMypyOutput(Utils.StripAnsi(raw)),
            new RunOptions()
        );
    }
}
