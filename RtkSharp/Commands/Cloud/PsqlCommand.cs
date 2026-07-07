using System.Text.RegularExpressions;
using RtkSharp.Core;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk psql</c> CLI verb: runs the real <c>psql</c> binary and compresses its
/// table/expanded (<c>\x</c>) output. Faithful port of <c>src/cmds/cloud/psql_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>PASSTHROUGH-classified, not a meta-command.</b> Rust's own <c>RTK_META_COMMANDS</c> list does
/// not include <c>"psql"</c> — <c>main.rs</c>'s <c>test_every_subcommand_is_classified</c> lists it
/// under <c>PASSTHROUGH</c>. Its clap arm is a single <c>trailing_var_arg = true,
/// allow_hyphen_values = true</c> catch-all with no positional/flag validation of its own, so there
/// is no clap-equivalent rejection to reproduce — this port never throws
/// <see cref="CommandArgumentParseException"/>.
/// </para>
/// <para>
/// <b><c>#[command(disable_help_flag = true)]</c> has no C# analog needed.</b> That attribute only
/// stops clap itself from intercepting <c>-h</c>/<c>--help</c> before they reach
/// <c>psql_cmd::run</c>'s trailing args; RtkSharp's own dispatch layer has no global help
/// interceptor to disable in the first place; a literal <c>--help</c>/<c>-h</c> token already flows
/// through to the real <c>psql</c> binary unchanged, matching the intended behavior automatically.
/// </para>
/// <para>
/// <b>Early-exit-on-failure, stdout-only filtering, tee'd raw output</b> — ported via
/// <see cref="CommandRunner.RunFilteredAsync"/>'s <see cref="RunOptions"/>
/// (<c>SkipFilterOnFailure: true</c>, <c>FilterStdoutOnly: true</c>, <c>TeeLabel: "psql"</c>),
/// mirroring Rust's <c>RunOptions::stdout_only().tee("psql").early_exit_on_failure()</c> exactly.
/// </para>
/// </remarks>
public static partial class PsqlCommand
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
    /// Registry entry point. Runs <c>rtk psql</c> with the given arguments (the remainder after the
    /// <c>psql</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>psql</c>, forwarded verbatim to real <c>psql</c>.</param>
    /// <returns>psql's real exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity);

    /// <summary>
    /// The testable core of <see cref="RunAsync(string[])"/>.
    /// </summary>
    /// <param name="args">The arguments to forward to <c>psql</c>.</param>
    /// <param name="verbose">The top-level verbosity count; &gt; 0 prints the resolved command line to stderr.</param>
    /// <returns>psql's real exit code.</returns>
    public static Task<int> RunAsync(string[] args, int verbose)
    {
        if (verbose > 0)
        {
            Console.Error.Write($"Running: psql {string.Join(' ', args)}\n");
        }

        return CommandRunner.RunFilteredAsync(
            "psql",
            args,
            "psql",
            string.Join(' ', args),
            FilterPsqlOutput,
            new RunOptions(TeeLabel: "psql", FilterStdoutOnly: true, SkipFilterOnFailure: true));
    }

    /// <summary>
    /// Faithful port of <c>filter_psql_output</c> (<c>psql_cmd.rs</c>:48-61): dispatches to the
    /// expanded (<c>\x</c>) or table filter based on which shape is detected, or passes non-table
    /// output (e.g. <c>COPY</c>/notices) through unchanged.
    /// </summary>
    /// <param name="output">The raw captured stdout.</param>
    /// <returns>The filtered output.</returns>
    internal static string FilterPsqlOutput(string output)
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
    internal static bool IsTableFormat(string output) =>
        SplitLines(output).Any(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Contains("-+-") || trimmed.Contains("---+---");
        });

    /// <summary>Faithful port of <c>is_expanded_format</c> (<c>psql_cmd.rs</c>:70-72).</summary>
    /// <param name="output">The raw captured stdout.</param>
    /// <returns>True if a <c>-[ RECORD N ]-</c> header is present anywhere.</returns>
    internal static bool IsExpandedFormat(string output) => ExpandedRecordRegex().IsMatch(output);

    /// <summary>
    /// Faithful port of <c>filter_table</c> (<c>psql_cmd.rs</c>:79-125): strips separator lines and
    /// the row-count footer, converts <c>|</c>-delimited columns to tab-separated, and caps data
    /// rows at <see cref="MaxTableRows"/> with an overflow count.
    /// </summary>
    /// <param name="output">The raw captured stdout.</param>
    /// <returns>The compacted, tab-separated table.</returns>
    internal static string FilterTable(string output)
    {
        var result = new List<string>();
        var dataRows = 0;
        var totalRows = 0;

        foreach (var line in SplitLines(output))
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
    internal static string FilterExpanded(string output)
    {
        var result = new List<string>();
        var currentPairs = new List<string>();
        string? currentRecord = null;
        var recordCount = 0;

        foreach (var line in SplitLines(output))
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

    /// <summary>
    /// Splits text into lines exactly as Rust's <c>str::lines()</c> does (on <c>\n</c>, stripping a
    /// trailing <c>\r</c>, no trailing empty entry after a final <c>\n</c>).
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <returns>The lines, in order.</returns>
    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        if (text.Length == 0)
        {
            return result;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                result.Add(StripCarriageReturn(text, start, i));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            result.Add(StripCarriageReturn(text, start, text.Length));
        }

        return result;
    }

    private static string StripCarriageReturn(string text, int start, int end)
    {
        if (end > start && text[end - 1] == '\r')
        {
            end--;
        }

        return text[start..end];
    }
}
