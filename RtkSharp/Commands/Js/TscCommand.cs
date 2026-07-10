using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Js;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Implements the <c>rtk tsc</c> CLI verb: groups TypeScript compiler diagnostics by file (most
/// errors first, no per-file cap) and prints a one-line summary plus a top-error-codes breakdown.
/// Faithful port of <c>src/cmds/js/tsc_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffered capture, not streaming — an explicit architecture decision.</b> Rust ships two
/// parallel, behaviorally-equivalent implementations: a real streaming <c>TscHandler</c>
/// (<c>tsc_cmd.rs</c>:45-105, a <c>BlockHandler</c> driven by <c>core::stream::BlockStreamFilter</c>
/// over <c>runner::run_streamed</c> — this is what actually executes when a user runs <c>rtk tsc</c>),
/// and a buffered <c>filter_tsc_output</c> (<c>tsc_cmd.rs</c>:107-214) that Rust's own <c>run()</c>
/// never calls — it exists solely for <c>#[cfg(test)]</c> to exercise directly, and Rust's own test
/// suite exercises this buffered path exclusively (all six <c>#[cfg(test)]</c> functions call
/// <c>filter_tsc_output</c> or run the streaming handler through a synthetic single-shot harness, never
/// against a live streaming child process). This port implements <see cref="FilterTscOutput"/> as the
/// buffered equivalent of <c>filter_tsc_output</c> and drives it through <see cref="CommandRunner"/>'s
/// existing captured-filter pipeline (the same one <c>npm</c>/<c>npx</c> use) rather than introducing a
/// new tsc-specific streaming primitive into RtkSharp's execution layer.
/// </para>
/// <para>
/// <b>Correction: the two paths were NOT behaviorally equivalent for the zero-error case, and this has
/// been fixed.</b> A prior version of this port carried the buffered <c>filter_tsc_output</c>'s
/// zero-error branching verbatim — returning <c>"TypeScript: No errors found"</c> only when the raw
/// output happened to contain the literal substring <c>"Found 0 errors"</c>, and otherwise falling back
/// to a generic <c>"TypeScript compilation completed"</c> message. But real <c>rtk tsc</c> invocations run
/// through the streaming <c>TscHandler::format_summary</c> path, which returns
/// <c>"TypeScript: No errors found"</c> unconditionally whenever <c>error_count == 0</c> — no substring
/// check at all. Since a plain <c>tsc --noEmit</c> on a clean project typically prints nothing and exits
/// 0, that is exactly the case where the substring is absent and the ported buffered logic diverged from
/// what users actually observe. <see cref="FilterTscOutput"/> now always returns
/// <c>"TypeScript: No errors found"</c> on zero errors, matching the streaming path's real, observed
/// behavior; the substring-conditional generic-message fallback has been removed. Flagged here for Phase
/// 8's compatibility ledger (Task 8): <b>tsc runs via buffered capture, not true line-by-line
/// streaming</b> — a user piping enormous compiler output (many thousands of diagnostic lines) will see
/// output only after the process exits, rather than incrementally. That timing difference remains the
/// one known, accepted divergence; the zero-error message content divergence described above has been
/// eliminated.
/// </para>
/// <para>
/// <b>Tool resolution: <c>tool_exists("tsc")</c>, not <see cref="PackageManagerDetection"/>.</b> Rust's
/// <c>run()</c> (<c>tsc_cmd.rs</c>:17-25) calls the plain <c>tool_exists("tsc")</c> helper and, on
/// failure, hardcodes a fallback to <c>npx tsc</c> — it does NOT call
/// <c>detect_package_manager()</c>/<c>package_manager_exec()</c> (the pnpm/yarn/npx-exec ladder Task 1
/// ported as <see cref="PackageManagerDetection"/>, used by other future filters like playwright). This
/// port matches that exactly: <see cref="ToolExists"/> mirrors <c>tool_exists</c> via
/// <see cref="PathResolver.Resolve(string)"/> (the same convention <c>TreeCommand</c> established for
/// its own <c>tool_exists("tree")</c> port), and the fallback is unconditionally <c>npx tsc</c> — never
/// pnpm/yarn.
/// </para>
/// <para>
/// <b>Implicit tracking via <see cref="CommandRunner"/>.</b> Rust's <c>run()</c> delegates straight to
/// <c>runner::run_streamed(...)</c> (<c>tsc_cmd.rs</c>:36-42) — tracking happens implicitly inside that
/// shared runner skeleton, with no manual <c>TimedExecution</c> call in <c>tsc_cmd.rs</c> itself. This
/// matches <c>npm</c>/<c>npx</c>'s wiring (Phase 8 Task 2), not <c>pnpm</c>'s manual-<c>TimedExecution</c>
/// pattern (Task 3): <see cref="ExecuteAsync"/> routes through <see cref="CommandRunner.RunFilteredAsync"/>
/// with no separate tracking call.
/// </para>
/// <para>
/// <b>No cap on errors shown per file — deliberate divergence from most RTK truncation conventions.</b>
/// <c>filter_tsc_output</c> (<c>tsc_cmd.rs</c>:196-211) shows every single error for every file with no
/// <c>CAP_LIST</c>/<c>MAX_LISTING</c>-style limit anywhere in the loop; Rust's own
/// <c>test_no_file_limit</c> proves this with 15 files, asserting every one appears. Only individual
/// error messages and continuation/context lines are truncated, to 120 Unicode scalar values via
/// <c>utils::truncate</c> (ported here as <see cref="Utils.Truncate"/>).
/// </para>
/// </remarks>
public static class TscCommand
{
    /// <summary>
    /// Registry entry point for the <c>tsc</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/> (the
    /// registry delegate cannot receive it as an argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>tsc</c> verb.</param>
    /// <returns><c>tsc</c>'s exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunTscSafeAsync(args, RuntimeOptions.Verbosity);

    /// <summary>
    /// Test-friendly overload of the <c>tsc</c> entry point taking an explicit verbosity value instead
    /// of reading <see cref="RuntimeOptions"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>tsc</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <returns><c>tsc</c>'s exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunTscSafeAsync(string[] args, int verbose)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await ExecuteAsync(args, verbose).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Fail-loud, same convention as NpmCommand/PnpmCommand: an rtk-level failure surfaces as
            // `rtk: {message}`. Guarded proactively, not thrown from this command today — see
            // DockerCommand/PrismaCommand's remarks for the swallowed-exception bug this prevents.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Resolves <c>tsc</c> (directly, or via <c>npx tsc</c>), runs it through
    /// <see cref="CommandRunner.RunFilteredAsync"/> with <see cref="FilterTscOutput"/> and tee-to-disk
    /// enabled, and returns the child's exit code. Ports the body of Rust's <c>run()</c>
    /// (<c>tsc_cmd.rs</c>:16-43).
    /// </summary>
    /// <param name="args">The arguments following the <c>tsc</c> verb.</param>
    /// <param name="verbose">The verbosity level; a nonzero value logs the resolved command being run.</param>
    /// <returns><c>tsc</c>'s exit code.</returns>
    internal static Task<int> ExecuteAsync(string[] args, int verbose)
    {
        var tscExists = ToolExists("tsc");

        var cmdArgs = new List<string>();
        string fileName;
        string toolLabel;

        if (tscExists)
        {
            fileName = "tsc";
            toolLabel = "tsc";
        }
        else
        {
            fileName = "npx";
            cmdArgs.Add("tsc");
            toolLabel = "npx tsc";
        }

        cmdArgs.AddRange(args);

        var argsDisplay = string.Join(' ', args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {toolLabel} {argsDisplay}\n");
        }

        return CommandRunner.RunFilteredAsync(
            fileName,
            cmdArgs,
            "tsc",
            argsDisplay,
            FilterTscOutput,
            new RunOptions(TeeLabel: "tsc")
        );
    }

    /// <summary>
    /// Reports whether <paramref name="name"/> is directly resolvable on <c>PATH</c>. Mirrors Rust's
    /// <c>tool_exists</c> (<c>which::which(name).is_ok()</c>, <c>src/core/utils.rs</c>:361-363) via
    /// <see cref="PathResolver.Resolve(string)"/>, the same convention <c>TreeCommand</c> uses for its
    /// own <c>tool_exists("tree")</c> port.
    /// </summary>
    /// <param name="name">The tool binary name to check.</param>
    /// <returns>True if <paramref name="name"/> resolves to a real path on <c>PATH</c>.</returns>
    internal static bool ToolExists(string name) => PathResolver.Resolve(name) != name;

    /// <summary>
    /// Buffered filter for tsc's raw diagnostic output. Delegates to
    /// <see cref="TscFilters.FilterTscOutput"/> (moved to <c>RtkSharp.Filters</c> in Task 7 of the
    /// filters-library extraction — pure text filtering, no process execution or file I/O).
    /// </summary>
    /// <param name="output">The raw <c>tsc</c> output (stdout+stderr) to filter.</param>
    /// <returns>The grouped, summarized output.</returns>
    internal static string FilterTscOutput(string output) => TscFilters.FilterTscOutput(output);
}
