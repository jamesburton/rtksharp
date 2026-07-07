using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.System;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="SummaryCommand"/>, the <c>rtk summary</c> heuristic output summarizer.
/// Faithful-port target: Rust <c>src/cmds/system/summary.rs</c>, which has NO <c>#[cfg(test)]</c>
/// module of its own — so there are no oracle unit tests to port verbatim here. This file instead
/// exercises <see cref="SummaryCommand.SummarizeOutput"/>, <see cref="SummaryCommand.DetectOutputType"/>,
/// and <see cref="SummaryCommand.ExtractNumber"/> against representative fixtures matching each
/// documented heuristic branch, plus <see cref="SummaryCommand.ParseArgs"/> and a
/// <see cref="IProcessExecutor"/>-injected integration test of the full run path.
/// </summary>
public sealed class SummaryCommandTests
{
    // --- ExtractNumber ---

    [Fact]
    public void ExtractNumber_MatchesDigitsBeforeWord()
    {
        Assert.Equal(12, SummaryCommand.ExtractNumber("12 passed", "passed"));
    }

    [Fact]
    public void ExtractNumber_NoMatch_ReturnsNull()
    {
        Assert.Null(SummaryCommand.ExtractNumber("no numbers here", "passed"));
    }

    // --- DetectOutputType ---

    [Fact]
    public void DetectOutputType_TestCommand_ClassifiedAsTestResults()
    {
        Assert.Equal(SummaryCommand.OutputType.TestResults, SummaryCommand.DetectOutputType("some output", "cargo test"));
    }

    [Fact]
    public void DetectOutputType_PassedAndFailedInOutput_ClassifiedAsTestResults()
    {
        Assert.Equal(SummaryCommand.OutputType.TestResults, SummaryCommand.DetectOutputType("3 passed, 1 failed", "make check"));
    }

    [Fact]
    public void DetectOutputType_BuildCommand_ClassifiedAsBuildOutput()
    {
        Assert.Equal(SummaryCommand.OutputType.BuildOutput, SummaryCommand.DetectOutputType("Compiling foo v0.1.0", "cargo build"));
    }

    [Fact]
    public void DetectOutputType_ErrorPrefixed_ClassifiedAsLogOutput()
    {
        Assert.Equal(SummaryCommand.OutputType.LogOutput, SummaryCommand.DetectOutputType("error: something broke", "run.sh"));
    }

    [Fact]
    public void DetectOutputType_JsonLooking_ClassifiedAsJsonOutput()
    {
        Assert.Equal(SummaryCommand.OutputType.JsonOutput, SummaryCommand.DetectOutputType("""{"a": 1}""", "curl api"));
    }

    [Fact]
    public void DetectOutputType_ShortNarrowLines_ClassifiedAsListOutput()
    {
        Assert.Equal(SummaryCommand.OutputType.ListOutput, SummaryCommand.DetectOutputType("apple\nbanana\ncherry", "ls"));
    }

    [Fact]
    public void DetectOutputType_LongProseLines_ClassifiedAsGeneric()
    {
        // A single line with 20 words fails the "< 10 words" list heuristic, so it falls to Generic.
        var longLine = string.Join(" ", Enumerable.Repeat("word", 20));
        Assert.Equal(SummaryCommand.OutputType.Generic, SummaryCommand.DetectOutputType(longLine, "echo"));
    }

    // --- SummarizeOutput: header ---

