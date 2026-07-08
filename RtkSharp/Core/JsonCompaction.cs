using System;
using System.Linq;
using System.Text.Json;

namespace RtkSharp.Core;

/// <summary>
/// Generic, values-preserving JSON-compaction algorithm: long strings are truncated, large arrays
/// are summarized, and the result renders each key on its own line for readability. Faithful port
/// of <c>json_cmd::filter_json_compact</c>/<c>compact_json</c> (<c>src/cmds/system/json_cmd.rs</c>:
/// 91-178). Shared by <c>rtk aws</c>'s and <c>rtk az</c>'s generic (no-dedicated-filter) fallback
/// paths — extracted from <see cref="RtkSharp.Commands.Cloud.AwsFilters.FilterJsonCompact"/>, which
/// is now a one-line delegating wrapper preserving byte-identical output.
/// </summary>
public static class JsonCompaction
{
    /// <summary>Parses <paramref name="jsonStr"/> and returns its compact, values-preserving rendering.</summary>
    /// <param name="jsonStr">The raw JSON text to compact.</param>
    /// <param name="maxDepth">The maximum nesting depth to render before truncating with <c>...</c>.</param>
    /// <returns>The compact rendering.</returns>
    public static string Compact(string jsonStr, int maxDepth)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        return CompactJson(doc.RootElement, 0, maxDepth);
    }

    private static string CompactJson(JsonElement value, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);

        if (depth > maxDepth)
        {
            return $"{indent}...";
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return $"{indent}null";
            case JsonValueKind.True:
                return $"{indent}true";
            case JsonValueKind.False:
                return $"{indent}false";
            case JsonValueKind.Number:
                return $"{indent}{value.GetRawText()}";
            case JsonValueKind.String:
                {
                    var s = value.GetString() ?? string.Empty;
                    if (s.Length > 80)
                    {
                        var end = Math.Min(77, s.Length);
                        return $"{indent}\"{s[..end]}...\"";
                    }

                    return $"{indent}\"{s}\"";
                }

            case JsonValueKind.Array:
                {
                    var arr = value.EnumerateArray().ToList();
                    if (arr.Count == 0)
                    {
                        return $"{indent}[]";
                    }

                    if (arr.Count > 5)
                    {
                        var first = CompactJson(arr[0], depth + 1, maxDepth).Trim();
                        return $"{indent}[{first}, ... +{arr.Count - 1} more]";
                    }

                    if (arr.All(IsSimpleJson))
                    {
                        var inline = arr.Select(x => CompactJson(x, 0, maxDepth).Trim());
                        return $"{indent}[{string.Join(", ", inline)}]";
                    }

                    var arrLines = new System.Collections.Generic.List<string> { $"{indent}[" };
                    foreach (var item in arr)
                    {
                        arrLines.Add($"{CompactJson(item, depth + 1, maxDepth)},");
                    }

                    arrLines.Add($"{indent}]");
                    return string.Join('\n', arrLines);
                }

            case JsonValueKind.Object:
                {
                    var props = value.EnumerateObject().ToList();
                    if (props.Count == 0)
                    {
                        return $"{indent}{{}}";
                    }

                    var keys = props.Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();
                    var map = props.ToDictionary(p => p.Name, p => p.Value);
                    var objLines = new System.Collections.Generic.List<string> { $"{indent}{{" };

                    for (var i = 0; i < keys.Count; i++)
                    {
                        var key = keys[i];
                        var val = map[key];
                        if (IsSimpleJson(val))
                        {
                            var valStr = CompactJson(val, 0, maxDepth).Trim();
                            objLines.Add($"{indent}  {key}: {valStr}");
                        }
                        else
                        {
                            objLines.Add($"{indent}  {key}:");
                            objLines.Add(CompactJson(val, depth + 1, maxDepth));
                        }

                        if (i >= 20)
                        {
                            objLines.Add($"{indent}  ... +{keys.Count - i - 1} more keys");
                            break;
                        }
                    }

                    objLines.Add($"{indent}}}");
                    return string.Join('\n', objLines);
                }

            default:
                return $"{indent}null";
        }
    }

    private static bool IsSimpleJson(JsonElement v) =>
        v.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String;
}
