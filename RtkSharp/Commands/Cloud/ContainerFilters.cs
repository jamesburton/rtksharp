using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Shared kubectl/oc filtering logic, faithfully ported from the generic, <c>tool: &amp;str</c>
/// -parameterized functions in Rust <c>src/cmds/cloud/container.rs</c> (<c>run_k8s_json</c>,
/// <c>k8s_pods</c>, <c>k8s_services</c>, <c>k8s_logs</c>, <c>run_k8s_get</c> (as
/// <c>RunK8sGetAsync</c>), <c>k8s_get_target</c>, <c>k8s_get_requests_raw_output</c>). Both
/// <see cref="KubectlCommand"/> and <see cref="OcCommand"/> call into these, since in Rust ~100% of
/// this logic is shared between the two tools — only the resolved binary name ("kubectl" vs "oc")
/// differs.
/// </summary>
/// <remarks>
/// The pure <c>JsonElement</c>-in/<c>string</c>-out formatters (<c>format_kubectl_pods</c>/
/// <c>format_kubectl_services</c>) were extracted to
/// <see cref="RtkSharp.Filters.Commands.Cloud.ContainerFilters"/> — this class calls them via a
/// fully-qualified reference (both classes share the simple name <c>ContainerFilters</c> in different
/// namespaces) from <see cref="RunK8sJsonAsync"/>'s <paramref name="filterFn"/> delegates below.
/// </remarks>
internal static class ContainerFilters
{
    // ===================== k8s pods =====================

    /// <summary>Faithful port of <c>k8s_pods</c> (<c>container.rs</c>:333-340).</summary>
    public static Task<int> RunK8sPodsAsync(string tool, IReadOnlyList<string> args, IProcessExecutor executor)
    {
        List<string> cmdArgs = ["get", "pods", "-o", "json", .. args];
        return RunK8sJsonAsync(cmdArgs, tool, "get pods", RtkSharp.Filters.Commands.Cloud.ContainerFilters.FormatKubectlPods, executor);
    }

    // ===================== k8s services =====================

    /// <summary>Faithful port of <c>k8s_services</c> (<c>container.rs</c>:419-426).</summary>
    public static Task<int> RunK8sServicesAsync(string tool, IReadOnlyList<string> args, IProcessExecutor executor)
    {
        List<string> cmdArgs = ["get", "services", "-o", "json", .. args];
        return RunK8sJsonAsync(cmdArgs, tool, "get services", RtkSharp.Filters.Commands.Cloud.ContainerFilters.FormatKubectlServices, executor);
    }

    // ===================== k8s logs =====================

