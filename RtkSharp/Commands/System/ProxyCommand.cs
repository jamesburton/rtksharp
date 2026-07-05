using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Rewrite;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk proxy</c> CLI verb: executes an arbitrary command with live console
/// passthrough on both stdout/stderr while simultaneously capturing a capped copy of each stream for
/// direct <see cref="Tracker"/>-backed usage tracking (zero token savings by design, since proxy never
/// filters). Faithful port of Rust <c>Commands::Proxy</c>'s dispatch arm (<c>main.rs</c>:2333-2511).
/// </summary>
/// <remarks>
/// <para>
/// <b>No signal handler — Windows non-goal, disclosed not silently omitted.</b> Rust's arm also
/// registers a <c>#[cfg(unix)]</c> <c>libc::signal</c> handler (SIGINT/SIGTERM) that kills the tracked
/// child PID before re-raising the signal, to guard against orphaned children when
/// <c>panic=abort</c> skips the <c>ChildGuard</c>'s <c>Drop</c> impl (issue #897). That handler has no
/// Windows equivalent in the Rust source either — this port's target platform is Windows, so only the
/// <c>ChildGuard</c>-equivalent best-effort disposal is ported (handled inside
/// <see cref="StreamingExecutor"/>'s own try/catch, not duplicated here). See the Phase 6 plan's Scope
/// Decision for the full rationale.
/// </para>
/// <para>
/// <b>Single-raw-arg re-split special case (issue #388).</b> When <c>proxy</c> receives exactly one
/// raw argument (e.g. because the caller quoted the whole command,
/// <c>rtk proxy "git log --format=\"%H %s\""</c>), that single argument is re-split with
/// <see cref="ShellLexer.ShellSplit"/> into a command name and its arguments. Two or more raw
/// arguments are used directly as <c>args[0]</c>/<c>args[1..]</c> with <b>no</b> re-splitting — this
/// asymmetry is deliberate, not a bug, and must not be "corrected" to always re-split.
/// </para>
/// <para>
/// <b>Fail-loud for proxy's own top-level errors, never-block only for tracking.</b> An empty argument
/// list or a spawn failure surfaces as <c>rtk: {message}</c> on stderr with exit code 1 — the same
/// top-level error-propagation convention <see cref="RunCommand"/> uses — while the
/// <see cref="TimedExecution.Track"/> call after a successful run silently swallows any tracking
/// failure (constructing a <see cref="Tracker"/>, or persisting the row) per its own documented
/// "never block the user" contract.
/// </para>
/// </remarks>
public static class ProxyCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk proxy</c> with the given arguments (the remainder after the
    /// <c>proxy</c> verb), using the default <see cref="StreamingExecutor"/>.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>proxy</c>.</param>
    /// <returns>The wrapped command's exit code (or 1 on a proxy-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, executor: null);

    /// <summary>
    /// Runs <c>rtk proxy</c> with an injectable <see cref="IStreamingExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>proxy</c>.</param>
    /// <param name="executor">The streaming executor to spawn the wrapped command with, or null for the default.</param>
    /// <returns>The wrapped command's exit code (or 1 on a proxy-level failure).</returns>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, IStreamingExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await RunCoreAsync(args, executor).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Resolves the raw <c>proxy</c> arguments into a command name and its arguments, applying the
    /// single-raw-arg re-split special case (issue #388) exactly as Rust's arm does
    /// (<c>main.rs</c>:2350-2366): when exactly one raw argument was supplied, re-splits it via
    /// <see cref="ShellLexer.ShellSplit"/> (falling back to treating it as a bare command name with no
    /// arguments if the split yields only one token); when two or more raw arguments were supplied,
    /// the first is used directly as the command name and the rest as its arguments, with no
    /// re-splitting attempted.
    /// </summary>
    /// <param name="args">The raw CLI arguments following the <c>proxy</c> verb (must be non-empty).</param>
    /// <returns>The resolved command name and its arguments.</returns>
    internal static (string CmdName, IReadOnlyList<string> CmdArgs) ResolveCommand(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 1)
        {
            var parts = ShellLexer.ShellSplit(args[0]);
            if (parts.Count > 1)
            {
                return (parts[0], parts.Skip(1).ToArray());
            }

            return (args[0], Array.Empty<string>());
        }

        return (args[0], args.Skip(1).ToArray());
    }

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> args, IStreamingExecutor? executor)
    {
        if (args.Count == 0)
        {
            throw new InvalidOperationException(
                "proxy requires a command to execute\nUsage: rtk proxy <command> [args...]");
        }

        // Timer starts before spawning, mirroring Rust's `TimedExecution::start()` placement
        // (main.rs:2345) - immediately after the empty-args guard, before any argument re-splitting
        // or spawning work.
        var timer = TimedExecution.Start();

        var (cmdName, cmdArgs) = ResolveCommand(args);
        var joinedArgs = string.Join(' ', cmdArgs);

        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Proxy mode: {cmdName} {joinedArgs}\n");
        }

        var exec = executor ?? new StreamingExecutor();
        var request = new ExecutionRequest(cmdName, cmdArgs);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to execute command: {cmdName}{detail}");
        }

        // Exact Rust concatenation (main.rs:2498-2500): `format!("{}{}", stdout, stderr)` - stdout
        // immediately followed by stderr, no separator of any kind.
        var fullOutput = result.Stdout + result.Stderr;

        // `format!("{} {}", cmd_name, cmd_args.join(" "))` always inserts a literal space between
        // cmd_name and the joined args, even when cmd_args is empty (yielding a trailing space) -
        // replicated verbatim via string interpolation rather than a conditional join for byte-exact
        // parity with the tracked original_cmd/rtk_cmd strings.
        var originalCmd = $"{cmdName} {joinedArgs}";
        var rtkCmd = $"rtk proxy {cmdName} {joinedArgs}";

        // Track usage (input = output since no filtering) - main.rs:2502-2508.
        timer.Track(originalCmd, rtkCmd, fullOutput, fullOutput);

        return Utils.ExitCodeFromStatus(result.ExitCode, cmdName);
    }
}
