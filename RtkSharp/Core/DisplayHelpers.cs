using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RtkSharp.Core;

/// <summary>
/// Formats token counts and savings tables for terminal display. Faithful port of Rust
/// <c>src/core/display_helpers.rs</c>. Eliminates duplication across gain/economics reporting by
/// providing a single interface-based system for displaying daily/weekly/monthly data, mirroring
/// the Rust module's stated purpose (<c>display_helpers.rs:1-4</c>).
/// </summary>
public static class DisplayHelpers
{
    /// <summary>
    /// Formats a duration in milliseconds to a human-readable string. Faithful port of Rust
    /// <c>format_duration</c> (<c>display_helpers.rs:10-20</c>).
    /// </summary>
    /// <param name="ms">The duration in milliseconds.</param>
    /// <returns>
    /// <c>"{ms}ms"</c> when under 1000ms; <c>"{seconds:F1}s"</c> (one decimal place) when under
    /// 60,000ms; otherwise <c>"{minutes}m{seconds}s"</c> using whole (truncated, integer-divided)
    /// seconds.
    /// </returns>
    public static string FormatDuration(long ms)
    {
        if (ms < 1000)
        {
            return $"{ms}ms";
        }

        if (ms < 60_000)
        {
            var seconds = ms / 1000.0;
            return seconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
        }

        var minutes = ms / 60_000;
        var wholeSeconds = (ms % 60_000) / 1000;
        return $"{minutes}m{wholeSeconds}s";
    }

    /// <summary>
    /// Prints a period-based statistics table (daily/weekly/monthly breakdown with a re-summed
    /// TOTAL row) to the given output. Faithful port of Rust's generic <c>print_period_table&lt;T:
    /// PeriodStats&gt;</c> (<c>display_helpers.rs:62-140</c>), using .NET's static abstract
    /// interface members as the equivalent of Rust's trait associated functions/consts, so a single
    /// shared implementation serves <see cref="Tracking.DayStats"/>, <see cref="Tracking.WeekStats"/>,
    /// and <see cref="Tracking.MonthStats"/> without duplicating the table-layout logic per type.
    /// </summary>
    /// <typeparam name="T">The period statistics type to render (e.g. <see cref="Tracking.DayStats"/>).</typeparam>
    /// <param name="data">The period entries to render, in display order. May be empty.</param>
    /// <returns>
    /// The fully rendered table (including headers, per-period rows, TOTAL row, and surrounding
    /// blank lines) as a single string, or the empty-data guard message when <paramref name="data"/>
    /// is empty. Callers write this to stdout with <c>"\n"</c> line endings already embedded, per
    /// RTK's convention of building formatted output as plain strings rather than using
    /// <c>Console.WriteLine</c>/<c>Environment.NewLine</c>.
    /// </returns>
    public static string PrintPeriodTable<T>(IReadOnlyList<T> data)
        where T : IPeriodStats
    {
        if (data.Count == 0)
        {
            return $"No {T.Label.ToLowerInvariant()} data available.\n";
        }

        var periodWidth = T.PeriodWidth;
        var separator = new string('═', T.SeparatorWidth);
        var rule = new string('─', T.SeparatorWidth);

        var sb = new StringBuilder();
        sb.Append('\n');
        sb.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"{T.Icon} {T.Label} Breakdown ({data.Count} {T.Label.ToLowerInvariant()}s)"));
        sb.Append('\n');
        sb.Append(separator);
        sb.Append('\n');

        var headerLabel = T.Label switch
        {
            "Weekly" => "Week",
            "Monthly" => "Month",
            _ => "Date",
        };
        AppendHeaderRow(sb, periodWidth, headerLabel, "Cmds", "Input", "Output", "Saved", "Save%", "Time");
        sb.Append(rule);
        sb.Append('\n');

        foreach (var period in data)
        {
            AppendDataRow(
                sb,
                periodWidth,
                period.Period,
                period.Commands.ToString(CultureInfo.InvariantCulture),
                Utils.FormatTokens(period.InputTokens),
                Utils.FormatTokens(period.OutputTokens),
                Utils.FormatTokens(period.SavedTokens),
                period.SavingsPct,
                FormatDuration(period.AvgTimeMs));
        }

        // Re-sum across all periods rather than averaging the per-period averages — matches Rust's
        // explicit total_* accumulation (display_helpers.rs:111-125).
        long totalCmds = 0;
        long totalInput = 0;
        long totalOutput = 0;
        long totalSaved = 0;
        long totalTime = 0;
        foreach (var period in data)
        {
            totalCmds += period.Commands;
            totalInput += period.InputTokens;
            totalOutput += period.OutputTokens;
            totalSaved += period.SavedTokens;
            totalTime += period.TotalTimeMs;
        }

        var avgPct = totalInput > 0 ? (totalSaved / (double)totalInput) * 100.0 : 0.0;
        var avgTime = totalCmds > 0 ? totalTime / totalCmds : 0;

        sb.Append(rule);
        sb.Append('\n');
        AppendDataRow(
            sb,
            periodWidth,
            "TOTAL",
            totalCmds.ToString(CultureInfo.InvariantCulture),
            Utils.FormatTokens(totalInput),
            Utils.FormatTokens(totalOutput),
            Utils.FormatTokens(totalSaved),
            avgPct,
            FormatDuration(avgTime));
        sb.Append('\n');

        return sb.ToString();
    }

    /// <summary>
    /// Appends the header row: <c>"{label,-width} {"Cmds",7} {"Input",10} {"Output",10}
    /// {"Saved",10} {"Save%",7} {"Time",8}"</c> — every column, including the percent column, is a
    /// plain right-padded text field here (Rust's header uses <c>{:&gt;7}</c> for "Save%", unlike the
    /// numeric <c>{:&gt;6.1}%</c> used by data/TOTAL rows — see <see cref="AppendDataRow"/>),
    /// matching <c>display_helpers.rs:79-93</c> exactly.
    /// </summary>
    private static void AppendHeaderRow(
        StringBuilder sb,
        int periodWidth,
        string period,
        string cmds,
        string input,
        string output,
        string saved,
        string pct,
        string time)
    {
        sb.Append(period.PadRight(periodWidth));
        sb.Append(' ');
        sb.Append(cmds.PadLeft(7));
        sb.Append(' ');
        sb.Append(input.PadLeft(10));
        sb.Append(' ');
        sb.Append(output.PadLeft(10));
        sb.Append(' ');
        sb.Append(saved.PadLeft(10));
        sb.Append(' ');
        sb.Append(pct.PadLeft(7));
        sb.Append(' ');
        sb.Append(time.PadLeft(8));
        sb.Append('\n');
    }

    /// <summary>
    /// Appends one data/TOTAL table row: <c>"{period,-width} {cmds,7} {input,10} {output,10}
    /// {saved,10} {pct:F1,6}% {time,8}"</c>. The percent value is formatted to one decimal place and
    /// padded to 6 characters, then the literal <c>%</c> suffix is appended <em>unpadded</em> —
    /// mirroring Rust's <c>{:&gt;6.1}%</c> format specifier exactly (the padding width applies only to
    /// the numeric part, not the trailing <c>%</c>), per <c>display_helpers.rs:97-107, 128-137</c>.
    /// </summary>
    private static void AppendDataRow(
        StringBuilder sb,
        int periodWidth,
        string period,
        string cmds,
        string input,
        string output,
        string saved,
        double pct,
        string time)
    {
        sb.Append(period.PadRight(periodWidth));
        sb.Append(' ');
        sb.Append(cmds.PadLeft(7));
        sb.Append(' ');
        sb.Append(input.PadLeft(10));
        sb.Append(' ');
        sb.Append(output.PadLeft(10));
        sb.Append(' ');
        sb.Append(saved.PadLeft(10));
        sb.Append(' ');
        sb.Append(pct.ToString("F1", CultureInfo.InvariantCulture).PadLeft(6));
        sb.Append('%');
        sb.Append(' ');
        sb.Append(time.PadLeft(8));
        sb.Append('\n');
    }
}

