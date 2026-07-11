using System;
using System.Collections.Generic;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="SummaryFilters"/>, the pure heuristic output summarizer behind
/// <c>rtk summary</c>. Moved from <c>RtkSharp.Tests.Commands.System.SummaryCommandTests</c> when
/// <c>SummarizeOutput</c>, <c>DetectOutputType</c>, and <c>ExtractNumber</c> moved from
/// <c>RtkSharp.Commands.System.SummaryCommand</c> to <see cref="SummaryFilters"/> (Task 14 of the
/// filters-library extraction). Faithful-port target: Rust <c>src/cmds/system/summary.rs</c>,
/// which has NO <c>#[cfg(test)]</c> module of its own — so there are no oracle unit tests to port
/// verbatim here; this file exercises each heuristic branch against representative fixtures.
/// Argument parsing and the shell-invocation <c>RunCoreAsync</c> flow are not pure and remain in
/// <c>SummaryCommand</c> alongside its own tests.
/// </summary>
public sealed class SummaryFiltersTests
{
    // --- ExtractNumber ---

    [Fact]
    public void ExtractNumber_MatchesDigitsBeforeWord()
    {
        Assert.Equal(12, SummaryFilters.ExtractNumber("12 passed", "passed"));
    }

    [Fact]
    public void ExtractNumber_NoMatch_ReturnsNull()
    {
        Assert.Null(SummaryFilters.ExtractNumber("no numbers here", "passed"));
    }

    // --- DetectOutputType ---

    [Fact]
    public void DetectOutputType_TestCommand_ClassifiedAsTestResults()
    {
        Assert.Equal(SummaryFilters.OutputType.TestResults, SummaryFilters.DetectOutputType("some output", "cargo test"));
    }

    [Fact]
    public void DetectOutputType_PassedAndFailedInOutput_ClassifiedAsTestResults()
    {
        Assert.Equal(SummaryFilters.OutputType.TestResults, SummaryFilters.DetectOutputType("3 passed, 1 failed", "make check"));
    }

    [Fact]
    public void DetectOutputType_BuildCommand_ClassifiedAsBuildOutput()
    {
        Assert.Equal(SummaryFilters.OutputType.BuildOutput, SummaryFilters.DetectOutputType("Compiling foo v0.1.0", "cargo build"));
    }

    [Fact]
    public void DetectOutputType_ErrorPrefixed_ClassifiedAsLogOutput()
    {
        Assert.Equal(SummaryFilters.OutputType.LogOutput, SummaryFilters.DetectOutputType("error: something broke", "run.sh"));
    }

    [Fact]
    public void DetectOutputType_JsonLooking_ClassifiedAsJsonOutput()
    {
        Assert.Equal(SummaryFilters.OutputType.JsonOutput, SummaryFilters.DetectOutputType("""{"a": 1}""", "curl api"));
    }

    [Fact]
    public void DetectOutputType_ShortNarrowLines_ClassifiedAsListOutput()
    {
        Assert.Equal(SummaryFilters.OutputType.ListOutput, SummaryFilters.DetectOutputType("apple\nbanana\ncherry", "ls"));
    }

    [Fact]
    public void DetectOutputType_LongProseLines_ClassifiedAsGeneric()
    {
        // A single line with 20 words fails the "< 10 words" list heuristic, so it falls to Generic.
        var longLine = string.Join(" ", Enumerable.Repeat("word", 20));
        Assert.Equal(SummaryFilters.OutputType.Generic, SummaryFilters.DetectOutputType(longLine, "echo"));
    }

    // --- SummarizeOutput: header ---

