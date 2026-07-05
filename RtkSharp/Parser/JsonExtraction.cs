namespace RtkSharp.Parser;

/// <summary>
/// Banner-prefix-tolerant JSON object extraction, used by tools whose stdout is a JSON blob
/// preceded by non-JSON banner lines (pnpm workspace scope lines, dotenv loader messages, etc.).
/// Faithful port of Rust <c>extract_json_object</c> (<c>src/parser/mod.rs:145-199</c>).
/// </summary>
public static class JsonExtraction
{
    /// <summary>The vitest-specific marker Rust tries first, since it is the most reliable anchor.</summary>
    private const string VitestMarker = "\"numTotalTests\"";

    /// <summary>
    /// Extracts the first complete JSON object from <paramref name="input"/>, tolerating a
    /// non-JSON prefix (banner lines).
    /// </summary>
    /// <remarks>
    /// Strategy, matching Rust exactly:
    /// <list type="number">
    /// <item>Find <c>"numTotalTests"</c> (a vitest-specific marker) and walk backward to that
    /// object's opening brace; if the marker is absent, fall back to the first line whose trimmed
    /// content starts with <c>{</c>.</item>
    /// <item>Brace-balance forward from that position (honoring string escapes, so braces inside
    /// string values don't confuse the balance) to find the matching closing brace.</item>
    /// <item>Return the slice spanning the complete JSON object.</item>
    /// </list>
    /// </remarks>
    /// <param name="input">The raw tool output, possibly with a non-JSON prefix.</param>
    /// <returns>The extracted JSON object text, or <see langword="null"/> if no valid JSON object was found.</returns>
    public static string? ExtractJsonObject(string input)
    {
        int startPos;
        var markerIndex = input.IndexOf(VitestMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            // Walk backward to find the opening brace of this object.
            var before = input[..markerIndex];
            var braceIndex = before.LastIndexOf('{');
            startPos = braceIndex >= 0 ? braceIndex : 0;
        }
        else
        {
            // Fallback: find the first `{` on its own line or after whitespace.
            var found = FindFirstBraceLineStart(input);
            if (found is null)
            {
                return null;
            }

            startPos = found.Value;
        }

        return BraceBalanceForward(input, startPos);
    }

    /// <summary>
    /// Locates the byte/char offset of the first line whose trimmed content starts with <c>{</c>,
    /// matching Rust's <c>input.lines().enumerate()</c> loop plus running offset accumulation
    /// (<c>src/parser/mod.rs:152-167</c>).
    /// </summary>
    private static int? FindFirstBraceLineStart(string input)
    {
        var offset = 0;
        foreach (var line in SplitLines(input))
        {
            if (line.TrimStart().StartsWith('{'))
            {
                return offset;
            }

            offset += line.Length + 1; // +1 for the '\n' Rust's summed `l.len() + 1` assumes.
        }

        return null;
    }

    /// <summary>
    /// Brace-balances forward from <paramref name="startPos"/>, honoring string escapes, to find
    /// the matching closing brace. Faithful port of <c>src/parser/mod.rs:170-198</c>.
    /// </summary>
    private static string? BraceBalanceForward(string input, int startPos)
    {
        var depth = 0;
        var inString = false;
        var escapeNext = false;

        for (var i = startPos; i < input.Length; i++)
        {
            var ch = input[i];

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escapeNext = true;
            }
            else if (ch == '"')
            {
                inString = !inString;
            }
            else if (ch == '{' && !inString)
            {
                depth++;
            }
            else if (ch == '}' && !inString)
            {
                depth--;
                if (depth == 0)
                {
                    return input[startPos..(i + 1)];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Splits <paramref name="input"/> into lines the same way Rust's <c>str::lines()</c> does:
    /// split on <c>\n</c>, with any trailing <c>\r</c> stripped from each line.
    /// </summary>
    private static IEnumerable<string> SplitLines(string input)
    {
        foreach (var line in input.Split('\n'))
        {
            yield return line.EndsWith('\r') ? line[..^1] : line;
        }
    }
}
