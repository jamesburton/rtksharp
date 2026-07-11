using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure output-grouping logic for the <c>rtk grep</c> CLI verb: turns raw NUL-separated rg/grep
/// match output into a compact, per-file grouped, length-capped rendering. Ported from
/// <c>src/cmds/system/grep_cmd.rs</c>. The process-execution, argument-cluster-parsing, and
/// rg/grep-fallback dispatch logic lives in <see cref="RtkSharp.Commands.System.GrepCommand"/>
/// (<c>RtkSharp</c>).
/// </summary>
public static class GrepFilters
{
    /// <summary>
    /// The default per-file match cap used by <see cref="BuildGroupedOutput"/> when the caller
    /// does not supply an explicit value. Matches <c>Config</c>'s documented
    /// <c>Limits.GrepMaxPerFile</c> default of 25. This library is a pure dependency-free
    /// consumer with no knowledge of a caller's <c>~/.config/rtk/config.toml</c> (a library
    /// consumer such as CodeSharp has no such file at all), so callers that want a
    /// user-configured cap must resolve it themselves and pass it explicitly — matching the
    /// precedent set for <c>OutputParserSupport.TruncatePassthrough</c>'s
    /// <c>DefaultPassthroughMaxChars</c>.
    /// </summary>
    public const int DefaultGrepMaxPerFile = 25;

    /// <summary>
    /// Parses a single rg/grep match or context line of the form <c>file\0line[:-]content</c>.
    /// The underlying command is invoked with <c>-0</c> (rg) / <c>--null</c> (grep) so the filename
    /// is NUL-separated from <c>line[:-]content</c>; NUL cannot appear in file paths, so content or
    /// path colons never confuse the parser (issue #1436). This pattern is built from arbitrary
    /// captured output, so it is compiled once and reused.
    /// </summary>
    private static readonly Regex MatchLineRegex =
        new(@"^([^\x00]+)\x00(\d+)([:-])(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the grouped, capped, truncated output block from raw NUL-separated rg/grep output.
    /// Mirrors the grouping half of grep_cmd.rs's <c>run</c>, including the "never-worse" guard
    /// that falls back to the plain <c>file:line:content</c> rendering when grouping did not shrink
    /// the output.
    /// </summary>
    /// <param name="rawOutput">Raw NUL-separated output from rg/grep.</param>
    /// <param name="patternDisplay">The display form of the pattern(s).</param>
    /// <param name="maxLen">Maximum displayed line length.</param>
    /// <param name="maxResults">Maximum number of displayed result lines.</param>
    /// <param name="contextOnly">When true, show only the match context.</param>
    /// <param name="grepMaxPerFile">
    /// The maximum matches shown per file before an in-file overflow is folded into the overall
    /// <c>[+N more]</c> marker. Defaults to <see cref="DefaultGrepMaxPerFile"/> (25); pass the
    /// caller's configured <c>limits.grep_max_per_file</c> to honor a customized value.
    /// </param>
    /// <returns>The rendered output block (ending in a newline).</returns>
    public static string BuildGroupedOutput(
        string rawOutput,
        string patternDisplay,
        int maxLen,
        int maxResults,
        bool contextOnly,
        int grepMaxPerFile = DefaultGrepMaxPerFile)
    {
        Regex? contextRe = null;
        if (contextOnly)
        {
            try
            {
                contextRe = new Regex(
                    $".{{0,20}}{Regex.Escape(patternDisplay)}.*",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                contextRe = null;
            }
        }

        // Insertion order preserved to mirror Rust's HashMap.len() (file count) and per-file order.
        var byFile = new Dictionary<string, List<(int LineNum, bool IsMatch, string Content)>>(StringComparer.Ordinal);
        foreach (var line in ReadFilters.SplitLines(rawOutput))
        {
            var parsed = ParseMatchLine(line);
            if (parsed is not { } entry)
            {
                continue;
            }

            var cleaned = CleanLine(entry.Content, maxLen, contextRe, patternDisplay);
            if (!byFile.TryGetValue(entry.File, out var bucket))
            {
                bucket = [];
                byFile[entry.File] = bucket;
            }

            bucket.Add((entry.LineNum, entry.IsMatch, cleaned));
        }

        var totalMatches = byFile.Values.SelectMany(v => v).Count(e => e.IsMatch);

        var sb = new StringBuilder();
        sb.Append($"{totalMatches} matches in {byFile.Count} files:\n\n");

        var shown = 0;
        var files = byFile.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        foreach (var (file, entries) in files)
        {
            if (shown >= maxResults)
            {
                break;
            }

            var fileDisplay = CompactPath(file);
            foreach (var (lineNum, isMatch, content) in entries.Take(grepMaxPerFile))
            {
                if (shown >= maxResults)
                {
                    break;
                }

                sb.Append(isMatch
                    ? $"{fileDisplay}:{lineNum}:{content}\n"
                    : $"{fileDisplay}-{lineNum}-{content}\n");
                shown++;
            }
        }

        var totalLines = byFile.Values.Sum(v => v.Count);
        if (totalLines > shown)
        {
            sb.Append($"[+{totalLines - shown} more]\n");
        }

        var rtkOutput = sb.ToString();

        // Never-worse: show plain `file:line:content` (NUL -> `:`) if grouping didn't shrink it.
        var plain = rawOutput.Replace('\0', ':');
        return Utf8Len(rtkOutput) < Utf8Len(plain) ? rtkOutput : plain;
    }

    /// <summary>
    /// Parses a single rg/grep match or context line of the form <c>file\0line[:-]content</c>.
    /// Returns null for lines that do not match the expected shape. The bool is true for match
    /// lines (<c>:</c> separator) and false for context lines (<c>-</c> separator). Mirrors
    /// grep_cmd.rs's <c>parse_match_line</c>.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <returns>The parsed components, or null.</returns>
    public static (string File, int LineNum, bool IsMatch, string Content)? ParseMatchLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var m = MatchLineRegex.Match(line);
        if (!m.Success)
        {
            return null;
        }

        if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var lineNum))
        {
            return null;
        }

