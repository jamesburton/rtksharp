using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using RtkSharp.Core;

namespace RtkSharp.Execution;

/// <summary>
/// The outcome of a <see cref="ILineFilteringExecutor"/> run.
/// </summary>
/// <param name="Raw">
/// The full raw (unfiltered) merged stdout+stderr text, each stream capped independently at
/// <see cref="LineFilteringExecutor.RawCap"/> bytes (soft cap: a one-time warning is printed, the
/// remainder of that stream is simply not accumulated further — unlike <see cref="StreamingExecutor"/>'s
/// silent hard cap).
/// </param>
/// <param name="Filtered">The concatenation of everything the filter chose to echo, in emission order.</param>
/// <param name="ExitCode">The child process's exit code, or a sentinel (127) if it never started.</param>
/// <param name="WasStarted">Whether the process was successfully started.</param>
/// <param name="Failure">A human-readable failure description, or null if the process started normally.</param>
public sealed record LineFilteringResult(
    string Raw,
    string Filtered,
    int ExitCode,
    bool WasStarted,
    string? Failure
);

/// <summary>
/// Abstraction over <see cref="LineFilteringExecutor"/>, allowing commands and tests to substitute a
/// fake merged-channel line-filtering executor.
/// </summary>
public interface ILineFilteringExecutor
{
    /// <summary>
    /// Executes a child process, merging its stdout/stderr into a single line-ordered sequence and
    /// feeding each complete line through <paramref name="filter"/>.
    /// </summary>
    /// <param name="request">The process execution request.</param>
    /// <param name="filter">The line filter deciding what to echo to the console.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The execution result.</returns>
    ValueTask<LineFilteringResult> ExecuteAsync(
        ExecutionRequest request,
        IStreamFilter filter,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Executes a child process with stdout/stderr merged into a single ordered sequence of complete
/// lines, feeding each line through an <see cref="IStreamFilter"/> and echoing to the real console
/// only the lines the filter chooses to emit. Faithful port of Rust <c>core::runner::run_streamed</c>
/// / <c>core::stream::run_streaming</c>'s <c>FilterMode::Streaming</c> branch (<c>src/core/stream.rs</c>
/// :334-412), used by <c>rtk err</c>/<c>rtk test</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a distinct operation from <c>rtk proxy</c>'s <see cref="StreamingExecutor"/></b> — see
/// that type's remarks for the full comparison. In short: this executor merges stdout+stderr into one
/// ordered channel of <i>complete lines</i> (not raw byte chunks), feeds each through
/// <see cref="IStreamFilter.FeedLine"/>, and echoes only what the filter returns (filtered
/// passthrough, not full passthrough). Its capture cap is <see cref="RawCap"/> (10 MiB, matching Rust
/// <c>RAW_CAP</c>) applied independently per stream, and exceeding it is a one-time <i>soft</i>
/// warning — the stream keeps flowing through the filter, only the raw-capture accumulation for that
/// stream stops — unlike <see cref="StreamingExecutor"/>'s silent 1 MiB hard cap.
/// </para>
/// <para>
/// <b>Null stdin.</b> Rust's <c>run_streamed</c> always calls <c>run_streaming</c> with
/// <c>StdinMode::Null</c> (<c>core::runner.rs</c>:167), i.e. the child's stdin is immediately closed
/// (EOF), never inherited from the parent. This executor mirrors that by redirecting stdin and
/// closing it right after the process starts.
/// </para>
/// </remarks>
public sealed class LineFilteringExecutor : ILineFilteringExecutor
{
    /// <summary>
    /// The maximum number of bytes accumulated into each stream's raw-capture buffer before that
    /// stream's accumulation soft-caps (with a one-time warning). Matches Rust
    /// <c>core::stream::RAW_CAP</c> (<c>const RAW_CAP: usize = 10_485_760;</c> — 10 MiB).
    /// </summary>
    public const int RawCap = 10_485_760;

    private readonly int _rawCap;

    /// <summary>
    /// Creates an executor using the production <see cref="RawCap"/> (10 MiB).
    /// </summary>
    public LineFilteringExecutor()
        : this(RawCap)
    {
    }

    /// <summary>
    /// Creates an executor with an overridden per-stream raw-capture cap, so tests can exercise the
    /// soft-cap warning behavior without generating 10 MiB of real child output.
    /// </summary>
    /// <param name="rawCapOverride">The byte cap to apply to each stream's raw-capture buffer.</param>
    internal LineFilteringExecutor(int rawCapOverride)
    {
        _rawCap = rawCapOverride;
    }

    /// <inheritdoc />
    public async ValueTask<LineFilteringResult> ExecuteAsync(
        ExecutionRequest request,
        IStreamFilter filter,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        using var process = new Process();
        process.StartInfo = CreateStartInfo(request);

        try
        {
            if (!process.Start())
            {
                return new LineFilteringResult(string.Empty, string.Empty, 127, false, "Process did not start.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new LineFilteringResult(string.Empty, string.Empty, 127, false, ex.Message);
        }

        // Stdio::null() equivalent: close stdin immediately so the child observes EOF rather than
        // inheriting rtk's own stdin (Rust's run_streamed always uses StdinMode::Null).
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Best-effort: if the handle is already unusable, the child will still see EOF once it
            // tries to read stdin (the pipe's write end has no other holder).
        }

        var channel = Channel.CreateUnbounded<(string Line, bool IsStderr)>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

        var stdoutTask = PumpLinesAsync(process.StandardOutput, channel.Writer, isStderr: false, cancellationToken);
        var stderrTask = PumpLinesAsync(process.StandardError, channel.Writer, isStderr: true, cancellationToken);

        var pumpsDone = Task.Run(async () =>
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            channel.Writer.TryComplete();
        }, CancellationToken.None);

        try
        {
            var rawStdout = new StringBuilder();
            var rawStderr = new StringBuilder();
            var rawStdoutBytes = 0;
            var rawStderrBytes = 0;
            var cappedOut = false;
            var cappedErr = false;
            var filtered = new StringBuilder();
            var lastWasStderr = false;

            await foreach (var (line, isStderr) in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var lineBytes = Encoding.UTF8.GetByteCount(line);

                if (isStderr)
                {
                    if (!cappedErr)
                    {
                        if (rawStderrBytes + lineBytes < _rawCap)
                        {
                            rawStderr.Append(line).Append('\n');
                            rawStderrBytes += lineBytes + 1;
                        }
                        else
                        {
                            cappedErr = true;
                            await Console.Error.WriteAsync(
                                "[rtk] warning: stderr exceeds 10 MiB — capture truncated\n").ConfigureAwait(false);
                        }
                    }
                }
                else if (!cappedOut)
                {
                    if (rawStdoutBytes + lineBytes < _rawCap)
                    {
                        rawStdout.Append(line).Append('\n');
                        rawStdoutBytes += lineBytes + 1;
                    }
                    else
                    {
                        cappedOut = true;
                        await Console.Error.WriteAsync(
                            "[rtk] warning: stdout exceeds 10 MiB — filter input truncated\n").ConfigureAwait(false);
                    }
                }

                lastWasStderr = isStderr;
                var output = filter.FeedLine(line);
                if (output is not null)
                {
                    filtered.Append(output);
                    var dest = isStderr ? Console.Error : Console.Out;
                    await dest.WriteAsync(output).ConfigureAwait(false);
                }
            }

            await pumpsDone.ConfigureAwait(false);

            var tail = filter.Flush();
            if (!string.IsNullOrEmpty(tail))
            {
                filtered.Append(tail);
                var dest = lastWasStderr ? Console.Error : Console.Out;
                await dest.WriteAsync(tail).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            var raw = rawStdout.ToString() + rawStderr.ToString();

            var post = filter.OnExit(exitCode, raw);
            if (post is not null)
            {
                filtered.Append(post);
                var dest = lastWasStderr ? Console.Error : Console.Out;
                await dest.WriteAsync(post).ConfigureAwait(false);
            }

            return new LineFilteringResult(raw, filtered.ToString(), exitCode, true, null);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>
    /// Reads complete lines from <paramref name="reader"/> and publishes each to
    /// <paramref name="writer"/> tagged with <paramref name="isStderr"/>, until end-of-stream.
    /// </summary>
    /// <param name="reader">The child process's redirected stdout or stderr reader.</param>
    /// <param name="writer">The shared merged-channel writer.</param>
    /// <param name="isStderr">Whether <paramref name="reader"/> is the stderr stream.</param>
    /// <param name="cancellationToken">A token to cancel the read loop.</param>
    private static async Task PumpLinesAsync(
        StreamReader reader,
        ChannelWriter<(string Line, bool IsStderr)> writer,
        bool isStderr,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            await writer.WriteAsync((line, isStderr), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateStartInfo(ExecutionRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PathResolver.ResolveForRequest(request),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Redirected (then immediately closed) rather than inherited — see remarks: Rust's
            // run_streamed always uses StdinMode::Null, unlike Proxy's inherited stdin.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort only: the process may have already exited concurrently.
        }
    }
}
