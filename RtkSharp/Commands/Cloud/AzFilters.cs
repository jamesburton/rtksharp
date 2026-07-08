using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Pure filter and redaction functions for <c>rtk az</c>. A pure RtkSharp superset feature — no
/// Rust oracle exists for <c>az</c>, so these functions define their own compact, token-optimized
/// rendering rather than porting one. Design source of truth:
/// <c>docs/superpowers/specs/2026-07-08-az-command-module-design.md</c>. Mirrors the
/// <c>AwsCommand</c>/<see cref="AwsFilters"/> split; reuses <see cref="AwsFilters.FilterResult"/>
/// rather than duplicating an identical type.
/// </summary>
internal static class AzFilters
{
    // Matches AwsFilters.MaxItems (core/truncate.rs's CAP_LIST equivalent) for consistency.
    private const int MaxItems = 20;

    // ===================== account show / account list =====================

    /// <summary>Formats <c>az account show</c>'s single-object JSON.</summary>
    public static AwsFilters.FilterResult? FilterAccountShow(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var name = JStr(v, "name", "?");
            var id = JStr(v, "id", "?");
            var tenantId = JStr(v, "tenantId", "?");
            var state = JStr(v, "state", "?");
            var userName = JNestedStr(v, "user", "name", "?");
            var userType = JNestedStr(v, "user", "type", "?");
            return AwsFilters.FilterResult.New($"{name} ({id}) tenant:{tenantId} state:{state} user:{userName}({userType})");
        }
    }

    /// <summary>Formats <c>az account list</c>'s bare top-level array of the same object shape as <c>show</c>, marking the default subscription with <c>*</c>.</summary>
    public static AwsFilters.FilterResult? FilterAccountList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = v.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var acct in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var marker = JBoolStr(acct, "isDefault", "false") == "true" ? "*" : " ";
                var name = JStr(acct, "name", "?");
                var id = JStr(acct, "id", "?");
                var tenantId = JStr(acct, "tenantId", "?");
                var state = JStr(acct, "state", "?");
                result.Add($"{marker}{name} ({id}) tenant:{tenantId} state:{state}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "accounts");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    // ===================== group list / group show =====================

    private static string FormatGroup(JsonElement g)
    {
        var name = JStr(g, "name", "?");
        var location = JStr(g, "location", "?");
        var state = JNestedStr(g, "properties", "provisioningState", "?");

        var tagKeys = new List<string>();
        if (TryGetProp(g, "tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var t in tags.EnumerateObject())
            {
                tagKeys.Add(t.Name);
            }
        }

        var tagsSuffix = tagKeys.Count == 0 ? string.Empty : $" tags:[{string.Join(',', tagKeys)}]";
        return $"{name} {location} {state}{tagsSuffix}";
    }

    /// <summary>Formats <c>az group list</c>'s bare top-level array of resource-group objects.</summary>
    public static AwsFilters.FilterResult? FilterGroupList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = v.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var g in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatGroup(g));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "resource groups");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az group show</c>'s single resource-group object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterGroupShow(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return AwsFilters.FilterResult.New(FormatGroup(v));
        }
    }

    // ===================== deployment group list / deployment group show =====================

    private static string FormatDeployment(JsonElement d)
    {
        var name = JStr(d, "name", "?");
        var state = JNestedStr(d, "properties", "provisioningState", "?");
        var mode = JNestedStr(d, "properties", "mode", "?");
        var duration = JNestedStr(d, "properties", "duration", "?");

        var resourceCount = 0;
        if (TryGetProp(d, "properties", out var props) && TryGetProp(props, "outputResources", out var outRes)
            && outRes.ValueKind == JsonValueKind.Array)
        {
            resourceCount = outRes.GetArrayLength();
        }

        var line = $"{name} {state} {mode} dur:{duration} resources:{resourceCount}";

        if (TryGetProp(d, "properties", out var props2) && TryGetProp(props2, "parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object)
        {
            var paramPairs = new List<string>();
            foreach (var p in parameters.EnumerateObject())
            {
                if (!TryGetProp(p.Value, "value", out var val) || !IsSimpleJson(val))
                {
                    continue;
                }

                var valStr = val.ValueKind == JsonValueKind.String ? val.GetString()! : val.GetRawText();
                paramPairs.Add($"{p.Name}={valStr}");
            }

            if (paramPairs.Count > 0)
            {
                line += $"\n  params: {string.Join(", ", paramPairs)}";
            }
        }

        return line;
    }

    /// <summary>Formats <c>az deployment group list</c>'s bare top-level array of deployment objects.</summary>
    public static AwsFilters.FilterResult? FilterDeploymentGroupList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = v.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var d in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatDeployment(d));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "deployments");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az deployment group show</c>'s single deployment object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterDeploymentGroupShow(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return AwsFilters.FilterResult.New(FormatDeployment(v));
        }
    }

    // ===================== shared JSON helpers (local copy — not shared with AwsFilters, per spec) =====================

    private static bool TryParse(string jsonStr, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(jsonStr);
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    private static bool TryGetProp(JsonElement el, string prop, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string JStr(JsonElement el, string prop, string dflt) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt : dflt;

    private static string JNestedStr(JsonElement el, string prop, string inner, string dflt) =>
        TryGetProp(el, prop, out var v) ? JStr(v, inner, dflt) : dflt;

    private static string JBoolStr(JsonElement el, string prop, string dflt) =>
        TryGetProp(el, prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? (v.GetBoolean() ? "true" : "false")
            : dflt;

    private static bool IsSimpleJson(JsonElement v) =>
        v.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String;

    /// <summary>Faithful port of <c>join_with_overflow</c> (<c>core/utils.rs</c>:147-153), same as <c>AwsFilters</c>'s copy.</summary>
    private static string JoinWithOverflow(List<string> items, int total, int max, string label)
    {
        var text = string.Join('\n', items);
        if (total > max)
        {
            text += $"\n… +{total - max} more {label}";
        }

        return text;
    }
}