        var file = m.Groups[1].Value;
        var isMatch = m.Groups[3].Value == ":";
        var content = m.Groups[4].Value;
        return (file, lineNum, isMatch, content);
    }

    /// <summary>
    /// Trims and, when necessary, truncates a match line around the pattern so it fits within
    /// <paramref name="maxLen"/>. When <paramref name="contextRe"/> is provided and matches, a
    /// short context window is returned instead. Mirrors grep_cmd.rs's <c>clean_line</c>. Slicing
    /// operates on Unicode scalar values (matching Rust's <c>chars()</c>) so multibyte input is
    /// never split mid-character.
    /// </summary>
    /// <param name="line">The raw line content.</param>
    /// <param name="maxLen">The maximum length budget.</param>
    /// <param name="contextRe">Optional context-window regex, or null.</param>
    /// <param name="pattern">The search pattern (used to center truncation).</param>
    /// <returns>The cleaned line.</returns>
    public static string CleanLine(string line, int maxLen, Regex? contextRe, string pattern)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.Trim();

        if (contextRe is not null)
        {
            var m = contextRe.Match(trimmed);
            if (m.Success && Utf8Len(m.Value) <= maxLen)
            {
                return m.Value;
            }
        }

        if (Utf8Len(trimmed) <= maxLen)
        {
            return trimmed;
        }

        var lower = trimmed.ToLowerInvariant();
        var patternLower = pattern.ToLowerInvariant();
        var pos = lower.IndexOf(patternLower, StringComparison.Ordinal);

        var chars = trimmed.EnumerateRunes().ToList();
        var charLen = chars.Count;

        if (pos >= 0)
        {
            var charPos = lower[..pos].EnumerateRunes().Count();

            var start = Math.Max(0, charPos - maxLen / 3);
            var end = Math.Min(start + maxLen, charLen);
            start = end == charLen ? Math.Max(0, end - maxLen) : start;

            var slice = RunesToString(chars, start, end);
            if (start > 0 && end < charLen)
            {
                return $"...{slice}...";
            }

            return start > 0 ? $"...{slice}" : $"{slice}...";
        }

        var truncated = RunesToString(chars, 0, Math.Min(maxLen - 3, charLen));
        return $"{truncated}...";
    }

    /// <summary>
    /// Compacts a long <c>/</c>-separated path to <c>first/.../parent/name</c>. Paths at or under
    /// 50 bytes, or with three or fewer segments, are returned unchanged. Mirrors grep_cmd.rs's
    /// <c>compact_path</c>.
    /// </summary>
    /// <param name="path">The path to compact.</param>
    /// <returns>The compacted (or original) path.</returns>
    public static string CompactPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Utf8Len(path) <= 50)
        {
            return path;
        }

        var parts = path.Split('/');
        if (parts.Length <= 3)
        {
            return path;
        }

        return $"{parts[0]}/.../{parts[^2]}/{parts[^1]}";
    }

    /// <summary>Concatenates runes <c>[start, end)</c> into a string.</summary>
    private static string RunesToString(IReadOnlyList<Rune> runes, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            sb.Append(runes[i].ToString());
        }

        return sb.ToString();
    }

    /// <summary>Returns the UTF-8 byte length of <paramref name="s"/> (matching Rust's <c>str::len()</c>).</summary>
    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);
}
