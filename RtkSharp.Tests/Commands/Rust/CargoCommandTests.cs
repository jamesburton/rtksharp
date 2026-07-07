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

    // ===================== filter_cargo_build =====================

    [Fact]
    public void FilterCargoBuild_Success_ReportsCratesCompiled()
    {
        const string output =
            "   Compiling libc v0.2.153\n" +
            "   Compiling cfg-if v1.0.0\n" +
            "   Compiling rtk v0.5.0\n" +
            "    Finished dev [unoptimized + debuginfo] target(s) in 15.23s\n";

        var result = CargoBuildTestFilters.FilterCargoBuild(output);

        Assert.Contains("cargo build", result, StringComparison.Ordinal);
        Assert.Contains("3 crates compiled", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoBuild_Errors_ShowsErrorBlock()
    {
        const string output =
            "   Compiling rtk v0.5.0\n" +
            "error[E0308]: mismatched types\n" +
            " --> src/main.rs:10:5\n" +
            "  |\n" +
            "10|     \"hello\"\n" +
            "  |     ^^^^^^^ expected `i32`, found `&str`\n" +
            "\n" +
            "error: aborting due to 1 previous error\n";

        var result = CargoBuildTestFilters.FilterCargoBuild(output);

        Assert.Contains("1 errors", result, StringComparison.Ordinal);
        Assert.Contains("E0308", result, StringComparison.Ordinal);
        Assert.Contains("mismatched types", result, StringComparison.Ordinal);
    }

    // ===================== filter_cargo_test =====================

    [Fact]
    public void FilterCargoTest_AllPass_CompactFormat()
    {
        const string output =
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

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("cargo test: 15 passed (1 suite, 0.01s)", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiling", result, StringComparison.Ordinal);
        Assert.DoesNotContain("test utils", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoTest_Failures_ShowsFailureBlock()
    {
        const string output =
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

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("FAILURES", result, StringComparison.Ordinal);
        Assert.Contains("test_b", result, StringComparison.Ordinal);
        Assert.Contains("test result:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoTest_MultiSuiteAllPass_Aggregates()
    {
        const string output =
            "   Compiling rtk v0.5.0\n" +
            "    Finished test [unoptimized + debuginfo] target(s) in 2.53s\n" +
            "     Running unittests src/lib.rs (target/debug/deps/rtk-abc123)\n" +
            "\n" +
            "running 50 tests\n" +
            "test result: ok. 50 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.45s\n" +
            "\n" +
            "     Running unittests src/main.rs (target/debug/deps/rtk-def456)\n" +
            "\n" +
            "running 30 tests\n" +
            "test result: ok. 30 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.30s\n" +
            "\n" +
            "     Running tests/integration.rs (target/debug/deps/integration-ghi789)\n" +
            "\n" +
            "running 25 tests\n" +
            "test result: ok. 25 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.25s\n" +
            "\n" +
            "   Doc-tests rtk\n" +
            "\n" +
            "running 32 tests\n" +
            "test result: ok. 32 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.45s\n";

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("cargo test: 137 passed (4 suites, 1.45s)", result, StringComparison.Ordinal);
        Assert.DoesNotContain("running", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoTest_MultiSuiteWithFailures_DoesNotAggregate()
    {
        const string output =
            "     Running unittests src/lib.rs\n" +
            "\n" +
            "running 20 tests\n" +
            "test result: ok. 20 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.10s\n" +
            "\n" +
            "     Running unittests src/main.rs\n" +
            "\n" +
            "running 15 tests\n" +
            "test foo::test_bad ... FAILED\n" +
            "\n" +
            "failures:\n" +
            "\n" +
            "---- foo::test_bad stdout ----\n" +
            "thread panicked at 'assertion failed'\n" +
            "\n" +
            "test result: FAILED. 14 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.05s\n" +
            "\n" +
            "     Running tests/integration.rs\n" +
            "\n" +
            "running 10 tests\n" +
            "test result: ok. 10 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.02s\n";

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("FAILURES", result, StringComparison.Ordinal);
        Assert.Contains("test_bad", result, StringComparison.Ordinal);
        Assert.Contains("test result:", result, StringComparison.Ordinal);
        Assert.Contains("20 passed", result, StringComparison.Ordinal);
        Assert.Contains("14 passed", result, StringComparison.Ordinal);
        Assert.Contains("10 passed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoTest_AllSuitesZeroTests_CompactFormat()
    {
        const string output =
            "     Running unittests src/empty1.rs\n" +
            "\n" +
            "running 0 tests\n" +
            "\n" +
            "test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n" +
            "\n" +
            "     Running unittests src/empty2.rs\n" +
            "\n" +
            "running 0 tests\n" +
            "\n" +
            "test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n" +
            "\n" +
            "     Running tests/empty3.rs\n" +
            "\n" +
            "running 0 tests\n" +
            "\n" +
            "test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n";

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("cargo test: 0 passed (3 suites, 0.00s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoTest_WithIgnoredAndFiltered_ShowsCounts()
    {
        const string output =
            "     Running unittests src/lib.rs\n" +
            "\n" +
            "running 50 tests\n" +
            "test result: ok. 45 passed; 0 failed; 3 ignored; 0 measured; 2 filtered out; finished in 0.50s\n" +
            "\n" +
            "     Running tests/integration.rs\n" +
            "\n" +
            "running 20 tests\n" +
            "test result: ok. 18 passed; 0 failed; 2 ignored; 0 measured; 0 filtered out; finished in 0.20s\n";

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("cargo test: 63 passed, 5 ignored, 2 filtered out (2 suites, 0.70s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoTest_SingleSuite_UsesSingularSuiteWord()
    {
        const string output =
            "     Running unittests src/main.rs\n" +
            "\n" +
            "running 15 tests\n" +
            "test result: ok. 15 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n";

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("cargo test: 15 passed (1 suite, 0.01s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoTest_RegexFallback_ShowsRawLine()
    {
        const string output =
            "     Running unittests src/main.rs\n" +
            "\n" +
            "running 15 tests\n" +
            "test result: MALFORMED LINE WITHOUT PROPER FORMAT\n";

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("test result: MALFORMED", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoTest_CompileError_PreservesErrorHeader()
    {
        const string output =
            "   Compiling rtk v0.31.0 (/workspace/projects/rtk)\n" +
            "error[E0425]: cannot find value `missing_symbol` in this scope\n" +
            " --> tests/repro_compile_fail.rs:3:13\n" +
            "  |\n" +
            "3 |     let _ = missing_symbol;\n" +
            "  |             ^^^^^^^^^^^^^^ not found in this scope\n" +
            "\n" +
            "For more information about this error, try `rustc --explain E0425`.\n" +
            "error: could not compile `rtk` (test \"repro_compile_fail\") due to 1 previous error\n";

        var result = CargoBuildTestFilters.FilterCargoTest(output);

        Assert.Contains("cargo test: 1 errors, 0 warnings (1 crates)", result, StringComparison.Ordinal);
        Assert.Contains("error[E0425]", result, StringComparison.Ordinal);
        Assert.Contains("--> tests/repro_compile_fail.rs:3:13", result, StringComparison.Ordinal);
        Assert.False(result.StartsWith('|'));
    }

    // ===================== filter_cargo_clippy =====================

    [Fact]
    public void FilterCargoClippy_Clean_ReportsNoIssues()
    {
        const string output =
            "    Checking rtk v0.5.0\n" +
            "    Finished dev [unoptimized + debuginfo] target(s) in 1.53s\n";

        var result = CargoNonStreamingFilters.FilterCargoClippy(output);

        Assert.Contains("cargo clippy: No issues found", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoClippy_Warnings_GroupsByLintRule()
    {
        const string output =
            "    Checking rtk v0.5.0\n" +
            "warning: unused variable: `x` [unused_variables]\n" +
            " --> src/main.rs:10:9\n" +
            "  |\n" +
            "10|     let x = 5;\n" +
            "  |         ^ help: if this is intentional, prefix it with an underscore: `_x`\n" +
            "\n" +
            "warning: this function has too many arguments [clippy::too_many_arguments]\n" +
            " --> src/git.rs:16:1\n" +
            "  |\n" +
            "16| pub fn run(a: i32, b: i32, c: i32, d: i32, e: i32, f: i32, g: i32, h: i32) {}\n" +
            "  |\n" +
            "\n" +
            "warning: `rtk` (bin) generated 2 warnings\n" +
            "    Finished dev [unoptimized + debuginfo] target(s) in 1.53s\n";

        var result = CargoNonStreamingFilters.FilterCargoClippy(output);

        Assert.Contains("0 errors, 2 warnings", result, StringComparison.Ordinal);
        Assert.Contains("unused_variables", result, StringComparison.Ordinal);
        Assert.Contains("clippy::too_many_arguments", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoClippy_IncludesErrorDetails()
    {
        const string output =
            "    Checking rtk v0.5.0\n" +
            "error: struct literals are not allowed here\n" +
            "warning: unused variable: `x` [unused_variables]\n" +
            "    Finished dev [unoptimized + debuginfo] target(s) in 1.53s\n";

        var result = CargoNonStreamingFilters.FilterCargoClippy(output);

        Assert.Contains("cargo clippy: 1 errors, 1 warnings", result, StringComparison.Ordinal);
        Assert.Contains("Errors:", result, StringComparison.Ordinal);
        Assert.Contains("struct literals are not allowed here", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoClippy_ShowsFullErrorBlock()
    {
        const string output =
            "    Checking rtk v0.5.0\n" +
            "error[E0308]: mismatched types\n" +
            " --> src/main.rs:10:5\n" +
            "  |\n" +
            "9 |     fn foo() -> i32 {\n" +
            "  |                 --- expected `i32` because of return type\n" +
            "10|     \"hello\"\n" +
            "  |     ^^^^^^^ expected `i32`, found `&str`\n" +
            "\n" +
            "error: aborting due to 1 previous error\n";

        var result = CargoNonStreamingFilters.FilterCargoClippy(output);

        Assert.Contains("cargo clippy: 1 errors, 0 warnings", result, StringComparison.Ordinal);
        Assert.Contains("error[E0308]: mismatched types", result, StringComparison.Ordinal);
        Assert.Contains("src/main.rs:10:5", result, StringComparison.Ordinal);
        Assert.Contains("expected `i32`, found `&str`", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoClippy_MultipleErrors_ShowsAllBlocks()
    {
        const string output =
            "error[E0308]: mismatched types\n" +
            " --> src/foo.rs:5:3\n" +
            "\n" +
            "error[E0425]: cannot find value `x`\n" +
            " --> src/bar.rs:12:9\n" +
            "\n" +
            "error: aborting due to 2 previous errors\n";

        var result = CargoNonStreamingFilters.FilterCargoClippy(output);

        Assert.Contains("2 errors", result, StringComparison.Ordinal);
        Assert.Contains("src/foo.rs:5:3", result, StringComparison.Ordinal);
        Assert.Contains("src/bar.rs:12:9", result, StringComparison.Ordinal);
    }

    // ===================== filter_cargo_install =====================

    [Fact]
    public void FilterCargoInstall_Success_ShowsCrateAndDeps()
    {
        const string output =
            "  Installing rtk v0.11.0\n" +
            "  Downloading crates ...\n" +
            "  Downloaded anyhow v1.0.80\n" +
            "  Downloaded clap v4.5.0\n" +
            "   Compiling libc v0.2.153\n" +
            "   Compiling cfg-if v1.0.0\n" +
            "   Compiling anyhow v1.0.80\n" +
            "   Compiling clap v4.5.0\n" +
            "   Compiling rtk v0.11.0\n" +
            "    Finished `release` profile [optimized] target(s) in 45.23s\n" +
            "  Replacing /Users/user/.cargo/bin/rtk\n" +
            "   Replaced package `rtk v0.9.4` with `rtk v0.11.0` (/Users/user/.cargo/bin/rtk)\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("cargo install", result, StringComparison.Ordinal);
        Assert.Contains("rtk v0.11.0", result, StringComparison.Ordinal);
        Assert.Contains("5 deps compiled", result, StringComparison.Ordinal);
        Assert.Contains("Replaced", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiling", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_Replace_ShowsReplacingAndReplaced()
    {
        const string output =
            "  Installing rtk v0.11.0\n" +
            "   Compiling rtk v0.11.0\n" +
            "    Finished `release` profile [optimized] target(s) in 10.0s\n" +
            "  Replacing /Users/user/.cargo/bin/rtk\n" +
            "   Replaced package `rtk v0.9.4` with `rtk v0.11.0` (/Users/user/.cargo/bin/rtk)\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("cargo install", result, StringComparison.Ordinal);
        Assert.Contains("Replacing", result, StringComparison.Ordinal);
        Assert.Contains("Replaced", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_Error_ShowsErrorAndStripsAborting()
    {
        const string output =
            "  Installing rtk v0.11.0\n" +
            "   Compiling rtk v0.11.0\n" +
            "error[E0308]: mismatched types\n" +
            " --> src/main.rs:10:5\n" +
            "  |\n" +
            "10|     \"hello\"\n" +
            "  |     ^^^^^^^ expected `i32`, found `&str`\n" +
            "\n" +
            "error: aborting due to 1 previous error\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("cargo install: 1 error", result, StringComparison.Ordinal);
        Assert.Contains("E0308", result, StringComparison.Ordinal);
        Assert.Contains("mismatched types", result, StringComparison.Ordinal);
        Assert.DoesNotContain("aborting", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_AlreadyInstalled_ReportsCrateVersion()
    {
        const string output = "  Ignored package `rtk v0.11.0`, is already installed\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("already installed", result, StringComparison.Ordinal);
        Assert.Contains("rtk v0.11.0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_UpToDate_ReportsPathBasedCrate()
    {
        const string output = "  Ignored package `cargo-deb v2.1.0 (/Users/user/cargo-deb)`, is already installed\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("already installed", result, StringComparison.Ordinal);
        Assert.Contains("cargo-deb v2.1.0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_EmptyOutput_ReportsZeroDeps()
    {
        var result = CargoNonStreamingFilters.FilterCargoInstall("");

        Assert.Contains("cargo install", result, StringComparison.Ordinal);
        Assert.Contains("0 deps compiled", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_PathWarning_IsKept()
    {
        const string output =
            "  Installing rtk v0.11.0\n" +
            "   Compiling rtk v0.11.0\n" +
            "    Finished `release` profile [optimized] target(s) in 10.0s\n" +
            "  Replacing /Users/user/.cargo/bin/rtk\n" +
            "   Replaced package `rtk v0.9.4` with `rtk v0.11.0` (/Users/user/.cargo/bin/rtk)\n" +
            "warning: be sure to add `/Users/user/.cargo/bin` to your PATH\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("cargo install", result, StringComparison.Ordinal);
        Assert.Contains("be sure to add", result, StringComparison.Ordinal);
        Assert.Contains("Replaced", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_MultipleErrors_ShowsBoth()
    {
        const string output =
            "  Installing rtk v0.11.0\n" +
            "   Compiling rtk v0.11.0\n" +
            "error[E0308]: mismatched types\n" +
            " --> src/main.rs:10:5\n" +
            "  |\n" +
            "10|     \"hello\"\n" +
            "  |     ^^^^^^^ expected `i32`, found `&str`\n" +
            "\n" +
            "error[E0425]: cannot find value `foo`\n" +
            " --> src/lib.rs:20:9\n" +
            "  |\n" +
            "20|     foo\n" +
            "  |     ^^^ not found in this scope\n" +
            "\n" +
            "error: aborting due to 2 previous errors\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("2 errors", result, StringComparison.Ordinal);
        Assert.Contains("E0308", result, StringComparison.Ordinal);
        Assert.Contains("E0425", result, StringComparison.Ordinal);
        Assert.DoesNotContain("aborting", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_LockingAndBlocking_AreStripped()
    {
        const string output =
            "  Locking 45 packages to latest compatible versions\n" +
            "  Blocking waiting for file lock on package cache\n" +
            "  Downloading crates ...\n" +
            "  Downloaded serde v1.0.200\n" +
            "   Compiling serde v1.0.200\n" +
            "   Compiling rtk v0.11.0\n" +
            "    Finished `release` profile [optimized] target(s) in 30.0s\n" +
            "  Installing rtk v0.11.0\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("cargo install", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Locking", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Blocking", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoInstall_FromPath_DoesNotExtractCrateInfo()
    {
        const string output =
            "  Installing /Users/user/projects/rtk\n" +
            "   Compiling rtk v0.11.0\n" +
            "    Finished `release` profile [optimized] target(s) in 10.0s\n";

        var result = CargoNonStreamingFilters.FilterCargoInstall(output);

        Assert.Contains("cargo install", result, StringComparison.Ordinal);
        Assert.Contains("1 deps compiled", result, StringComparison.Ordinal);
    }

    // ===================== format_crate_info =====================

    [Fact]
    public void FormatCrateInfo_NameAndVersion_JoinsBoth()
    {
        Assert.Equal("rtk v0.11.0", CargoNonStreamingFilters.FormatCrateInfo("rtk", "v0.11.0", ""));
    }

    [Fact]
    public void FormatCrateInfo_NameOnly_ReturnsName()
    {
        Assert.Equal("rtk", CargoNonStreamingFilters.FormatCrateInfo("rtk", "", ""));
    }

    [Fact]
    public void FormatCrateInfo_NoName_ReturnsFallback()
    {
        Assert.Equal("package", CargoNonStreamingFilters.FormatCrateInfo("", "", "package"));
    }

    [Fact]
    public void FormatCrateInfo_NoNameWithVersion_ReturnsFallbackNotVersion()
    {
        Assert.Equal("fallback", CargoNonStreamingFilters.FormatCrateInfo("", "v0.1.0", "fallback"));
    }

    // ===================== filter_cargo_nextest =====================

    [Fact]
    public void FilterCargoNextest_AllPass_CompactSingleLine()
    {
        const string output =
            "   Compiling rtk v0.15.2\n" +
            "    Finished `test` profile [unoptimized + debuginfo] target(s) in 0.04s\n" +
            "────────────────────────────\n" +
            "    Starting 301 tests across 1 binary\n" +
            "        PASS [   0.009s] (1/301) rtk::bin/rtk cargo_cmd::tests::test_one\n" +
            "        PASS [   0.008s] (2/301) rtk::bin/rtk cargo_cmd::tests::test_two\n" +
            "        PASS [   0.007s] (301/301) rtk::bin/rtk cargo_cmd::tests::test_last\n" +
            "────────────────────────────\n" +
            "     Summary [   0.192s] 301 tests run: 301 passed, 0 skipped\n";

        var result = CargoNonStreamingFilters.FilterCargoNextest(output);

        Assert.Equal("cargo nextest: 301 passed (1 binary, 0.192s)", result);
    }

    [Fact]
    public void FilterCargoNextest_WithFailures_ShowsFailureDetailsAndSummary()
    {
        const string output =
            "    Starting 4 tests across 1 binary (1 test skipped)\n" +
            "        PASS [   0.006s] (1/4) test-proj tests::passing_test\n" +
            "        FAIL [   0.006s] (2/4) test-proj tests::failing_test\n" +
            "\n" +
            "  stderr ───\n" +
            "\n" +
            "    thread 'tests::failing_test' panicked at src/lib.rs:15:9:\n" +
            "    assertion `left == right` failed\n" +
            "      left: 1\n" +
            "     right: 2\n" +
            "\n" +
            "  Cancelling due to test failure: 2 tests still running\n" +
            "        PASS [   0.007s] (3/4) test-proj tests::another_passing\n" +
            "        FAIL [   0.006s] (4/4) test-proj tests::another_failing\n" +
            "\n" +
            "  stderr ───\n" +
            "\n" +
            "    thread 'tests::another_failing' panicked at src/lib.rs:20:9:\n" +
            "    something went wrong\n" +
            "\n" +
            "────────────────────────────\n" +
            "     Summary [   0.007s] 4 tests run: 2 passed, 2 failed, 1 skipped\n" +
            "        FAIL [   0.006s] (2/4) test-proj tests::failing_test\n" +
            "        FAIL [   0.006s] (4/4) test-proj tests::another_failing\n" +
            "error: test run failed\n";

        var result = CargoNonStreamingFilters.FilterCargoNextest(output);

        Assert.Contains("tests::failing_test", result, StringComparison.Ordinal);
        Assert.Contains("tests::another_failing", result, StringComparison.Ordinal);
        Assert.Contains("panicked", result, StringComparison.Ordinal);
        Assert.Contains("2 passed, 2 failed, 1 skipped", result, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", result, StringComparison.Ordinal);

        // Post-summary FAIL recaps must not create duplicate FAIL header entries.
        Assert.Equal(2, CountOccurrences(result, "FAIL ["));
        Assert.DoesNotContain("error: test run failed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoNextest_WithSkipped_ShowsSkippedCount()
    {
        const string output =
            "    Starting 50 tests across 2 binaries (3 tests skipped)\n" +
            "        PASS [   0.010s] (1/50) rtk::bin/rtk test_one\n" +
            "        PASS [   0.010s] (50/50) rtk::bin/rtk test_last\n" +
            "────────────────────────────\n" +
            "     Summary [   0.500s] 50 tests run: 50 passed, 3 skipped\n";

        var result = CargoNonStreamingFilters.FilterCargoNextest(output);

        Assert.Equal("cargo nextest: 50 passed, 3 skipped (2 binaries, 0.500s)", result);
    }

    [Fact]
    public void FilterCargoNextest_SingleFailure_ShowsDetailAndNoDuplicateHeader()
    {
        const string output =
            "    Starting 2 tests across 1 binary\n" +
            "        PASS [   0.005s] (1/2) proj tests::good\n" +
            "        FAIL [   0.005s] (2/2) proj tests::bad\n" +
            "\n" +
            "  stderr ───\n" +
            "\n" +
            "    thread 'tests::bad' panicked at src/lib.rs:5:9:\n" +
            "    assertion failed: false\n" +
            "\n" +
            "────────────────────────────\n" +
            "     Summary [   0.010s] 2 tests run: 1 passed, 1 failed\n" +
            "        FAIL [   0.005s] (2/2) proj tests::bad\n" +
            "error: test run failed\n";

        var result = CargoNonStreamingFilters.FilterCargoNextest(output);

        Assert.Contains("assertion failed: false", result, StringComparison.Ordinal);
        Assert.Contains("1 passed, 1 failed", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "FAIL ["));
    }

    [Fact]
    public void FilterCargoNextest_MultipleBinaries_ShowsBinaryCount()
    {
        const string output =
            "    Starting 100 tests across 5 binaries\n" +
            "        PASS [   0.010s] (100/100) test_last\n" +
            "────────────────────────────\n" +
            "     Summary [   1.234s] 100 tests run: 100 passed, 0 skipped\n";

        var result = CargoNonStreamingFilters.FilterCargoNextest(output);

        Assert.Equal("cargo nextest: 100 passed (5 binaries, 1.234s)", result);
    }

    [Fact]
    public void FilterCargoNextest_CompilationLinesStripped()
    {
        const string output =
            "   Compiling serde v1.0.200\n" +
            "   Compiling rtk v0.15.2\n" +
            "   Downloading crates ...\n" +
            "    Finished `test` profile [unoptimized + debuginfo] target(s) in 5.00s\n" +
            "────────────────────────────\n" +
            "    Starting 10 tests across 1 binary\n" +
            "        PASS [   0.010s] (10/10) test_last\n" +
            "────────────────────────────\n" +
            "     Summary [   0.050s] 10 tests run: 10 passed, 0 skipped\n";

        var result = CargoNonStreamingFilters.FilterCargoNextest(output);

        Assert.DoesNotContain("Compiling", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Finished", result, StringComparison.Ordinal);
        Assert.Contains("cargo nextest: 10 passed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCargoNextest_Empty_ReturnsEmpty()
    {
        var result = CargoNonStreamingFilters.FilterCargoNextest("");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FilterCargoNextest_CancellationNotice_IsIncluded()
    {
        const string output =
            "    Starting 3 tests across 1 binary\n" +
            "        FAIL [   0.005s] (1/3) proj tests::bad\n" +
            "\n" +
            "  stderr ───\n" +
            "\n" +
            "    thread panicked at 'oops'\n" +
            "\n" +
            "  Cancelling due to test failure: 2 tests still running\n" +
            "────────────────────────────\n" +
            "     Summary [   0.010s] 3 tests run: 2 passed, 1 failed\n" +
            "        FAIL [   0.005s] (1/3) proj tests::bad\n" +
            "error: test run failed\n";

        var result = CargoNonStreamingFilters.FilterCargoNextest(output);

        Assert.Contains("Cancelling due to test failure", result, StringComparison.Ordinal);
        Assert.Contains("1 failed", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "FAIL ["));
    }

    [Fact]
    public void FilterCargoNextest_SummaryRegexFallback_ShowsRawSummary()
    {
        const string output =
            "    Starting 5 tests across 1 binary\n" +
            "        PASS [   0.005s] (5/5) test_last\n" +
            "────────────────────────────\n" +
            "     Summary MALFORMED LINE\n";

        var result = CargoNonStreamingFilters.FilterCargoNextest(output);

        Assert.Contains("Summary MALFORMED", result, StringComparison.Ordinal);
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
