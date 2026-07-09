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

    // Secret-shaped key names — starting list per the design spec, applied to both quoted
    // (JsonCompaction.Compact's "key: \"value\"" rendering) and unquoted ("key=value", used by
    // FormatDeployment's params line) styles.
    //
    // This is a deliberate, documented heuristic, not an exhaustive taxonomy: real-world secret
    // key names are effectively unbounded (any org can name a deployment parameter anything), so
    // this list only covers the shapes seen in practice so far. Extend it as new secret-shaped
    // key names are discovered in the wild rather than treating this as a closed set.
    private static readonly string[] SensitiveKeyNames =
    {
        "connectionString", "key", "keys", "password", "secret", "token", "sasToken",
        "primaryKey", "secondaryKey", "accessKey",
    };

    private static readonly Regex SensitiveKeyRegexQuoted = BuildSensitiveKeyRegexQuoted();
    private static readonly Regex SensitiveKeyRegexUnquoted = BuildSensitiveKeyRegexUnquoted();

    // Targets `az storage account keys list`'s real shape (confirmed via a live capture this
    // session): the secret's JSON field name is literally `value`, too generic to redact by key
    // name alone, so this instead anchors on the keyName/permissions/value sibling sequence that
    // JsonCompaction.Compact's alphabetical key ordering always produces.
    private static readonly Regex StorageAccountKeyValueRegex = new(
        @"(?<=keyName:\s*""key\d+"",?\s*\n\s*permissions:\s*""[^""]*"",?\s*\n\s*value:\s*"")[^""]*",
        RegexOptions.Compiled);

    // The `(?<![A-Za-z0-9_])[A-Za-z0-9]*` left side (in place of a plain `\b`) lets the key-name
    // alternation match as a case-insensitive SUFFIX of a longer camelCase identifier —
    // `adminPassword`, `sqlAdminPassword`, `clientSecret` — not just an exact whole-word key,
    // since that's the naming convention real Azure deployment parameters overwhelmingly use.
    // The lookbehind still anchors the match to the start of an identifier (so it can't begin
    // mid-word), and the trailing `\b` still rejects a sensitive word used as a PREFIX of an
    // unrelated word (`passwordless` does not match) because the next character after the
    // candidate suffix must be a non-identifier character (`:`/`=`/whitespace).
    private static Regex BuildSensitiveKeyRegexQuoted()
    {
        var joined = string.Join('|', SensitiveKeyNames.Select(Regex.Escape));
        return new Regex(
            $@"(?<prefix>(?<![A-Za-z0-9_])[A-Za-z0-9]*(?:{joined})\b\s*:\s*"")(?<value>[^""]*)(?="")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static Regex BuildSensitiveKeyRegexUnquoted()
    {
        var joined = string.Join('|', SensitiveKeyNames.Select(Regex.Escape));
        return new Regex(
            $@"(?<prefix>(?<![A-Za-z0-9_])[A-Za-z0-9]*(?:{joined})\b\s*=\s*)(?<value>[^\s,;]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

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

    // ===================== webapp list / webapp show =====================

    private static string FormatWebapp(JsonElement w)
    {
        var name = JStr(w, "name", "?");
        var state = JStr(w, "state", "?");
        var kind = JStr(w, "kind", "?");
        var location = JStr(w, "location", "?");
        var sku = JStr(w, "sku", "?");
        var https = JBoolStr(w, "httpsOnly", "?");
        var rg = JStr(w, "resourceGroup", "?");
        var host = JStr(w, "defaultHostName", "?");
        return $"{name} {state} {kind} {location} sku:{sku} https:{https} rg:{rg} host:{host}";
    }

    /// <summary>Formats <c>az webapp list</c>'s bare top-level array of App Service site objects.</summary>
    public static AwsFilters.FilterResult? FilterWebappList(string jsonStr)
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
            foreach (var w in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatWebapp(w));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "apps");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az webapp show</c>'s single App Service site object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterWebappShow(string jsonStr)
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

            return AwsFilters.FilterResult.New(FormatWebapp(v));
        }
    }

    // ===================== storage account list / storage account show =====================

    private static string FormatStorageAccount(JsonElement s)
    {
        var name = JStr(s, "name", "?");
        var kind = JStr(s, "kind", "?");
        var skuName = JNestedStr(s, "sku", "name", "?");
        var location = JStr(s, "location", "?");
        var tls = JStr(s, "minimumTlsVersion", "?");
        var https = JBoolStr(s, "enableHttpsTrafficOnly", "?");
        var tier = JStr(s, "accessTier", "?");
        var rg = JStr(s, "resourceGroup", "?");
        return $"{name} {kind} {skuName} {location} tls:{tls} https:{https} tier:{tier} rg:{rg}";
    }

    /// <summary>Formats <c>az storage account list</c>'s bare top-level array of storage account objects.</summary>
    public static AwsFilters.FilterResult? FilterStorageAccountList(string jsonStr)
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
            foreach (var s in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatStorageAccount(s));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "storage accounts");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az storage account show</c>'s single storage account object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterStorageAccountShow(string jsonStr)
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

            return AwsFilters.FilterResult.New(FormatStorageAccount(v));
        }
    }

    // ===================== functionapp list / functionapp show =====================

    private static string FormatFunctionApp(JsonElement w)
    {
        var name = JStr(w, "name", "?");
        var state = JStr(w, "state", "?");
        var kind = JStr(w, "kind", "?");
        var location = JStr(w, "location", "?");
        var sku = JStr(w, "sku", "?");
        var https = JBoolStr(w, "httpsOnly", "?");
        var rg = JStr(w, "resourceGroup", "?");
        var host = JStr(w, "defaultHostName", "?");
        var runtime = JNestedStr(w, "siteConfig", "linuxFxVersion", string.Empty);
        if (runtime.Length == 0)
        {
            runtime = JNestedStr(w, "siteConfig", "windowsFxVersion", "?");
        }

        return $"{name} {state} {kind} {location} sku:{sku} https:{https} rg:{rg} host:{host} runtime:{runtime}";
    }

    /// <summary>Formats <c>az functionapp list</c>'s bare top-level array of Function App site objects (same <c>Microsoft.Web/sites</c> shape as <c>webapp</c>, plus a runtime-stack field).</summary>
    public static AwsFilters.FilterResult? FilterFunctionAppList(string jsonStr)
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
            foreach (var w in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatFunctionApp(w));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "function apps");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az functionapp show</c>'s single Function App site object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterFunctionAppShow(string jsonStr)
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

            return AwsFilters.FilterResult.New(FormatFunctionApp(v));
        }
    }

    /// <summary>
    /// Redacts secret-shaped values from already-filtered <c>az</c> output. Deliberately does NOT
    /// redact subscription/tenant IDs (see the design spec's Redaction section) — real ARM resource
    /// IDs embed the subscription ID inline, and redacting them would break resource IDs users need
    /// for follow-up <c>az</c> commands. Applied by <c>AzCommand</c> to every named filter's result
    /// and to the generic JSON-compaction fallback's output, before printing.
    /// </summary>
    public static string Redact(string text)
    {
        text = StorageAccountKeyValueRegex.Replace(text, "[REDACTED]");
        text = SensitiveKeyRegexQuoted.Replace(text, "${prefix}[REDACTED]");
        text = SensitiveKeyRegexUnquoted.Replace(text, "${prefix}[REDACTED]");
        return text;
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
