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
    // Disclosed-gap ecosystem filters: identity passthrough, not a faked filter.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("cargo-test")]
    [InlineData("pytest")]
    [InlineData("go-test")]
    [InlineData("go-build")]
    [InlineData("tsc")]
    [InlineData("vitest")]
    [InlineData("log")]
    [InlineData("mypy")]
    [InlineData("ruff-check")]
    [InlineData("ruff-format")]
    [InlineData("prettier")]
    public void ResolveFilter_DisclosedGapAlias_ResolvesToIdentityPassthrough(string alias)
    {
        var f = PipeCommand.ResolveFilter(alias)!;
        var input = "arbitrary unrelated content that a real filter would compact\n";
        Assert.Equal(input, f(input));
    }

    // -----------------------------------------------------------------------
    // Auto-detect signature heuristics (pipe_cmd.rs:142-198) - one per branch
    // -----------------------------------------------------------------------

    [Fact]
    public void AutoDetectFilter_CargoTestSignature_ResolvesToIdentityGap()
    {
        var input = "test result: ok. 5 passed; 0 failed; 0 ignored; 0 measured\n";
        var f = PipeCommand.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_PytestSignature_ResolvesToIdentityGap()
    {
        var input = "=== test session starts ===\ncollected 3 items\n";
        var f = PipeCommand.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_GoTestNdjsonSignature_ResolvesToIdentityGap()
    {
        var input = "{\"Time\":\"2024-01-01T00:00:00Z\",\"Action\":\"run\",\"Package\":\"example/pkg\"}\n" +
                    "{\"Time\":\"2024-01-01T00:00:01Z\",\"Action\":\"pass\",\"Package\":\"example/pkg\"}\n";
        var f = PipeCommand.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_MypySignature_ResolvesToIdentityGap()
    {
        var input = "src/app.py:42: error: Argument 1 has incompatible type [arg-type]\n" +
                    "src/utils.py:10: error: Missing return statement [return]\n" +
                    "Found 2 errors in 2 files\n";
        var f = PipeCommand.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_VitestJsonSignature_ResolvesToIdentityGap()
    {
        var input = "{\"testResults\":[],\"numTotalTests\":0}\n";
        var f = PipeCommand.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_GrepFormat_ResolvesToGrepWrapper()
    {
        var input = "src/main.rs:42:fn main() {\nsrc/lib.rs:10:pub fn helper() {}\n";
        var f = PipeCommand.AutoDetectFilter(input);
        var output = f(input);
        Assert.Contains("matches in", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_FindPaths_ResolvesToFindWrapper()
    {
        var input = "./src/main.rs\n./src/lib.rs\n./src/cmd/mod.rs\n./tests/foo.rs\n";
        var f = PipeCommand.AutoDetectFilter(input);
        var output = f(input);
        Assert.Contains("4 files", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_FindAbsolutePaths_ResolvesToFindWrapper()
    {
        var input = "/home/user/src/main.rs\n/home/user/src/lib.rs\n/home/user/tests/foo.rs\n";
        var f = PipeCommand.AutoDetectFilter(input);
        var output = f(input);
        Assert.Contains("3 files", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_FindNotTriggeredForFewLines_ResolvesToIdentity()
    {
        var input = "./src/main.rs\n./src/lib.rs\n";
        var f = PipeCommand.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_FindNotTriggeredForGrepOutput_ResolvesToGrepInstead()
    {
        var input = "src/main.rs:42:fn main() {\nsrc/lib.rs:10:pub fn helper() {}\nsrc/a.rs:1:x\n";
        var f = PipeCommand.AutoDetectFilter(input);
        var output = f(input);
        Assert.DoesNotContain("files in", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_UnknownContent_ResolvesToIdentityFallback()
    {
        var input = "some random text that doesn't match any filter pattern\n";
        var f = PipeCommand.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_EmptyInput_ResolvesToIdentity()
    {
        var f = PipeCommand.AutoDetectFilter(string.Empty);
        Assert.Equal(string.Empty, f(string.Empty));
    }

    [Fact]
    public void AutoDetectFilter_SurrogatePairAtSlicingBoundary_DoesNotThrow_ResolvesToIdentity()
    {
        // .NET's astral-plane equivalent of Rust's multi-byte-UTF-8-at-byte-1024 test: build a
        // string where char index 1024 falls in the middle of a surrogate pair (U+1F600, 😀).
        var input = new string('a', 1023) + "\U0001F600"; // 'a'*1023 then a 2-UTF-16-unit emoji
        var f = PipeCommand.AutoDetectFilter(input);
        var output = f(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void AutoDetectFilter_SingleLineUnknown_ResolvesToIdentity()
    {
        var input = "hello world\n";
        var f = PipeCommand.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    // -----------------------------------------------------------------------
    // grep_wrapper: grouping, per-file cap, header format (pipe_cmd.rs:60-96)
    // -----------------------------------------------------------------------

    [Fact]
    public void GrepWrapper_GroupsByFile_WithCorrectHeader()
    {
        var input = "src/main.rs:42:fn main() {\nsrc/lib.rs:10:pub fn helper() {}\n";
        var output = PipeCommand.GrepWrapper(input);

        Assert.StartsWith("2 matches in 2F:\n\n", output, StringComparison.Ordinal);
        Assert.Contains("[file] src/lib.rs (1):", output, StringComparison.Ordinal);
        Assert.Contains("[file] src/main.rs (1):", output, StringComparison.Ordinal);
    }

    [Fact]
    public void GrepWrapper_NoMatchingLines_ReturnsInputUnchanged()
    {
        var input = "no colons here at all\njust plain text\n";
        Assert.Equal(input, PipeCommand.GrepWrapper(input));
    }

    [Fact]
    public void GrepWrapper_CapsMatchesPerFile_AtTen_WithOverflowMarker()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 15; i++)
        {
            sb.Append($"src/big.rs:{i}:line number {i}\n");
        }

        var output = PipeCommand.GrepWrapper(sb.ToString());

        Assert.StartsWith("15 matches in 1F:\n\n", output, StringComparison.Ordinal);
        Assert.Contains("[file] src/big.rs (15):", output, StringComparison.Ordinal);
        Assert.Contains("+5\n", output, StringComparison.Ordinal);

        // Only the first 10 line numbers should appear as their own "  NNNN: " entries.
        Assert.Contains("line number 10", output, StringComparison.Ordinal);
        Assert.DoesNotContain("line number 11", output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // find_wrapper: grouping, dir/file caps, header format (pipe_cmd.rs:98-140)
    // -----------------------------------------------------------------------

    [Fact]
    public void FindWrapper_GroupsByDirectory_WithCorrectHeader()
    {
        var input = "./src/main.rs\n./src/lib.rs\n./tests/foo.rs\n";
        var output = PipeCommand.FindWrapper(input);

        Assert.StartsWith("3 files in 2 dirs:\n\n", output, StringComparison.Ordinal);
        Assert.Contains("./src/  (2)", output, StringComparison.Ordinal);
        Assert.Contains("./tests/  (1)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FindWrapper_EmptyInput_ReturnsInputUnchanged()
    {
        Assert.Equal(string.Empty, PipeCommand.FindWrapper(string.Empty));
    }

    [Fact]
    public void FindWrapper_CapsFilesPerDirectory_AtTen_WithOverflowMarker()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 15; i++)
        {
            sb.Append($"./src/file{i}.rs\n");
        }

        var output = PipeCommand.FindWrapper(sb.ToString());

        Assert.StartsWith("15 files in 1 dirs:\n\n", output, StringComparison.Ordinal);
        Assert.Contains("./src/  (15)", output, StringComparison.Ordinal);
        Assert.Contains("+5\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FindWrapper_CapsDirectoryCount_AtTwenty_WithMoreDirsMarker()
    {
        var sb = new StringBuilder();
        for (var d = 1; d <= 25; d++)
        {
            sb.Append($"./dir{d:D2}/file.rs\n");
        }

        var output = PipeCommand.FindWrapper(sb.ToString());

        Assert.StartsWith("25 files in 25 dirs:\n\n", output, StringComparison.Ordinal);
        Assert.Contains("+5 more dirs", output, StringComparison.Ordinal);
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
