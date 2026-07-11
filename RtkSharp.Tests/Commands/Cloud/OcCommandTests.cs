using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.Cloud;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="OcCommand"/>'s dispatch behavior (<c>get</c>/<c>pods</c>/<c>services</c>/
/// <c>logs</c>/passthrough), mirroring <see cref="KubectlCommandTests"/>'s own dispatch coverage to
/// confirm <see cref="OcCommand"/> reaches the same shared
/// <c>RtkSharp.Commands.Cloud.ContainerFilters</c> execution logic with <c>tool = "oc"</c>. Rust's own
/// "oc support" test section (<c>test_oc_pods_savings</c>, which verifies
/// <c>RtkSharp.Filters.Commands.Cloud.ContainerFilters.FormatKubectlPods</c> achieves &gt;=60% token
/// savings against a real OpenShift <c>oc get pods -o json</c> capture) moved to
/// <c>RtkSharp.Filters.Tests.Commands.Cloud.ContainerFiltersTests</c> alongside its
/// <c>Fixtures/oc_pods.json</c> fixture, since it exercises a pure filter method, not dispatch.
/// </summary>
public sealed class OcCommandTests
{
    // ===================== RunCoreAsync: dispatch via a recording/responding fake executor =====================

    [Fact]
    public async Task RunCoreAsync_NoArgs_ThrowsCommandArgumentParseException()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => OcCommand.RunCoreAsync([], verbose: 0, executor));
        Assert.Contains("requires a subcommand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_GetPods_ParsesJsonAndFormats()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"items": []}"""));

        var stdout = await KubectlCommandTests.CaptureStdoutAsync(
            () => OcCommand.RunCoreAsync(["get", "pods"], verbose: 0, executor));

        Assert.Equal("No pods found\n", stdout);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "pods", "-o", "json"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_GetServices_ParsesJsonAndFormats()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"items": []}"""));

        var stdout = await KubectlCommandTests.CaptureStdoutAsync(
            () => OcCommand.RunCoreAsync(["get", "services"], verbose: 0, executor));

        Assert.Equal("No services found\n", stdout);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "services", "-o", "json"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_GetUnsupportedResource_PassesThroughWithGetPrefix()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var exit = await OcCommand.RunCoreAsync(["get", "routes"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "routes"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RunCoreAsync_Pods_BuildsNamespaceArgs()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"items": []}"""));

        await OcCommand.RunCoreAsync(["pods", "-n", "openshift-monitoring"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "pods", "-o", "json", "-n", "openshift-monitoring"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_Services_BuildsAllNamespacesArgs()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"items": []}"""));

        await OcCommand.RunCoreAsync(["services", "-A"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "services", "-o", "json", "-A"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_NoPod_ThrowsCommandArgumentParseException()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => OcCommand.RunCoreAsync(["logs"], verbose: 0, executor));
        Assert.Contains("required arguments", ex.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_EmptyPodArgument_PrintsUsageMessage()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var stdout = await KubectlCommandTests.CaptureStdoutAsync(
            () => OcCommand.RunCoreAsync(["logs", ""], verbose: 0, executor));

        Assert.Equal("Usage: rtk oc logs <pod>\n", stdout);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_AnalyzesViaLogCommand()
    {
        var executor = new RecordingExecutor(_ => Ok("2024-01-01 10:00:00 ERROR: boom\n"));

        var stdout = await KubectlCommandTests.CaptureStdoutAsync(
            () => OcCommand.RunCoreAsync(["logs", "my-pod"], verbose: 0, executor));

        Assert.Contains("Logs for my-pod:", stdout, StringComparison.Ordinal);
        Assert.Contains("ERRORS", stdout, StringComparison.Ordinal);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["logs", "--tail", "100", "my-pod"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_UnrecognizedSubcommand_UsesInheritedPassthroughWithFullArgs()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var exit = await OcCommand.RunCoreAsync(["rollout", "status", "dc/my-app"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["rollout", "status", "dc/my-app"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    // ===================== helpers =====================

    private static ExecutionResult Ok(string stdout) =>
        new(stdout, "", ExitCode: 0, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

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
