using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.Cloud;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="OcCommand"/>. Includes a faithful, test-for-test port of Rust
/// <c>src/cmds/cloud/container.rs</c>'s "oc support" test section (<c>test_oc_pods_savings</c>), which
/// verifies <c>format_kubectl_pods</c> achieves &gt;=60% token savings against a real OpenShift
/// <c>oc get pods -o json</c> capture — proof that the shared kubectl/oc pod-formatting logic in
/// <see cref="ContainerFilters"/> works correctly against an oc-flavored fixture, not just synthetic
/// kubectl-shaped JSON. The fixture at <c>Fixtures/oc_pods.json</c> mirrors
/// <c>tests/fixtures/oc_pods.json</c> byte-for-byte. Dispatch-level coverage below (mirroring
/// <see cref="KubectlCommandTests"/>) confirms <see cref="OcCommand"/> reaches the same shared
/// <see cref="ContainerFilters"/> logic with <c>tool = "oc"</c>.
/// </summary>
public sealed class OcCommandTests
{
    // ===================== test_oc_pods_savings (container.rs's own mod tests, "oc support" section) =====================

    [Fact]
    public void FormatKubectlPods_OcPodsFixture_AchievesAtLeast60PercentTokenSavings()
    {
        var inputStr = LoadFixture("Fixtures/oc_pods.json");
        using var doc = JsonDocument.Parse(inputStr);

        var output = ContainerFilters.FormatKubectlPods(doc.RootElement);

        var inputTokens = CountTokens(inputStr);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"Expected >=60% savings, got {savings:F1}%");
    }

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

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string LoadFixture(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var dir = Path.GetDirectoryName(sourceFile)!;
        return File.ReadAllText(Path.Combine(dir, relativePath));
    }

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
