using System.Text;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk test</c> CLI verb: runs an arbitrary test command, captures its combined
/// stdout+stderr, and prints a compact pass/fail summary instead of the raw test-runner output.
/// Faithful port of Rust <c>runner::run_test</c>/<c>extract_test_summary</c>
/// (<c>src/cmds/rust/runner.rs</c>:132-146, 181-280) plus its dispatch arm (<c>src/main.rs</c>:1733-
/// 1736).
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffered, not streamed.</b> Unlike <c>rtk err</c> (which uses the merged-channel
/// <see cref="LineFilteringExecutor"/>), Rust's <c>run_test</c> goes through
/// <c>run_filtered</c>/<c>run_captured_filter</c> — a fully-buffered <c>FilterMode::CaptureOnly</c>
/// capture (<c>src/core/stream.rs</c>:463-478), not the streaming branch. Nothing is echoed live;
/// the ecosystem-summary filter is applied once, after the process exits, to the complete captured
/// text. This port mirrors that with a plain <see cref="IProcessExecutor"/> capture rather than
/// <see cref="LineFilteringExecutor"/>.
/// </para>
/// <para>
/// <b>Different cap behavior for stdout vs. stderr (a genuine Rust-source asymmetry, not a
/// simplification of this port).</b> The <c>CaptureOnly</c> branch's stdout accumulation soft-caps
/// at <see cref="RawCap"/> bytes with a one-time warning (<c>"[rtk] warning: output exceeds 10 MiB —
/// filter input truncated"</c>) — the same message text used by other buffered-mode captures in
/// Rust — while its stderr accumulation (read on a separate thread in Rust; represented here as the
/// separately-captured <see cref="ExecutionResult.Stderr"/>) soft-caps silently, with no warning at
/// all. This is deliberately preserved as-is.
/// </para>
/// <para>
/// <b>Not a meta-command; does go through the hook/integrity gate.</b> See
/// <see cref="ErrCommand"/>'s remarks for the full explanation — the same asymmetry and rationale
/// apply here.
/// </para>
/// </remarks>
public static class TestCommand
{
    /// <summary>
    /// The maximum number of bytes accumulated into the stdout capture buffer before it soft-caps
    /// (with a one-time warning). Matches Rust <c>core::stream::RAW_CAP</c> (10 MiB). Stderr shares
    /// the same limit but caps silently (see remarks).
    /// </summary>
    internal const int RawCap = 10_485_760;

    /// <summary>
    /// Registry entry point. Runs <c>rtk test</c> with the given arguments (the remainder after the
    /// <c>test</c> verb), using the default <see cref="ProcessExecutor"/>.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>test</c> — the raw test command to run.</param>
    /// <returns>The wrapped command's exit code (or 1 on a top-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, executor: null);

    /// <summary>
    /// Runs <c>rtk test</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>test</c>.</param>
    /// <param name="executor">The process executor to use, or null for the default.</param>
    /// <returns>The wrapped command's exit code (or 1 on a top-level failure).</returns>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, IProcessExecutor? executor)
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

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> args, IProcessExecutor? executor)
    {
        // Rust: `let cmd = command.join(" ");` (main.rs:1734).
        var command = string.Join(' ', args);

        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running tests: {command}\n");
        }

        var timer = TimedExecution.Start();

        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var flag = OperatingSystem.IsWindows() ? "/C" : "-c";
        var request = new ExecutionRequest(
            shell,
            new[] { flag, command },
            CaptureMode: ExecutionCaptureMode.Separate
        );

        var exec = executor ?? new ProcessExecutor();
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run test{detail}");
        }

        var (cappedStdout, _) = ApplyStdoutCap(result.Stdout, RawCap);
        var cappedStderr = ApplyStderrCapSilently(result.Stderr, RawCap);

        // Rust: `let raw = format!("{}{}", raw_stdout, raw_stderr);` (stream.rs:490).
        var raw = cappedStdout + cappedStderr;

        var filtered = TestFilters.ExtractTestSummary(raw, command);

        // core::runner.rs's print_with_hint: filtered + tee hint (if any), always println!-terminated.
        CommandRunner.PrintWithHint(filtered, raw, "test", result.ExitCode);

        var cmdLabel = $"test {command}";
        timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, filtered);

        return result.ExitCode;
    }

    /// <summary>
    /// Accumulates <paramref name="raw"/>'s lines up to <paramref name="capBytes"/> UTF-8 bytes,
    /// printing a one-time warning and stopping accumulation (not truncating mid-line) once the cap
    /// would be exceeded. Faithful port of the stdout-side cap loop in <c>FilterMode::CaptureOnly</c>
    /// (<c>src/core/stream.rs</c>:463-478). Exposed with an injectable <paramref name="capBytes"/> so
    /// tests can exercise the cap without generating 10 MiB of real output.
    /// </summary>
    /// <param name="raw">The full captured stdout text.</param>
    /// <param name="capBytes">The byte cap to apply (production always uses <see cref="RawCap"/>).</param>
    /// <returns>The (possibly truncated) text, and whether the cap was hit.</returns>
    internal static (string Text, bool Capped) ApplyStdoutCap(string raw, int capBytes)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (string.Empty, false);
        }

        var sb = new StringBuilder();
        var bytes = 0;

        foreach (var line in RtkSharp.Core.SourceFilterLineSplitter.SplitLines(raw))
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (bytes + lineBytes < capBytes)
            {
                sb.Append(line).Append('\n');
                bytes += lineBytes + 1;
            }
            else
            {
                Console.Error.Write("[rtk] warning: output exceeds 10 MiB — filter input truncated\n");
                return (sb.ToString(), true);
            }
        }

        return (sb.ToString(), false);
    }

    /// <summary>
    /// Same accumulate-up-to-cap behavior as <see cref="ApplyStdoutCap"/>, but silent: no warning is
    /// printed when the cap is hit. Faithful port of the stderr-reader-thread cap loop in the
    /// non-streaming branch (<c>src/core/stream.rs</c>:416-428), which sets its own <c>capped</c>
    /// flag with no corresponding <c>eprintln!</c>.
    /// </summary>
    /// <param name="raw">The full captured stderr text.</param>
    /// <param name="capBytes">The byte cap to apply (production always uses <see cref="RawCap"/>).</param>
    /// <returns>The (possibly truncated) text.</returns>
    internal static string ApplyStderrCapSilently(string raw, int capBytes)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var bytes = 0;

        foreach (var line in RtkSharp.Core.SourceFilterLineSplitter.SplitLines(raw))
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (bytes + lineBytes < capBytes)
            {
                sb.Append(line).Append('\n');
                bytes += lineBytes + 1;
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

}
