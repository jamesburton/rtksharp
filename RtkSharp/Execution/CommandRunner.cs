using RtkSharp.Core;

namespace RtkSharp.Execution;

/// <summary>
/// Options controlling how <see cref="CommandRunner"/> captures, filters, and prints a
/// child process's output.
/// </summary>
/// <param name="TeeLabel">
/// When set, raw output is teed to disk (subject to <see cref="Tee.TeeAndHint"/>'s own
/// thresholds) and a recovery hint is appended after the filtered output.
/// </param>
/// <param name="FilterStdoutOnly">
/// When true, the filter receives only captured stdout; when false, it receives stdout
/// and stderr combined.
/// </param>
/// <param name="SkipFilterOnFailure">
/// When true and the process exits non-zero, filtering is bypassed entirely and raw
/// stdout/stderr are printed unchanged — useful for commands whose filtered view assumes
/// well-formed output that a failure won't produce.
/// </param>
/// <param name="NoTrailingNewline">
/// When true (and <see cref="TeeLabel"/> is unset), the filtered output is written without
/// an appended newline.
/// </param>
/// <param name="InheritStdin">
/// When true, the child process inherits rtk's own stdin. Needed for commands that read
/// from a pipe (e.g. <c>cat file | rtk wc</c>); otherwise the child sees an empty stdin.
/// </param>
public sealed record RunOptions(
    string? TeeLabel = null,
    bool FilterStdoutOnly = false,
    bool SkipFilterOnFailure = false,
    bool NoTrailingNewline = false,
    bool InheritStdin = false
);

