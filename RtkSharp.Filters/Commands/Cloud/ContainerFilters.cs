using System.Text.Json;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Cloud;

/// <summary>
/// Pure JSON-formatting logic shared by <c>rtk kubectl</c>/<c>rtk oc</c>: renders a parsed
/// <c>kubectl get pods -o json</c>/<c>get services -o json</c> document into a compact,
/// token-optimized summary. Extracted verbatim from
/// <c>RtkSharp.Commands.Cloud.ContainerFilters</c>'s <c>FormatKubectlPods</c>/
/// <c>FormatKubectlServices</c> — signatures and logic are unchanged, only visibility moved from
/// <c>internal</c> to <c>public</c> and the containing type moved to this
/// <c>RtkSharp.Filters.Commands.Cloud.ContainerFilters</c> class (same simple name, different
/// namespace — disambiguate with a fully-qualified reference at call sites in the impure remainder).
/// The process-execution/dispatch code that calls these methods (<c>RunK8sPodsAsync</c>,
/// <c>RunK8sServicesAsync</c>, <c>RunK8sJsonAsync</c>, and every other <c>kubectl</c>/<c>oc</c> helper
/// not listed here) stays in <c>RtkSharp.Commands.Cloud.ContainerFilters</c> — see that class for the
/// original Rust source pointers (<c>src/cmds/cloud/container.rs</c>) preserved in each method's XML
/// doc. Only the two <see cref="JsonElement"/>-typed formatters below moved in this task; a
/// <c>string</c>-&gt;<see cref="JsonElement"/> parsing wrapper is deliberately deferred to a later
/// task's <c>FilterRegistry</c> work, not added here.
/// </summary>
public static class ContainerFilters
{
    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs</c>) — max pod issues shown before truncation.</summary>
    private const int CapWarnings = 10;

    /// <summary>Rust's <c>CAP_LIST</c> (<c>core/truncate.rs</c>) — max services shown before truncation.</summary>
    private const int CapList = 20;

    /// <summary>Faithful port of <c>format_kubectl_pods</c> (<c>container.rs</c>:342-417).</summary>
    public static string FormatKubectlPods(JsonElement json)
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

    /// <summary>Faithful port of <c>format_kubectl_services</c> (<c>container.rs</c>:428-478).</summary>
    public static string FormatKubectlServices(JsonElement json)
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
