using RtkSharp.Core;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Execution;

public class CommandRunnerTests
{
    private sealed class RecordingTokenTracker : ITokenTracker
    {
        public int CallCount { get; private set; }
        public string? LastCommand { get; private set; }
        public int LastRawLength { get; private set; }
        public int LastFilteredLength { get; private set; }

        public void Record(string command, int rawLength, int filteredLength)
        {
            CallCount++;
            LastCommand = command;
            LastRawLength = rawLength;
            LastFilteredLength = filteredLength;
        }
    }

    private static (string FileName, string[] Arguments) EchoCommand(string text) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/d", "/s", "/c", $"echo {text}"])
            : ("sh", ["-c", $"printf '{text}'"]);

    private static (string FileName, string[] Arguments) ExitWithCode(int code) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/d", "/s", "/c", $"exit {code}"])
            : ("sh", ["-c", $"exit {code}"]);

    [Fact]
    public async Task RunFilteredAsync_AppliesFilterToCapturedOutput()
    {
        var (fileName, arguments) = EchoCommand("hello");
        var tracker = new RecordingTokenTracker();

        int exitCode = await CommandRunner.RunFilteredAsync(
            fileName,
            arguments,
            "echo",
            "hello",
            output => output.Trim().ToUpperInvariant(),
            new RunOptions(NoTrailingNewline: true),
            tokenTracker: tracker
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(1, tracker.CallCount);
        Assert.Equal("echo hello", tracker.LastCommand);
        Assert.Equal("HELLO".Length, tracker.LastFilteredLength);
    }

    [Fact]
    public async Task RunFilteredAsync_FilterThrows_FallsBackToRawOutputAndPreservesExitCode()
    {
        var (fileName, arguments) = ExitWithCode(5);
        var tracker = new RecordingTokenTracker();

        int exitCode = await CommandRunner.RunFilteredAsync(
            fileName,
            arguments,
            "boom",
            "",
            _ => throw new InvalidOperationException("filter exploded"),
            new RunOptions(NoTrailingNewline: true),
            tokenTracker: tracker
        );

        Assert.Equal(5, exitCode);
        Assert.Equal(1, tracker.CallCount);
    }

    [Fact]
    public async Task RunFilteredAsync_NonZeroExit_PropagatesExitCode()
    {
        var (fileName, arguments) = ExitWithCode(42);

        int exitCode = await CommandRunner.RunFilteredAsync(
            fileName,
            arguments,
            "exitcode",
            "",
            output => output,
            new RunOptions(NoTrailingNewline: true)
        );

        Assert.Equal(42, exitCode);
    }

    [Fact]
    public async Task RunFilteredAsync_SkipFilterOnFailure_LeavesOutputRawOnFailure()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/d", "/s", "/c", "echo raw-output & exit 9" })
            : ("sh", new[] { "-c", "printf raw-output; exit 9" });
        var tracker = new RecordingTokenTracker();
        var filterWasCalled = false;

        int exitCode = await CommandRunner.RunFilteredAsync(
            fileName,
            arguments,
            "skipfail",
            "",
            output =>
            {
                filterWasCalled = true;
                return output.ToUpperInvariant();
            },
            new RunOptions(SkipFilterOnFailure: true, NoTrailingNewline: true),
            tokenTracker: tracker
        );

        Assert.Equal(9, exitCode);
        Assert.False(filterWasCalled);
        Assert.Equal(1, tracker.CallCount);
    }

    [Fact]
    public async Task RunFilteredWithExitAsync_PassesExitCodeToFilter()
    {
        var (fileName, arguments) = ExitWithCode(7);
        int? observedExitCode = null;

        int exitCode = await CommandRunner.RunFilteredWithExitAsync(
            fileName,
            arguments,
            "exitaware",
            "",
            (output, code) =>
            {
                observedExitCode = code;
                return output;
            },
            new RunOptions(NoTrailingNewline: true)
        );

        Assert.Equal(7, exitCode);
        Assert.Equal(7, observedExitCode);
    }
}
