using System.Globalization;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Compact filter for <c>wc</c> — strips redundant paths and alignment padding from raw
/// <c>wc</c> output. Single-file counts collapse to a terse form (<c>wc file</c> →
/// <c>30L 96W 978B</c>, <c>wc -l file</c> → <c>30</c>); multi-file listings become a compact
/// table with the common path prefix removed and the <c>total</c> row shown as <c>Σ</c>.
/// </summary>
/// <remarks>
/// Ported faithfully from <c>src/cmds/system/wc_cmd.rs</c>. The process-execution entry point
/// (<c>RunAsync</c>) lives in <see cref="RtkSharp.Commands.System.WcCommand"/>.
/// </remarks>
public static class WcFilters
{
    /// <summary>Which columns the user requested, controlling the compact output shape.</summary>
    public enum WcMode
    {
        /// <summary>Default: lines, words, bytes (rendered <c>NL NW NB</c>).</summary>
        Full,

        /// <summary>Lines only (<c>-l</c>).</summary>
        Lines,

        /// <summary>Words only (<c>-w</c>).</summary>
        Words,

        /// <summary>Bytes only (<c>-c</c>).</summary>
        Bytes,

        /// <summary>Chars only (<c>-m</c>).</summary>
        Chars,

        /// <summary>Multiple count flags combined — keep the compact numeric format.</summary>
        Mixed
    }

    /// <summary>
    /// Determines the output mode from the requested flags. A single count flag selects that
    /// column; combined or multiple count flags select <see cref="WcMode.Mixed"/>; no count
    /// flags select <see cref="WcMode.Full"/>. Combined short flags (e.g. <c>-lw</c>) are
    /// decomposed. Mirrors wc_cmd.rs's <c>detect_mode</c>.
    /// </summary>
    /// <param name="args">The <c>wc</c> arguments.</param>
    /// <returns>The detected mode.</returns>
    public static WcMode DetectMode(string[] args)
    {
        var flags = args.Where(a => a.StartsWith('-')).ToList();
        if (flags.Count == 0)
        {
            return WcMode.Full;
        }

        var hasL = false;
        var hasW = false;
        var hasC = false;
        var hasM = false;
        var flagCount = 0;

        foreach (var flag in flags)
        {
            foreach (var ch in flag.Skip(1))
            {
                switch (ch)
                {
                    case 'l':
                        hasL = true;
                        flagCount++;
                        break;
                    case 'w':
                        hasW = true;
                        flagCount++;
                        break;
                    case 'c':
                        hasC = true;
                        flagCount++;
                        break;
                    case 'm':
                        hasM = true;
                        flagCount++;
                        break;
                }
            }
        }

        if (flagCount == 0)
        {
            return WcMode.Full;
        }

        if (flagCount > 1)
        {
            return WcMode.Mixed;
        }

        if (hasL)
        {
            return WcMode.Lines;
        }

        if (hasW)
        {
            return WcMode.Words;
        }

        if (hasC)
        {
            return WcMode.Bytes;
        }

        return hasM ? WcMode.Chars : WcMode.Full;
    }

    /// <summary>
    /// Compacts raw <c>wc</c> output for the given <paramref name="mode"/>: single-line output
    /// (one file or stdin) is terse; multi-line output becomes a compact table. Mirrors
    /// wc_cmd.rs's <c>filter_wc_output</c>.
    /// </summary>
    /// <param name="raw">Raw stdout from <c>wc</c>.</param>
    /// <param name="mode">The detected output mode.</param>
    /// <returns>The compacted output.</returns>
    public static string FilterWcOutput(string raw, WcMode mode)
    {
        var lines = ReadFilters.SplitLines(raw.Trim());

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        return lines.Count == 1
            ? FormatSingleLine(lines[0], mode)
            : FormatMultiLine(lines, mode);
    }

    /// <summary>Formats a single <c>wc</c> output line (one file or stdin). Mirrors <c>format_single_line</c>.</summary>
    /// <param name="line">The single output line.</param>
    /// <param name="mode">The detected output mode.</param>
    /// <returns>The compacted line.</returns>
    private static string FormatSingleLine(string line, WcMode mode)
    {
        var parts = SplitWhitespace(line);

        switch (mode)
        {
            case WcMode.Lines:
            case WcMode.Words:
            case WcMode.Bytes:
            case WcMode.Chars:
                return parts.Length > 0 ? parts[0] : string.Empty;

            case WcMode.Full:
                return parts.Length >= 3
                    ? $"{parts[0]}L {parts[1]}W {parts[2]}B"
                    : line.Trim();

            case WcMode.Mixed:
                if (parts.Length >= 2)
                {
                    var lastIsPath = !IsNumeric(parts[^1]);
                    return lastIsPath
                        ? string.Join(' ', parts[..^1])
                        : string.Join(' ', parts);
                }

                return line.Trim();

            default:
                return line.Trim();
        }
    }

