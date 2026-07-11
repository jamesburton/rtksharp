using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Filters directory listings into a compact tree format. Groups directories first (with a
/// trailing <c>/</c>), renders human-readable file sizes, and — when a long listing was
/// requested — prefixes each entry with its octal permissions.
/// </summary>
/// <remarks>
/// Ported faithfully from <c>src/cmds/system/ls.rs</c>. This class holds the pure filtering
/// logic; the process-execution entry point (<c>RunAsync</c>) lives in
/// <see cref="RtkSharp.Commands.System.LsCommand"/>.
/// </remarks>
public static partial class LsFilters
{
    /// <summary>
    /// Matches the date+time portion in <c>ls -la</c> output, which serves as a stable
    /// anchor regardless of owner/group column width. E.g. <c>" Mar 31 16:18 "</c> or
    /// <c>" Dec 25  2024 "</c>.
    /// </summary>
    private static readonly Regex LsDateRegex = BuildLsDateRegex();

    // Mirrors reduced(CAP_WARNINGS, 5) == 5 from src/core/truncate.rs — the maximum number
    // of file-extension buckets shown inline in the interactive summary line.
    private const int MaxExtSummary = 5;

    [GeneratedRegex(
        @"\s+(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}\s+(?:\d{4}|\d{2}:\d{2})\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BuildLsDateRegex();

    /// <summary>
    /// Filters raw <c>ls -la</c> output into the compact listing (entries only, no interactive
    /// summary), applying dir-first grouping, human sizes, noise filtering, and — when
    /// <paramref name="showLong"/> is set — octal permission prefixes. Falls back to the raw
    /// text unchanged when nothing parseable was found in otherwise-real content (e.g. a
    /// non-English locale). This is the deterministic, piped-mode form of the filter.
    /// </summary>
    /// <param name="raw">Raw stdout from <c>ls -la</c>.</param>
    /// <param name="showAll">When true, noise directories (e.g. <c>.git</c>, <c>target</c>) are kept.</param>
    /// <param name="showLong">When true, each entry is prefixed with its octal permissions.</param>
    /// <returns>The compacted listing.</returns>
    public static string FilterLs(string raw, bool showAll, bool showLong) =>
        ApplyFilter(raw, showAll, showLong, includeSummary: false);

    /// <summary>
    /// Core filter shared by <c>RunAsync</c> and <see cref="FilterLs"/>: runs
    /// <see cref="CompactLs"/>, applies the raw fallback, and optionally appends the summary.
    /// </summary>
    /// <param name="raw">Raw stdout from <c>ls -la</c>.</param>
    /// <param name="showAll">When true, noise directories are kept.</param>
    /// <param name="showLong">When true, entries are prefixed with octal permissions.</param>
    /// <param name="includeSummary">When true (interactive), append the summary line.</param>
    /// <returns>The compacted listing, or the raw text when it could not be parsed.</returns>
    public static string ApplyFilter(string raw, bool showAll, bool showLong, bool includeSummary)
    {
        var (entries, summary, parsedCount) = CompactLs(raw, showAll, showLong);

        // If no lines were parsed (e.g., unrecognized locale), fall back to raw output.
        // This is safer than returning "(empty)" for a non-empty directory.
        var hasRealContent = EnumerateLines(raw)
            .Any(l => !l.StartsWith("total ", StringComparison.Ordinal) && l.Length > 0 && !IsDotDir(l));
        if (parsedCount == 0 && hasRealContent)
        {
            return raw;
        }

        return includeSummary ? entries + summary : entries;
    }

    /// <summary>
    /// Detects whether the "show all" mode is requested: a bundled short flag containing
    /// <c>a</c> (e.g. <c>-la</c>) or the long form <c>--all</c>.
    /// </summary>
    /// <param name="args">The command arguments.</param>
    /// <returns>True when hidden/noise directories should be kept.</returns>
    public static bool DetectShowAll(string[] args) =>
        args.Any(a => (a.StartsWith('-') && !a.StartsWith("--") && a.Contains('a')) || a == "--all");

    /// <summary>
    /// Detects whether a long listing is requested. Per <c>man ls</c> it is triggered by
    /// <c>-l</c> and also implied by <c>-g</c>, <c>-n</c>, <c>-o</c>, <c>--full-time</c>, or
    /// GNU <c>--format=long</c>/<c>--format=verbose</c>. In any of these cases octal
    /// permissions are preserved.
    /// </summary>
    /// <param name="args">The command arguments.</param>
    /// <returns>True when entries should be prefixed with octal permissions.</returns>
    public static bool DetectShowLong(string[] args) =>
        args.Any(a =>
        {
            if (a is "--full-time" or "--format=long" or "--format=verbose")
            {
                return true;
            }

            if (a.StartsWith('-') && !a.StartsWith("--"))
            {
                return a.Any(c => c is 'l' or 'g' or 'n' or 'o');
            }

            return false;
        });

    /// <summary>Formats a byte count into a human-readable size (e.g. <c>2.3K</c>, <c>1.0M</c>).</summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>The formatted size string.</returns>
    public static string HumanSize(ulong bytes)
    {
        if (bytes >= 1_048_576)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}M", bytes / 1_048_576.0);
        }

        if (bytes >= 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}K", bytes / 1024.0);
        }

        return $"{bytes}B";
    }

    /// <summary>
    /// Parses a single <c>ls -la</c> line into <c>(fileType, perms, size, name)</c>, using the
    /// date field as a stable anchor so owner/group names containing spaces don't break
    /// parsing. Returns null for <c>.</c>/<c>..</c> entries and unrecognized lines.
    /// </summary>
    /// <param name="line">A single line of <c>ls -la</c> output.</param>
    /// <returns>The parsed tuple, or null if the line is not a parseable entry.</returns>
    public static (char FileType, string Perms, ulong Size, string Name)? ParseLsLine(string line)
    {
        // Skip . and .. entries before date parsing (works for non-English locales too).
        if (IsDotDir(line))
        {
            return null;
        }

        var dateMatch = LsDateRegex.Match(line);
        if (!dateMatch.Success)
        {
            return null;
        }

        var name = line[(dateMatch.Index + dateMatch.Length)..];

        var beforeDate = line[..dateMatch.Index];
        var beforeParts = beforeDate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (beforeParts.Length < 4)
        {
            return null;
        }

        var perms = beforeParts[0];
        var fileType = perms[0];

        // Size is the rightmost parseable number before the date. nlinks is also numeric but
        // appears earlier; scanning from the end guarantees we hit the size field first.
        ulong size = 0;
        for (var i = beforeParts.Length - 1; i >= 0; i--)
        {
            if (ulong.TryParse(beforeParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var s))
            {
                size = s;
                break;
            }
        }

        return (fileType, perms, size, name);
    }

    /// <summary>
    /// Returns true if the line represents a <c>.</c> or <c>..</c> directory entry, which
    /// <c>ls -la</c> always emits and which carry no meaningful content.
    /// </summary>
    /// <param name="line">A single line of <c>ls -la</c> output.</param>
    /// <returns>True when the trimmed line ends with <c>.</c> or <c>..</c>.</returns>
    private static bool IsDotDir(string line)
    {
        var trimmed = line.Trim();
        return trimmed.EndsWith('.') || trimmed.EndsWith("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// Converts an <c>ls</c>-style permission string (e.g. <c>-rw-r--r--</c>, <c>drwxr-xr-x</c>)
    /// into octal notation (e.g. <c>644</c>, <c>755</c>, <c>4755</c>). Special bits
    /// (setuid/setgid/sticky) are encoded as a leading 4th digit only when any are set.
    /// </summary>
    /// <param name="perms">The 10-character permission field from <c>ls</c>.</param>
    /// <returns>The octal permission string, or null if the input is not a permission field.</returns>
    public static string? PermsToOctal(string perms)
    {
        if (perms.Length < 10 || !IsAscii(perms))
        {
            return null;
        }

        var b = perms;

        var ownerX = b[3] is 'x' or 's';
        var groupX = b[6] is 'x' or 's';
        var otherX = b[9] is 'x' or 't';

        var owner = PermValue(b[1] == 'r', b[2] == 'w', ownerX);
        var group = PermValue(b[4] == 'r', b[5] == 'w', groupX);
        var other = PermValue(b[7] == 'r', b[8] == 'w', otherX);

        var setuid = b[3] is 's' or 'S';
        var setgid = b[6] is 's' or 'S';
        var sticky = b[9] is 't' or 'T';
        var special = PermValue(setuid, setgid, sticky);

        return special > 0
            ? $"{special}{owner}{group}{other}"
            : $"{owner}{group}{other}";

        static uint PermValue(bool read, bool write, bool exec) =>
            ((read ? 1u : 0u) << 2) | ((write ? 1u : 0u) << 1) | (exec ? 1u : 0u);
    }

    /// <summary>
    /// Parses <c>ls -la</c> output into the compact form: directories first (with trailing
    /// <c>/</c>), then files with sizes, optionally prefixed with octal permissions.
    /// </summary>
    /// <param name="raw">Raw stdout from <c>ls -la</c>.</param>
    /// <param name="showAll">When true, noise directories are kept.</param>
    /// <param name="showLong">When true, entries are prefixed with octal permissions.</param>
    /// <returns>
    /// A tuple of the entries block, the interactive summary block, and the count of
    /// successfully parsed non-header lines. When the count is 0 but the input had content,
    /// the caller falls back to raw output.
    /// </returns>
    public static (string Entries, string Summary, int ParsedCount) CompactLs(
        string raw, bool showAll, bool showLong)
    {
        var dirs = new List<(string Name, string? Octal)>();
        var files = new List<(string Name, string Size, string? Octal)>();
        var byExt = new Dictionary<string, int>(StringComparer.Ordinal);
        var linesSeen = 0;
        var parsedCount = 0;
        var dotdirs = 0;

        foreach (var line in EnumerateLines(raw))
        {
            if (line.StartsWith("total ", StringComparison.Ordinal) || line.Length == 0)
            {
                continue;
            }

            linesSeen++;

            var parsed = ParseLsLine(line);
            if (parsed is null)
            {
                if (IsDotDir(line))
                {
                    dotdirs++;
                }

                continue;
            }

            parsedCount++;
            var (fileType, perms, size, name) = parsed.Value;

            // Filter noise dirs unless -a.
            if (!showAll && SystemConstants.NoiseDirs.Any(noise => name == noise))
            {
                continue;
            }

            // Only parse perms when the user actually wants the long listing.
            var octal = showLong ? PermsToOctal(perms) : null;

            if (fileType == 'd')
            {
                dirs.Add((name, octal));
            }
            else
            {
                // Regular files, symlinks, character/block devices, pipes, sockets.
                var dot = name.LastIndexOf('.');
                var ext = dot >= 0 ? name[dot..] : "no ext";
                byExt[ext] = byExt.GetValueOrDefault(ext) + 1;
                files.Add((name, HumanSize(size), octal));
            }
        }

        if (dirs.Count == 0 && files.Count == 0)
        {
            if (linesSeen > 0 && parsedCount == 0)
            {
                if (dotdirs == linesSeen)
                {
                    // Only . and .. entries (empty directory).
                    return ("(empty)\n", string.Empty, 0);
                }

                // Real content that couldn't be parsed (e.g., non-English locale).
                return (string.Empty, string.Empty, 0);
            }

            return ("(empty)\n", string.Empty, 0);
        }

        var entries = new StringBuilder();

        // Dirs first, compact.
        foreach (var (name, octal) in dirs)
        {
            if (octal is not null)
            {
                entries.Append(octal).Append("  ");
            }

            entries.Append(name).Append("/\n");
        }

        // Files with size.
        foreach (var (name, size, octal) in files)
        {
            if (octal is not null)
            {
                entries.Append(octal).Append("  ");
            }

            entries.Append(name).Append("  ").Append(size).Append('\n');
        }

        // Summary line (separate so caller can suppress when piped).
        var summary = new StringBuilder();
        summary.Append(CultureInfo.InvariantCulture, $"\nSummary: {files.Count} files, {dirs.Count} dirs");
        if (byExt.Count > 0)
        {
            // Order by count desc; tie-break on extension name for deterministic output
            // (Rust iterates a HashMap, so its tie order is unspecified — this is stricter).
            var extCounts = byExt
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            var extParts = extCounts
                .Take(MaxExtSummary)
                .Select(kv => $"{kv.Value} {kv.Key}");
            summary.Append(" (");
            summary.Append(string.Join(", ", extParts));
            if (extCounts.Count > MaxExtSummary)
            {
                summary.Append(CultureInfo.InvariantCulture, $", +{extCounts.Count - MaxExtSummary} more");
            }

            summary.Append(')');
        }

        summary.Append('\n');

        return (entries.ToString(), summary.ToString(), parsedCount);
    }

    /// <summary>Splits text into lines on <c>\n</c>, trimming a trailing <c>\r</c> from each.</summary>
    /// <param name="text">The text to split.</param>
    /// <returns>The lines, matching Rust's <c>str::lines</c> semantics.</returns>
    private static IEnumerable<string> EnumerateLines(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            yield return line.EndsWith('\r') ? line[..^1] : line;
        }
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > '\x7f')
            {
                return false;
            }
        }

        return true;
    }
}
