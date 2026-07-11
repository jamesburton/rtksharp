using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure log-deduplication/summarization logic for <c>rtk log</c>, also reused as shared
/// infrastructure by <c>rtk docker logs</c>/<c>rtk docker compose logs</c>. Faithful port of Rust
/// <c>src/cmds/system/log_cmd.rs</c>'s pure functions. Extracted early (ahead of Task 14's System
/// ecosystem sweep) from <c>RtkSharp.Commands.System.LogCommand</c> specifically to unblock
/// <c>RtkSharp.Filters.Commands.Cloud.DockerFilters.FormatComposeLogs</c>, which the extraction
/// brief always intended to move but which was deferred pending this method's availability in
/// <c>RtkSharp.Filters</c> (see <c>DockerFilters</c>'s prior remarks, now removed). Only
/// <see cref="AnalyzeLogs"/> and the private helpers it exclusively depends on
/// (<see cref="TruncateLogLine"/>, <see cref="NormalizeLogLine"/>) moved — the rest of
/// <c>LogCommand</c> (<c>Run</c>/<c>RunFile</c>/<c>RunStdin</c>, which execute process I/O and write
/// to <c>Console</c>) remains impure infrastructure and is still Task 14's job.
/// </summary>
public static class LogFilters
{
    private static readonly Regex TimestampRe = new(
        @"^\d{4}[-/]\d{2}[-/]\d{2}[T ]\d{2}:\d{2}:\d{2}[.,]?\d*\s*", RegexOptions.Compiled);

    private static readonly Regex UuidRe = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private static readonly Regex HexRe = new(@"0x[0-9a-fA-F]+", RegexOptions.Compiled);

    private static readonly Regex NumRe = new(@"\b\d{4,}\b", RegexOptions.Compiled);

    private static readonly Regex PathRe = new(@"/[\w./\-]+", RegexOptions.Compiled);

    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs:7</c>) — the max unique errors shown.</summary>
    private const int MaxLogErrors = 10;

    /// <summary>Rust's <c>reduced(CAP_WARNINGS, 5)</c> (<c>core/truncate.rs</c>) — the max unique warnings shown.</summary>
    private const int MaxLogWarns = 5;

    /// <summary>
    /// Analyzes log content and returns the deduplicated summary — the reusable entry point Rust's
    /// <c>run_stdin_str</c> (<c>log_cmd.rs</c>:64-66) exposes for other modules (<c>docker logs</c>,
    /// <c>docker compose logs</c>).
    /// </summary>
    /// <param name="content">The raw log content.</param>
    /// <returns>The summary text.</returns>
    public static string AnalyzeLogs(string content)
    {
        var errorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var infoCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var uniqueErrors = new List<string>();
        var uniqueWarnings = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(content))
        {
            var lineLower = line.ToLowerInvariant();
            var normalized = NormalizeLogLine(line);

            if (lineLower.Contains("error", StringComparison.Ordinal)
                || lineLower.Contains("fatal", StringComparison.Ordinal)
                || lineLower.Contains("panic", StringComparison.Ordinal)
                || lineLower.Contains("critical", StringComparison.Ordinal)
                || lineLower.Contains("alert", StringComparison.Ordinal)
                || lineLower.Contains("emerg", StringComparison.Ordinal)
                || lineLower.Contains("severe", StringComparison.Ordinal))
            {
                var count = errorCounts.GetValueOrDefault(normalized);
                if (count == 0)
                {
                    uniqueErrors.Add(line);
                }

                errorCounts[normalized] = count + 1;
            }
            else if (lineLower.Contains("warn", StringComparison.Ordinal) || lineLower.Contains("notice", StringComparison.Ordinal))
            {
                var count = warnCounts.GetValueOrDefault(normalized);
                if (count == 0)
                {
                    uniqueWarnings.Add(line);
                }

                warnCounts[normalized] = count + 1;
            }
            else if (lineLower.Contains("info", StringComparison.Ordinal))
            {
                infoCounts[normalized] = infoCounts.GetValueOrDefault(normalized) + 1;
            }
        }

        var totalErrors = errorCounts.Values.Sum();
        var totalWarnings = warnCounts.Values.Sum();
        var totalInfo = infoCounts.Values.Sum();

        var result = new List<string>
        {
            "Log Summary",
            $"   [error] {totalErrors} errors ({errorCounts.Count} unique)",
            $"   [warn] {totalWarnings} warnings ({warnCounts.Count} unique)",
            $"   [info] {totalInfo} info messages",
            "",
        };

        if (uniqueErrors.Count > 0)
        {
            result.Add("[ERRORS]");

            var errorList = errorCounts.OrderByDescending(kv => kv.Value).ToList();
            foreach (var (normalized, count) in errorList.Take(MaxLogErrors))
            {
                var original = uniqueErrors.FirstOrDefault(e => NormalizeLogLine(e) == normalized) ?? normalized;
                var truncated = TruncateLogLine(original);
                result.Add(count > 1 ? $"   [×{count}] {truncated}" : $"   {truncated}");
            }

            if (errorList.Count > MaxLogErrors)
            {
                result.Add($"   ... +{errorList.Count - MaxLogErrors} more unique errors");
            }

            result.Add("");
        }

        if (uniqueWarnings.Count > 0)
        {
            result.Add("[WARNINGS]");

            var warnList = warnCounts.OrderByDescending(kv => kv.Value).ToList();
            foreach (var (normalized, count) in warnList.Take(MaxLogWarns))
            {
                var original = uniqueWarnings.FirstOrDefault(w => NormalizeLogLine(w) == normalized) ?? normalized;
                var truncated = TruncateLogLine(original);
                result.Add(count > 1 ? $"   [×{count}] {truncated}" : $"   {truncated}");
            }

            if (warnList.Count > MaxLogWarns)
            {
                result.Add($"   ... +{warnList.Count - MaxLogWarns} more unique warnings");
            }
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Truncates a log line to 97 Unicode scalar values plus <c>"..."</c> when its UTF-8 byte length
    /// exceeds 100 — the same byte-length-check/rune-count-truncate split as the Rust source
    /// (<c>original.len() &gt; 100</c> is a byte count; <c>original.chars().take(97)</c> is a rune
    /// count), never a naive <see cref="string.Length"/> (UTF-16 code unit) truncation.
    /// </summary>
    /// <param name="original">The original log line.</param>
    /// <returns>The (possibly truncated) line.</returns>
    private static string TruncateLogLine(string original)
    {
        if (Encoding.UTF8.GetByteCount(original) <= 100)
        {
            return original;
        }

        return string.Concat(original.EnumerateRunes().Take(97).Select(r => r.ToString())) + "...";
    }

    /// <summary>
    /// Normalizes a log line for deduplication: strips a leading timestamp, then replaces UUIDs, hex
    /// literals, 4+ digit numbers, and path-like segments with placeholder tokens. Faithful port of
    /// <c>normalize_log_line</c> (<c>log_cmd.rs</c>:219-233).
    /// </summary>
    /// <param name="line">The raw log line.</param>
    /// <returns>The normalized line, trimmed.</returns>
    internal static string NormalizeLogLine(string line)
    {
        var normalized = TimestampRe.Replace(line, "");
        normalized = UuidRe.Replace(normalized, "<UUID>");
        normalized = HexRe.Replace(normalized, "<HEX>");
        normalized = NumRe.Replace(normalized, "<NUM>");
        normalized = PathRe.Replace(normalized, "<PATH>");
        return normalized.Trim();
    }
}
