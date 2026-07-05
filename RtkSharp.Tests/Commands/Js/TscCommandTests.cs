using System;
using System.Linq;
using System.Text;
using RtkSharp.Commands.Js;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="TscCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>tsc_cmd.rs</c> — specifically the buffered <c>filter_tsc_output</c> tests
/// (<c>test_filter_tsc_output</c>, <c>test_every_error_message_shown</c>,
/// <c>test_continuation_lines_preserved</c>, <c>test_no_file_limit</c>, <c>test_filter_no_errors</c>)
/// plus original coverage for the top-codes-by-frequency computation, the 120-char truncation, and the
/// <see cref="TscCommand.TscErrorRegex"/> pattern itself. <c>ExecuteAsync</c>/<c>RunTscSafeAsync</c> are
/// not exercised end-to-end: they construct their own <c>ProcessExecutor</c> via
/// <see cref="RtkSharp.Execution.CommandRunner"/> with no injection seam, so invoking them would risk
/// spawning a real <c>tsc</c>/<c>npx</c> process as a side effect of the test suite (same convention
/// <c>NpmCommandTests</c>/<c>PnpmCommandTests</c> follow for their own <c>CommandRunner</c>-based
/// routes).
/// </summary>
public sealed class TscCommandTests
{
    // -----------------------------------------------------------------------
    // FilterTscOutput - ports tsc_cmd.rs's test_filter_tsc_output
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterTscOutput_GroupsByFileAndReplacesSummaryLine()
    {
        var output = "\n" +
            "src/server/api/auth.ts(12,5): error TS2322: Type 'string' is not assignable to type 'number'.\n" +
            "src/server/api/auth.ts(15,10): error TS2345: Argument of type 'number' is not assignable to parameter of type 'string'.\n" +
            "src/components/Button.tsx(8,3): error TS2339: Property 'onClick' does not exist on type 'ButtonProps'.\n" +
            "src/components/Button.tsx(10,5): error TS2322: Type 'string' is not assignable to type 'number'.\n" +
            "\n" +
            "Found 4 errors in 2 files.\n";

        var result = TscCommand.FilterTscOutput(output);

        Assert.Contains("TypeScript: 4 errors in 2 files", result, StringComparison.Ordinal);
        Assert.Contains("auth.ts (2 errors)", result, StringComparison.Ordinal);
        Assert.Contains("Button.tsx (2 errors)", result, StringComparison.Ordinal);
        Assert.Contains("TS2322", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Found 4 errors", result, StringComparison.Ordinal); // Summary line replaced.
    }

    // -----------------------------------------------------------------------
    // FilterTscOutput - ports tsc_cmd.rs's test_every_error_message_shown
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterTscOutput_EveryErrorMessageIndividuallyVisible()
    {
        var output =
            "src/api.ts(10,5): error TS2322: Type 'string' is not assignable to type 'number'.\n" +
            "src/api.ts(20,5): error TS2322: Type 'boolean' is not assignable to type 'string'.\n" +
            "src/api.ts(30,5): error TS2322: Type 'null' is not assignable to type 'object'.\n";

        var result = TscCommand.FilterTscOutput(output);

        Assert.Contains("Type 'string' is not assignable to type 'number'", result, StringComparison.Ordinal);
        Assert.Contains("Type 'boolean' is not assignable to type 'string'", result, StringComparison.Ordinal);
        Assert.Contains("Type 'null' is not assignable to type 'object'", result, StringComparison.Ordinal);
        Assert.Contains("L10:", result, StringComparison.Ordinal);
        Assert.Contains("L20:", result, StringComparison.Ordinal);
        Assert.Contains("L30:", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterTscOutput - ports tsc_cmd.rs's test_continuation_lines_preserved
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterTscOutput_ContinuationLinesPreserved()
    {
        var output =
            "src/app.tsx(10,3): error TS2322: Type '{ children: Element; }' is not assignable to type 'Props'.\n" +
            "  Property 'children' does not exist on type 'Props'.\n" +
            "src/app.tsx(20,5): error TS2345: Argument of type 'number' is not assignable to parameter of type 'string'.\n";

        var result = TscCommand.FilterTscOutput(output);

        Assert.Contains("Property 'children' does not exist on type 'Props'", result, StringComparison.Ordinal);
        Assert.Contains("L10:", result, StringComparison.Ordinal);
        Assert.Contains("L20:", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterTscOutput - ports tsc_cmd.rs's test_no_file_limit (deliberately NO cap, unlike most RTK
    // truncation conventions - this test would fail immediately if a cap were accidentally introduced)
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterTscOutput_NoFileLimit_AllFifteenFilesShown()
    {
        var output = string.Concat(Enumerable.Range(1, 15)
            .Select(i => $"src/file{i}.ts({i},1): error TS2322: Error in file {i}.\n"));

        var result = TscCommand.FilterTscOutput(output);

        Assert.Contains("15 errors in 15 files", result, StringComparison.Ordinal);
        for (var i = 1; i <= 15; i++)
        {
            Assert.Contains($"file{i}.ts", result, StringComparison.Ordinal);
        }
    }

    // -----------------------------------------------------------------------
    // FilterTscOutput - ports tsc_cmd.rs's test_filter_no_errors (both no-error message variants)
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterTscOutput_FoundZeroErrorsSubstring_ReturnsNoErrorsFoundMessage()
    {
        var result = TscCommand.FilterTscOutput("Found 0 errors. Watching for file changes.");
        Assert.Equal("TypeScript: No errors found", result);
    }

    [Fact]
    public void FilterTscOutput_NoMatchesAndNoFoundZeroErrorsSubstring_ReturnsGenericCompletedMessage()
    {
        var result = TscCommand.FilterTscOutput("Compilation finished with 0 issues.");
        Assert.Equal("TypeScript compilation completed", result);
    }

    // -----------------------------------------------------------------------
    // Top-codes-by-frequency computation - top 5 by count, only emitted when >1 distinct code appears
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterTscOutput_MoreThanFiveDistinctCodes_ShowsOnlyTop5ByFrequency()
    {
        // TS9001 x4, TS9002 x3, TS9003 x2, TS9004 x2, TS9005 x1, TS9006 x1, TS9007 x1 - 7 distinct
        // codes, only the top 5 by frequency should appear in the "Top codes:" line.
        var lines = new StringBuilder();
        void AddErrors(string code, int count)
        {
            for (var i = 0; i < count; i++)
            {
                lines.Append($"src/f_{code}_{i}.ts({i + 1},1): error {code}: message.\n");
            }
        }

        AddErrors("TS9001", 4);
        AddErrors("TS9002", 3);
        AddErrors("TS9003", 2);
        AddErrors("TS9004", 2);
        AddErrors("TS9005", 1);
        AddErrors("TS9006", 1);
        AddErrors("TS9007", 1);

        var result = TscCommand.FilterTscOutput(lines.ToString());

        Assert.Contains("Top codes:", result, StringComparison.Ordinal);
        Assert.Contains("TS9001 (4x)", result, StringComparison.Ordinal);
        Assert.Contains("TS9002 (3x)", result, StringComparison.Ordinal);

        var topCodesLine = result
            .Split('\n')
            .Single(l => l.StartsWith("Top codes:", StringComparison.Ordinal));
        Assert.Equal(5, topCodesLine.Split(',').Length); // Only the top 5 of 7 distinct codes appear.
    }

    [Fact]
    public void FilterTscOutput_SingleDistinctCode_OmitsTopCodesLine()
    {
        var output =
            "src/a.ts(1,1): error TS2322: message one.\n" +
            "src/b.ts(2,1): error TS2322: message two.\n";

        var result = TscCommand.FilterTscOutput(output);

        Assert.DoesNotContain("Top codes:", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // 120-char truncation of individual error messages and context lines
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterTscOutput_LongErrorMessage_TruncatedTo120Chars()
    {
        var longMessage = new string('a', 200);
        var output = $"src/long.ts(1,1): error TS2322: {longMessage}\n";

        var result = TscCommand.FilterTscOutput(output);

        var messageLine = result.Split('\n').Single(l => l.StartsWith("  L1:", StringComparison.Ordinal));
        var afterCode = messageLine["  L1: TS2322 ".Length..];

        Assert.Equal(120, afterCode.Length);
        Assert.EndsWith("...", afterCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterTscOutput_LongContextLine_TruncatedTo120Chars()
    {
        var longContext = new string('b', 200);
        var output =
            "src/long.ts(1,1): error TS2322: short message.\n" +
            $"  {longContext}\n";

        var result = TscCommand.FilterTscOutput(output);

        var contextLine = result.Split('\n').Single(l => l.StartsWith("    b", StringComparison.Ordinal));
        var trimmed = contextLine.TrimStart();

        Assert.Equal(120, trimmed.Length);
        Assert.EndsWith("...", trimmed, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // TSC_ERROR regex - matching/non-matching representative lines
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("src/app.ts(12,5): error TS2322: Type 'string' is not assignable to type 'number'.")]
    [InlineData("src/app.ts(1,1): warning TS6133: 'x' is declared but never read.")]
    [InlineData("C:\\repo\\src\\app.ts(100,42): error TS1005: ';' expected.")]
    public void TscErrorRegex_MatchesRepresentativeDiagnosticLines(string line) =>
        Assert.Matches(TscCommand.TscErrorRegex(), line);

    [Theory]
    [InlineData("  Property 'children' does not exist on type 'Props'.")]
    [InlineData("Found 4 errors in 2 files.")]
    [InlineData("")]
    [InlineData("src/app.ts: error TS2322: missing location info.")]
    public void TscErrorRegex_DoesNotMatchNonDiagnosticLines(string line) =>
        Assert.DoesNotMatch(TscCommand.TscErrorRegex(), line);

    [Fact]
    public void TscErrorRegex_CapturesGroupsCorrectly()
    {
        var match = TscCommand.TscErrorRegex().Match(
            "src/server/api/auth.ts(12,5): error TS2322: Type 'string' is not assignable to type 'number'.");

        Assert.True(match.Success);
        Assert.Equal("src/server/api/auth.ts", match.Groups[1].Value);
        Assert.Equal("12", match.Groups[2].Value);
        Assert.Equal("5", match.Groups[3].Value);
        Assert.Equal("error", match.Groups[4].Value);
        Assert.Equal("TS2322", match.Groups[5].Value);
        Assert.Equal("Type 'string' is not assignable to type 'number'.", match.Groups[6].Value);
    }

    // -----------------------------------------------------------------------
    // ToolExists - pure PathResolver-based check
    // -----------------------------------------------------------------------

    [Fact]
    public void ToolExists_UnresolvableName_ReturnsFalse() =>
        Assert.False(TscCommand.ToolExists("definitely-not-a-real-binary-xyz123"));
}
