using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.Cloud;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="KubectlCommand"/> and the shared kubectl/oc logic in
/// <see cref="ContainerFilters"/>, a faithful, test-for-test port of the kubectl-relevant portions of
/// Rust <c>src/cmds/cloud/container.rs</c>'s own <c>#[cfg(test)] mod tests</c>
/// (<c>test_k8s_get_target_pods_aliases</c>, <c>test_k8s_get_target_services_aliases</c>,
/// <c>test_k8s_get_target_unsupported_resource</c>, <c>test_k8s_get_target_respects_output_flags</c>),
/// plus new formatting/dispatch coverage for <c>format_kubectl_pods</c>/<c>format_kubectl_services</c>
/// and the <c>get</c>/<c>pods</c>/<c>services</c>/<c>logs</c>/passthrough dispatch — code paths the
/// Rust oracle itself never exposed as pure, independently testable functions in isolation from process
/// execution. The <c>test_oc_pods_savings</c> fixture-driven savings test lives in
/// <see cref="OcCommandTests"/> alongside the other oc-specific coverage, since it is Rust's own "oc
/// support" test section.
/// </summary>
public sealed class KubectlCommandTests
{
    // ===================== K8sGetTarget (container.rs's own mod tests) =====================

    [Theory]
    [InlineData("po")]
    [InlineData("pod")]
    [InlineData("pods")]
    public void K8sGetTarget_PodsAliases_ReturnsPodsWithRest(string resource)
    {
        string[] args = [resource, "-n", "default"];

        var result = ContainerFilters.K8sGetTarget(args);

        Assert.NotNull(result);
        Assert.Equal("pods", result!.Value.Resource);
        Assert.Equal(["-n", "default"], result.Value.Remaining);
    }

    [Theory]
    [InlineData("svc")]
    [InlineData("service")]
    [InlineData("services")]
    public void K8sGetTarget_ServicesAliases_ReturnsServicesWithRest(string resource)
    {
        string[] args = [resource, "-A"];

        var result = ContainerFilters.K8sGetTarget(args);

        Assert.NotNull(result);
        Assert.Equal("services", result!.Value.Resource);
        Assert.Equal(["-A"], result.Value.Remaining);
    }

    [Fact]
    public void K8sGetTarget_UnsupportedResource_ReturnsNull()
    {
        Assert.Null(ContainerFilters.K8sGetTarget(["deployments"]));
    }

    [Theory]
    [InlineData("-o")]
    [InlineData("-owide")]
    [InlineData("--output")]
    [InlineData("--output=json")]
    public void K8sGetTarget_RespectsOutputFlags_ReturnsNull(string outputFlag)
    {
        string[] args = ["pods", outputFlag, "wide"];

        Assert.Null(ContainerFilters.K8sGetTarget(args));
    }

    // ===================== FormatKubectlPods (new coverage: no Rust-exposed pure function to mirror directly, beyond the oc fixture test) =====================

    [Fact]
    public void FormatKubectlPods_EmptyItems_ReturnsNoPodsFound()
    {
        using var doc = JsonDocument.Parse("""{"items": []}""");

        Assert.Equal("No pods found\n", ContainerFilters.FormatKubectlPods(doc.RootElement));
    }

    [Fact]
    public void FormatKubectlPods_MissingItems_ReturnsNoPodsFound()
    {
        using var doc = JsonDocument.Parse("""{}""");

        Assert.Equal("No pods found\n", ContainerFilters.FormatKubectlPods(doc.RootElement));
    }