    /// <summary>Formats multiple <c>wc</c> lines as a compact table. Mirrors <c>format_multi_line</c>.</summary>
    /// <param name="lines">The output lines (per-file rows plus a trailing <c>total</c> row).</param>
    /// <param name="mode">The detected output mode.</param>
    /// <returns>The compacted table.</returns>
    private static string FormatMultiLine(IReadOnlyList<string> lines, WcMode mode)
    {
        var result = new List<string>();

        // Common directory prefix, to shorten displayed paths.
        var paths = lines
            .Select(line => SplitWhitespace(line))
            .Where(parts => parts.Length > 0)
            .Select(parts => parts[^1])
            .Where(p => p != "total")
            .ToList();

        var commonPrefix = FindCommonPrefix(paths);

        foreach (var line in lines)
        {
            var parts = SplitWhitespace(line);
            if (parts.Length == 0)
            {
                continue;
            }

            var isTotal = parts[^1] == "total";

            switch (mode)
            {
                case WcMode.Lines:
                case WcMode.Words:
                case WcMode.Bytes:
                case WcMode.Chars:
                    if (isTotal)
                    {
                        result.Add($"Σ {First(parts)}");
                    }
                    else
                    {
                        var name = StripPrefix(parts[^1], commonPrefix);
                        result.Add($"{First(parts)} {name}");
                    }

                    break;

                case WcMode.Full:
                    if (isTotal)
                    {
                        result.Add($"Σ {First(parts)}L {At(parts, 1)}W {At(parts, 2)}B");
                    }
                    else if (parts.Length >= 4)
                    {
                        var name = StripPrefix(parts[3], commonPrefix);
                        result.Add($"{parts[0]}L {parts[1]}W {parts[2]}B {name}");
                    }
                    else
                    {
                        result.Add(line.Trim());
                    }

                    break;

                case WcMode.Mixed:
                    if (isTotal)
                    {
                        var nums = parts[..^1];
                        result.Add($"Σ {string.Join(' ', nums)}");
                    }
                    else if (parts.Length >= 2)
                    {
                        var lastIsPath = !IsNumeric(parts[^1]);
                        if (lastIsPath)
                        {
                            var name = StripPrefix(parts[^1], commonPrefix);
                            var nums = parts[..^1];
                            result.Add($"{string.Join(' ', nums)} {name}");
                        }
                        else
                        {
                            result.Add(string.Join(' ', parts));
                        }
                    }
                    else
                    {
                        result.Add(line.Trim());
                    }

                    break;
            }
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Finds the longest common directory prefix (ending at a <c>/</c>) shared by all paths.
    /// Mirrors <c>find_common_prefix</c>: returns empty for zero/one paths or when no shared
    /// directory prefix exists.
    /// </summary>
    /// <param name="paths">The file paths.</param>
    /// <returns>The common prefix (including the trailing <c>/</c>), or empty.</returns>
    public static string FindCommonPrefix(IReadOnlyList<string> paths)
    {
        if (paths.Count <= 1)
        {
            return string.Empty;
        }

        var first = paths[0];
        var pos = first.LastIndexOf('/');
        if (pos < 0)
        {
            return string.Empty;
        }

        var prefix = first[..(pos + 1)];
        if (paths.All(p => p.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return prefix;
        }

        // Try shorter prefixes by removing right-most segments.
        var candidate = prefix;
        while (candidate.Length > 0)
        {
            if (paths.All(p => p.StartsWith(candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }

            var nextPos = candidate[..(candidate.Length - 1)].LastIndexOf('/');
            if (nextPos >= 0)
            {
                candidate = candidate[..(nextPos + 1)];
            }
            else
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>Strips <paramref name="prefix"/> from the front of <paramref name="path"/> if present. Mirrors <c>strip_prefix</c>.</summary>
    /// <param name="path">The path to shorten.</param>
    /// <param name="prefix">The prefix to remove.</param>
    /// <returns>The shortened path, or the original if the prefix does not match.</returns>
    public static string StripPrefix(string path, string prefix)
    {
        if (prefix.Length == 0)
        {
            return path;
        }

        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    private static string First(string[] parts) => parts.Length > 0 ? parts[0] : "0";

    private static string At(string[] parts, int index) => index < parts.Length ? parts[index] : "0";

    private static string[] SplitWhitespace(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // Rust's `parse::<u64>()` accepts only plain non-negative digits (no sign, no separators).
    private static bool IsNumeric(string s) =>
        ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