/// <summary>
/// Period-based statistics that can be rendered by <see cref="DisplayHelpers.PrintPeriodTable{T}"/>.
/// Faithful port of Rust's <c>PeriodStats</c> trait (<c>display_helpers.rs:23-59</c>); the
/// per-type-constant trait items (<c>icon()</c>, <c>label()</c>, <c>period_width()</c>,
/// <c>separator_width()</c>) are ported as .NET static abstract interface members — the closest
/// language equivalent to Rust's static-dispatch trait associated functions — while the
/// per-instance accessors are ordinary instance members.
/// </summary>
public interface IPeriodStats
{
    /// <summary>Icon for this period type (e.g., <c>"D"</c>, <c>"W"</c>, <c>"M"</c>).</summary>
    static abstract string Icon { get; }

    /// <summary>Label for this period type (e.g., <c>"Daily"</c>, <c>"Weekly"</c>, <c>"Monthly"</c>).</summary>
    static abstract string Label { get; }

    /// <summary>Period column width for alignment.</summary>
    static abstract int PeriodWidth { get; }

    /// <summary>Total separator line width.</summary>
    static abstract int SeparatorWidth { get; }

    /// <summary>Period identifier (e.g., <c>"2026-01-20"</c>, <c>"01-20 → 01-26"</c>, <c>"2026-01"</c>).</summary>
    string Period { get; }

    /// <summary>Number of commands in this period.</summary>
    int Commands { get; }

    /// <summary>Input tokens in this period.</summary>
    long InputTokens { get; }

    /// <summary>Output tokens in this period.</summary>
    long OutputTokens { get; }

    /// <summary>Saved tokens in this period.</summary>
    long SavedTokens { get; }

    /// <summary>Savings percentage.</summary>
    double SavingsPct { get; }

    /// <summary>Total execution time in milliseconds.</summary>
    long TotalTimeMs { get; }

    /// <summary>Average execution time per command in milliseconds.</summary>
    long AvgTimeMs { get; }
}
