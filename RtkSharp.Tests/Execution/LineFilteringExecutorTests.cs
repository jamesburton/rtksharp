using System.Text;
using RtkSharp.Core;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Execution;

/// <summary>
/// Tests for <see cref="LineFilteringExecutor"/>, the merged-channel, line-by-line streaming
/// executor backing <c>rtk err</c>/<c>rtk test</c>. Verifies it is genuinely distinct from
/// <see cref="StreamingExecutor"/>: lines (not byte chunks), a filter that decides what to echo, and
/// a soft (not hard) 10 MiB per-stream cap.
/// </summary>
public sealed class LineFilteringExecutorTests
{
    /// <summary>A test double that records every line it was fed and echoes a configurable subset.</summary>
    private sealed class RecordingFilter : IStreamFilter
    {
        public List<(string Line, bool WasStderrHint)> FedLines { get; } = [];
        public Func<string, string?> FeedLineImpl { get; set; } = line => line + "\n";
        public string FlushResult { get; set; } = string.Empty;
        public Func<int, string, string?>? OnExitImpl { get; set; }

        public string? FeedLine(string line)
        {
            FedLines.Add((line, false));
            return FeedLineImpl(line);
        }

        public string Flush() => FlushResult;

        public string? OnExit(int exitCode, string raw) => OnExitImpl?.Invoke(exitCode, raw);
    }

    private static ExecutionRequest WindowsCommand(string script) =>
        new("cmd", ["/d", "/s", "/c", script]);

    [Fact]
    public async Task ExecuteAsync_MergesStdoutAndStderr_AsCompleteLines_NotByteChunks()
    {
        var executor = new LineFilteringExecutor();
        var filter = new RecordingFilter();

        // Interleave stdout/stderr writes; the filter must observe each as one whole line, never a
        // partial chunk - this is the defining difference from StreamingExecutor's byte-chunk mode.
        var request = WindowsCommand("echo out-one & echo err-one 1>&2 & echo out-two & echo err-two 1>&2");

        var result = await executor.ExecuteAsync(request, filter);

        Assert.True(result.WasStarted);
        Assert.Equal(0, result.ExitCode);

        // cmd.exe's `echo` includes the trailing space that preceded the next `&` token, so match
        // on the trimmed line rather than requiring an exact string.
        var seenLines = filter.FedLines.Select(f => f.Line.Trim()).ToList();
        Assert.Contains("out-one", seenLines);
        Assert.Contains("err-one", seenLines);
        Assert.Contains("out-two", seenLines);
        Assert.Contains("err-two", seenLines);

        // Every fed value is a single complete line with no embedded newline - proof lines were
        // parsed out, not passed through as raw byte chunks.
        Assert.All(seenLines, l => Assert.DoesNotContain('\n', l));
    }

