using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RtkSharp.Filters.Commands.Cloud;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="ContainerFilters"/>'s two pure JSON-formatting methods
/// (<see cref="ContainerFilters.FormatKubectlPods"/>/<see cref="ContainerFilters.FormatKubectlServices"/>),
/// shared by both <c>rtk kubectl</c> and <c>rtk oc</c>. Consolidates the pure-formatter coverage
/// that previously lived split across <c>RtkSharp.Tests.Commands.Cloud.KubectlCommandTests</c>
/// (the bulk of <c>FormatKubectlPods</c>/<c>FormatKubectlServices</c> coverage, plus a
/// <c>K8sGetTarget</c> section that stayed behind since that method did not move) and
/// <c>RtkSharp.Tests.Commands.Cloud.OcCommandTests</c> (Rust's own <c>test_oc_pods_savings</c>
/// fixture-driven "oc support" test, proving the shared formatter works against a real
/// OpenShift-flavored capture, not just synthetic kubectl-shaped JSON) — both test classes' own
/// <c>RunCoreAsync</c>-level dispatch coverage stays behind in their original files (CLI-level,
/// not pure-filter).
/// </summary>
public sealed class ContainerFiltersTests
{
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

    

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string LoadFixture(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var dir = Path.GetDirectoryName(sourceFile)!;
        return File.ReadAllText(Path.Combine(dir, relativePath));
    }
}
