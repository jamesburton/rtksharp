using System;
using System.Collections.Generic;

namespace RtkSharp.Core.Tracking;

/// <summary>
/// Individual command record from tracking history. Faithful port of Rust <c>CommandRecord</c>
/// (<c>tracking.rs:99-108</c>).
/// </summary>
public sealed class CommandRecord
{
    /// <summary>UTC timestamp when the command was executed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The RTK command that was executed (e.g., <c>"rtk ls"</c>).</summary>
    public required string RtkCmd { get; init; }

    /// <summary>Number of tokens saved (input minus output).</summary>
    public required int SavedTokens { get; init; }

    /// <summary>Savings percentage (<c>(saved / input) * 100</c>).</summary>
    public required double SavingsPct { get; init; }
}

/// <summary>
/// Per-command aggregate statistics: command name, invocation count, total tokens saved, average
/// savings percentage, and average execution time. Port of Rust's <c>CommandStats</c> tuple alias
/// <c>(String, usize, usize, f64, u64)</c> (<c>tracking.rs:224</c>).
/// </summary>
public sealed class CommandStat
{
    /// <summary>The RTK command name (e.g., <c>"rtk git status"</c>).</summary>
    public required string Command { get; init; }

    /// <summary>Number of times this command was recorded.</summary>
    public required int Count { get; init; }

    /// <summary>Total tokens saved across all invocations of this command.</summary>
    public required int SavedTokens { get; init; }

    /// <summary>Average savings percentage across all invocations of this command.</summary>
    public required double AvgSavingsPct { get; init; }

    /// <summary>Average execution time (milliseconds) across all invocations of this command.</summary>
    public required long AvgTimeMs { get; init; }
}

/// <summary>
/// A single day's total tokens saved, used for <see cref="GainSummary.ByDay"/>. Port of Rust's
/// <c>by_day: Vec&lt;(String, usize)&gt;</c> field (<c>tracking.rs:133</c>).
/// </summary>
public sealed class DaySavings
{
    /// <summary>ISO date (<c>YYYY-MM-DD</c>).</summary>
    public required string Date { get; init; }

    /// <summary>Total tokens saved on this day.</summary>
    public required int SavedTokens { get; init; }
}

/// <summary>
/// Aggregated statistics across all recorded commands. Port of Rust <c>GainSummary</c>
/// (<c>tracking.rs:114-134</c>). Returned by <see cref="Tracker.GetSummary"/> and
/// <see cref="Tracker.GetSummaryFiltered"/>.
/// </summary>
public sealed class GainSummary
{
    /// <summary>Total number of commands recorded.</summary>
    public required int TotalCommands { get; init; }

    /// <summary>Total input tokens across all commands.</summary>
    public required int TotalInput { get; init; }

    /// <summary>Total output tokens across all commands.</summary>
    public required int TotalOutput { get; init; }

    /// <summary>Total tokens saved (input minus output) across all commands.</summary>
    public required int TotalSaved { get; init; }

    /// <summary>Average savings percentage across all commands.</summary>
    public required double AvgSavingsPct { get; init; }

    /// <summary>Total execution time across all commands (milliseconds).</summary>
    public required long TotalTimeMs { get; init; }

    /// <summary>Average execution time per command (milliseconds).</summary>
    public required long AvgTimeMs { get; init; }

    /// <summary>Top 10 commands by tokens saved.</summary>
    public required IReadOnlyList<CommandStat> ByCommand { get; init; }

    /// <summary>Last 30 days of activity, ordered oldest to newest.</summary>
    public required IReadOnlyList<DaySavings> ByDay { get; init; }
}

/// <summary>
/// Daily statistics for token savings and execution metrics. Port of Rust <c>DayStats</c>
/// (<c>tracking.rs:154-172</c>).
/// </summary>
public sealed class DayStats
{
    /// <summary>ISO date (<c>YYYY-MM-DD</c>).</summary>
    public required string Date { get; init; }

    /// <summary>Number of commands executed this day.</summary>
    public required int Commands { get; init; }

    /// <summary>Total input tokens for this day.</summary>
    public required int InputTokens { get; init; }

    /// <summary>Total output tokens for this day.</summary>
    public required int OutputTokens { get; init; }

    /// <summary>Total tokens saved this day.</summary>
    public required int SavedTokens { get; init; }

    /// <summary>Savings percentage for this day.</summary>
    public required double SavingsPct { get; init; }