    [Fact]
    public async Task ExecuteAsync_OnlyEchoesLinesTheFilterReturnsNonNullFor()
    {
        var executor = new LineFilteringExecutor();
        var filter = new RecordingFilter
        {
            // Only echo lines containing "keep"; everything else is suppressed - proof this is
            // filtered passthrough, not full passthrough like StreamingExecutor.
            FeedLineImpl = line => line.Contains("keep", StringComparison.Ordinal) ? line + "\n" : null
        };

        var request = WindowsCommand("echo keep-this & echo drop-this");

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            var result = await executor.ExecuteAsync(request, filter);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("keep-this", capture.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("drop-this", capture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RawCaptureContainsEverything_EvenSuppressedLines()
    {
        var executor = new LineFilteringExecutor();
        var filter = new RecordingFilter { FeedLineImpl = _ => null };

        var request = WindowsCommand("echo suppressed-line");
        var result = await executor.ExecuteAsync(request, filter);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("suppressed-line", result.Raw);
        Assert.Equal(string.Empty, result.Filtered);
    }

    [Fact]
    public async Task ExecuteAsync_CallsFlushAfterStreamEnds_BeforeExitCodeKnown()
    {
        var executor = new LineFilteringExecutor();
        var filter = new RecordingFilter
        {
            FeedLineImpl = _ => null,
            FlushResult = "FLUSHED-TAIL\n"
        };

        var request = WindowsCommand("echo line-one");
        var result = await executor.ExecuteAsync(request, filter);

        Assert.Contains("FLUSHED-TAIL", result.Filtered);
    }

    [Fact]
    public async Task ExecuteAsync_CallsOnExitWithExitCodeAndFullRaw()
    {
        var executor = new LineFilteringExecutor();
        string? capturedRaw = null;
        var capturedExit = -999;

        var filter = new RecordingFilter
        {
            FeedLineImpl = _ => null,
            OnExitImpl = (exitCode, raw) =>
            {
                capturedExit = exitCode;
                capturedRaw = raw;
                return "ON-EXIT-MARKER\n";
            }
        };

        var request = WindowsCommand("echo body-line & exit 5");
        var result = await executor.ExecuteAsync(request, filter);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal(5, capturedExit);
        Assert.Contains("body-line", capturedRaw);
        Assert.Contains("ON-EXIT-MARKER", result.Filtered);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesNonZeroExitCode()
    {
        var executor = new LineFilteringExecutor();
        var filter = new RecordingFilter { FeedLineImpl = _ => null };

        var result = await executor.ExecuteAsync(WindowsCommand("exit 42"), filter);

        Assert.True(result.WasStarted);
        Assert.Equal(42, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_CommandNotFound_ReturnsNotStarted()
    {
        var executor = new LineFilteringExecutor();
        var filter = new RecordingFilter { FeedLineImpl = _ => null };

        var result = await executor.ExecuteAsync(
            new ExecutionRequest("rtksharp-definitely-missing-command-xyz", []), filter);

        Assert.False(result.WasStarted);
        Assert.Equal(127, result.ExitCode);
        Assert.NotNull(result.Failure);
    }

    // -----------------------------------------------------------------------
    // 10 MiB (overridable) soft-warning cap: one-time warning, not silent truncation, not a hard bail.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_StdoutExceedingCap_PrintsExactWarning_ProcessStillCompletesSuccessfully()
    {
        // Small override cap so the test doesn't need to generate real 10 MiB of output.
        var executor = new LineFilteringExecutorTestAccessor(rawCapOverride: 50);
        var filter = new RecordingFilter { FeedLineImpl = line => line + "\n" };

        // Each line is ~20 bytes; several lines will exceed a 50-byte cap.
        var request = WindowsCommand(
            "echo aaaaaaaaaaaaaaaaaa & echo bbbbbbbbbbbbbbbbbb & echo cccccccccccccccccc & echo dddddddddddddddddd");

        var originalErr = Console.Error;
        var errCapture = new StringWriter();
        try
        {
            Console.SetError(errCapture);
            var result = await executor.ExecuteAsync(request, filter);

            // Not a hard bail: the command still completes successfully.
            Assert.True(result.WasStarted);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.Contains(
            "[rtk] warning: stdout exceeds 10 MiB — filter input truncated",
            errCapture.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_StdoutCap_WarnsOnlyOnce_NotPerLine()
    {
        var executor = new LineFilteringExecutorTestAccessor(rawCapOverride: 20);
        var filter = new RecordingFilter { FeedLineImpl = line => line + "\n" };

        var request = WindowsCommand(
            "echo aaaaaaaaaaaaaaaaaa & echo bbbbbbbbbbbbbbbbbb & echo cccccccccccccccccc & echo dddddddddddddddddd & echo eeeeeeeeeeeeeeeeee");

        var originalErr = Console.Error;
        var errCapture = new StringWriter();
        try
        {
            Console.SetError(errCapture);
            await executor.ExecuteAsync(request, filter);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var occurrences = CountOccurrences(errCapture.ToString(), "filter input truncated");
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task ExecuteAsync_StderrExceedingCap_PrintsExactWarning_Silently_NoStdoutWarningConfusion()
    {
        var executor = new LineFilteringExecutorTestAccessor(rawCapOverride: 20);
        var filter = new RecordingFilter { FeedLineImpl = line => line + "\n" };

        var request = WindowsCommand(
            "echo aaaaaaaaaaaaaaaaaa 1>&2 & echo bbbbbbbbbbbbbbbbbb 1>&2 & echo cccccccccccccccccc 1>&2");

        var originalErr = Console.Error;
        var errCapture = new StringWriter();
        try
        {
            Console.SetError(errCapture);
            var result = await executor.ExecuteAsync(request, filter);
            Assert.True(result.WasStarted);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.Contains(
            "[rtk] warning: stderr exceeds 10 MiB — capture truncated",
            errCapture.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_CapHit_StopsAccumulatingRawCapture_ButFilterStillSeesLaterLines()
    {
        var executor = new LineFilteringExecutorTestAccessor(rawCapOverride: 20);
        var filter = new RecordingFilter { FeedLineImpl = line => line + "\n" };

        var request = WindowsCommand(
            "echo aaaaaaaaaaaaaaaaaa & echo bbbbbbbbbbbbbbbbbb & echo cccccccccccccccccc & echo final-marker-line");

        var originalErr = Console.Error;
        var errCapture = new StringWriter();
        try
        {
            Console.SetError(errCapture);
            var result = await executor.ExecuteAsync(request, filter);

            // The filter (and hence live-echoed "Filtered" text) still sees every line, even after
            // the raw-capture cap was hit - only the raw accumulation for that stream stops.
            Assert.Contains("final-marker-line", result.Filtered);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Exposes <see cref="LineFilteringExecutor"/>'s internal cap-override constructor to tests.</summary>
    private sealed class LineFilteringExecutorTestAccessor(int rawCapOverride) : ILineFilteringExecutor
    {
        private readonly LineFilteringExecutor _inner = new(rawCapOverride);

        public ValueTask<LineFilteringResult> ExecuteAsync(
            ExecutionRequest request,
            IStreamFilter filter,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteAsync(request, filter, cancellationToken);
    }
}
