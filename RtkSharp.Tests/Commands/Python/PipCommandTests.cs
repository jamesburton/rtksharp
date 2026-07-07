using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Python;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Python;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/python/pip_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (<c>filter_pip_list</c>/<c>filter_pip_outdated</c>), plus new dispatch-level coverage for the
/// <c>rtk pip</c> entry point — unlike <see cref="RuffCommand"/>/<see cref="PytestCommand"/>/
/// <see cref="MypyCommand"/> (all non-injectable, buffered-through-<c>CommandRunner</c> commands),
/// <see cref="PipCommand"/> takes an injectable <see cref="IProcessExecutor"/> and tool-existence
/// predicate — the same shape <c>DockerCommandTests</c> established for <see cref="Cloud.DockerCommand"/> —
/// so its subcommand dispatch (list/outdated/passthrough) and the <c>pip</c>-missing/<c>uv</c>-fallback
/// branch can be exercised deterministically.
/// </summary>
public sealed class PipCommandTests
{
    // ===================== filter_pip_list =====================

    [Fact]
    public void FilterPipList_ReportsCountAndPackages()
    {
        const string output = """
            [
              {"name": "requests", "version": "2.31.0"},
              {"name": "pytest", "version": "7.4.0"},
              {"name": "rich", "version": "13.0.0"}
            ]
            """;

        var result = PipFilters.FilterPipList(output);
        Assert.Contains("3 packages", result, StringComparison.Ordinal);
        Assert.Contains("requests", result, StringComparison.Ordinal);
        Assert.Contains("2.31.0", result, StringComparison.Ordinal);
        Assert.Contains("pytest", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPipList_Empty_ReportsNoPackagesInstalled()
    {
        const string output = "[]";
        var result = PipFilters.FilterPipList(output);
        Assert.Contains("No packages installed", result, StringComparison.Ordinal);
    }

    // ===================== filter_pip_outdated =====================

    [Fact]
    public void FilterPipOutdated_None_ReportsAllUpToDate()
    {
        const string output = "[]";
        var result = PipFilters.FilterPipOutdated(output);
        Assert.Contains("All packages up to date", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPipOutdated_Some_ShowsVersionArrow()
    {
        const string output = """
            [
              {"name": "requests", "version": "2.31.0", "latest_version": "2.32.0"},
              {"name": "pytest", "version": "7.4.0", "latest_version": "8.0.0"}
            ]
            """;

        var result = PipFilters.FilterPipOutdated(output);
        Assert.Contains("2 packages", result, StringComparison.Ordinal);
        Assert.Contains("requests", result, StringComparison.Ordinal);
        Assert.Contains("2.31.0 → 2.32.0", result, StringComparison.Ordinal);
        Assert.Contains("pytest", result, StringComparison.Ordinal);
        Assert.Contains("7.4.0 → 8.0.0", result, StringComparison.Ordinal);
    }

    // ===================== dispatch: rtk pip list/outdated/passthrough =====================

    [Fact]
    public async Task RunAsync_List_InvokesPipListFormatJson()
    {
        var executor = new RecordingExecutor(_ => Ok("""[{"name": "requests", "version": "1.0.0"}]"""));
        var stdout = await CaptureStdoutAsync(() =>
            PipCommand.RunAsync(["list"], 0, executor, PipExists));

        Assert.Single(executor.Requests);
        Assert.Contains("list", executor.Requests[0].Arguments);
        Assert.Contains("--format=json", executor.Requests[0].Arguments);
        Assert.Contains("requests", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Outdated_InvokesPipListOutdatedFormatJson()
    {
        var executor = new RecordingExecutor(_ => Ok("[]"));
        var stdout = await CaptureStdoutAsync(() =>
            PipCommand.RunAsync(["outdated"], 0, executor, PipExists));

        Assert.Single(executor.Requests);
        Assert.Contains("--outdated", executor.Requests[0].Arguments);
        Assert.Contains("--format=json", executor.Requests[0].Arguments);
        Assert.Contains("All packages up to date", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Install_PassesThroughFullArgsIncludingSubcommand()
    {
        var executor = new RecordingExecutor(_ => Ok("Successfully installed requests-2.31.0\n"));
        var stdout = await CaptureStdoutAsync(() =>
            PipCommand.RunAsync(["install", "requests"], 0, executor, PipExists));

        Assert.Single(executor.Requests);
        // Rust's run_passthrough is called with the FULL args (including "install"), not args[1..].
        Assert.Equal(["install", "requests"], executor.Requests[0].Arguments);
        Assert.Contains("Successfully installed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UnknownSubcommand_FallsBackToPassthrough()
    {
        var executor = new RecordingExecutor(_ => Ok("pip 24.0 from ...\n"));
        var stdout = await CaptureStdoutAsync(() =>
            PipCommand.RunAsync(["--version"], 0, executor, PipExists));

        Assert.Single(executor.Requests);
        Assert.Equal(["--version"], executor.Requests[0].Arguments);
        Assert.Contains("pip 24.0", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PipMissingUvPresent_FallsBackToUvPip()
    {
        var executor = new RecordingExecutor(_ => Ok("[]"));
        var stdout = await CaptureStdoutAsync(() =>
            PipCommand.RunAsync(["list"], 0, executor, name => name == "uv"));

        Assert.Single(executor.Requests);
        // uv-fallback prefixes the invocation with "pip" before the subcommand's own args.
        Assert.Equal(["pip", "list", "--format=json"], executor.Requests[0].Arguments);
        Assert.Contains("uv", executor.Requests[0].FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_PipAndUvBothPresent_PrefersPipNotUv()
    {
        // Rust's own comment: auto-substituting `uv pip` unconditionally showed the wrong
        // environment — use_uv must be false whenever real `pip` is resolvable, even if `uv` is too.
        var executor = new RecordingExecutor(_ => Ok("[]"));
        await CaptureStdoutAsync(() =>
            PipCommand.RunAsync(["list"], 0, executor, _ => true));

        Assert.Single(executor.Requests);
        // No leading "pip" prefix token (that's only added for the uv-fallback path).
        Assert.Equal(["list", "--format=json"], executor.Requests[0].Arguments);
    }

    private static bool PipExists(string name) => name == "pip";

    private static ExecutionResult Ok(string stdout) =>
        new(stdout, "", ExitCode: 0, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

    private static async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var previous = Console.Out;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetOut(writer);
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private sealed class RecordingExecutor(Func<ExecutionRequest, ExecutionResult> responder) : IProcessExecutor
    {
        public List<ExecutionRequest> Requests { get; } = [];

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(responder(request));
        }
    }
}
