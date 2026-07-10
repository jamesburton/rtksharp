using RtkSharp.Commands.Jvm;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Jvm;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/jvm/mvn_cmd.rs</c>'s phase-detection and quiet-mode-detection
/// <c>#[cfg(test)]</c> tests, plus new dispatch-level coverage for the <c>rtk mvn</c> entry point that
/// Rust's own oracle doesn't expose as pure functions.
/// </summary>
public sealed class MvnCommandTests
{
    // ── Phase detection ──────────────────────────────────────────────────────

    [Fact]
    public void Phase_Test() => Assert.Equal(MvnPhase.Test, MvnCommand.DetectPhase(["test"]));

    [Fact]
    public void Phase_IntegrationTest() => Assert.Equal(MvnPhase.Test, MvnCommand.DetectPhase(["integration-test"]));

    [Fact]
    public void Phase_Compile() => Assert.Equal(MvnPhase.Compile, MvnCommand.DetectPhase(["compile"]));

    [Fact]
    public void Phase_TestCompile() => Assert.Equal(MvnPhase.Compile, MvnCommand.DetectPhase(["test-compile"]));

    [Fact]
    public void Phase_Install() => Assert.Equal(MvnPhase.Package, MvnCommand.DetectPhase(["install"]));

    [Fact]
    public void Phase_Package() => Assert.Equal(MvnPhase.Package, MvnCommand.DetectPhase(["package"]));

    [Fact]
    public void Phase_Verify() => Assert.Equal(MvnPhase.Package, MvnCommand.DetectPhase(["verify"]));

    [Fact]
    public void Phase_Deploy() => Assert.Equal(MvnPhase.Package, MvnCommand.DetectPhase(["deploy"]));

    [Fact]
    public void Phase_CleanInstall_IsPackage() => Assert.Equal(MvnPhase.Package, MvnCommand.DetectPhase(["clean", "install"]));

    [Fact]
    public void Phase_FlagsBeforeGoal() => Assert.Equal(MvnPhase.Test, MvnCommand.DetectPhase(["-B", "-DskipTests", "test"]));

    [Fact]
    public void Phase_CleanOnly_Passthrough() => Assert.Equal(MvnPhase.Passthrough, MvnCommand.DetectPhase(["clean"]));

    [Fact]
    public void Phase_Site_Passthrough() => Assert.Equal(MvnPhase.Passthrough, MvnCommand.DetectPhase(["site"]));

    [Fact]
    public void Phase_PluginGoal_Passthrough() => Assert.Equal(MvnPhase.Passthrough, MvnCommand.DetectPhase(["dependency:tree"]));

    [Fact]
    public void Phase_Empty_Passthrough() => Assert.Equal(MvnPhase.Passthrough, MvnCommand.DetectPhase(Array.Empty<string>()));

    [Fact]
    public void Phase_VersionLong() => Assert.Equal(MvnPhase.Passthrough, MvnCommand.DetectPhase(["--version"]));

    [Fact]
    public void Phase_VersionShort() => Assert.Equal(MvnPhase.Passthrough, MvnCommand.DetectPhase(["-v"]));

    [Fact]
    public void Phase_VersionJavaStyle() => Assert.Equal(MvnPhase.Passthrough, MvnCommand.DetectPhase(["-version"]));

    [Fact]
    public void Phase_Help() => Assert.Equal(MvnPhase.Passthrough, MvnCommand.DetectPhase(["--help"]));

    // ── Quiet-mode detection ─────────────────────────────────────────────────

    [Fact]
    public void Quiet_DetectsShortFlag()
    {
        Assert.True(MvnCommand.IsQuiet(["-q", "test"]));
        Assert.True(MvnCommand.IsQuiet(["test", "-q"]));
        Assert.True(MvnCommand.IsQuiet(["-B", "-q", "-DskipFoo", "install"]));
    }

    [Fact]
    public void Quiet_DetectsLongFlag() => Assert.True(MvnCommand.IsQuiet(["--quiet", "test"]));

    [Fact]
    public void Quiet_DoesNotMatchUnrelatedFlags()
    {
        Assert.False(MvnCommand.IsQuiet(["-Q", "test"]));
        Assert.False(MvnCommand.IsQuiet(["-quiet", "test"]));
        Assert.False(MvnCommand.IsQuiet(["-B", "test"]));
    }

    // ── Dispatch-level coverage (new; no direct Rust #[cfg(test)] analog) ────
    //
    // Only the passthrough path is exercised here with a fake executor: CommandRunner.RunFilteredAsync
    // (used by the Test/Compile/Package buffered paths) constructs its own ProcessExecutor internally
    // with no injection point — matching CargoCommand's identical buffered-path design (see
    // CargoCommand's own remarks on RunBufferedAsync). Those paths' filter logic is exercised directly
    // via RtkSharp.Filters.Tests.Commands.Jvm.MvnFiltersTests instead, mirroring how Rust's own
    // #[cfg(test)] suite never drives filter_surefire/filter_compile/filter_package through a live
    // process either.

    [Fact]
    public async Task RunAsync_Passthrough_UsesInheritCaptureMode()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await MvnCommand.RunAsync(["clean"], fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.CapturedRequest!.CaptureMode);
        Assert.Equal(["clean"], fake.CapturedRequest.Arguments);
    }

    [Fact]
    public async Task RunAsync_DebugFlag_ForcesPassthrough()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await MvnCommand.RunAsync(["test", "-X"], fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.CapturedRequest!.CaptureMode);
        Assert.Equal(["test", "-X"], fake.CapturedRequest.Arguments);
    }

    [Fact]
    public async Task RunAsync_ExitCodePropagatesFromExecutor_OnPassthrough()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 3, TimeSpan.Zero, true, null, false));

        var exitCode = await MvnCommand.RunAsync(["clean"], fake);

        Assert.Equal(3, exitCode);
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
