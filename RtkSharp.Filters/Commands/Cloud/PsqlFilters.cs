using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Cloud;

/// <summary>
/// Pure filtering logic for the <c>rtk psql</c> proxy: compresses <c>psql</c>'s table/expanded
/// (<c>\x</c>) output. Extracted verbatim from <c>RtkSharp.Commands.Cloud.PsqlCommand</c> — signatures
/// and logic are unchanged, only visibility moved from <c>internal</c> to <c>public</c> and the
/// containing type from <c>PsqlCommand</c> to <c>PsqlFilters</c>. See
/// <c>RtkSharp.Commands.Cloud.PsqlCommand</c> for the process-execution/dispatch code that calls these
/// methods, and for the original Rust source pointers (<c>src/cmds/cloud/psql_cmd.rs</c>) preserved in
/// each method's XML doc.
/// </summary>
public static partial class PsqlFilters
{
    private const int MaxTableRows = 20;
    private const int MaxExpandedRecords = 20;

    [GeneratedRegex(@"-\[ RECORD \d+ \]-")]
    private static partial Regex ExpandedRecordRegex();

    [GeneratedRegex(@"^[-+]+$")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"^\(\d+ rows?\)$")]
    private static partial Regex RowCountRegex();

    [GeneratedRegex(@"^-\[ RECORD (\d+) \]-")]
    private static partial Regex RecordHeaderRegex();

    /// <summary>
    /// Faithful port of <c>filter_psql_output</c> (<c>psql_cmd.rs</c>:48-61): dispatches to the
    /// expanded (<c>\x</c>) or table filter based on which shape is detected, or passes non-table
    /// output (e.g. <c>COPY</c>/notices) through unchanged.
    /// </summary>
    /// <param name="output">The raw captured stdout.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterPsqlOutput(string output)
    {
        if (output.Trim().Length == 0)
        {
            return string.Empty;
        }

        if (IsExpandedFormat(output))
        {
            return FilterExpanded(output);
        }

        if (IsTableFormat(output))
        {
            return FilterTable(output);
        }

        return output;
    }

    /// <summary>Faithful port of <c>is_table_format</c> (<c>psql_cmd.rs</c>:63-68).</summary>
    /// <param name="output">The raw captured stdout.</param>
    /// <returns>True if any line contains a column-separator run.</returns>
    public static bool IsTableFormat(string output) =>
        SourceFilterLineSplitter.SplitLines(output).Any(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Contains("-+-") || trimmed.Contains("---+---");
        });

    /// <summary>Faithful port of <c>is_expanded_format</c> (<c>psql_cmd.rs</c>:70-72).</summary>
    /// <param name="output">The raw captured stdout.</param>
    /// <returns>True if a <c>-[ RECORD N ]-</c> header is present anywhere.</returns>
    public static bool IsExpandedFormat(string output) => ExpandedRecordRegex().IsMatch(output);

    /// <summary>
    /// Faithful port of <c>filter_table</c> (<c>psql_cmd.rs</c>:79-125): strips separator lines and
    /// the row-count footer, converts <c>|</c>-delimited columns to tab-separated, and caps data
    /// rows at <see cref="MaxTableRows"/> with an overflow count.
    /// </summary>
    /// <param name="output">The raw captured stdout.</param>
    /// <returns>The compacted, tab-separated table.</returns>
    public static string FilterTable(string output)
    {
        var result = new List<string>();
        var dataRows = 0;
        var totalRows = 0;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            var trimmed = line.Trim();

            if (SeparatorRegex().IsMatch(trimmed))
            {
                continue;
            }

            if (RowCountRegex().IsMatch(trimmed))
            {
                continue;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Contains('|'))
            {
                totalRows++;
                if (totalRows > 1)
                {
                    dataRows++;
                }

                if (dataRows <= MaxTableRows || totalRows == 1)
                {
                    var cols = trimmed.Split('|').Select(c => c.Trim());
                    result.Add(string.Join('\t', cols));
                }
            }
            else
            {
                result.Add(trimmed);
            }
        }

        if (dataRows > MaxTableRows)
        {
            result.Add($"... +{dataRows - MaxTableRows} more rows");
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Faithful port of <c>filter_expanded</c> (<c>psql_cmd.rs</c>:129-183): converts each
    /// <c>-[ RECORD N ]-</c> block to a one-liner <c>[N] key=val key2=val2</c> line, capping at
    /// <see cref="MaxExpandedRecords"/> with an overflow count.
    /// </summary>
    /// <param name="output">The raw captured stdout.</param>
    /// <returns>The compacted, one-line-per-record output.</returns>
    public static string FilterExpanded(string output)
    {
        var result = new List<string>();
        var currentPairs = new List<string>();
        string? currentRecord = null;
        var recordCount = 0;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            var trimmed = line.Trim();

            if (RowCountRegex().IsMatch(trimmed))
            {
                continue;
            }

            var headerMatch = RecordHeaderRegex().Match(trimmed);
            if (headerMatch.Success)
            {
                if (currentRecord is { } rec)
                {
                    if (recordCount <= MaxExpandedRecords)
                    {
                        result.Add($"{rec} {string.Join(' ', currentPairs)}");
                    }

                    currentPairs.Clear();
                }

                recordCount++;
                currentRecord = $"[{headerMatch.Groups[1].Value}]";
            }
            else if (trimmed.Contains('|') && currentRecord is not null)
            {
                var parts = trimmed.Split('|', 2);
                if (parts.Length == 2)
                {
                    currentPairs.Add($"{parts[0].Trim()}={parts[1].Trim()}");
                }
            }
            else if (trimmed.Length == 0)
            {
                continue;
            }
            else if (currentRecord is null)
            {
                result.Add(trimmed);
            }
        }

        if (currentRecord is { } lastRec)
        {
            if (recordCount <= MaxExpandedRecords)
            {
                result.Add($"{lastRec} {string.Join(' ', currentPairs)}");
            }
        }

        if (recordCount > MaxExpandedRecords)
        {
            result.Add($"... +{recordCount - MaxExpandedRecords} more records");
        }

        return string.Join('\n', result);
    }
}
