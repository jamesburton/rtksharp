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
/// The pure <c>SummarizeOutput</c>/<c>DetectOutputType</c>/<c>ExtractNumber</c> heuristics moved
/// to <c>RtkSharp.Filters.Commands.System.SummaryFilters</c> - see
/// <c>RtkSharp.Filters.Tests.Commands.System.SummaryFiltersTests</c>. This file covers
/// <see cref="SummaryCommand.ParseArgs"/> and a <see cref="IProcessExecutor"/>-injected
/// integration test of the full run path.
/// </summary>
public sealed class SummaryCommandTests
{
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
