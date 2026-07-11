using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk summary</c> CLI verb: runs an arbitrary shell command and prints a
/// heuristic summary of its output (auto-detecting test results, build output, logs, JSON, plain
/// lists, or falling back to a generic head/tail view). Faithful port of Rust
/// <c>src/cmds/system/summary.rs</c> (including its inline test module).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a meta-command (deliberate Rust-source asymmetry, same as <c>err</c>/<c>test</c>/<c>diff</c>).</b>
/// Rust's <c>RTK_META_COMMANDS</c> list (<c>main.rs</c>:1170-1191) does not include <c>"summary"</c> —
/// it is classified <c>PASSTHROUGH</c> instead (<c>main.rs</c>:2945). A clap-layer parse failure would
/// fall back to a raw PATH-exec of the original argv rather than a clean clap usage error. In
/// practice, Rust's <c>Summary { command: Vec&lt;String&gt; }</c> clap variant (<c>main.rs</c>:303-307)
/// uses <c>trailing_var_arg = true, allow_hyphen_values = true</c>, so essentially any argument
/// vector (including zero args, or args starting with <c>-</c>) parses successfully — there is no
/// realistic parse-failure path for this command, so <see cref="ParseArgs"/> never throws.
/// </para>
/// <para>
/// <b>Shell invocation mirrors <c>err</c>/<c>test</c>.</b> Rust's <c>run</c> (<c>summary.rs</c>:15-39)
/// spawns <c>cmd /C &lt;command&gt;</c> on Windows or <c>sh -c &lt;command&gt;</c> elsewhere via
/// <c>exec_capture</c> (captured, not streamed — unlike <c>err</c>/<c>test</c>'s streaming filters).
/// This port uses <see cref="ProcessExecutor"/> with <see cref="ExecutionCaptureMode.Separate"/> to
/// match, then combines stdout/stderr with a single <c>"\n"</c> join exactly as Rust's
/// <c>format!("{}\n{}", result.stdout, result.stderr)</c> does.
/// </para>
/// </remarks>
public static class SummaryCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk summary</c> with the given arguments (the remainder after
    /// the <c>summary</c> verb) — the raw command to run and summarize.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>summary</c>.</param>
    /// <returns>The wrapped command's exit code (or 1 on a top-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, executor: null);

    /// <summary>
    /// Runs <c>rtk summary</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>summary</c>.</param>
    /// <param name="executor">The process executor to use, or null for the default.</param>
    /// <returns>The wrapped command's exit code (or 1 on a top-level failure).</returns>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        var command = ParseArgs(args);

        try
        {
            return await RunCoreAsync(command, RuntimeOptions.Verbosity, executor, Console.Out, Console.Error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Parses <c>rtk summary</c>'s arguments: <c>trailing_var_arg</c> collects everything following
    /// the verb, joined with spaces into a single shell command string. Faithful port of the clap
    /// <c>Commands::Summary</c> variant (<c>main.rs</c>:303-307) and its dispatch
    /// (<c>let cmd = command.join(" ");</c>, <c>main.rs</c>:1857).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>summary</c>.</param>
    /// <returns>The joined shell command string (empty when no arguments were given).</returns>
    internal static string ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return string.Join(' ', args);
    }

    /// <summary>
    /// The testable core: runs <paramref name="command"/> through a shell, captures its combined
    /// output, and prints the heuristic summary. Faithful port of <c>summary::run</c>
    /// (<c>summary.rs</c>:15-39).
    /// </summary>
    /// <param name="command">The shell command to run.</param>
    /// <param name="verbose">The global verbosity level (mirrors Rust's <c>cli.verbose: u8</c>).</param>
    /// <param name="executor">The process executor to use, or null for the default.</param>
    /// <param name="stdout">The destination for the summary.</param>
    /// <param name="stderr">The destination for the verbose diagnostic line.</param>
    /// <returns>The wrapped command's exit code.</returns>
    internal static async Task<int> RunCoreAsync(
        string command, int verbose, IProcessExecutor? executor, TextWriter stdout, TextWriter stderr)
    {
        if (verbose > 0)
        {
            stderr.Write($"Running and summarizing: {command}\n");
        }

        var timer = TimedExecution.Start();

        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var flag = OperatingSystem.IsWindows() ? "/C" : "-c";
        var request = new ExecutionRequest(shell, new[] { flag, command });

        var exec = executor ?? new ProcessExecutor();
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to execute command{detail}");
        }

        var raw = $"{result.Stdout}\n{result.Stderr}";
        var summary = SummaryFilters.SummarizeOutput(raw, command, result.ExitCode == 0);

        stdout.Write(summary + "\n");
        timer.Track(command, "rtk summary", raw, summary);

        return result.ExitCode;
    }
}