    [Fact]
    public void FormatKubectlPods_AllRunning_ShowsCountAndRunningTotal()
    {
        var json = """
        {
          "items": [
            {"metadata": {"namespace": "default", "name": "a"}, "status": {"phase": "Running"}},
            {"metadata": {"namespace": "default", "name": "b"}, "status": {"phase": "Running"}}
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = ContainerFilters.FormatKubectlPods(doc.RootElement);

        Assert.Equal("2 pods: 2\n", result);
    }

    [Fact]
    public void FormatKubectlPods_PendingAndFailed_ListsIssuesWithWarnHeader()
    {
        var json = """
        {
          "items": [
            {"metadata": {"namespace": "ns1", "name": "pending-pod"}, "status": {"phase": "Pending"}},
            {"metadata": {"namespace": "ns1", "name": "failed-pod"}, "status": {"phase": "Failed"}}
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = ContainerFilters.FormatKubectlPods(doc.RootElement);

        Assert.Equal("2 pods: 1 pending, 1 [x]\n[warn] Issues:\n  ns1/pending-pod Pending\n  ns1/failed-pod Failed\n", result);
    }

    [Fact]
    public void FormatKubectlPods_SumsRestartCountsAcrossContainerStatuses()
    {
        var json = """
        {
          "items": [
            {
              "metadata": {"namespace": "default", "name": "a"},
              "status": {
                "phase": "Running",
                "containerStatuses": [
                  {"name": "c1", "restartCount": 3},
                  {"name": "c2", "restartCount": 2}
                ]
              }
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = ContainerFilters.FormatKubectlPods(doc.RootElement);

        Assert.Equal("1 pods: 1, 5 restarts\n", result);
    }

    [Fact]
    public void FormatKubectlPods_UnknownPhaseWithCrashLoopBackOff_CountsAsFailedIssue()
    {
        var json = """
        {
          "items": [
            {
              "metadata": {"namespace": "default", "name": "crashy"},
              "status": {
                "phase": "Unknown",
                "containerStatuses": [
                  {"name": "c1", "restartCount": 4, "state": {"waiting": {"reason": "CrashLoopBackOff"}}}
                ]
              }
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = ContainerFilters.FormatKubectlPods(doc.RootElement);

        Assert.Equal("1 pods: 1 [x], 4 restarts\n[warn] Issues:\n  default/crashy CrashLoopBackOff\n", result);
    }

    [Fact]
    public void FormatKubectlPods_MoreThanCapWarningsIssues_TruncatesWithMoreCount()
    {
        var pods = string.Join(",", Enumerable.Range(0, 11)
            .Select(i => "{\"metadata\": {\"namespace\": \"ns\", \"name\": \"p" + i + "\"}, \"status\": {\"phase\": \"Pending\"}}"));
        using var doc = JsonDocument.Parse("{\"items\": [" + pods + "]}");

        var result = ContainerFilters.FormatKubectlPods(doc.RootElement);

        Assert.Contains("11 pending", result, StringComparison.Ordinal);
        Assert.Contains("  … +1 more", result, StringComparison.Ordinal);
        // Only the first CAP_WARNINGS (10) issue lines are listed individually.
        Assert.Equal(10, result.Split('\n').Count(l => l.TrimStart().StartsWith("ns/p", StringComparison.Ordinal)));
    }

    // ===================== FormatKubectlServices (new coverage) =====================

    [Fact]
    public void FormatKubectlServices_EmptyItems_ReturnsNoServicesFound()
    {
        using var doc = JsonDocument.Parse("""{"items": []}""");

        Assert.Equal("No services found\n", ContainerFilters.FormatKubectlServices(doc.RootElement));
    }

    [Fact]
    public void FormatKubectlServices_SamePortAndTarget_ShowsSinglePortNumber()
    {
        var json = """
        {
          "items": [
            {
              "metadata": {"namespace": "default", "name": "web"},
              "spec": {"type": "ClusterIP", "ports": [{"port": 80, "targetPort": 80}]}
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = ContainerFilters.FormatKubectlServices(doc.RootElement);

        Assert.Equal("1 services:\n  default/web ClusterIP [80]\n", result);
    }

    [Fact]
    public void FormatKubectlServices_DifferentPortAndTarget_ShowsArrow()
    {
        var json = """
        {
          "items": [
            {
              "metadata": {"namespace": "default", "name": "web"},
              "spec": {"type": "ClusterIP", "ports": [{"port": 80, "targetPort": 8080}]}
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = ContainerFilters.FormatKubectlServices(doc.RootElement);

        Assert.Equal("1 services:\n  default/web ClusterIP [80→8080]\n", result);
    }

    [Fact]
    public void FormatKubectlServices_StringTargetPort_ParsesAsInt()
    {
        var json = """
        {
          "items": [
            {
              "metadata": {"namespace": "default", "name": "web"},
              "spec": {"type": "ClusterIP", "ports": [{"port": 80, "targetPort": "8080"}]}
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = ContainerFilters.FormatKubectlServices(doc.RootElement);

        Assert.Equal("1 services:\n  default/web ClusterIP [80→8080]\n", result);
    }

    [Fact]
    public void FormatKubectlServices_MoreThanCapListEntries_TruncatesAndEndsWithNewline()
    {
        var services = string.Join(",", Enumerable.Range(0, 21)
            .Select(i => "{\"metadata\": {\"namespace\": \"ns\", \"name\": \"svc" + i + "\"}, \"spec\": {\"type\": \"ClusterIP\", \"ports\": []}}"));
        using var doc = JsonDocument.Parse("{\"items\": [" + services + "]}");

        var result = ContainerFilters.FormatKubectlServices(doc.RootElement);

        Assert.Contains("21 services:", result, StringComparison.Ordinal);
        Assert.Contains("… +1 more", result, StringComparison.Ordinal);
        // The truncated branch unconditionally appends a trailing newline after the "more"/hint text
        // (container.rs:475's `out.push('\n')`) — unlike format_kubectl_pods, which does NOT.
        Assert.EndsWith("\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatKubectlServices_NotTruncated_LastLineEndsWithNewlineFromLoop()
    {
        var json = """
        {
          "items": [
            {"metadata": {"namespace": "ns", "name": "svc0"}, "spec": {"type": "ClusterIP", "ports": []}}
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = ContainerFilters.FormatKubectlServices(doc.RootElement);

        Assert.Equal("1 services:\n  ns/svc0 ClusterIP []\n", result);
    }

    // ===================== RunCoreAsync: dispatch via a recording/responding fake executor =====================

    [Fact]
    public async Task RunCoreAsync_NoArgs_ThrowsCommandArgumentParseException()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => KubectlCommand.RunCoreAsync([], verbose: 0, executor));
        Assert.Contains("requires a subcommand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_GetPods_ParsesJsonAndFormats()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"items": []}"""));

        var stdout = await CaptureStdoutAsync(() => KubectlCommand.RunCoreAsync(["get", "pods"], verbose: 0, executor));

        Assert.Equal("No pods found\n", stdout);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "pods", "-o", "json"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_GetUnsupportedResource_PassesThroughWithGetPrefix()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var exit = await KubectlCommand.RunCoreAsync(["get", "deployments", "-A"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "deployments", "-A"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RunCoreAsync_GetPodsWithOutputFlag_PassesThroughRawRatherThanFiltering()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        await KubectlCommand.RunCoreAsync(["get", "pods", "-o", "wide"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "pods", "-o", "wide"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RunCoreAsync_Pods_BuildsNamespaceArgs()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"items": []}"""));

        await KubectlCommand.RunCoreAsync(["pods", "-n", "kube-system"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "pods", "-o", "json", "-n", "kube-system"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_Pods_AllNamespacesFlagOverridesNamespace()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"items": []}"""));

        await KubectlCommand.RunCoreAsync(["pods", "-n", "kube-system", "-A"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        // build_k8s_namespace_args: `if all { -A } else if namespace { -n <ns> }` — all wins outright.
        Assert.Equal(["get", "pods", "-o", "json", "-A"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_Services_BuildsNamespaceArgs()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"items": []}"""));

        await KubectlCommand.RunCoreAsync(["services", "-A"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["get", "services", "-o", "json", "-A"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_NoPod_ThrowsCommandArgumentParseException()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => KubectlCommand.RunCoreAsync(["logs"], verbose: 0, executor));
        Assert.Contains("required arguments", ex.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_EmptyPodArgument_PrintsUsageMessage()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var stdout = await CaptureStdoutAsync(() => KubectlCommand.RunCoreAsync(["logs", ""], verbose: 0, executor));

        Assert.Equal("Usage: rtk kubectl logs <pod>\n", stdout);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_WithContainerFlag_ForwardsTailAndContainerArgs()
    {
        var executor = new RecordingExecutor(_ => Ok("2024-01-01 10:00:00 ERROR: boom\n"));

        var stdout = await CaptureStdoutAsync(
            () => KubectlCommand.RunCoreAsync(["logs", "my-pod", "-c", "sidecar"], verbose: 0, executor));

        Assert.Contains("Logs for my-pod:", stdout, StringComparison.Ordinal);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["logs", "--tail", "100", "my-pod", "-c", "sidecar"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_UnrecognizedSubcommand_UsesInheritedPassthroughWithFullArgs()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var exit = await KubectlCommand.RunCoreAsync(["describe", "pod", "my-pod"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["describe", "pod", "my-pod"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RunCoreAsync_NonZeroExit_PrintsRawStdoutStderrAndPropagatesExitCode()
    {
        var executor = new RecordingExecutor(_ => new ExecutionResult(
            "partial\n", "error: pods not found", ExitCode: 1, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false));

        var (stdout, stderr, exit) = await CaptureStdoutAndStderrAndExitAsync(
            () => KubectlCommand.RunCoreAsync(["get", "pods"], verbose: 0, executor));

        Assert.Equal(1, exit);
        Assert.Equal("partial\n", stdout);
        Assert.Contains("error: pods not found", stderr, StringComparison.Ordinal);
    }

    // ===================== helpers =====================

    private static ExecutionResult Ok(string stdout) =>
        new(stdout, "", ExitCode: 0, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

    internal static async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
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

    private static async Task<(string Stdout, string Stderr, int Exit)> CaptureStdoutAndStderrAndExitAsync(Func<Task<int>> action)
    {
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var outWriter = new StringWriter { NewLine = "\n" };
        var errWriter = new StringWriter { NewLine = "\n" };
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        try
        {
            var exit = await action();
            return (outWriter.ToString(), errWriter.ToString(), exit);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }
    }

    internal sealed class RecordingExecutor(Func<ExecutionRequest, ExecutionResult> responder) : IProcessExecutor
    {
        public List<ExecutionRequest> Requests { get; } = [];

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(responder(request));
        }
    }
}
