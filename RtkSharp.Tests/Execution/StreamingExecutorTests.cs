using System.Text;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Execution;

public class StreamingExecutorTests
{
    /// <summary>
    /// A <see cref="TextWriter"/> that records every chunk written to it (as an in-order log and
    /// a concatenated buffer) and invokes a callback synchronously for each chunk — used to
    /// observe live passthrough writes as they happen, not just the end result.
    /// </summary>
    private sealed class RecordingTextWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly Action<string>? _onWrite;

        public RecordingTextWriter(Action<string>? onWrite = null) => _onWrite = onWrite;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            _buffer.Append(value);
            _onWrite?.Invoke(value.ToString());
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            _buffer.Append(value);
            _onWrite?.Invoke(value);
        }

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Write(new string(buffer.Span));
            return Task.CompletedTask;
        }

        public override Task FlushAsync() => Task.CompletedTask;

        public override string ToString() => _buffer.ToString();
    }

    [Fact]
    public async Task ExecuteAsync_StreamsLiveBeforeProcessExits()
    {
        var executor = new StreamingExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", "echo first-chunk & ping -n 2 127.0.0.1 >nul & echo second-chunk"])
            : new ExecutionRequest("sh", ["-c", "echo first-chunk; sleep 1; echo second-chunk"]);

        var firstWriteTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingTextWriter? liveWriterRef = null;
        var liveWriter = new RecordingTextWriter(_ =>
        {
            // Console's Write(ReadOnlySpan<char>)/synchronized-wrapper plumbing may deliver each
            // chunk one character at a time rather than as a single string, so match against the
            // cumulative buffer (not the individual callback argument) for "has 'first-chunk'
            // appeared yet by now".
            if (liveWriterRef!.ToString().Contains("first-chunk"))
            {
                firstWriteTcs.TrySetResult();
            }
        });
        liveWriterRef = liveWriter;

        var originalOut = Console.Out;
        try
        {
            Console.SetOut(liveWriter);
            var executeTask = executor.ExecuteAsync(request).AsTask();

            // The first live chunk must arrive well before the ~1s delayed second chunk causes
            // the process to exit — proving passthrough happens in real time, not just once at
            // the end. This is a race between two real events (not a fixed sleep), so it is not
            // timing-flaky: the delayed child write is ~1000ms out, and the first chunk streams
            // through almost immediately after being spawned.
            var winner = await Task.WhenAny(firstWriteTcs.Task, executeTask);
            Assert.Same(firstWriteTcs.Task, winner);
            Assert.False(executeTask.IsCompleted, "the child process should still be running when the first live chunk arrives");

            var result = await executeTask;

            Assert.True(result.WasStarted);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("first-chunk", result.Stdout);
            Assert.Contains("second-chunk", result.Stdout);

            // What was streamed live matches what was captured — genuine passthrough, not a
            // buffered replay after the fact.
            var live = liveWriter.ToString();
            Assert.Contains("first-chunk", live);
            Assert.Contains("second-chunk", live);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CapturesStderrLiveAndInBuffer()
    {
        var executor = new StreamingExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", "echo err-line 1>&2"])
            : new ExecutionRequest("sh", ["-c", "echo err-line 1>&2"]);

        var liveWriter = new RecordingTextWriter();
        var originalErr = Console.Error;
        try
        {
            Console.SetError(liveWriter);
            var result = await executor.ExecuteAsync(request);

            Assert.True(result.WasStarted);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("err-line", result.Stderr);
            Assert.Contains("err-line", liveWriter.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CapsCapturedBufferAt1MiB_ButPassthroughIsUncapped()
    {
        var executor = new StreamingExecutor();

        // 1100 lines of 1000 'a' characters each: 1,102,200 bytes on Windows (CRLF) / 1,101,100
        // on Unix (LF) — both comfortably over the 1 MiB (1,048,576-byte) capture cap, so the
        // capture buffer must truncate while live passthrough (observed via the recording
        // writer) must not.
        var line = new string('a', 1000);
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", $"for /L %i in (1,1,1100) do @echo {line}"])
            : new ExecutionRequest("sh", ["-c", $"for i in $(seq 1 1100); do echo {line}; done"]);

        var liveWriter = new RecordingTextWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(liveWriter);
            var result = await executor.ExecuteAsync(request);

            Assert.True(result.WasStarted);
            Assert.Equal(0, result.ExitCode);

            // Captured buffer is truncated at exactly the cap (content is pure ASCII, so char
            // count == byte count).
            Assert.Equal(StreamingExecutor.CaptureCapBytes, result.Stdout.Length);

            // Live passthrough saw the full, uncapped output — well beyond the capture cap.
            Assert.True(
                liveWriter.ToString().Length > StreamingExecutor.CaptureCapBytes,
                "live passthrough should not be truncated by the capture cap"
            );
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesSuccessExitCode()
    {
        var executor = new StreamingExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", "exit 0"])
            : new ExecutionRequest("sh", ["-c", "exit 0"]);

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesNonZeroExitCode()
    {
        var executor = new StreamingExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", "exit 37"])
            : new ExecutionRequest("sh", ["-c", "exit 37"]);

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.Equal(37, result.ExitCode);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCommandNotFoundResult()
    {
        var executor = new StreamingExecutor();

        var result = await executor.ExecuteAsync(
            new ExecutionRequest("rtksharp-definitely-missing-command", [])
        );

        Assert.False(result.WasStarted);
        Assert.Equal(127, result.ExitCode);
        Assert.NotNull(result.Failure);
    }
}
