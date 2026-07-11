using System.Text;
using System.Text.Json;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure filtering logic for the <c>rtk json</c> CLI verb: renders JSON compactly with values
/// preserved (<see cref="FilterJsonCompact"/>) or as a keys-only schema view
/// (<see cref="FilterJsonSchema"/>). Ported from Rust <c>src/cmds/system/json_cmd.rs</c>. The
/// file/stdin reading, argument parsing, and tracking logic lives in
/// <see cref="RtkSharp.Commands.System.JsonCommand"/> (<c>RtkSharp</c>).
/// </summary>
public static class JsonFilters
{
    private const int MaxObjectKeysCompact = 20;
    private const int MaxObjectKeysSchema = 15;
    private const int MaxArrayItemsInline = 5;
    private const int CompactStringByteThreshold = 80;
    private const int CompactStringTruncateByteOffset = 77;
    private const int SchemaStringByteThreshold = 50;
    private const int SchemaDateLikeByteLength = 10;

    /// <summary>
    /// Parses a JSON string and returns a compact representation with values preserved (long strings
    /// truncated, arrays summarized). Faithful port of <c>filter_json_compact</c> (<c>json_cmd.rs</c>:91-94).
    /// </summary>
    /// <param name="jsonStr">The raw JSON text.</param>
    /// <param name="maxDepth">The maximum nesting depth to expand before eliding with <c>...</c>.</param>
    /// <returns>The compact rendering.</returns>
    /// <exception cref="JsonException">The input is not valid JSON.</exception>
    public static string FilterJsonCompact(string jsonStr, int maxDepth)
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
                    if (Utf8ByteLength(s) > CompactStringByteThreshold)
                    {
                        var truncated = FloorCharBoundaryTruncate(s, CompactStringTruncateByteOffset);
                        return $"{indent}\"{truncated}...\"";
                    }

