using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RtkSharp.Execution;

/// <summary>
/// Executes a child process with stdout/stderr piped, streaming both to the real console live
/// (byte-for-byte, uncapped) while simultaneously accumulating a capped in-memory copy of each
/// stream for callers that need to inspect, filter, tee, or track the output afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>rtk proxy</c>'s bespoke dual-thread pipe-draining code in
/// <c>main.rs</c>'s <c>Commands::Proxy</c> arm: two readers (one per stream) copy 8 KiB chunks
/// from the child's pipe straight to the parent's real stdout/stderr, while independently
/// capturing into a buffer capped at <see cref="CaptureCapBytes"/> (1 MiB) per stream. Capture
/// stops accumulating once a stream's cap is reached, but live passthrough continues uncapped
/// for the lifetime of the child.
/// </para>
/// <para>
/// <b>This is a distinct operation from Rust's <c>core::runner::run_streamed</c></b> (used by
/// <c>rtk err</c>/<c>rtk test</c> via <c>core::stream::run_streaming</c>'s
/// <c>FilterMode::Streaming</c> branch), not an alternate name for the same thing:
/// <c>run_streamed</c> merges stdout/stderr into a single line-ordered channel, feeds each
/// complete line through a <c>StreamFilter</c>, and echoes to the console only the lines the
/// filter chooses to emit (i.e. partial, filtered passthrough) — whereas this executor always
/// echoes <i>everything</i>, byte-for-byte, with stdout/stderr kept on their own streams. Their
/// capture caps also differ (1 MiB here per stream vs. 10 MiB — <c>RAW_CAP</c> — there), and
/// <c>run_streamed</c>'s cap is a soft one-time warning rather than a silent stop. A future
/// line-filtering executor for <c>rtk err</c>/<c>rtk test</c> should be built as its own
/// component layered on top of a line-splitting reader, not retrofitted into this type.
/// </para>
/// </remarks>
public sealed class StreamingExecutor : IStreamingExecutor
{
    /// <summary>
    /// The maximum number of bytes accumulated into the in-memory capture buffer per stream.
    /// Matches Rust <c>Proxy</c>'s <c>const CAP: usize = 1_048_576;</c> (1 MiB). Live passthrough
    /// to the real console is never capped by this value.
    /// </summary>
    public const int CaptureCapBytes = 1_048_576;

    private const int ChunkSizeBytes = 8192;

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process();
        process.StartInfo = CreateStartInfo(request);

        try
        {
            if (!process.Start())
            {
                stopwatch.Stop();
                return new ExecutionResult("", "", 127, stopwatch.Elapsed, false, "Process did not start.", false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            stopwatch.Stop();
            return new ExecutionResult("", "", 127, stopwatch.Elapsed, false, ex.Message, false);
        }

        try
        {
            // Start draining both pipes immediately and concurrently: the child's stdout/stderr
            // buffers are bounded, so if either pipe isn't read while the other blocks, the child
            // can deadlock against its own output. Reading both up front (rather than after
            // WaitForExitAsync) mirrors Rust spawning both reader threads before child.wait().
            var stdoutTask = PumpAndCaptureAsync(process.StandardOutput.BaseStream, Console.Out, cancellationToken);
            var stderrTask = PumpAndCaptureAsync(process.StandardError.BaseStream, Console.Error, cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var stdoutBytes = await stdoutTask.ConfigureAwait(false);
            var stderrBytes = await stderrTask.ConfigureAwait(false);

            var stdout = Encoding.UTF8.GetString(stdoutBytes);
            var stderr = Encoding.UTF8.GetString(stderrBytes);

            return new ExecutionResult(stdout, stderr, process.ExitCode, stopwatch.Elapsed, true, null, false);
        }
        catch
        {
            // Best-effort child cleanup on any unexpected failure (cancellation, pipe I/O error,
            // etc.) — the C# equivalent of Rust's Drop-based ChildGuard: never leave an orphaned
            // child process behind just because the parent gave up early.
            TryKill(process);
            throw;
        }
    }

    /// <summary>
    /// Drains <paramref name="source"/> in <see cref="ChunkSizeBytes"/> chunks, writing each
    /// chunk's decoded text immediately to <paramref name="live"/> (uncapped) while
    /// simultaneously accumulating the raw bytes into a buffer capped at
    /// <see cref="CaptureCapBytes"/>. Returns the captured (possibly-truncated) bytes once the
    /// stream reaches EOF.
    /// </summary>
    /// <param name="source">The child process's redirected stdout or stderr stream.</param>
    /// <param name="live">The real console writer to stream decoded chunks to as they arrive.</param>
    /// <param name="cancellationToken">A token to cancel the read loop.</param>
    /// <returns>The captured bytes, truncated at <see cref="CaptureCapBytes"/> if exceeded.</returns>
    private static async Task<byte[]> PumpAndCaptureAsync(
        Stream source,
        TextWriter live,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[ChunkSizeBytes];
        var charBuffer = new char[ChunkSizeBytes];
        var decoder = Encoding.UTF8.GetDecoder();
        using var captured = new MemoryStream();
        var capturedLength = 0;

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            if (capturedLength < CaptureCapBytes)
            {
                var take = Math.Min(bytesRead, CaptureCapBytes - capturedLength);
                captured.Write(buffer, 0, take);
                capturedLength += take;
            }

            // Live passthrough is always uncapped, regardless of the capture cap above.
            var charCount = decoder.GetCharCount(buffer, 0, bytesRead, flush: false);
            if (charCount > charBuffer.Length)
            {
                charBuffer = new char[charCount];
            }

            var actualChars = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0, flush: false);
            if (actualChars > 0)
            {
                await live.WriteAsync(charBuffer.AsMemory(0, actualChars)).ConfigureAwait(false);
                await live.FlushAsync().ConfigureAwait(false);
            }
        }

        return captured.ToArray();
    }

    private static ProcessStartInfo CreateStartInfo(ExecutionRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PathResolver.ResolveForRequest(request),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Stdin is deliberately left un-redirected (inherited from the parent), matching
            // Rust's default `std::process::Command` behavior when `.stdin(...)` is never
            // called — both `Proxy` and `Err`/`Test`'s `build_shell_command` rely on this.
            RedirectStandardInput = false,
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
            // Best-effort only: the process may have already exited concurrently, or cleanup
            // itself may fail — either way there is nothing further to report.
        }
    }
}