    /// <summary>Faithful port of <c>k8s_logs</c> (<c>container.rs</c>:480-507).</summary>
    public static async Task<int> RunK8sLogsAsync(string tool, IReadOnlyList<string> args, IProcessExecutor executor)
    {
        var pod = args.Count > 0 ? args[0] : "";
        if (pod.Length == 0)
        {
            Console.Out.Write($"Usage: rtk {tool} logs <pod>\n");
            return 0;
        }

        List<string> cmdArgs = ["logs", "--tail", "100", pod, .. args.Skip(1)];
        var label = $"logs {pod}";
        var cmdLabel = $"{tool} {label}";

        var timer = TimedExecution.Start();
        var result = await ExecAsync(executor, tool, cmdArgs).ConfigureAwait(false);
        var raw = result.Stdout + result.Stderr;

        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                Console.Out.Write(result.Stdout);
            }

            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                Console.Error.Write(result.Stderr);
            }

            timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, raw);
            return result.ExitCode;
        }

        var filtered = $"Logs for {pod}:\n{LogFilters.AnalyzeLogs(result.Stdout)}";
        Console.Out.Write(filtered + "\n");
        timer.Track(cmdLabel, $"rtk {cmdLabel}", result.Stdout, filtered);
        return result.ExitCode;
    }

    // ===================== k8s get (pods/services alias resolution + passthrough) =====================

    /// <summary>Faithful port of <c>run_k8s_get</c> (<c>container.rs</c>:757-768), exposed publicly as
    /// <c>run_kubectl_get</c>/<c>run_oc_get</c> for the two tools.</summary>
    public static Task<int> RunK8sGetAsync(string tool, IReadOnlyList<string> args, int verbose, IProcessExecutor executor)
    {
        var target = K8sGetTarget(args);
        if (target is { Resource: "pods" } pods)
        {
            return RunK8sPodsAsync(tool, pods.Remaining, executor);
        }

        if (target is { Resource: "services" } services)
        {
            return RunK8sServicesAsync(tool, services.Remaining, executor);
        }

        List<string> passthroughArgs = ["get", .. args];
        return RunPassthroughAsync(tool, passthroughArgs, verbose, executor);
    }

    /// <summary>Faithful port of <c>k8s_get_target</c> (<c>container.rs</c>:770-782).</summary>
    internal static (string Resource, string[] Remaining)? K8sGetTarget(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return null;
        }

        var resource = args[0];
        var rest = args.Skip(1).ToArray();
        if (K8sGetRequestsRawOutput(rest))
        {
            return null;
        }

        return resource switch
        {
            "po" or "pod" or "pods" => ("pods", rest),
            "svc" or "service" or "services" => ("services", rest),
            _ => null,
        };
    }

    /// <summary>Faithful port of <c>k8s_get_requests_raw_output</c> (<c>container.rs</c>:784-792).</summary>
    internal static bool K8sGetRequestsRawOutput(IReadOnlyList<string> args) =>
        args.Any(arg =>
            arg is "-o" or "--output" or "-w" or "--watch" or "--show-labels" or "--show-kind"
            || arg.StartsWith("-o", StringComparison.Ordinal)
            || arg.StartsWith("--output=", StringComparison.Ordinal));

    // ===================== namespace/logs CLI-arg helpers =====================
    // Faithful port of main.rs's `build_k8s_namespace_args`/`build_k8s_logs_args` (main.rs:1363-1381),
    // shared by both KubectlCommand and OcCommand's `pods`/`services`/`logs` subcommands.

    /// <summary>Faithful port of <c>build_k8s_namespace_args</c> (<c>main.rs</c>:1363-1372).</summary>
    internal static List<string> BuildK8sNamespaceArgs(string? namespaceArg, bool all)
    {
        var args = new List<string>();
        if (all)
        {
            args.Add("-A");
        }
        else if (namespaceArg is not null)
        {
            args.Add("-n");
            args.Add(namespaceArg);
        }

        return args;
    }

    /// <summary>Faithful port of <c>build_k8s_logs_args</c> (<c>main.rs</c>:1374-1381).</summary>
    internal static List<string> BuildK8sLogsArgs(string pod, string? container)
    {
        var args = new List<string> { pod };
        if (container is not null)
        {
            args.Add("-c");
            args.Add(container);
        }

        return args;
    }

    // ===================== passthrough =====================

    /// <summary>Faithful port of <c>run_passthrough</c> (<c>core/runner.rs</c>:235-249): inherits the
    /// child's console streams directly, tracked as 0%-savings passthrough.</summary>
    public static async Task<int> RunPassthroughAsync(string tool, IReadOnlyList<string> args, int verbose, IProcessExecutor executor)
    {
        if (verbose > 0)
        {
            Console.Error.Write($"{tool} passthrough: [{string.Join(", ", args.Select(a => $"\"{a}\""))}]\n");
        }

        var timer = TimedExecution.Start();
        var fileName = PathResolver.Resolve(tool);
        var request = new ExecutionRequest(fileName, args, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await executor.ExecuteAsync(request).ConfigureAwait(false);

        var cmdLabel = $"{tool} {string.Join(' ', args)}".Trim();
        timer.TrackPassthrough(cmdLabel, $"rtk {cmdLabel} (passthrough)");
        return result.ExitCode;
    }

    // ===================== shared execution/json plumbing =====================

    /// <summary>Faithful port of <c>run_k8s_json</c> (<c>container.rs</c>:36-55): runs the given kubectl/oc
    /// JSON-producing command, and on success parses stdout as JSON and hands it to <paramref name="filterFn"/>;
    /// on a JSON parse failure, warns to stderr and passes stdout through unchanged. Uses
    /// <c>RunOptions::stdout_only().early_exit_on_failure().no_trailing_newline()</c> semantics: on a
    /// non-zero exit, prints raw stdout/stderr unfiltered and returns early; on success, prints the
    /// filtered text with NO added trailing newline (the filter functions themselves own their own
    /// trailing newline, or lack thereof).</summary>
    private static async Task<int> RunK8sJsonAsync(
        IReadOnlyList<string> cmdArgs,
        string tool,
        string label,
        Func<JsonElement, string> filterFn,
        IProcessExecutor executor)
    {
        var cmdLabel = $"{tool} {label}";
        var timer = TimedExecution.Start();
        var result = await ExecAsync(executor, tool, cmdArgs).ConfigureAwait(false);
        var raw = result.Stdout + result.Stderr;

        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                Console.Out.Write(result.Stdout);
            }

            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                Console.Error.Write(result.Stderr);
            }

            timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, raw);
            return result.ExitCode;
        }

        string filtered;
        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            filtered = filterFn(doc.RootElement.Clone());
        }
        catch (JsonException e)
        {
            Console.Error.Write($"[rtk] {tool}: JSON parse failed: {e.Message}\n");
            filtered = result.Stdout;
        }

        Console.Out.Write(filtered);
        timer.Track(cmdLabel, $"rtk {cmdLabel}", result.Stdout, filtered);
        return result.ExitCode;
    }

    private static Task<ExecutionResult> ExecAsync(IProcessExecutor executor, string tool, IReadOnlyList<string> args) =>
        executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve(tool), args)).AsTask();
}
