using System;
using System.Text;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="PipeFilters"/>: the auto-detect signature heuristics and the two
/// pipe-only mini filters (<c>grep_wrapper</c>/<c>find_wrapper</c>). Moved from
/// <c>RtkSharp.Tests.Commands.System.PipeCommandTests</c> when <c>AutoDetectFilter</c>,
/// <c>GrepWrapper</c>, and <c>FindWrapper</c> moved from
/// <c>RtkSharp.Commands.System.PipeCommand</c> to <see cref="PipeFilters"/> (Task 14 of the
/// filters-library extraction). Filter-alias resolution (<c>ResolveFilter</c>), the stdin-size
/// hard-bail, and the exception-safety fallback (<c>ApplyFilter</c>) are not pure and remain in
/// <c>PipeCommand</c> alongside its end-to-end tests.
/// </summary>
public sealed class PipeFiltersTests
{
    // -----------------------------------------------------------------------
    // Auto-detect signature heuristics (pipe_cmd.rs:142-198) - one per branch
    // -----------------------------------------------------------------------

    [Fact]
    public void AutoDetectFilter_CargoTestSignature_DelegatesToCargoFilter()
    {
        var input = "test result: ok. 5 passed; 0 failed; 0 ignored; 0 measured\n";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.NotEqual(input, output);
    }

    [Fact]
    public void AutoDetectFilter_PytestSignature_DelegatesToPytestFilter()
    {
        var input = "=== test session starts ===\ncollected 3 items\n3 passed in 0.01s\n";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.NotEqual(input, output);
    }

    [Fact]
    public void AutoDetectFilter_GoTestNdjsonSignature_DelegatesToGoFilter()
    {
        var input = "{\"Time\":\"2024-01-01T00:00:00Z\",\"Action\":\"run\",\"Package\":\"example/pkg\"}\n" +
                    "{\"Time\":\"2024-01-01T00:00:01Z\",\"Action\":\"pass\",\"Package\":\"example/pkg\"}\n";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.NotEqual(input, output);
        Assert.Contains("Go test", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_MypySignature_DelegatesToMypyFilter()
    {
        var input = "src/app.py:42: error: Argument 1 has incompatible type [arg-type]\n" +
                    "src/utils.py:10: error: Missing return statement [return]\n" +
                    "Found 2 errors in 2 files\n";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.Contains("mypy:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_VitestJsonSignature_DelegatesToVitestWrapper()
    {
        const string input = """{"testResults":[],"numTotalTests":0,"numPassedTests":0,"numFailedTests":0,"success":true}""";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.NotEqual(input, output);
    }

    [Fact]
    public void AutoDetectFilter_GrepFormat_ResolvesToGrepWrapper()
    {
        var input = "src/main.rs:42:fn main() {\nsrc/lib.rs:10:pub fn helper() {}\n";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.Contains("matches in", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_FindPaths_ResolvesToFindWrapper()
    {
        var input = "./src/main.rs\n./src/lib.rs\n./src/cmd/mod.rs\n./tests/foo.rs\n";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.Contains("4 files", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_FindAbsolutePaths_ResolvesToFindWrapper()
    {
        var input = "/home/user/src/main.rs\n/home/user/src/lib.rs\n/home/user/tests/foo.rs\n";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.Contains("3 files", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_FindNotTriggeredForFewLines_ResolvesToIdentity()
    {
        var input = "./src/main.rs\n./src/lib.rs\n";
        var f = PipeFilters.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_FindNotTriggeredForGrepOutput_ResolvesToGrepInstead()
    {
        var input = "src/main.rs:42:fn main() {\nsrc/lib.rs:10:pub fn helper() {}\nsrc/a.rs:1:x\n";
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.DoesNotContain("files in", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoDetectFilter_UnknownContent_ResolvesToIdentityFallback()
    {
        var input = "some random text that doesn't match any filter pattern\n";
        var f = PipeFilters.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    [Fact]
    public void AutoDetectFilter_EmptyInput_ResolvesToIdentity()
    {
        var f = PipeFilters.AutoDetectFilter(string.Empty);
        Assert.Equal(string.Empty, f(string.Empty));
    }

    [Fact]
    public void AutoDetectFilter_SurrogatePairAtSlicingBoundary_DoesNotThrow_ResolvesToIdentity()
    {
        // .NET's astral-plane equivalent of Rust's multi-byte-UTF-8-at-byte-1024 test: build a
        // string where char index 1024 falls in the middle of a surrogate pair (U+1F600, 😀).
        var input = new string('a', 1023) + "\U0001F600"; // 'a'*1023 then a 2-UTF-16-unit emoji
        var f = PipeFilters.AutoDetectFilter(input);
        var output = f(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void AutoDetectFilter_SingleLineUnknown_ResolvesToIdentity()
    {
        var input = "hello world\n";
        var f = PipeFilters.AutoDetectFilter(input);
        Assert.Equal(input, f(input));
    }

    // -----------------------------------------------------------------------
    // grep_wrapper: grouping, per-file cap, header format (pipe_cmd.rs:60-96)
    // -----------------------------------------------------------------------

    [Fact]
    public void GrepWrapper_GroupsByFile_WithCorrectHeader()
    {
        var input = "src/main.rs:42:fn main() {\nsrc/lib.rs:10:pub fn helper() {}\n";
        var output = PipeFilters.GrepWrapper(input);

        Assert.StartsWith("2 matches in 2F:\n\n", output, StringComparison.Ordinal);
        Assert.Contains("[file] src/lib.rs (1):", output, StringComparison.Ordinal);
        Assert.Contains("[file] src/main.rs (1):", output, StringComparison.Ordinal);
    }

    [Fact]
    public void GrepWrapper_NoMatchingLines_ReturnsInputUnchanged()
    {
        var input = "no colons here at all\njust plain text\n";
        Assert.Equal(input, PipeFilters.GrepWrapper(input));
    }

    [Fact]
    public void GrepWrapper_CapsMatchesPerFile_AtTen_WithOverflowMarker()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 15; i++)
        {
            sb.Append($"src/big.rs:{i}:line number {i}\n");
        }

        var output = PipeFilters.GrepWrapper(sb.ToString());

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
        var output = PipeFilters.FindWrapper(input);

        Assert.StartsWith("3 files in 2 dirs:\n\n", output, StringComparison.Ordinal);
        Assert.Contains("./src/  (2)", output, StringComparison.Ordinal);
        Assert.Contains("./tests/  (1)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FindWrapper_EmptyInput_ReturnsInputUnchanged()
    {
        Assert.Equal(string.Empty, PipeFilters.FindWrapper(string.Empty));
    }

    [Fact]
    public void FindWrapper_CapsFilesPerDirectory_AtTen_WithOverflowMarker()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 15; i++)
        {
            sb.Append($"./src/file{i}.rs\n");
        }

        var output = PipeFilters.FindWrapper(sb.ToString());

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

        var output = PipeFilters.FindWrapper(sb.ToString());

        Assert.StartsWith("25 files in 25 dirs:\n\n", output, StringComparison.Ordinal);
        Assert.Contains("+5 more dirs", output, StringComparison.Ordinal);
    }
}
