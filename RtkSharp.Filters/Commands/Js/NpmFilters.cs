using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic for the <c>npm</c>/<c>npx</c> CLI proxy. Extracted from
/// <c>RtkSharp.Commands.Js.NpmCommand</c> (Task 7 of the filters-library extraction) — everything
/// here is a pure function of already-captured text, with no process execution or file I/O.
/// </summary>
public static class NpmFilters
{
    /// <summary>
    /// Filters npm/npx run output: strips script banners, npm's own <c>WARN</c>/<c>notice</c> lines,
    /// progress-spinner glyphs, short lines, and blank lines. If everything gets filtered out, prints
    /// the literal <c>"ok"</c>. Ports <c>filter_npm_output</c> (<c>npm_cmd.rs</c>:136-168) exactly.
    /// </summary>
    /// <param name="output">The raw <c>npm</c>/<c>npx</c> output to filter.</param>
    /// <returns>The filtered output, or <c>"ok"</c> when nothing remained.</returns>
    public static string FilterNpmOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            // Skip npm boilerplate (script banners like "> project@1.0.0 build").
            if (line.StartsWith('>') && line.Contains('@'))
            {
                continue;
            }

            var trimmedStart = line.TrimStart();

            // Skip npm lifecycle noise.
            if (trimmedStart.StartsWith("npm WARN", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmedStart.StartsWith("npm notice", StringComparison.Ordinal))
            {
                continue;
            }

            // Skip progress indicators. Rust's `&&` binds tighter than `||` here: the third condition
            // is "contains '...' AND is under 10 chars", not "contains '...' and (separately) under 10".
            if (line.Contains('⸩') || line.Contains('⸨') || (line.Contains("...", StringComparison.Ordinal) && line.Length < 10))
            {
                continue;
            }

            // Skip empty lines.
            if (line.Trim().Length == 0)
            {
                continue;
            }

            result.Add(line);
        }

        return result.Count == 0 ? "ok" : string.Join("\n", result);
    }
}
