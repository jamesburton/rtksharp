using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic for the <c>next build</c> CLI proxy. Extracted from
/// <c>RtkSharp.Commands.Js.NextCommand</c> (Task 7 of the filters-library extraction) — everything
/// here is a pure function of already-captured text, with no process execution or file I/O.
/// </summary>
/// <remarks>
/// <b>Dead code preserved as a documented no-op, not silently dropped.</b> Rust declares a
/// <c>ROUTE_PATTERN</c> regex (<c>next_cmd.rs</c>:34-36) that is never actually used anywhere in
/// <c>filter_next_build</c> — route counting is done purely via <c>line.starts_with(...)</c> character
/// checks on the route-type symbols (<c>○ ● ◐ λ</c>), and only <c>BUNDLE_PATTERN</c> is matched via
/// regex. This port omits the equivalent of <c>ROUTE_PATTERN</c> entirely rather than porting genuinely
/// dead code, since it has zero observable effect on any output.
/// </remarks>
public static partial class NextFilters
{
    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs:7</c>) — the bundle-table row cap (<c>MAX_BUNDLES</c>, <c>next_cmd.rs</c>:117).</summary>
    private const int MaxBundles = 10;

    /// <summary>
    /// Matches a Next.js build-summary bundle row: route symbol, name/route, own size, and total
    /// (First Load JS) size. Faithful port of <c>BUNDLE_PATTERN</c> (<c>next_cmd.rs</c>:39-41).
    /// </summary>
    [GeneratedRegex(@"^[○●◐λ✓]\s+([\w/\-.]+)\s+(\d+(?:\.\d+)?)\s*(kB|B)\s+(\d+(?:\.\d+)?)\s*(kB|B)")]
    internal static partial Regex BundlePatternRegex();

    /// <summary>
    /// Matches the first numeric-plus-unit ("34.2s", "1250ms") token on a line. Faithful port of
    /// <c>TIME_RE</c> (<c>next_cmd.rs</c>:184).
    /// </summary>
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(s|ms)")]
    internal static partial Regex TimeRegex();

    /// <summary>
    /// Filters Next.js build output down to a route-count summary and a size-sorted bundle table.
    /// Faithful port of <c>filter_next_build</c> (<c>next_cmd.rs</c>:44-153).
    /// </summary>
    /// <param name="output">The raw <c>next build</c> output (stdout+stderr combined) to filter.</param>
    /// <returns>The condensed build summary.</returns>
    public static string FilterNextBuild(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var routesStatic = 0;
        var routesDynamic = 0;
        var routesTotal = 0;
        var bundles = new List<(string Route, double Size, double? PctChange)>();
        var warnings = 0;
        var errors = 0;
        var buildTime = string.Empty;

        var cleanOutput = Utils.StripAnsi(output);

        foreach (var line in SourceFilterLineSplitter.SplitLines(cleanOutput))
        {
            // Count route types by leading symbol.
            if (line.StartsWith('○'))
            {
                routesStatic++;
                routesTotal++;
            }
            else if (line.StartsWith('●') || line.StartsWith('◐'))
            {
                routesDynamic++;
                routesTotal++;
            }
            else if (line.StartsWith('λ'))
            {
                routesTotal++;
            }

            // Extract bundle information (route + size + total size).
            var match = BundlePatternRegex().Match(line);
            if (match.Success)
            {
                var route = match.Groups[1].Value;
                var size = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var total = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);

                double? pctChange = total > 0.0 ? ((total - size) / size) * 100.0 : null;

                bundles.Add((route, total, pctChange));
            }

            // Count warnings and errors.
            var lower = line.ToLowerInvariant();
            if (lower.Contains("warning", StringComparison.Ordinal))
            {
                warnings++;
            }

            if (lower.Contains("error", StringComparison.Ordinal) && !lower.Contains("0 error", StringComparison.Ordinal))
            {
                errors++;
            }

            // Extract build time.
            if (line.Contains("Compiled", StringComparison.Ordinal) || line.Contains("in", StringComparison.Ordinal))
            {
                var extracted = ExtractTime(line);
                if (extracted is not null)
                {
                    buildTime = extracted;
                }
            }
        }

        // Detect if build was skipped (already built).
        var alreadyBuilt = cleanOutput.Contains("already optimized", StringComparison.Ordinal)
            || cleanOutput.Contains("Cache", StringComparison.Ordinal)
            || (routesTotal == 0 && cleanOutput.Contains("Ready", StringComparison.Ordinal));

        var result = new StringBuilder();
        result.Append("Next.js Build\n");

        if (alreadyBuilt && routesTotal == 0)
        {
            result.Append("Already built (using cache)\n\n");
        }
        else if (routesTotal > 0)
        {
            result.Append($"{routesTotal} routes ({routesStatic} static, {routesDynamic} dynamic)\n\n");
        }

        if (bundles.Count > 0)
        {
            result.Append("Bundles:\n");

            // Sort by size (descending) and show top MaxBundles.
            bundles.Sort((a, b) => b.Size.CompareTo(a.Size));

            foreach (var (route, size, pctChange) in bundles.Take(MaxBundles))
            {
                var warningMarker = pctChange is { } pct && pct > 10.0
                    ? $" [warn] (+{pct.ToString("F0", CultureInfo.InvariantCulture)}%)"
                    : string.Empty;

                var truncatedRoute = Utils.Truncate(route, 30);
                var sizeStr = size.ToString("F0", CultureInfo.InvariantCulture);
                result.Append($"  {truncatedRoute,-30} {sizeStr,6} kB{warningMarker}\n");
            }

            if (bundles.Count > MaxBundles)
            {
                result.Append($"\n  ... +{bundles.Count - MaxBundles} more routes\n");
            }

            result.Append('\n');
        }

        // Show build time and status.
        if (buildTime.Length > 0)
        {
            result.Append($"Time: {buildTime} | ");
        }

        result.Append($"Errors: {errors} | Warnings: {warnings}\n");

        return result.ToString().Trim();
    }

    /// <summary>
    /// Extracts the first numeric-plus-unit time token (e.g. <c>"34.2s"</c>, <c>"1250ms"</c>) from a
    /// build-status line. Faithful port of <c>extract_time</c> (<c>next_cmd.rs</c>:181-190).
    /// </summary>
    /// <param name="line">The line to extract a time token from.</param>
    /// <returns>The extracted <c>"{number}{unit}"</c> token, or null if none is present.</returns>
    public static string? ExtractTime(string line)
    {
        var match = TimeRegex().Match(line);
        return match.Success ? $"{match.Groups[1].Value}{match.Groups[2].Value}" : null;
    }
}
