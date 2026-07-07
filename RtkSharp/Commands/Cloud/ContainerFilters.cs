using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Shared kubectl/oc filtering logic, faithfully ported from the generic, <c>tool: &amp;str</c>
/// -parameterized functions in Rust <c>src/cmds/cloud/container.rs</c> (<c>run_k8s_json</c>,
/// <c>k8s_pods</c>, <c>format_kubectl_pods</c>, <c>k8s_services</c>, <c>format_kubectl_services</c>,
/// <c>k8s_logs</c>, <c>run_k8s_get</c> (as <c>RunK8sGetAsync</c>), <c>k8s_get_target</c>,
/// <c>k8s_get_requests_raw_output</c>). Both <see cref="KubectlCommand"/> and <see cref="OcCommand"/>
/// call into these, since in Rust ~100% of this logic is shared between the two tools — only the
/// resolved binary name ("kubectl" vs "oc") differs.
/// </summary>
internal static class ContainerFilters
{
    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs</c>) — max pod issues shown before truncation.</summary>
    private const int CapWarnings = 10;

    /// <summary>Rust's <c>CAP_LIST</c> (<c>core/truncate.rs</c>) — max services shown before truncation.</summary>
    private const int CapList = 20;

    // ===================== k8s pods =====================

    /// <summary>Faithful port of <c>k8s_pods</c> (<c>container.rs</c>:333-340).</summary>
    public static Task<int> RunK8sPodsAsync(string tool, IReadOnlyList<string> args, IProcessExecutor executor)
    {
        List<string> cmdArgs = ["get", "pods", "-o", "json", .. args];
        return RunK8sJsonAsync(cmdArgs, tool, "get pods", FormatKubectlPods, executor);
    }

    /// <summary>Faithful port of <c>format_kubectl_pods</c> (<c>container.rs</c>:342-417).</summary>
    internal static string FormatKubectlPods(JsonElement json)
    {
        var pods = GetNonEmptyArray(json, "items");
        if (pods is null)
        {
            return "No pods found\n";
        }

        int running = 0, pending = 0, failed = 0;
        long restartsTotal = 0;
        var issues = new List<string>();

        foreach (var pod in pods)
        {
            var ns = GetStringProp(GetProp(pod, "metadata"), "namespace", "-");
            var name = GetStringProp(GetProp(pod, "metadata"), "name", "-");
            var phase = GetStringProp(GetProp(pod, "status"), "phase", "Unknown");

            var containerStatuses = GetArray(GetProp(pod, "status"), "containerStatuses");
            foreach (var c in containerStatuses)
            {
                restartsTotal += GetInt64Prop(c, "restartCount");
            }

            switch (phase)
            {
                case "Running":
                    running++;
                    break;
                case "Pending":
                    pending++;
                    issues.Add($"{ns}/{name} Pending");
                    break;
                case "Failed" or "Error":
                    failed++;
                    issues.Add($"{ns}/{name} {phase}");
                    break;
                default:
                    foreach (var c in containerStatuses)
                    {
                        var waiting = GetProp(GetProp(c, "state"), "waiting");
                        if (TryGetStringProp(waiting, "reason", out var w)
                            && (w.Contains("CrashLoop", StringComparison.Ordinal) || w.Contains("Error", StringComparison.Ordinal)))
                        {
                            failed++;
                            issues.Add($"{ns}/{name} {w}");
                        }
                    }

                    break;
            }
        }

        var parts = new List<string>();
        if (running > 0)
        {
            parts.Add($"{running}");
        }

        if (pending > 0)
        {
            parts.Add($"{pending} pending");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} [x]");
        }

        if (restartsTotal > 0)
        {
            parts.Add($"{restartsTotal} restarts");
        }

        var out_ = $"{pods.Count} pods: {string.Join(", ", parts)}\n";
        if (issues.Count > 0)
        {
            out_ += "[warn] Issues:\n";
            foreach (var issue in issues.Take(CapWarnings))
            {
                out_ += $"  {issue}\n";
            }

            if (issues.Count > CapWarnings)
            {
                out_ += $"  … +{issues.Count - CapWarnings} more";
                var allIssues = string.Join('\n', issues);
                if (Tee.ForceTeeTailHint(allIssues, "kubectl-pods", CapWarnings + 1) is { } hint)
                {
                    out_ += $" {hint}";
                }
            }
        }

        return out_;
    }

    // ===================== k8s services =====================

    /// <summary>Faithful port of <c>k8s_services</c> (<c>container.rs</c>:419-426).</summary>
    public static Task<int> RunK8sServicesAsync(string tool, IReadOnlyList<string> args, IProcessExecutor executor)
    {
        List<string> cmdArgs = ["get", "services", "-o", "json", .. args];
        return RunK8sJsonAsync(cmdArgs, tool, "get services", FormatKubectlServices, executor);
    }

    /// <summary>Faithful port of <c>format_kubectl_services</c> (<c>container.rs</c>:428-478).</summary>
    internal static string FormatKubectlServices(JsonElement json)
    {
        var services = GetNonEmptyArray(json, "items");
        if (services is null)
        {
            return "No services found\n";
        }

        var out_ = $"{services.Count} services:\n";

        var allLines = services.Select(svc =>
        {
            var ns = GetStringProp(GetProp(svc, "metadata"), "namespace", "-");
            var name = GetStringProp(GetProp(svc, "metadata"), "name", "-");
            var svcType = GetStringProp(GetProp(svc, "spec"), "type", "-");
            var ports = GetArray(GetProp(svc, "spec"), "ports").Select(p =>
            {
                var port = GetInt64Prop(p, "port");
                var target = TryGetInt64Prop(p, "targetPort", out var t)
                    ? t
                    : TryGetStringProp(p, "targetPort", out var s) && long.TryParse(s, out var parsed)
                        ? parsed
                        : port;
                return port == target ? $"{port}" : $"{port}→{target}";
            });
            return $"  {ns}/{name} {svcType} [{string.Join(",", ports)}]";
        }).ToList();

        foreach (var line in allLines.Take(CapList))
        {
            out_ += $"{line}\n";
        }

        if (allLines.Count > CapList)
        {
            out_ += $"  … +{allLines.Count - CapList} more";
            var allText = string.Join('\n', allLines);
            if (Tee.ForceTeeTailHint(allText, "kubectl-services", CapList + 1) is { } hint)
            {
                out_ += $" {hint}";
            }

            out_ += "\n";
        }

        return out_;
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

        var filtered = $"Logs for {pod}:\n{LogCommand.AnalyzeLogs(result.Stdout)}";
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

    // ===================== JSON access helpers =====================
    // serde_json::Value indexing (`json["field"]`) returns `Value::Null` for missing keys rather than
    // throwing/panicking, and `.as_str()`/`.as_array()`/`.as_i64()` return `None` for a type mismatch —
    // these helpers replicate that permissive, never-throwing lookup behavior over `JsonElement`.

    private static JsonElement GetProp(JsonElement element, string prop)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(prop, out var value))
        {
            return value;
        }

        return default;
    }

    private static string GetStringProp(JsonElement element, string prop, string fallback) =>
        TryGetStringProp(element, prop, out var value) ? value : fallback;

    private static bool TryGetStringProp(JsonElement element, string prop, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }

    private static long GetInt64Prop(JsonElement element, string prop) =>
        TryGetInt64Prop(element, prop, out var value) ? value : 0;

    private static bool TryGetInt64Prop(JsonElement element, string prop, out long value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n))
        {
            value = n;
            return true;
        }

        value = 0;
        return false;
    }

    private static List<JsonElement> GetArray(JsonElement element, string prop)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.Array)
        {
            return v.EnumerateArray().ToList();
        }

        return [];
    }

    /// <summary>Mirrors <c>json["items"].as_array().filter(|a| !a.is_empty())</c> — returns null both
    /// when the property is missing/non-array AND when it is an empty array.</summary>
    private static List<JsonElement>? GetNonEmptyArray(JsonElement element, string prop)
    {
        var array = GetArray(element, prop);
        return array.Count > 0 ? array : null;
    }
}
