using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk err</c> CLI verb: runs an arbitrary command through a merged-stream,
/// line-by-line error/warning-block filter, echoing only error blocks (and their indented/blank
/// continuation lines) live as they occur, then a final "no errors" or "[FAIL]"+tail summary once
/// the command exits. Faithful port of Rust <c>runner::run_err</c> (<c>src/cmds/rust/runner.rs</c>
/// :117-130) plus its dispatch arm (<c>src/main.rs</c>:1728-1731).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a meta-command (deliberate Rust-source asymmetry).</b> Unlike <c>run</c>/<c>proxy</c>/
/// <c>pipe</c>/<c>gain</c>, Rust's own <c>RTK_META_COMMANDS</c> list does <i>not</i> include
/// <c>"err"</c> (or <c>"test"</c>) — both are classified under Rust's own <c>PASSTHROUGH</c> set
/// instead (<c>main.rs</c>'s <c>test_every_subcommand_is_classified</c> test). This looks like it
/// could be an oversight, but per house rule is preserved as-is: <see cref="ErrCommand"/> is
/// registered in <see cref="RtkSharp.Cli.CommandRegistry"/> the same way <c>git</c>/<c>gh</c>/
/// <c>dotnet</c> are (a normal dispatch entry, not a "reject bad flags before ever spawning
/// anything" meta-gate).
/// </para>
/// <para>
/// <b>Does go through the hook/integrity gate.</b> Rust's <c>is_operational_command</c>
/// (<c>main.rs</c>:2563-2564) explicitly includes <c>Commands::Err</c>/<c>Commands::Test</c> — they
/// are commands invoked via the hook pipeline, unlike meta-commands run directly by the user. As of
/// this port, <c>Program.cs</c> does not yet distinguish operational-vs-meta commands at the
/// integrity-check level (no such gate has been built for any command yet), so this is a no-op for
/// now — nothing further is added here in anticipation of gating that doesn't exist elsewhere in the
/// port yet.
/// </para>
/// <para>
/// <b>Null stdin, tee-to-disk enabled.</b> Spawned via <c>cmd /C &lt;command&gt;</c> on Windows
/// (Rust's <c>build_shell_command</c>), with stdin immediately closed (see
/// <see cref="LineFilteringExecutor"/>'s remarks) and raw output teed to disk under the <c>"err"</c>
/// label (<c>RunOptions::with_tee("err")</c>), matching <c>core::runner::run_streamed</c>.
/// </para>
/// </remarks>
public static class ErrCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk err</c> with the given arguments (the remainder after the
    /// <c>err</c> verb), using the default <see cref="LineFilteringExecutor"/>.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>err</c> — the raw command to run.</param>
    /// <returns>The wrapped command's exit code (or 1 on a top-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, executor: null);

    /// <summary>
    /// Runs <c>rtk err</c> with an injectable <see cref="ILineFilteringExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>err</c>.</param>
    /// <param name="executor">The line-filtering executor to use, or null for the default.</param>
    /// <returns>The wrapped command's exit code (or 1 on a top-level failure).</returns>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, ILineFilteringExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await RunCoreAsync(args, executor).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Guarded proactively, not thrown from this command today — see DockerCommand/
            // PrismaCommand's remarks for the swallowed-exception bug this prevents.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> args, ILineFilteringExecutor? executor)
    {
        // Rust: `let cmd = command.join(" ");` (main.rs:1729) — the trailing raw args joined with
        // spaces form the single shell command string.
        var command = string.Join(' ', args);

        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running: {command}\n");
        }

        var timer = TimedExecution.Start();

        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var flag = OperatingSystem.IsWindows() ? "/C" : "-c";
        var request = new ExecutionRequest(shell, new[] { flag, command });

        var exec = executor ?? new LineFilteringExecutor();
        var filter = new ErrorStreamFilter();
        var result = await exec.ExecuteAsync(request, filter).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run err{detail}");
        }

        // core::runner.rs's RunMode::Streamed branch only ever prints the tee hint line after the
        // fact (println!("{}", hint)) — the filtered content itself was already streamed live to the
        // console line-by-line while the child ran, so it is not reprinted here.
        var hint = Tee.TeeAndHint(result.Raw, "err", result.ExitCode);
        if (hint is not null)
        {
            Console.Out.Write(hint + "\n");
        }

        var cmdLabel = $"err {command}";
        timer.Track(cmdLabel, $"rtk {cmdLabel}", result.Raw, result.Filtered);

        return result.ExitCode;
    }
}