    [Fact]
    public void SummarizeOutput_Success_ShowsOkIcon()
    {
        var result = SummaryCommand.SummarizeOutput("line1\nline2", "echo hi", success: true);
        Assert.Contains("[ok] Command: echo hi", result, StringComparison.Ordinal);
        Assert.Contains("2 lines of output", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeOutput_Failure_ShowsFailIcon()
    {
        var result = SummaryCommand.SummarizeOutput("boom", "false", success: false);
        Assert.Contains("[FAIL] Command: false", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: test results branch ---

    [Fact]
    public void SummarizeOutput_TestResults_CountsPassedAndFailed()
    {
        var output = "test foo ... ok\ntest bar ... FAILED\n2 passed; 1 failed";
        var result = SummaryCommand.SummarizeOutput(output, "cargo test", success: false);

        Assert.Contains("Test Results:", result, StringComparison.Ordinal);
        Assert.Contains("passed", result, StringComparison.Ordinal);
        Assert.Contains("[FAIL]", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: build branch ---

    [Fact]
    public void SummarizeOutput_BuildOutput_CountsErrorsAndWarnings()
    {
        var output = "Compiling foo v0.1.0\nerror[E0001]: bad thing\nwarning: unused variable";
        var result = SummaryCommand.SummarizeOutput(output, "cargo build", success: false);

        Assert.Contains("Build Summary:", result, StringComparison.Ordinal);
        Assert.Contains("[error] 1 errors", result, StringComparison.Ordinal);
        Assert.Contains("[warn] 1 warnings", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeOutput_BuildOutput_NoErrorsOrWarnings_ShowsSuccessLine()
    {
        var output = "Compiling foo v0.1.0\nFinished dev profile";
        var result = SummaryCommand.SummarizeOutput(output, "cargo build", success: true);

        Assert.Contains("[ok] Build successful", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: log branch ---

    [Fact]
    public void SummarizeOutput_LogOutput_CountsErrorWarnInfo()
    {
        var output = "error: bad\nwarn: careful\ninfo: fyi\ninfo: fyi2";
        var result = SummaryCommand.SummarizeOutput(output, "run.sh", success: false);

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
        var result = SummaryCommand.SummarizeOutput(output, "ls", success: true);

        Assert.Contains("List (15 items):", result, StringComparison.Ordinal);
        Assert.Contains("... +5 more", result, StringComparison.Ordinal);
    }

    // --- SummarizeOutput: JSON branch ---

    [Fact]
    public void SummarizeOutput_JsonArray_ShowsItemCount()
    {
        var result = SummaryCommand.SummarizeOutput("[1, 2, 3]", "curl api", success: true);
        Assert.Contains("JSON Output:", result, StringComparison.Ordinal);
        Assert.Contains("Array with 3 items", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeOutput_JsonObject_ShowsKeys()
    {
        var result = SummaryCommand.SummarizeOutput("""{"a": 1, "b": 2}""", "curl api", success: true);
        Assert.Contains("Object with 2 keys:", result, StringComparison.Ordinal);
        Assert.Contains("a", result, StringComparison.Ordinal);
        Assert.Contains("b", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeOutput_InvalidJson_ReportsInvalid()
    {
        var result = SummaryCommand.SummarizeOutput("{not valid", "curl api", success: true);
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
        var result = SummaryCommand.SummarizeOutput(output, "echo", success: true);

        Assert.Contains("Output:", result, StringComparison.Ordinal);
        Assert.Contains("number 0", result, StringComparison.Ordinal);
        Assert.Contains("...", result, StringComparison.Ordinal);
        Assert.Contains("number 19", result, StringComparison.Ordinal);
    }

    // --- ParseArgs ---

    [Fact]
    public void ParseArgs_JoinsWithSpaces()
    {
        Assert.Equal("echo hello world", SummaryCommand.ParseArgs(["echo", "hello", "world"]));
    }

    [Fact]
    public void ParseArgs_Empty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SummaryCommand.ParseArgs([]));
    }

    [Fact]
    public void ParseArgs_HyphenPrefixedArgs_NeverThrows()
    {
        // trailing_var_arg + allow_hyphen_values: no realistic parse failure exists for this command.
        Assert.Equal("ls -la --color", SummaryCommand.ParseArgs(["ls", "-la", "--color"]));
    }

    // --- RunCoreAsync integration ---

    [Fact]
    public async Task RunCoreAsync_SuccessfulCommand_PrintsSummaryAndReturnsExitCode()
    {
        var executor = new RecordingExecutor(_ => new ExecutionResult("3 passed", "", 0, TimeSpan.Zero, true, null, false));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await SummaryCommand.RunCoreAsync("cargo test", 0, executor, stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("[ok] Command: cargo test", stdout.ToString(), StringComparison.Ordinal);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task RunCoreAsync_FailedCommand_PropagatesExitCode()
    {
        var executor = new RecordingExecutor(_ => new ExecutionResult("", "boom", 1, TimeSpan.Zero, true, null, false));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await SummaryCommand.RunCoreAsync("false", 0, executor, stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("[FAIL]", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_ProcessDidNotStart_ThrowsInvalidOperationException()
    {
        var executor = new RecordingExecutor(_ => new ExecutionResult("", "", 127, TimeSpan.Zero, false, "not found", false));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SummaryCommand.RunCoreAsync("bogus-command", 0, executor, stdout, stderr));
    }

    /// <summary>Records every <see cref="ExecutionRequest"/> and returns a canned result per request.</summary>
    private sealed class RecordingExecutor(Func<ExecutionRequest, ExecutionResult> responder) : IProcessExecutor
    {
        public List<ExecutionRequest> Requests { get; } = new();

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(responder(request));
        }
    }
}