/// <summary>
/// Shared execute-and-filter pipeline used by every system command filter: runs the
/// underlying tool, applies a filter to its captured output, prints the result (with an
/// optional tee recovery hint), records token-savings metrics, and propagates the tool's
/// exit code. If the filter throws, the raw output is printed instead — filtering must
/// never crash the command or hide output from the user.
/// </summary>
/// <remarks>
/// Ported from <c>src/core/runner.rs</c>'s <c>run</c>/<c>run_filtered</c>/
/// <c>run_filtered_with_exit</c>/<c>print_with_hint</c>. The streaming variant
/// (<c>run_streamed</c>) was intentionally not ported: none of the six target system
/// commands (ls, read, wc, tree, find, grep) use it on their default execution path — the
/// three that call into the shared runner (ls, wc, tree) all use the captured-filter mode.
/// </remarks>
public static class CommandRunner
{
    /// <summary>
    /// Executes <paramref name="fileName"/> with <paramref name="arguments"/>, applies
    /// <paramref name="filter"/> to the captured output, prints the filtered result (with
    /// an optional tee hint), records the before/after size with <paramref name="tokenTracker"/>,
    /// and returns the tool's exit code. If <paramref name="filter"/> throws, the raw
    /// output is printed unchanged instead.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">The arguments to pass to the executable.</param>
    /// <param name="toolName">The tool's display name, used to build the tracking label.</param>
    /// <param name="argsDisplay">The display form of the arguments, used to build the tracking label.</param>
    /// <param name="filter">The filter applied to the captured output.</param>
    /// <param name="options">Capture, tee, and print behavior options.</param>
    /// <param name="environment">Environment variable overrides for the child process, or null to inherit.</param>
    /// <param name="tokenTracker">The tracker to record output sizes with, or null to use <see cref="NoOpTokenTracker"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The child process's exit code.</returns>
    public static Task<int> RunFilteredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string toolName,
        string argsDisplay,
        Func<string, string> filter,
        RunOptions options,
        IReadOnlyDictionary<string, string?>? environment = null,
        ITokenTracker? tokenTracker = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(filter);
        return RunFilteredWithExitAsync(
            fileName,
            arguments,
            toolName,
            argsDisplay,
            (text, _) => filter(text),
            options,
            environment,
            tokenTracker,
            cancellationToken
        );
    }

    /// <summary>
    /// Same as <see cref="RunFilteredAsync"/>, but <paramref name="filter"/> also receives
    /// the tool's exit code so it can tailor its output to success/failure.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">The arguments to pass to the executable.</param>
    /// <param name="toolName">The tool's display name, used to build the tracking label.</param>
    /// <param name="argsDisplay">The display form of the arguments, used to build the tracking label.</param>
    /// <param name="filter">The filter applied to the captured output; receives the exit code as its second argument.</param>
    /// <param name="options">Capture, tee, and print behavior options.</param>
    /// <param name="environment">Environment variable overrides for the child process, or null to inherit.</param>
    /// <param name="tokenTracker">The tracker to record output sizes with, or null to use <see cref="NoOpTokenTracker"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The child process's exit code.</returns>
    public static async Task<int> RunFilteredWithExitAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string toolName,
        string argsDisplay,
        Func<string, int, string> filter,
        RunOptions options,
        IReadOnlyDictionary<string, string?>? environment = null,
        ITokenTracker? tokenTracker = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(options);

        var tracker = tokenTracker ?? new NoOpTokenTracker();
        var executor = new ProcessExecutor();

        var request = new ExecutionRequest(
            fileName,
            arguments,
            Environment: environment,
            CaptureMode: ExecutionCaptureMode.Separate
        );

        var result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        var exitCode = result.ExitCode;
        var rawStdout = result.Stdout;
        var rawStderr = result.Stderr;

        // Best-effort combined view of stdout+stderr for filters that want both streams.
        // ProcessExecutor's Separate capture mode reads stdout and stderr independently, so
        // true chronological interleaving (as Rust's stream::run_streaming provides) isn't
        // available here; stdout-then-stderr concatenation is used instead. None of the six
        // Phase 5a target commands use FilterStdoutOnly=false, so this is a documented
        // limitation rather than an active behavior difference.
        var raw = rawStdout + rawStderr;

        var cmdLabel = $"{toolName} {argsDisplay}";

        if (options.SkipFilterOnFailure && exitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(rawStdout))
            {
                Console.Out.Write(rawStdout);
            }

            if (!string.IsNullOrWhiteSpace(rawStderr))
            {
                Console.Error.Write(rawStderr);
            }

            tracker.Record(cmdLabel, raw.Length, raw.Length);
            return exitCode;
        }

        var textToFilter = options.FilterStdoutOnly ? rawStdout : raw;

        string filtered;
        try
        {
            filtered = filter(textToFilter, exitCode);
        }
        catch (Exception ex)
        {
            // Mandatory fallback: a filter must never crash or hide output from the user.
            Console.Error.WriteLine($"rtk: filter warning: {ex.Message}");
            filtered = textToFilter;
        }

        if (options.TeeLabel is { } label)
        {
            PrintWithHint(filtered, raw, label, exitCode);
        }
        else if (options.NoTrailingNewline)
        {
            Console.Out.Write(filtered);
        }
        else
        {
            // Rust's println! always terminates with "\n"; WriteLine would emit
            // Environment.NewLine ("\r\n" on Windows) and break byte parity.
            Console.Out.Write(filtered + "\n");
        }

        var rawForTracking = options.FilterStdoutOnly ? rawStdout : raw;
        tracker.Record(cmdLabel, rawForTracking.Length, filtered.Length);
        return exitCode;
    }

    /// <summary>
    /// Prints <paramref name="filtered"/> output, followed by a tee recovery hint line if
    /// <paramref name="raw"/> was written to disk under <paramref name="teeLabel"/>.
    /// </summary>
    /// <param name="filtered">The already-filtered output to print.</param>
    /// <param name="raw">The raw (unfiltered) output, potentially teed to disk.</param>
    /// <param name="teeLabel">The command slug used to name the tee file.</param>
    /// <param name="exitCode">The exit code of the command that produced <paramref name="raw"/>.</param>
    public static void PrintWithHint(string filtered, string raw, string teeLabel, int exitCode)
    {
        var hint = Tee.TeeAndHint(raw, teeLabel, exitCode);

        // "\n" rather than WriteLine: Rust's println! always emits "\n", and WriteLine's
        // Environment.NewLine ("\r\n" on Windows) would break byte parity with the oracle.
        Console.Out.Write((hint is not null ? $"{filtered}\n{hint}" : filtered) + "\n");
    }
}
