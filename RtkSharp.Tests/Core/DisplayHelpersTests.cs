using System.Collections.Generic;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using Xunit;

namespace RtkSharp.Tests.Core;

/// <summary>
/// Port of Rust's <c>src/core/display_helpers.rs</c> test module (<c>display_helpers.rs:304-402</c>),
/// covering <see cref="DisplayHelpers.FormatDuration"/> boundary values, each
/// <see cref="IPeriodStats"/> implementation's per-type constants/period formatting, and
/// <see cref="DisplayHelpers.PrintPeriodTable{T}"/>'s exact table layout (including the empty-data
/// guard and the TOTAL row's re-summed-not-averaged computation).
/// </summary>
public sealed class DisplayHelpersTests
{
    // -----------------------------------------------------------------------
    // FormatDuration
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(999, "999ms")]
    [InlineData(1000, "1.0s")]
    [InlineData(1500, "1.5s")]
    [InlineData(59999, "60.0s")] // 59999 / 1000.0 = 59.999, rounds to "60.0" at 1 decimal place (F1).
    [InlineData(60000, "1m0s")]
    [InlineData(61000, "1m1s")]
    [InlineData(125_000, "2m5s")]
    public void FormatDuration_MatchesRustBoundaryBehavior(long ms, string expected)
    {
        Assert.Equal(expected, DisplayHelpers.FormatDuration(ms));
    }

    // -----------------------------------------------------------------------
    // IPeriodStats per-type constants and Period formatting
    // -----------------------------------------------------------------------

    [Fact]
    public void DayStats_ExposesExpectedTraitConstants()
    {
        var day = new DayStats
        {
            Date = "2026-01-20",
            Commands = 10,
            InputTokens = 1000,
            OutputTokens = 500,
            SavedTokens = 200,
            SavingsPct = 20.0,
            TotalTimeMs = 1500,
            AvgTimeMs = 150,
        };

        Assert.Equal("2026-01-20", ((IPeriodStats)day).Period);
        Assert.Equal(10, day.Commands);
        Assert.Equal(200, day.SavedTokens);
        Assert.Equal(150, day.AvgTimeMs);
        Assert.Equal("D", DayStats.Icon);
        Assert.Equal("Daily", DayStats.Label);
        Assert.Equal(12, DayStats.PeriodWidth);
        Assert.Equal(74, DayStats.SeparatorWidth);
    }

    [Fact]
    public void WeekStats_PeriodStripsYearDashPrefixFromBothDates()
    {
        var week = new WeekStats
        {
            WeekStart = "2026-01-20",
            WeekEnd = "2026-01-26",
            Commands = 50,
            InputTokens = 5000,
            OutputTokens = 2500,
            SavedTokens = 1000,
            SavingsPct = 40.0,
            TotalTimeMs = 5000,
            AvgTimeMs = 100,
        };

        Assert.Equal("01-20 → 01-26", ((IPeriodStats)week).Period);
        Assert.Equal(100, week.AvgTimeMs);
        Assert.Equal("W", WeekStats.Icon);
        Assert.Equal("Weekly", WeekStats.Label);
        Assert.Equal(22, WeekStats.PeriodWidth);
        Assert.Equal(82, WeekStats.SeparatorWidth);
    }

    [Fact]
    public void MonthStats_ExposesExpectedTraitConstants()
    {
        var month = new MonthStats
        {
            Month = "2026-01",
            Commands = 200,
            InputTokens = 20000,
            OutputTokens = 10000,
            SavedTokens = 5000,
            SavingsPct = 50.0,
            TotalTimeMs = 20000,
            AvgTimeMs = 100,
        };

        Assert.Equal("2026-01", ((IPeriodStats)month).Period);
        Assert.Equal(100, month.AvgTimeMs);
        Assert.Equal("M", MonthStats.Icon);
        Assert.Equal("Monthly", MonthStats.Label);
        Assert.Equal(10, MonthStats.PeriodWidth);
        Assert.Equal(74, MonthStats.SeparatorWidth);
    }

    // -----------------------------------------------------------------------
    // PrintPeriodTable — empty-data guard
    // -----------------------------------------------------------------------

    [Fact]
    public void PrintPeriodTable_Day_EmptyData_PrintsGuardMessageOnly()
    {
        var result = DisplayHelpers.PrintPeriodTable(new List<DayStats>());
        Assert.Equal("No daily data available.\n", result);
    }

    [Fact]
    public void PrintPeriodTable_Week_EmptyData_PrintsGuardMessageOnly()
    {
        var result = DisplayHelpers.PrintPeriodTable(new List<WeekStats>());
        Assert.Equal("No weekly data available.\n", result);
    }

    [Fact]
    public void PrintPeriodTable_Month_EmptyData_PrintsGuardMessageOnly()
    {
        var result = DisplayHelpers.PrintPeriodTable(new List<MonthStats>());
        Assert.Equal("No monthly data available.\n", result);
    }

    // -----------------------------------------------------------------------
    // PrintPeriodTable — byte-exact rendering, mirroring
    // display_helpers.rs:376-401 (test_print_period_table_with_data), plus a hand-verified
    // expected string built against the exact Rust format specifiers.
    // -----------------------------------------------------------------------

    [Fact]
    public void PrintPeriodTable_Day_TwoRows_RendersExactLayoutWithResummedTotal()
    {
        var data = new List<DayStats>
        {
            new DayStats
            {
                Date = "2026-01-20",
                Commands = 10,
                InputTokens = 1000,
                OutputTokens = 500,
                SavedTokens = 200,
                SavingsPct = 20.0,
                TotalTimeMs = 1500,
                AvgTimeMs = 150,
            },
            new DayStats
            {
                Date = "2026-01-21",
                Commands = 15,
                InputTokens = 1500,
                OutputTokens = 750,
                SavedTokens = 300,
                SavingsPct = 30.0,
                TotalTimeMs = 2250,
                AvgTimeMs = 150,
            },
        };

        var result = DisplayHelpers.PrintPeriodTable(data);

        // Re-summed TOTAL: cmds=25, input=2500, output=1250, saved=500,
        // avgPct = 500/2500*100 = 20.0 (NOT the average of 20.0 and 30.0, which would be 25.0),
        // totalTime=3750, avgTime = 3750/25 = 150.
        var doubleRule = new string('═', 74);
        var singleRule = new string('─', 74);
        var expected =
            "\n" +
            "D Daily Breakdown (2 dailys)\n" +
            doubleRule + "\n" +
            "Date            Cmds      Input     Output      Saved   Save%     Time\n" +
            singleRule + "\n" +
            "2026-01-20        10       1.0K        500        200   20.0%    150ms\n" +
            "2026-01-21        15       1.5K        750        300   30.0%    150ms\n" +
            singleRule + "\n" +
            "TOTAL             25       2.5K       1.2K        500   20.0%    150ms\n" +
            "\n";

        Assert.Equal(expected, result);
    }
}