    /// <summary>Total execution time for this day (milliseconds).</summary>
    public required long TotalTimeMs { get; init; }

    /// <summary>Average execution time per command (milliseconds).</summary>
    public required long AvgTimeMs { get; init; }
}

/// <summary>
/// Weekly statistics for token savings and execution metrics. Port of Rust <c>WeekStats</c>
/// (<c>tracking.rs:174-198</c>). Weeks start on Monday and end on Sunday, per SQLite's
/// <c>DATE(timestamp, 'weekday 0', '-6 days')</c>/<c>DATE(timestamp, 'weekday 0')</c> modifiers
/// (<c>'weekday 0'</c> resolves to "next Sunday, or today if already Sunday").
/// </summary>
public sealed class WeekStats
{
    /// <summary>Week start date (<c>YYYY-MM-DD</c>) — the Monday preceding (or equal to) the week's dates.</summary>
    public required string WeekStart { get; init; }

    /// <summary>Week end date (<c>YYYY-MM-DD</c>) — the Sunday on/after the week's dates.</summary>
    public required string WeekEnd { get; init; }

    /// <summary>Number of commands executed this week.</summary>
    public required int Commands { get; init; }

    /// <summary>Total input tokens for this week.</summary>
    public required int InputTokens { get; init; }

    /// <summary>Total output tokens for this week.</summary>
    public required int OutputTokens { get; init; }

    /// <summary>Total tokens saved this week.</summary>
    public required int SavedTokens { get; init; }

    /// <summary>Savings percentage for this week.</summary>
    public required double SavingsPct { get; init; }

    /// <summary>Total execution time for this week (milliseconds).</summary>
    public required long TotalTimeMs { get; init; }

    /// <summary>Average execution time per command (milliseconds).</summary>
    public required long AvgTimeMs { get; init; }
}

/// <summary>
/// Monthly statistics for token savings and execution metrics. Port of Rust <c>MonthStats</c>
/// (<c>tracking.rs:200-221</c>).
/// </summary>
public sealed class MonthStats
{
    /// <summary>Month identifier (<c>YYYY-MM</c>).</summary>
    public required string Month { get; init; }

    /// <summary>Number of commands executed this month.</summary>
    public required int Commands { get; init; }

    /// <summary>Total input tokens for this month.</summary>
    public required int InputTokens { get; init; }

    /// <summary>Total output tokens for this month.</summary>
    public required int OutputTokens { get; init; }

    /// <summary>Total tokens saved this month.</summary>
    public required int SavedTokens { get; init; }

    /// <summary>Savings percentage for this month.</summary>
    public required double SavingsPct { get; init; }

    /// <summary>Total execution time for this month (milliseconds).</summary>
    public required long TotalTimeMs { get; init; }

    /// <summary>Average execution time per command (milliseconds).</summary>
    public required long AvgTimeMs { get; init; }
}

/// <summary>
/// Individual parse failure record. Port of Rust <c>ParseFailureRecord</c> (<c>tracking.rs:1239-1246</c>).
/// </summary>
public sealed class ParseFailureRecord
{
    /// <summary>RFC-3339 timestamp string, as stored (not parsed to a typed value in Rust either).</summary>
    public required string Timestamp { get; init; }

    /// <summary>The raw command line that failed to parse.</summary>
    public required string RawCommand { get; init; }

    /// <summary>The parse error message.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Whether the raw-passthrough fallback succeeded despite the parse failure.</summary>
    public required bool FallbackSucceeded { get; init; }
}

/// <summary>
/// Aggregated parse failure summary. Port of Rust <c>ParseFailureSummary</c> (<c>tracking.rs:1248-1255</c>).
/// Returned by <see cref="Tracker.GetParseFailureSummary"/> for <c>rtk gain --failures</c>.
/// </summary>
public sealed class ParseFailureSummary
{
    /// <summary>Total number of recorded parse failures.</summary>
    public required int Total { get; init; }

    /// <summary>Percentage of parse failures where the raw-passthrough fallback succeeded.</summary>
    public required double RecoveryRate { get; init; }

    /// <summary>Top 10 raw commands by failure frequency: (command, count).</summary>
    public required IReadOnlyList<(string Command, int Count)> TopCommands { get; init; }

    /// <summary>Most recent 10 parse failures, newest first.</summary>
    public required IReadOnlyList<ParseFailureRecord> Recent { get; init; }
}
