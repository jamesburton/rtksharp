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
}
