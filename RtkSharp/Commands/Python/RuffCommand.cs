using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Python;

namespace RtkSharp.Commands.Python;

/// <summary>
/// Implements the <c>rtk ruff</c> CLI verb: runs Ruff's linter (<c>check</c>) or formatter
/// (<c>format</c>) and compacts the output — grouping check violations by rule/file, or
/// summarizing which files still need formatting. Faithful port of
/// <c>src/cmds/python/ruff_cmd.rs</c> plus its dispatch arm in <c>src/main.rs</c>
/// (<c>Commands::Ruff</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat argument surface, no subcommand enum.</b> Rust declares <c>Ruff { #[arg(trailing_var_arg
/// = true, allow_hyphen_values = true)] args: Vec&lt;String&gt; }</c> (<c>main.rs</c>:660-665) — a
/// single catch-all vector, not a <c>CargoCommands</c>/<c>DockerCommands</c>-style subcommand enum.
/// Clap accepts any token stream here (no required positional, no fixed variant set), so there is no
/// zero-args or unknown-subcommand failure mode to reproduce; this port never throws
/// <see cref="Cli.CommandArgumentParseException"/>.
/// </para>
/// <para>
/// <b>Mode detection, not subcommand dispatch.</b> <c>is_check</c>/<c>is_format</c> are derived from
/// inspecting <c>args[0]</c> (mirroring Rust's own heuristic exactly): empty args, an explicit
/// <c>"check"</c> token, or any first token that isn't a flag and isn't <c>"format"</c>/<c>"version"</c>
/// all mean "check mode". <c>is_format</c> is independently true whenever any argument equals
/// <c>"format"</c> (so <c>ruff format --check</c> is both not-check and is-format). Neither flag
/// implies routing through a different command path — both feed the same single filter closure.
/// </para>
/// <para>
/// <b>Buffered filter, not streamed.</b> Rust invokes <c>runner::run_filtered</c> with
/// <c>RunOptions::stdout_only()</c> — the whole process runs to completion before its stdout is
/// filtered, and only stdout (never stderr) feeds the filter. This port uses
/// <see cref="CommandRunner.RunFilteredAsync"/> with <c>FilterStdoutOnly: true</c>, the same pattern
/// <see cref="Rust.CargoCommand"/>'s buffered subcommands and <see cref="Js.TscCommand"/> use.
/// </para>
/// </remarks>
public static class RuffCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk ruff</c> with the given arguments (the remainder after the
    /// <c>ruff</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>ruff</c>.</param>
    /// <returns>Ruff's real exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity);

    /// <summary>
    /// Testable core of <see cref="RunAsync(string[])"/> taking an explicit verbosity level.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>ruff</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <returns>Ruff's real exit code.</returns>
    internal static Task<int> RunAsync(string[] args, int verbose)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Faithful port of ruff_cmd.rs:35-39's is_check/is_format heuristic.
        var isCheck = args.Length == 0
            || args[0] == "check"
            || (!args[0].StartsWith('-') && args[0] != "format" && args[0] != "version");
        var isFormat = args.Any(a => a == "format");

        var invocation = new List<string>();

        if (isCheck)
        {
            invocation.Add("check");

            // Exact-string match against "--output-format" (not a StartsWith check) — matches Rust's
            // `args.contains(&"--output-format".to_string())`, which does not match the
            // "--output-format=json" combined form a caller might already be passing.
            if (!args.Contains("--output-format"))
            {
                invocation.Add("--output-format=json");
            }

            var startIdx = args.Length > 0 && args[0] == "check" ? 1 : 0;
            for (var i = startIdx; i < args.Length; i++)
            {
                invocation.Add(args[i]);
            }

            if (args.Skip(startIdx).All(a => a.StartsWith('-') || a.Contains('=')))
            {
                invocation.Add(".");
            }
        }
        else
        {
            invocation.AddRange(args);
        }

        if (verbose > 0)
        {
            Console.Error.Write($"Running: ruff {string.Join(' ', args)}\n");
        }

        var argsDisplay = string.Join(' ', args);

        return CommandRunner.RunFilteredAsync(
            PathResolver.Resolve("ruff"),
            invocation,
            "ruff",
            argsDisplay,
            stdout => RuffFilters.FilterRuffOutput(stdout, isCheck, isFormat),
            new RunOptions(FilterStdoutOnly: true)
        );
    }
}
