using RtkSharp;
using Xunit;

namespace RtkSharp.Tests;

public class ProgramTests
{
    [Fact]
    public async Task RunAsync_VersionFlag_PrintsVersionAndReturnsZero()
    {
        int exitCode = await RtkProgram.RunAsync(new[] { "--version" });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_NoArgs_PrintsHelpAndReturnsZero()
    {
        int exitCode = await RtkProgram.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_UnknownCommand_PassesThroughWithExitCode()
    {
        var isWindows = OperatingSystem.IsWindows();
        string[] args = isWindows
            ? new[] { "cmd", "/c", "exit", "3" }
            : new[] { "sh", "-c", "exit 3" };

        int exitCode = await RtkProgram.RunAsync(args);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task RunAsync_CommandNotFound_ReturnsExitCode127()
    {
        int exitCode = await RtkProgram.RunAsync(new[] { "rtksharp-definitely-not-a-real-command-xyz" });

        Assert.Equal(127, exitCode);
    }
}
