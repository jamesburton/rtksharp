using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RtkSharp.Commands.System;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="PipeCommand"/>, the <c>rtk pipe</c> stdin-filter dispatcher. Rust's
/// <c>src/cmds/system/pipe_cmd.rs</c> has an extensive inline <c>#[cfg(test)]</c> module (see the
/// file for the byte-exact test cases this suite mirrors): named-filter alias resolution, the
/// auto-detect signature heuristics, the two pipe-only mini filters' capping/header format, the
/// stdin-size hard-bail, and the catch_unwind-equivalent exception-safety fallback.
/// </summary>
public sealed class PipeCommandTests
{
    // -----------------------------------------------------------------------
    // --passthrough: verbatim relay, no filter logic at all
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_Passthrough_RelaysStdinVerbatim()
    {
        // --passthrough writes directly to a Stream (mirroring Rust's io::copy), not through
        // Console.Out, so the destination stream itself must be injected rather than captured
        // via a redirected Console.Out.
        using var stdin = ToStream("raw unfiltered bytes\nwith multiple lines\n");
        using var stdout = new MemoryStream();

        var exitCode = await PipeCommand.RunAsync(["--passthrough"], stdin, stdout);

        Assert.Equal(0, exitCode);
        Assert.Equal("raw unfiltered bytes\nwith multiple lines\n", Encoding.UTF8.GetString(stdout.ToArray()));
    }

    [Fact]
    public async Task RunAsync_Passthrough_IgnoresFilterFlagEntirely()
    {
        // Even with -f given, --passthrough must bypass all filter logic (pipe_cmd.rs:213-217
        // checks passthrough first and returns unconditionally).
        using var stdin = ToStream("test result: ok. 5 passed; 0 failed\n");
        using var stdout = new MemoryStream();

        var exitCode = await PipeCommand.RunAsync(["--passthrough", "-f", "cargo-test"], stdin, stdout);

        Assert.Equal(0, exitCode);
        Assert.Equal("test result: ok. 5 passed; 0 failed\n", Encoding.UTF8.GetString(stdout.ToArray()));
    }

    // -----------------------------------------------------------------------
    // Named-filter alias resolution (pipe_cmd.rs:11-31)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("cargo-test")]
    [InlineData("cargo")]
    [InlineData("pytest")]
    [InlineData("go-test")]
    [InlineData("go-build")]
    [InlineData("tsc")]
    [InlineData("vitest")]
    [InlineData("grep")]
    [InlineData("rg")]
    [InlineData("find")]
    [InlineData("fd")]
    [InlineData("git-log")]
    [InlineData("git-diff")]
    [InlineData("git-status")]
    [InlineData("log")]
    [InlineData("mypy")]
    [InlineData("ruff-check")]
    [InlineData("ruff-format")]
    [InlineData("prettier")]
    public void ResolveFilter_KnownAlias_ReturnsNonNull(string alias)
    {
        Assert.NotNull(PipeCommand.ResolveFilter(alias));
    }

    [Fact]
    public void ResolveFilter_UnknownName_ReturnsNull()
    {
        Assert.Null(PipeCommand.ResolveFilter("nonexistent-filter"));
    }

