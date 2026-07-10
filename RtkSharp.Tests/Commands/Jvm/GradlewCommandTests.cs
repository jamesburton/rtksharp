using RtkSharp.Commands.Jvm;
using RtkSharp.Core;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Jvm;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/jvm/gradlew_cmd.rs</c>'s task-detection <c>#[cfg(test)]</c>
/// tests, plus dispatch-level coverage for the <c>rtk gradlew</c> entry point that Rust's own oracle
/// doesn't expose as pure functions. The pure filter tests (build/test/connected/lint/dependencies)
/// live in <c>RtkSharp.Filters.Tests.Commands.Jvm.GradlewFiltersTests</c>.
/// </summary>
public sealed class GradlewCommandTests
{
    // ── TASK DETECTION ───────────────────────────────────────────────────────

    [Fact]
    public void DetectTask_ConnectedWinsOverTest()
    {
        Assert.Equal(GradlewTask.ConnectedTest, GradlewCommand.DetectTask(["connectedDebugAndroidTest"]));
    }

    [Fact]
    public void DetectTask_AssembleDebug()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["assembleDebug"]));
    }

    [Fact]
    public void DetectTask_TestDebugUnitTest()
    {
        Assert.Equal(GradlewTask.Test, GradlewCommand.DetectTask(["testDebugUnitTest"]));
    }

    [Fact]
    public void DetectTask_ModulePrefixedTask()
    {
        Assert.Equal(GradlewTask.Test, GradlewCommand.DetectTask([":app:testDebugUnitTest"]));
    }

    [Fact]
    public void DetectTask_ModulePrefixedAssemble()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask([":app:assembleDebug"]));
    }

    [Fact]
    public void DetectTask_FlagValueDoesNotTriggerTest()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["assembleRelease", "-Pflavor=testRelease"]));
    }

    [Fact]
    public void DetectTask_MultiTaskUsesLast()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["clean", "assembleDebug"]));
    }

    [Fact]
    public void DetectTask_Lint()
    {
        Assert.Equal(GradlewTask.Lint, GradlewCommand.DetectTask(["lint"]));
    }

    [Fact]
    public void DetectTask_Ktlint()
    {
        Assert.Equal(GradlewTask.Lint, GradlewCommand.DetectTask(["ktlintCheck"]));
    }

    [Fact]
    public void DetectTask_Bundle()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["bundleRelease"]));
    }

    [Fact]
    public void DetectTask_UnknownPassthrough()
    {
        Assert.Equal(GradlewTask.Other, GradlewCommand.DetectTask(["signingReport"]));
    }

    [Fact]
    public void DetectTask_CleanAloneIsBuild()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["clean"]));
    }

    [Fact]
    public void DetectTask_InstallDebug()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["installDebug"]));
    }

    [Fact]
    public void DetectTask_UninstallDebug()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["uninstallDebug"]));
    }

    [Fact]
    public void DetectTask_CleanInstall()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["clean", "installDebug"]));
    }

    [Fact]
    public void DetectTask_Check()
    {
        Assert.Equal(GradlewTask.Test, GradlewCommand.DetectTask(["check"]));
    }

    [Fact]
    public void DetectTask_Dependencies()
    {
        Assert.Equal(GradlewTask.Dependencies, GradlewCommand.DetectTask(["dependencies"]));
    }

    [Fact]
    public void DetectTask_DependenciesWithModule()
    {
        Assert.Equal(GradlewTask.Dependencies, GradlewCommand.DetectTask([":app:dependencies"]));
    }

    // ── EDGE CASES ────────────────────────────────────────────────────────────

    [Fact]
    public void VerboseFlag_Detection()
    {
        string[] stacktraceArgs = ["assembleDebug", "--stacktrace"];
        Assert.Contains(stacktraceArgs, a => a is "--stacktrace" or "--info" or "--debug" or "--full-stacktrace");

        string[] infoArgs = ["testDebugUnitTest", "--info"];
        Assert.Contains(infoArgs, a => a is "--stacktrace" or "--info" or "--debug" or "--full-stacktrace");
    }

    // ── DISPATCH-LEVEL COVERAGE (new; no direct Rust #[cfg(test)] analog) ────

    [Fact]
    public async Task RunAsync_Build_DispatchesThroughLineFilteringExecutor()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 0, true, null));

        var exitCode = await GradlewCommand.RunAsync(["assembleDebug"], fake, processExecutor: null);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(["assembleDebug"], fake.CapturedRequest!.Arguments);
        Assert.IsType<GradlewBuildLineFilter>(fake.CapturedFilter);
    }

    [Fact]
    public async Task RunAsync_ExitCodePropagatesFromExecutor()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 7, true, null));
        var exitCode = await GradlewCommand.RunAsync(["assembleDebug"], fake, processExecutor: null);
        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task RunAsync_UnknownTask_PassesThroughUnfiltered()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await GradlewCommand.RunAsync(["signingReport"], lineFilteringExecutor: null, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.CapturedRequest!.CaptureMode);
        Assert.Equal(["signingReport"], fake.CapturedRequest.Arguments);
    }

    [Fact]
    public async Task RunAsync_StacktraceFlag_ForcesPassthrough()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await GradlewCommand.RunAsync(["testDebugUnitTest", "--stacktrace"], lineFilteringExecutor: null, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(["testDebugUnitTest", "--stacktrace"], fake.CapturedRequest!.Arguments);
    }

    private sealed class FakeLineFilteringExecutor(LineFilteringResult result) : ILineFilteringExecutor
    {
        public ExecutionRequest? CapturedRequest { get; private set; }

        public IStreamFilter? CapturedFilter { get; private set; }

        public ValueTask<LineFilteringResult> ExecuteAsync(
            ExecutionRequest request,
            IStreamFilter filter,
            CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            CapturedFilter = filter;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeProcessExecutor(ExecutionResult result) : IProcessExecutor
    {
        public ExecutionRequest? CapturedRequest { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            return ValueTask.FromResult(result);
        }
    }
}
