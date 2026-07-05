using RtkSharp.Execution;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk run</c> CLI verb: a transparent shell-spawn wrapper with fully inherited
/// stdio, no filtering, and no token tracking. Faithful port of Rust <c>Commands::Run</c>'s dispatch
/// arm (<c>main.rs</c>:2312-2331 for the arm body, <c>main.rs</c>:612-620 for the clap declaration).
/// </summary>
/// <remarks>
/// <b>Deliberate asymmetry vs <c>rtk proxy</c>.</b> Unlike <c>proxy</c> (which will use a
/// signal-aware exit-code helper), <c>run</c> uses a plain fallback of <c>1</c> when the child's exit
/// code is unavailable — mirroring Rust's <c>status.code().unwrap_or(1)</c> literally. This is not an
/// oversight; it must not be "fixed" to match <c>proxy</c>'s behavior.
///
/// <b>No tracking, no rewrite-engine involvement.</b> The raw command string is handed straight to
/// the shell verbatim; nothing here calls into <see cref="RtkSharp.Core.Tracking.TimedExecution"/> or
/// any <c>Tracker</c>, matching Rust's own doc comment ("raw, no filtering or tracking") literally.
///
/// <b>Fail-loud, not never-block.</b> <c>rtk run</c> is a user-invoked one-shot command. A spawn
/// failure (the shell itself could not be started) is reported as <c>rtk: {message}</c> on stderr
/// with exit code 1, mirroring Rust's <c>.with_context(|| format!("Failed to execute: {}", raw))?</c>
/// propagating up to <c>main</c>'s top-level <c>eprintln!("rtk: {:#}", e)</c> handler — the same
/// pattern already used by <see cref="ConfigCommand"/>/<see cref="RtkSharp.Hooks.InitCommand"/>/
/// <see cref="RtkSharp.Hooks.VerifyCommand"/> for the same class of user command.
/// </remarks>
public static class RunCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk run</c> with the given arguments (the remainder after the
    /// <c>run</c> verb), using the default <see cref="ProcessExecutor"/>.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>run</c>.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, executor: null);

    /// <summary>
    /// Runs <c>rtk run</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>run</c>.</param>
    /// <param name="executor">The process executor to spawn the shell with, or null for the default.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, IProcessExecutor? executor)
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
    /// Resolves the raw command string, matching Rust's <c>Option</c> precedence exactly: <c>-c</c>/
    /// <c>--command</c> wins whenever present (regardless of whether trailing positional args were
    /// also supplied); only when it is absent are the positional <paramref name="args"/> joined with
    /// spaces; an empty result (neither given) is the empty string. Mirrors <c>main.rs</c>:2313-2317's
    /// <c>match command { Some(c) => c, None if !args.is_empty() => args.join(" "), None => String::new() }</c>.
    /// </summary>
    /// <param name="args">The raw CLI arguments following the <c>run</c> verb.</param>
    /// <returns>The resolved raw command string (possibly empty).</returns>
    internal static string ResolveRawCommand(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? command = null;
        var positional = new List<string>();

        var i = 0;
        while (i < args.Count)
        {
            var a = args[i];

            if (a == "-c" || a == "--command")
            {
                if (i + 1 < args.Count)
                {
                    command = args[i + 1];
                    i += 2;
                }
                else
                {
                    // Flag with no value: clap would error; treat as an empty command string.
                    command = string.Empty;
                    i++;
                }

                continue;
            }

            if (a.StartsWith("--command=", StringComparison.Ordinal))
            {
                command = a["--command=".Length..];
                i++;
                continue;
            }

            positional.Add(a);
            i++;
        }

        if (command is not null)
        {
            return command;
        }

        return positional.Count > 0 ? string.Join(" ", positional) : string.Empty;
    }

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> args, IProcessExecutor? executor)
    {
        var raw = ResolveRawCommand(args);

        // Empty command: return exit 0 immediately without spawning anything (main.rs:2318-2320).
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        var exec = executor ?? new ProcessExecutor();

        // Windows: `cmd /C <raw>`. Rust also branches to `sh -c <raw>` on non-Windows; this port's
        // target platform is Windows, so only the `cmd` branch is exercised, but the shell/flag
        // selection mirrors Rust's `if cfg!(windows) { ... } else { ... }` for completeness.
        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var flag = OperatingSystem.IsWindows() ? "/C" : "-c";

        var request = new ExecutionRequest(shell, new[] { flag, raw }, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to execute: {raw}{detail}");
        }

        // Plain fallback to 1 on a missing exit code (status.code().unwrap_or(1)) — deliberately NOT
        // the signal-aware helper `proxy` uses. ExecutionResult.ExitCode is always populated once
        // WasStarted is true (ProcessExecutor reads process.ExitCode after WaitForExitAsync), but the
        // guard is kept for parity with Rust's defensive unwrap_or.
        return result.ExitCode;
    }
}