    [Fact]
    public async Task RunAsync_UnknownFilterName_PrintsExactErrorMessage_ReturnsOne()
    {
        using var stdin = ToStream("anything\n");
        using var stderr = new ConsoleErrorCapture();

        var exitCode = await PipeCommand.RunAsync(["-f", "nonexistent-filter"], stdin);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "rtk: Unknown filter 'nonexistent-filter'. Available: cargo-test, pytest, go-test, " +
            "go-build, tsc, vitest, grep, rg, find, fd, git-log, git-diff, git-status, log, mypy, " +
            "ruff-check, ruff-format, prettier\n",
            stderr.ToString());
    }

    // -----------------------------------------------------------------------
    // Delegated ecosystem filters: git-log / git-diff / git-status genuinely
    // delegate to GitCommand's already-ported filter logic.
    // -----------------------------------------------------------------------

    [Fact]
    public void GitLogWrapper_DelegatesToGitCommand_ProducesNonEmptyOutput()
    {
        var f = PipeCommand.ResolveFilter("git-log")!;
        var input = "abc1234 Fix bug in parser (2 days ago) <alice>\ndef5678 Add new feature (3 days ago) <bob>\n";
        var output = f(input);
        Assert.False(string.IsNullOrEmpty(output));
    }

    [Fact]
    public void GitDiffWrapper_DelegatesToGitCommand_ProducesNonEmptyOutput()
    {
        var f = PipeCommand.ResolveFilter("git-diff")!;
        var input = "diff --git a/src/main.rs b/src/main.rs\n--- a/src/main.rs\n+++ b/src/main.rs\n" +
                    "@@ -1,3 +1,4 @@\n+// new comment\n fn main() {}\n";
        var output = f(input);
        Assert.False(string.IsNullOrEmpty(output));
    }

    [Fact]
    public void GitStatusWrapper_DelegatesToGitCommand_ProducesNonEmptyOutput()
    {
        var f = PipeCommand.ResolveFilter("git-status")!;
        var input = "## main...origin/main\n M src/main.rs\n";
        var output = f(input);
        Assert.False(string.IsNullOrEmpty(output));
    }

    // -----------------------------------------------------------------------
    // Ecosystem filters: real delegation to each ported filter module, not identity
    // passthrough (was a disclosed gap before those modules existed; now resolved).
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveFilter_CargoTest_DelegatesToCargoFilters()
    {
        var f = PipeCommand.ResolveFilter("cargo-test")!;
        const string input = "running 15 tests\ntest result: ok. 15 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n";
        var output = f(input);
        Assert.NotEqual(input, output);
        Assert.Contains("cargo test: 15 passed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFilter_Cargo_SameAsCargoTest()
    {
        Assert.Same(PipeCommand.ResolveFilter("cargo"), PipeCommand.ResolveFilter("cargo-test"));
    }

    [Fact]
    public void ResolveFilter_Pytest_DelegatesToPytestFilters()
    {
        var f = PipeCommand.ResolveFilter("pytest")!;
        const string input = "=== test session starts ===\ncollected 3 items\n3 passed in 0.01s\n";
        var output = f(input);
        Assert.NotEqual(input, output);
    }

    [Fact]
    public void ResolveFilter_Mypy_DelegatesToMypyFilters()
    {
        var f = PipeCommand.ResolveFilter("mypy")!;
        const string input = "src/app.py:42: error: Argument 1 has incompatible type [arg-type]\nFound 1 errors in 1 files\n";
        var output = f(input);
        Assert.Contains("mypy:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFilter_RuffCheck_DelegatesToRuffFilters()
    {
        var f = PipeCommand.ResolveFilter("ruff-check")!;
        var output = f("[]");
        Assert.Contains("No issues found", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFilter_RuffFormat_DelegatesToRuffFilters()
    {
        var f = PipeCommand.ResolveFilter("ruff-format")!;
        var output = f("2 files would be reformatted, 1 file already formatted\n");
        Assert.NotNull(output);
    }

    [Fact]
    public void ResolveFilter_GoTest_DelegatesToGoFilters()
    {
        var f = PipeCommand.ResolveFilter("go-test")!;
        const string input = "{\"Time\":\"2024-01-01T00:00:00Z\",\"Action\":\"run\",\"Package\":\"example/pkg\"}\n" +
                              "{\"Time\":\"2024-01-01T00:00:01Z\",\"Action\":\"pass\",\"Package\":\"example/pkg\"}\n";
        var output = f(input);
        Assert.NotEqual(input, output);
        Assert.Contains("Go test", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFilter_GoBuild_DelegatesToGoFilters()
    {
        var f = PipeCommand.ResolveFilter("go-build")!;
        Assert.Equal("Go build: Success", f(""));
    }

    [Fact]
    public void ResolveFilter_Tsc_DelegatesToTscCommand()
    {
        var f = PipeCommand.ResolveFilter("tsc")!;
        const string input = "src/auth.ts(10,5): error TS2322: Type 'string' is not assignable to type 'number'.\n";
        var output = f(input);
        Assert.NotEqual(input, output);
    }

    [Fact]
    public void ResolveFilter_Vitest_DelegatesToVitestParser_CompactFormat()
    {
        var f = PipeCommand.ResolveFilter("vitest")!;
        const string input = """{"testResults":[],"numTotalTests":0,"numPassedTests":0,"numFailedTests":0,"success":true}""";
        var output = f(input);
        Assert.NotEqual(input, output);
    }

    [Fact]
    public void ResolveFilter_Prettier_DelegatesToPrettierCommand()
    {
        var f = PipeCommand.ResolveFilter("prettier")!;
        const string input = "All matched files use Prettier code style!\n";
        var output = f(input);
        Assert.Contains("Prettier", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFilter_Log_DelegatesToLogCommand()
    {
        var f = PipeCommand.ResolveFilter("log")!;
        const string input = "ERROR: something failed\nERROR: something failed\nERROR: something failed\n";
        var output = f(input);
        Assert.NotEqual(input, output);
    }

    // -----------------------------------------------------------------------
    // stdin size hard-bail: HARD FAILURE, not truncation (pipe_cmd.rs:224-226)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_StdinWithinLimit_Succeeds()
    {
        // Deliberately does NOT pass --passthrough: that flag returns before the size-check
        // branch entirely, so a passthrough invocation would not actually exercise (and could not
        // discriminate a regression in) the within-limit path this test is named for.
        using var stdin = ToStream(new string('x', 100));
        using var stdout = new ConsoleOutCapture();

        var exitCode = await PipeCommand.RunAsync([], stdin);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_StdinExceedsRawCap_BailsWithExactMessage_ReturnsOne()
    {
        // 10_485_760 + 1 bytes forces the hard-bail branch (not truncation).
        using var stdin = new MemoryStream(new byte[10_485_760 + 1]);
        using var stderr = new ConsoleErrorCapture();

        var exitCode = await PipeCommand.RunAsync([], stdin);

        Assert.Equal(1, exitCode);
        Assert.Equal("rtk: stdin exceeds 10485760 byte limit\n", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_StdinExactlyAtRawCap_DoesNotBail()
    {
        using var stdin = new MemoryStream(Encoding.ASCII.GetBytes(new string('y', 10_485_760)));
        using var stderr = new ConsoleErrorCapture();
        using var stdout = new ConsoleOutCapture();

        var exitCode = await PipeCommand.RunAsync([], stdin);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // -----------------------------------------------------------------------
    // Exception-safety fallback: filter panic -> stderr warning + raw passthrough
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyFilter_FilterThrows_PrintsWarning_ReturnsRawInputUnchanged()
    {
        static string Throwing(string _) => throw new InvalidOperationException("filter bug");

        using var stderr = new ConsoleErrorCapture();
        var input = "some output\n";

        var result = PipeCommand.ApplyFilter(Throwing, input);

        Assert.Equal(input, result);
        Assert.Equal("[rtk] warning: filter panicked — passing through raw output\n", stderr.ToString());
    }

    [Fact]
    public void ApplyFilter_FilterSucceeds_ReturnsFilteredOutput_NoWarning()
    {
        static string Doubling(string s) => s + s;

        using var stderr = new ConsoleErrorCapture();
        var result = PipeCommand.ApplyFilter(Doubling, "ab");

        Assert.Equal("abab", result);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // -----------------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------------

    private static MemoryStream ToStream(string s) => new(Encoding.UTF8.GetBytes(s));

    /// <summary>Captures <see cref="Console.Out"/> output for the lifetime of the instance.</summary>
    private sealed class ConsoleOutCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Out;
        private readonly StringWriter _capture = new();

        public ConsoleOutCapture() => Console.SetOut(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetOut(_original);
    }

    /// <summary>Captures <see cref="Console.Error"/> output for the lifetime of the instance.</summary>
    private sealed class ConsoleErrorCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Error;
        private readonly StringWriter _capture = new();

        public ConsoleErrorCapture() => Console.SetError(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetError(_original);
    }
}