                    return $"{indent}\"{s}\"";
                }

            case JsonValueKind.Array:
                {
                    var items = value.EnumerateArray().ToList();
                    if (items.Count == 0)
                    {
                        return $"{indent}[]";
                    }

                    if (items.Count > MaxArrayItemsInline)
                    {
                        var first = CompactJson(items[0], depth + 1, maxDepth);
                        return $"{indent}[{first.Trim()}, ... +{items.Count - 1} more]";
                    }

                    var allSimple = items.All(IsSimpleValue);
                    if (allSimple)
                    {
                        var inline = items.Select(v => CompactJson(v, depth + 1, maxDepth).Trim());
                        return $"{indent}[{string.Join(", ", inline)}]";
                    }

                    var lines = new List<string> { $"{indent}[" };
                    foreach (var item in items)
                    {
                        lines.Add($"{CompactJson(item, depth + 1, maxDepth)},");
                    }

                    lines.Add($"{indent}]");
                    return string.Join("\n", lines);
                }

            case JsonValueKind.Object:
                {
                    var properties = value.EnumerateObject().ToList();
                    if (properties.Count == 0)
                    {
                        return $"{indent}{{}}";
                    }

                    var lines = new List<string> { $"{indent}{{" };
                    var keys = properties.Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();

                    for (var i = 0; i < keys.Count; i++)
                    {
                        var key = keys[i];
                        var val = value.GetProperty(key);
                        var isSimple = IsSimpleValue(val);

                        if (isSimple)
                        {
                            var valStr = CompactJson(val, 0, maxDepth);
                            lines.Add($"{indent}  {key}: {valStr.Trim()}");
                        }
                        else
                        {
                            lines.Add($"{indent}  {key}:");
                            lines.Add(CompactJson(val, depth + 1, maxDepth));
                        }

                        if (i >= MaxObjectKeysCompact)
                        {
                            lines.Add($"{indent}  ... +{keys.Count - i - 1} more keys");
                            break;
                        }
                    }

                    lines.Add($"{indent}}}");
                    return string.Join("\n", lines);
                }

            default:
                return $"{indent}null";
        }
    }

    /// <summary>
    /// Parses a JSON string and returns its schema representation (types only, no values). Faithful
    /// port of <c>filter_json_string</c> (<c>json_cmd.rs</c>:182-185).
    /// </summary>
    /// <param name="jsonStr">The raw JSON text.</param>
    /// <param name="maxDepth">The maximum nesting depth to expand before eliding with <c>...</c>.</param>
    /// <returns>The schema rendering.</returns>
    /// <exception cref="JsonException">The input is not valid JSON.</exception>
    public static string FilterJsonSchema(string jsonStr, int maxDepth)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        return ExtractSchema(doc.RootElement, 0, maxDepth);
    }

    private static string ExtractSchema(JsonElement value, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);

        if (depth > maxDepth)
        {
            return $"{indent}...";
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return $"{indent}null";

            case JsonValueKind.True:
            case JsonValueKind.False:
                return $"{indent}bool";

            case JsonValueKind.Number:
                return $"{indent}{(IsI64(value) ? "int" : "float")}";

            case JsonValueKind.String:
                {
                    var s = value.GetString() ?? string.Empty;
                    var byteLen = Utf8ByteLength(s);
                    if (byteLen > SchemaStringByteThreshold)
                    {
                        return $"{indent}string[{byteLen}]";
                    }

                    if (s.Length == 0)
                    {
                        return $"{indent}string";
                    }

                    if (s.StartsWith("http", StringComparison.Ordinal))
                    {
                        return $"{indent}url";
                    }

                    if (s.Contains('-', StringComparison.Ordinal) && byteLen == SchemaDateLikeByteLength)
                    {
                        return $"{indent}date?";
                    }

                    return $"{indent}string";
                }

            case JsonValueKind.Array:
                {
                    var items = value.EnumerateArray().ToList();
                    if (items.Count == 0)
                    {
                        return $"{indent}[]";
                    }

                    var firstSchema = ExtractSchema(items[0], depth + 1, maxDepth);
                    var trimmed = firstSchema.Trim();
                    return items.Count == 1
                        ? $"{indent}[\n{firstSchema}\n{indent}]"
                        : $"{indent}[{trimmed}] ({items.Count})";
                }

            case JsonValueKind.Object:
                {
                    var properties = value.EnumerateObject().ToList();
                    if (properties.Count == 0)
                    {
                        return $"{indent}{{}}";
                    }

                    var lines = new List<string> { $"{indent}{{" };
                    var keys = properties.Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();

                    for (var i = 0; i < keys.Count; i++)
                    {
                        var key = keys[i];
                        var val = value.GetProperty(key);
                        var valSchema = ExtractSchema(val, depth + 1, maxDepth);
                        var valTrimmed = valSchema.Trim();
                        var isSimple = IsSimpleValue(val);

                        if (isSimple)
                        {
                            lines.Add(i < keys.Count - 1
                                ? $"{indent}  {key}: {valTrimmed},"
                                : $"{indent}  {key}: {valTrimmed}");
                        }
                        else
                        {
                            lines.Add($"{indent}  {key}:");
                            lines.Add(valSchema);
                        }

                        if (i >= MaxObjectKeysSchema)
                        {
                            lines.Add($"{indent}  ... +{keys.Count - i - 1} more keys");
                            break;
                        }
                    }

                    lines.Add($"{indent}}}");
                    return string.Join("\n", lines);
                }

            default:
                return $"{indent}null";
        }
    }

    private static bool IsSimpleValue(JsonElement value) => value.ValueKind is
        JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String;

    /// <summary>Mirrors Rust's <c>serde_json::Number::is_i64</c>: true when the number parses as a 64-bit integer.</summary>
    private static bool IsI64(JsonElement value) => value.TryGetInt64(out _);

    /// <summary>The UTF-8 byte length of <paramref name="s"/>, mirroring Rust's <c>str::len()</c>.</summary>
    public static int Utf8ByteLength(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// Truncates <paramref name="s"/> to the longest prefix whose UTF-8 byte length does not exceed
    /// <paramref name="maxBytes"/>, never splitting a multibyte character. Mirrors Rust's
    /// <c>s.floor_char_boundary(maxBytes)</c> followed by slicing.
    /// </summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="maxBytes">The maximum UTF-8 byte length of the returned prefix.</param>
    /// <returns>The truncated prefix.</returns>
    public static string FloorCharBoundaryTruncate(string s, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(s);

        var sb = new StringBuilder();
        var byteCount = 0;

        foreach (var rune in s.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes)
            {
                break;
            }

            sb.Append(rune.ToString());
            byteCount += runeBytes;
        }

        return sb.ToString();
    }
}
