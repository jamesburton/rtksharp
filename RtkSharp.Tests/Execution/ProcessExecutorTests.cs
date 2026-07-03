using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Execution;

public class ProcessExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_CapturesStdoutAndExitCode()
    {
        var executor = new ProcessExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", "echo rtksharp"])
            : new ExecutionRequest("sh", ["-c", "printf rtksharp"]);

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.False(result.TimedOut);
        Assert.Null(result.Failure);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("rtksharp", result.Stdout.Trim());
    }

    [Fact]
    public async Task ExecuteAsync_PreservesNonZeroExitCode()
    {
        var executor = new ProcessExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", "exit 37"])
            : new ExecutionRequest("sh", ["-c", "exit 37"]);

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.Equal(37, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCommandNotFoundResult()
    {
        var executor = new ProcessExecutor();

        var result = await executor.ExecuteAsync(
            new ExecutionRequest("rtksharp-definitely-missing-command", [])
        );

        Assert.False(result.WasStarted);
        Assert.Equal(127, result.ExitCode);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsTimeout()
    {
        var executor = new ProcessExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest(
                "cmd",
                ["/d", "/s", "/c", "ping -n 6 127.0.0.1 > nul"],
                Timeout: TimeSpan.FromMilliseconds(250)
            )
            : new ExecutionRequest(
                "sh",
                ["-c", "sleep 5"],
                Timeout: TimeSpan.FromMilliseconds(250)
            );

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.NotNull(result.Failure);
    }

    [WindowsOnlyFact]
    public async Task ExecuteAsync_ResolvesCmdWrapperFromRequestPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rtk_executor_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var scriptPath = Path.Combine(tempDir, "rtk-wrapper.cmd");
            await File.WriteAllTextAsync(scriptPath, "@echo off\r\necho wrapper-ok\r\nexit /b 23\r\n");

            var executor = new ProcessExecutor();
            var result = await executor.ExecuteAsync(
                new ExecutionRequest(
                    "rtk-wrapper",
                    [],
                    Environment: new Dictionary<string, string?>
                    {
                        ["PATH"] = tempDir,
                        ["PATHEXT"] = ".EXE;.CMD"
                    }
                )
            );

            Assert.True(result.WasStarted);
            Assert.Equal(23, result.ExitCode);
            Assert.Equal("wrapper-ok", result.Stdout.Trim());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MergedMode_InterleavesStdoutAndStderrInOrder()
    {
        var executor = new ProcessExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest(
                "cmd",
                ["/d", "/s", "/c", "echo out1 & echo err1 1>&2 & echo out2"],
                CaptureMode: ExecutionCaptureMode.Merged
            )
            : new ExecutionRequest(
                "sh",
                ["-c", "echo out1; echo err1 1>&2; echo out2"],
                CaptureMode: ExecutionCaptureMode.Merged
            );

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out1", result.Stdout);
        Assert.Contains("err1", result.Stdout);
        Assert.Contains("out2", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task ExecuteAsync_MergedMode_PreservesExitCodeAndFailureOnNotFound()
    {
        var executor = new ProcessExecutor();

        var result = await executor.ExecuteAsync(
            new ExecutionRequest(
                "rtksharp-definitely-missing-command",
                [],
                CaptureMode: ExecutionCaptureMode.Merged
            )
        );

        Assert.False(result.WasStarted);
        Assert.Equal(127, result.ExitCode);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task ExecuteAsync_MergedMode_HandlesLargeOutputWithoutTruncation()
    {
        var executor = new ProcessExecutor();
        const int lineCount = 5000; // each line ~15 bytes => well over 64KB total
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest(
                "cmd",
                ["/d", "/s", "/c", $"for /L %i in (1,1,{lineCount}) do @echo line-number-%i"],
                CaptureMode: ExecutionCaptureMode.Merged
            )
            : new ExecutionRequest(
                "sh",
                ["-c", $"for i in $(seq 1 {lineCount}); do echo line-number-$i; done"],
                CaptureMode: ExecutionCaptureMode.Merged
            );

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(lineCount, lines.Length);
        Assert.Contains("line-number-1", result.Stdout);
        Assert.Contains($"line-number-{lineCount}", result.Stdout);
    }

    [Fact]
    public async Task ExecuteAsync_StdinContent_IsPipedToChildAndClosed()
    {
        var executor = new ProcessExecutor();

        // findstr/grep read from stdin when given no file operand, so the child only produces
        // matching output if StdinContent was actually written and the stream then closed
        // (both tools block waiting for more input, or EOF, before exiting).
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("findstr", ["needle"], StdinContent: "haystack\nneedle-line\nhaystack2\n")
            : new ExecutionRequest("grep", ["needle"], StdinContent: "haystack\nneedle-line\nhaystack2\n");

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("needle-line", result.Stdout);
        Assert.DoesNotContain("haystack2", result.Stdout);
    }

    [Fact]
    public async Task ExecuteAsync_StdinContent_Empty_StillClosesStreamSoChildExits()
    {
        var executor = new ProcessExecutor();

        // With an empty StdinContent, findstr/grep should see immediate EOF (no match found)
        // rather than hang waiting for input — proving the stream is closed even when nothing
        // is written.
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("findstr", ["needle"], StdinContent: "")
            : new ExecutionRequest("grep", ["needle"], StdinContent: "");

        var result = await executor.ExecuteAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.WasStarted);
        Assert.NotEqual(0, result.ExitCode); // no match found
        Assert.Equal("", result.Stdout.Trim());
    }

    [Fact]
    public async Task ExecuteAsync_NoStdinContent_DoesNotRedirectStandardInput()
    {
        var executor = new ProcessExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", "echo no-stdin-needed"])
            : new ExecutionRequest("sh", ["-c", "echo no-stdin-needed"]);

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.WasStarted);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("no-stdin-needed", result.Stdout.Trim());
    }

    [Fact]
    public async Task ExecuteAsync_SeparateMode_StillKeepsStdoutAndStderrApart()
    {
        var executor = new ProcessExecutor();
        var request = OperatingSystem.IsWindows()
            ? new ExecutionRequest("cmd", ["/d", "/s", "/c", "echo onlyout"])
            : new ExecutionRequest("sh", ["-c", "printf onlyout"]);

        var result = await executor.ExecuteAsync(request);

        Assert.Equal("onlyout", result.Stdout.Trim());
        Assert.Equal("", result.Stderr);
    }
}
