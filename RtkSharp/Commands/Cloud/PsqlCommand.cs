using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Cloud;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk psql</c> CLI verb: runs the real <c>psql</c> binary and compresses its
/// table/expanded (<c>\x</c>) output. Faithful port of <c>src/cmds/cloud/psql_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>PASSTHROUGH-classified, not a meta-command.</b> Rust's own <c>RTK_META_COMMANDS</c> list does
/// not include <c>"psql"</c> — <c>main.rs</c>'s <c>test_every_subcommand_is_classified</c> lists it
/// under <c>PASSTHROUGH</c>. Its clap arm is a single <c>trailing_var_arg = true,
/// allow_hyphen_values = true</c> catch-all with no positional/flag validation of its own, so there
/// is no clap-equivalent rejection to reproduce — this port never throws
/// <see cref="CommandArgumentParseException"/>.
/// </para>
/// <para>
/// <b><c>#[command(disable_help_flag = true)]</c> has no C# analog needed.</b> That attribute only
/// stops clap itself from intercepting <c>-h</c>/<c>--help</c> before they reach
/// <c>psql_cmd::run</c>'s trailing args; RtkSharp's own dispatch layer has no global help
/// interceptor to disable in the first place; a literal <c>--help</c>/<c>-h</c> token already flows
/// through to the real <c>psql</c> binary unchanged, matching the intended behavior automatically.
/// </para>
/// <para>
/// <b>Early-exit-on-failure, stdout-only filtering, tee'd raw output</b> — ported via
/// <see cref="CommandRunner.RunFilteredAsync"/>'s <see cref="RunOptions"/>
/// (<c>SkipFilterOnFailure: true</c>, <c>FilterStdoutOnly: true</c>, <c>TeeLabel: "psql"</c>),
/// mirroring Rust's <c>RunOptions::stdout_only().tee("psql").early_exit_on_failure()</c> exactly.
/// </para>
/// </remarks>
public static class PsqlCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk psql</c> with the given arguments (the remainder after the
    /// <c>psql</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>psql</c>, forwarded verbatim to real <c>psql</c>.</param>
    /// <returns>psql's real exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity);

    /// <summary>
    /// The testable core of <see cref="RunAsync(string[])"/>.
    /// </summary>
    /// <param name="args">The arguments to forward to <c>psql</c>.</param>
    /// <param name="verbose">The top-level verbosity count; &gt; 0 prints the resolved command line to stderr.</param>
    /// <returns>psql's real exit code.</returns>
    public static Task<int> RunAsync(string[] args, int verbose)
    {
        if (verbose > 0)
        {
            Console.Error.Write($"Running: psql {string.Join(' ', args)}\n");
        }

        return CommandRunner.RunFilteredAsync(
            "psql",
            args,
            "psql",
            string.Join(' ', args),
            PsqlFilters.FilterPsqlOutput,
            new RunOptions(TeeLabel: "psql", FilterStdoutOnly: true, SkipFilterOnFailure: true));
    }
}