    [Fact]
    public void SummarizeOutput_Success_ShowsOkIcon()
    {
        var result = SummaryFilters.SummarizeOutput("line1\nline2", "echo hi", success: true);
        Assert.Contains("[ok] Command: echo hi", result, StringComparison.Ordinal);
        Assert.Contains("2 lines of output", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeOutput_Failure_ShowsFailIcon()
    {
        var result = SummaryFilters.SummarizeOutput("boom", "false", success: false);
        Assert.Contains("[FAIL] Command: false", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: test results branch ---

    [Fact]
    public void SummarizeOutput_TestResults_CountsPassedAndFailed()
    {
        var output = "test foo ... ok\ntest bar ... FAILED\n2 passed; 1 failed";
        var result = SummaryFilters.SummarizeOutput(output, "cargo test", success: false);

        Assert.Contains("Test Results:", result, StringComparison.Ordinal);
        Assert.Contains("passed", result, StringComparison.Ordinal);
        Assert.Contains("[FAIL]", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: build branch ---

    [Fact]
    public void SummarizeOutput_BuildOutput_CountsErrorsAndWarnings()
    {
        var output = "Compiling foo v0.1.0\nerror[E0001]: bad thing\nwarning: unused variable";
        var result = SummaryFilters.SummarizeOutput(output, "cargo build", success: false);

        Assert.Contains("Build Summary:", result, StringComparison.Ordinal);
        Assert.Contains("[error] 1 errors", result, StringComparison.Ordinal);
        Assert.Contains("[warn] 1 warnings", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeOutput_BuildOutput_NoErrorsOrWarnings_ShowsSuccessLine()
    {
        var output = "Compiling foo v0.1.0\nFinished dev profile";
        var result = SummaryFilters.SummarizeOutput(output, "cargo build", success: true);

        Assert.Contains("[ok] Build successful", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: log branch ---

    [Fact]
    public void SummarizeOutput_LogOutput_CountsErrorWarnInfo()
    {
        var output = "error: bad\nwarn: careful\ninfo: fyi\ninfo: fyi2";
        var result = SummaryFilters.SummarizeOutput(output, "run.sh", success: false);

        Assert.Contains("Log Summary:", result, StringComparison.Ordinal);
        Assert.Contains("[error] 1 errors", result, StringComparison.Ordinal);
        Assert.Contains("[warn] 1 warnings", result, StringComparison.Ordinal);
        Assert.Contains("[info] 2 info", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: list branch ---

    [Fact]
    public void SummarizeOutput_ListOutput_ShowsItemCountAndOverflow()
    {
        var lines = new List<string>();
        for (var i = 0; i < 15; i++)
        {
            lines.Add($"item{i}");
        }

        var output = string.Join("\n", lines);
        var result = SummaryFilters.SummarizeOutput(output, "ls", success: true);

        Assert.Contains("List (15 items):", result, StringComparison.Ordinal);
        Assert.Contains("... +5 more", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: JSON branch ---

    [Fact]
    public void SummarizeOutput_JsonArray_ShowsItemCount()
    {
        var result = SummaryFilters.SummarizeOutput("[1, 2, 3]", "curl api", success: true);
        Assert.Contains("JSON Output:", result, StringComparison.Ordinal);
        Assert.Contains("Array with 3 items", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeOutput_JsonObject_ShowsKeys()
    {
        var result = SummaryFilters.SummarizeOutput("""{"a": 1, "b": 2}""", "curl api", success: true);
        Assert.Contains("Object with 2 keys:", result, StringComparison.Ordinal);
        Assert.Contains("a", result, StringComparison.Ordinal);
        Assert.Contains("b", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeOutput_InvalidJson_ReportsInvalid()
    {
        var result = SummaryFilters.SummarizeOutput("{not valid", "curl api", success: true);
        Assert.Contains("(Invalid JSON)", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: generic branch ---

    [Fact]
    public void SummarizeOutput_GenericLongOutput_ShowsHeadAndTail()
    {
        var lines = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            lines.Add($"this is a fairly long descriptive prose line number {i} with several words in it");
        }

        var output = string.Join("\n", lines);
        var result = SummaryFilters.SummarizeOutput(output, "echo", success: true);

        Assert.Contains("Output:", result, StringComparison.Ordinal);
        Assert.Contains("number 0", result, StringComparison.Ordinal);
        Assert.Contains("...", result, StringComparison.Ordinal);
        Assert.Contains("number 19", result, StringComparison.Ordinal);
    }

}
