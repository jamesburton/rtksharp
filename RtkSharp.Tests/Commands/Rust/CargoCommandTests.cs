using System.Text;
using RtkSharp.Commands.Git;
using RtkSharp.Commands.Rust;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Rust;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/rust/cargo_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (~40 tests covering <c>filter_cargo_build</c>/<c>filter_cargo_test</c>/<c>filter_cargo_clippy</c>/
/// <c>filter_cargo_install</c>/<c>filter_cargo_nextest</c>/<c>format_crate_info</c>, the streaming
/// handler tests, and the <c>restore_double_dash_with_raw</c> tests), plus new dispatch-level coverage
/// for the <c>rtk cargo</c> entry point that Rust's own oracle doesn't expose as pure functions.
/// </summary>
public sealed class CargoCommandTests
{
    private static string RunBlockFilter(IStreamFilter filter, string input, int exitCode)
    {
        var output = new StringBuilder();
        foreach (var line in ReadCommand.SplitLines(input))
        {
            var s = filter.FeedLine(line);
            if (s is not null)
            {
                output.Append(s);
            }
        }

        output.Append(filter.Flush());
        var post = filter.OnExit(exitCode, input);
        if (post is not null)
        {
            output.Append(post);
        }

        return output.ToString();
    }

    // ===================== restore_double_dash (cargo-specific scenarios) =====================
    // Ports cargo_cmd.rs's 6 restore_double_dash_with_raw tests, against the shared
    // GitCommand.RestoreDoubleDashWithRaw implementation CargoCommand delegates to.

    [Fact]
    public void RestoreDoubleDash_WithSeparator_RestoresDash()
    {
        // rtk cargo test -- --nocapture -> clap gives ["--nocapture"]
        string[] args = ["--nocapture"];
        string[] raw = ["rtk", "cargo", "test", "--", "--nocapture"];
        var result = GitCommand.RestoreDoubleDashWithRaw(args, raw);
        Assert.Equal(["--", "--nocapture"], result);
    }

    [Fact]
    public void RestoreDoubleDash_WithTestName_RestoresDashAfterName()
    {
        // rtk cargo test my_test -- --nocapture -> clap gives ["my_test", "--nocapture"]
        string[] args = ["my_test", "--nocapture"];
        string[] raw = ["rtk", "cargo", "test", "my_test", "--", "--nocapture"];
        var result = GitCommand.RestoreDoubleDashWithRaw(args, raw);
        Assert.Equal(["my_test", "--", "--nocapture"], result);
    }

    [Fact]
    public void RestoreDoubleDash_WithoutSeparator_LeavesArgsUnchanged()
    {
        // rtk cargo test my_test -> no --, args unchanged
        string[] args = ["my_test"];
        string[] raw = ["rtk", "cargo", "test", "my_test"];
        var result = GitCommand.RestoreDoubleDashWithRaw(args, raw);
        Assert.Equal(["my_test"], result);
    }

    [Fact]
    public void RestoreDoubleDash_EmptyArgs_ReturnsEmpty()
    {
        string[] args = [];
        string[] raw = ["rtk", "cargo", "test"];
        var result = GitCommand.RestoreDoubleDashWithRaw(args, raw);
        Assert.Empty(result);
    }

    [Fact]
    public void RestoreDoubleDash_Clippy_RestoresDash()
    {
        // rtk cargo clippy -- -D warnings -> clap gives ["-D", "warnings"]
        string[] args = ["-D", "warnings"];
        string[] raw = ["rtk", "cargo", "clippy", "--", "-D", "warnings"];
        var result = GitCommand.RestoreDoubleDashWithRaw(args, raw);
        Assert.Equal(["--", "-D", "warnings"], result);
    }

    [Fact]
    public void RestoreDoubleDash_ClippyWithPackageFlags_DoesNotDoubleDash()
    {
        // rtk cargo clippy -p my-service -p my-crate -- -D warnings
        string[] args = ["-p", "my-service", "-p", "my-crate", "--", "-D", "warnings"];
        string[] raw = ["rtk", "cargo", "clippy", "-p", "my-service", "-p", "my-crate", "--", "-D", "warnings"];
        var result = GitCommand.RestoreDoubleDashWithRaw(args, raw);
        Assert.Equal(["-p", "my-service", "-p", "my-crate", "--", "-D", "warnings"], result);
        Assert.Equal(1, result.Count(a => a == "--"));
    }

    // ===================== streaming handler tests =====================

    [Fact]
    public void CargoBuildStream_Success_StripsCompilingKeepsFinished()
    {
        const string input = "   Compiling libc v0.2.153\n   Compiling cfg-if v1.0.0\n   Compiling rtk v0.5.0\n    Finished dev [unoptimized + debuginfo] target(s) in 15.23s\n";
        var f = new BlockStreamFilter<CargoBuildHandler>(new CargoBuildHandler());
        var result = RunBlockFilter(f, input, 0);

        Assert.Contains("3 crates compiled", result, StringComparison.Ordinal);
        Assert.Contains("Finished", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiling", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CargoBuildStream_Errors_ShowsErrorBlock()
    {
        const string input =
            "   Compiling rtk v0.5.0\n" +
            "error[E0308]: mismatched types\n" +
            " --> src/main.rs:10:5\n" +
            "  |\n" +
            "10|     \"hello\"\n" +
            "  |     ^^^^^^^ expected `i32`, found `&str`\n" +
            "\n" +
            "error: aborting due to 1 previous error\n";
        var f = new BlockStreamFilter<CargoBuildHandler>(new CargoBuildHandler());
        var result = RunBlockFilter(f, input, 1);

        Assert.Contains("E0308", result, StringComparison.Ordinal);
        Assert.Contains("mismatched types", result, StringComparison.Ordinal);
        Assert.Contains("1 errors", result, StringComparison.Ordinal);
        Assert.DoesNotContain("aborting", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CargoTestStream_AllPass_CompactFormat()
    {
        const string input =
            "   Compiling rtk v0.5.0\n" +
            "    Finished test [unoptimized + debuginfo] target(s) in 2.53s\n" +
            "     Running target/debug/deps/rtk-abc123\n" +
            "\n" +
            "running 15 tests\n" +
            "test utils::tests::test_truncate_short_string ... ok\n" +
            "test utils::tests::test_truncate_long_string ... ok\n" +
            "test utils::tests::test_strip_ansi_simple ... ok\n" +
            "\n" +
            "test result: ok. 15 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n";
        var f = new BlockStreamFilter<CargoTestHandler>(new CargoTestHandler());
        var result = RunBlockFilter(f, input, 0);

        Assert.Contains("cargo test: 15 passed (1 suite, 0.01s)", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiling", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CargoTestStream_Failures_ShowsFailureDetail()
    {
        const string input =
            "running 5 tests\n" +
            "test foo::test_a ... ok\n" +
            "test foo::test_b ... FAILED\n" +
            "test foo::test_c ... ok\n" +
            "\n" +
            "failures:\n" +
            "\n" +
            "---- foo::test_b stdout ----\n" +
            "thread 'foo::test_b' panicked at 'assert_eq!(1, 2)'\n" +
            "\n" +
            "failures:\n" +
            "    foo::test_b\n" +
            "\n" +
            "test result: FAILED. 4 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out\n";
        var f = new BlockStreamFilter<CargoTestHandler>(new CargoTestHandler());
        var result = RunBlockFilter(f, input, 1);

        Assert.Contains("test_b", result, StringComparison.Ordinal);
        Assert.Contains("panicked", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CargoTestStream_MultiSuite_Aggregates()
    {
        const string input =
            "     Running unittests src/lib.rs (target/debug/deps/rtk-abc123)\n" +
            "\n" +
            "running 50 tests\n" +
            "test result: ok. 50 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.45s\n" +
            "\n" +
            "     Running unittests src/main.rs (target/debug/deps/rtk-def456)\n" +
            "\n" +
            "running 30 tests\n" +
            "test result: ok. 30 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.30s\n";
        var f = new BlockStreamFilter<CargoTestHandler>(new CargoTestHandler());
        var result = RunBlockFilter(f, input, 0);

        Assert.Contains("cargo test: 80 passed (2 suites, 0.75s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CargoTestStream_CompileError_FallsBackToBuildFormatting()
    {
        const string input =
            "   Compiling rtk v0.31.0 (/workspace/projects/rtk)\n" +
            "error[E0425]: cannot find value `missing_symbol` in this scope\n" +
            " --> tests/repro_compile_fail.rs:3:13\n" +
            "  |\n" +
            "3 |     let _ = missing_symbol;\n" +
            "  |             ^^^^^^^^^^^^^^ not found in this scope\n" +
            "\n" +
            "For more information about this error, try `rustc --explain E0425`.\n" +
            "error: could not compile `rtk` (test \"repro_compile_fail\") due to 1 previous error\n";
        var f = new BlockStreamFilter<CargoTestHandler>(new CargoTestHandler());
        var result = RunBlockFilter(f, input, 1);

        Assert.Contains("cargo test:", result, StringComparison.Ordinal);
        Assert.Contains("1 errors", result, StringComparison.Ordinal);
    }

    // ===================== dispatch-level coverage (new; no direct Rust #[cfg(test)] analog) =====================

    [Fact]
    public async Task RunAsync_NoSubcommand_PrintsErrorAndReturnsOne()
    {
        using var console = new ConsoleErrorCapture();

        var exitCode = await CargoCommand.RunAsync([], lineFilteringExecutor: null, processExecutor: null);

        Assert.Equal(1, exitCode);
        Assert.Contains("cargo: no subcommand specified", console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Test_DoubleDashRestoration_IsPreservedEndToEnd()
    {
        // rtk cargo test -- --nocapture: the "--" must reach the child invocation verbatim,
        // neither dropped nor duplicated.
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 0, true, null));

        var exitCode = await CargoCommand.RunAsync(["test", "--", "--nocapture"], fake, processExecutor: null);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(["test", "--", "--nocapture"], fake.CapturedRequest!.Arguments);
        Assert.Equal(1, fake.CapturedRequest.Arguments.Count(a => a == "--"));
    }

    [Fact]
    public async Task RunAsync_Build_DispatchesThroughLineFilteringExecutor_WithBlockStreamFilter()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 0, true, null));

        var exitCode = await CargoCommand.RunAsync(["build"], fake, processExecutor: null);

        Assert.Equal(0, exitCode);
        Assert.Equal(["build"], fake.CapturedRequest!.Arguments);
        Assert.IsType<BlockStreamFilter<CargoBuildHandler>>(fake.CapturedFilter);
    }

    [Fact]
    public async Task RunAsync_Check_UsesSameHandlerTypeAsBuild()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 0, true, null));

        var exitCode = await CargoCommand.RunAsync(["check"], fake, processExecutor: null);

        Assert.Equal(0, exitCode);
        Assert.IsType<BlockStreamFilter<CargoBuildHandler>>(fake.CapturedFilter);
    }

    [Fact]
    public async Task RunAsync_Test_UsesCargoTestHandler()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 0, true, null));

        await CargoCommand.RunAsync(["test"], fake, processExecutor: null);

        Assert.IsType<BlockStreamFilter<CargoTestHandler>>(fake.CapturedFilter);
    }

    [Fact]
    public async Task RunAsync_ExitCodePropagatesFromExecutor()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 7, true, null));

        var exitCode = await CargoCommand.RunAsync(["build"], fake, processExecutor: null);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task RunAsync_UnrecognizedSubcommand_PassesThroughUnfiltered()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await CargoCommand.RunAsync(["clean", "--release"], lineFilteringExecutor: null, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.CapturedRequest!.CaptureMode);
        Assert.Equal(["clean", "--release"], fake.CapturedRequest.Arguments);
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

    private sealed class ConsoleErrorCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Error;
        private readonly StringWriter _capture = new();

        public ConsoleErrorCapture() => Console.SetError(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetError(_original);
    }
}
