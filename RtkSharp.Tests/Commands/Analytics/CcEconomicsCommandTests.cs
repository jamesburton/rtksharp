using RtkSharp.Analytics;
using RtkSharp.Commands.Analytics;
using RtkSharp.Core.Tracking;
using Xunit;

namespace RtkSharp.Tests.Commands.Analytics;

/// <summary>
/// Test-for-test port of Rust <c>src/analytics/cc_economics.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (14 tests): <c>test_convert_saturday_to_monday</c>, <c>test_period_economics_new</c>,
/// <c>test_compute_dual_metrics_with_data</c>, <c>test_compute_dual_metrics_zero_tokens</c>,
/// <c>test_compute_dual_metrics_no_ccusage_data</c>, <c>test_merge_monthly_both_present</c>,
/// <c>test_merge_monthly_only_ccusage</c>, <c>test_merge_monthly_only_rtk</c>,
/// <c>test_merge_monthly_sorted</c>, <c>test_compute_weighted_input_cpt</c>,
/// <c>test_compute_weighted_metrics_zero_tokens</c>, <c>test_compute_weighted_metrics_no_cache</c>,
/// <c>test_set_ccusage_stores_per_type_tokens</c>, <c>test_compute_totals</c>.
/// </summary>
public sealed class CcEconomicsCommandTests
{
    private static CcEconomicsCommand.PeriodEconomics NewPeriod(string label) =>
        CcEconomicsCommand.PeriodEconomics.New(label);

    // ===================== test_convert_saturday_to_monday =====================

    [Fact]
    public void ConvertSaturdayToMonday_ValidSaturday_ReturnsMonday()
    {
        // Saturday Jan 18 -> Monday Jan 20.
        Assert.Equal("2026-01-20", CcEconomicsCommand.ConvertSaturdayToMonday("2026-01-18"));
    }

    [Fact]
    public void ConvertSaturdayToMonday_InvalidFormat_ReturnsNull()
    {
        Assert.Null(CcEconomicsCommand.ConvertSaturdayToMonday("invalid"));
    }

    // ===================== test_period_economics_new =====================

    [Fact]
    public void PeriodEconomics_New_SetsLabelAndDefaultsToNull()
    {
        var p = NewPeriod("2026-01");

        Assert.Equal("2026-01", p.Label);
        Assert.Null(p.CcCost);
        Assert.Null(p.RtkCommands);
    }

    // ===================== test_compute_dual_metrics_with_data =====================

    [Fact]
    public void ComputeDualMetrics_WithData_ComputesBlendedAndActiveCpt()
    {
        var p = new CcEconomicsCommand.PeriodEconomics
        {
            Label = "2026-01",
            CcCost = 100.0,
            CcTotalTokens = 1_000_000,
            CcActiveTokens = 10_000,
            RtkSavedTokens = 5_000,
        };

        p.ComputeDualMetrics();

        Assert.NotNull(p.BlendedCpt);
        Assert.Equal(100.0 / 1_000_000.0, p.BlendedCpt!.Value);

        Assert.NotNull(p.ActiveCpt);
        Assert.Equal(100.0 / 10_000.0, p.ActiveCpt!.Value);

        Assert.NotNull(p.SavingsBlended);
        Assert.NotNull(p.SavingsActive);
    }

    // ===================== test_compute_dual_metrics_zero_tokens =====================

    [Fact]
    public void ComputeDualMetrics_ZeroTokens_LeavesCptNull()
    {
        var p = new CcEconomicsCommand.PeriodEconomics
        {
            Label = "2026-01",
            CcCost = 100.0,
            CcTotalTokens = 0,
            CcActiveTokens = 0,
            RtkSavedTokens = 5_000,
        };

        p.ComputeDualMetrics();

        Assert.Null(p.BlendedCpt);
        Assert.Null(p.ActiveCpt);
        Assert.Null(p.SavingsBlended);
        Assert.Null(p.SavingsActive);
    }

    // ===================== test_compute_dual_metrics_no_ccusage_data =====================

    [Fact]
    public void ComputeDualMetrics_NoCcusageData_LeavesCptNull()
    {
        var p = new CcEconomicsCommand.PeriodEconomics
        {
            Label = "2026-01",
            RtkSavedTokens = 5_000,
        };

        p.ComputeDualMetrics();

        Assert.Null(p.BlendedCpt);
        Assert.Null(p.ActiveCpt);
    }

    // ===================== test_merge_monthly_both_present =====================

    [Fact]
    public void MergeMonthly_BothPresent_MergesIntoSinglePeriod()
    {
        List<CcusagePeriod> cc =
        [
            new("2026-01", new CcusageMetrics(1000, 500, 100, 200, 1800, 12.34)),
        ];

        List<MonthStats> rtk =
        [
            new()
            {
                Month = "2026-01",
                Commands = 10,
                InputTokens = 800,
                OutputTokens = 400,
                SavedTokens = 5000,
                SavingsPct = 50.0,
                TotalTimeMs = 0,
                AvgTimeMs = 0,
            },
        ];

        var merged = CcEconomicsCommand.MergeMonthly(cc, rtk);

        Assert.Single(merged);
        Assert.Equal("2026-01", merged[0].Label);
        Assert.Equal(12.34, merged[0].CcCost);
        Assert.Equal(10, merged[0].RtkCommands);
    }

    // ===================== test_merge_monthly_only_ccusage =====================

    [Fact]
    public void MergeMonthly_OnlyCcusage_LeavesRtkCommandsNull()
    {
        List<CcusagePeriod> cc =
        [
            new("2026-01", new CcusageMetrics(1000, 500, 100, 200, 1800, 12.34)),
        ];

        var merged = CcEconomicsCommand.MergeMonthly(cc, []);

        Assert.Single(merged);
        Assert.Equal(12.34, merged[0].CcCost);
        Assert.Null(merged[0].RtkCommands);
    }

    // ===================== test_merge_monthly_only_rtk =====================

    [Fact]
    public void MergeMonthly_OnlyRtk_LeavesCcCostNull()
    {
        List<MonthStats> rtk =
        [
            new()
            {
                Month = "2026-01",
                Commands = 10,
                InputTokens = 800,
                OutputTokens = 400,
                SavedTokens = 5000,
                SavingsPct = 50.0,
                TotalTimeMs = 0,
                AvgTimeMs = 0,
            },
        ];

        var merged = CcEconomicsCommand.MergeMonthly(null, rtk);

        Assert.Single(merged);
        Assert.Null(merged[0].CcCost);
        Assert.Equal(10, merged[0].RtkCommands);
    }

    // ===================== test_merge_monthly_sorted =====================

    [Fact]
    public void MergeMonthly_MultipleMonths_SortedAscendingByLabel()
    {
        List<MonthStats> rtk =
        [
            new()
            {
                Month = "2026-03",
                Commands = 5,
                InputTokens = 100,
                OutputTokens = 50,
                SavedTokens = 1000,
                SavingsPct = 40.0,
                TotalTimeMs = 0,
                AvgTimeMs = 0,
            },
            new()
            {
                Month = "2026-01",
                Commands = 10,
                InputTokens = 200,
                OutputTokens = 100,
                SavedTokens = 2000,
                SavingsPct = 60.0,
                TotalTimeMs = 0,
                AvgTimeMs = 0,
            },
        ];

        var merged = CcEconomicsCommand.MergeMonthly(null, rtk);

        Assert.Equal(2, merged.Count);
        Assert.Equal("2026-01", merged[0].Label);
        Assert.Equal("2026-03", merged[1].Label);
    }

    // ===================== test_compute_weighted_input_cpt =====================

    [Fact]
    public void ComputeWeightedMetrics_WithAllTokenTypes_ComputesWeightedInputCpt()
    {
        var p = NewPeriod("2026-01");
        p.CcCost = 100.0;
        p.CcInputTokens = 1000;
        p.CcOutputTokens = 500;
        p.CcCacheCreateTokens = 200;
        p.CcCacheReadTokens = 5000;
        p.RtkSavedTokens = 10_000;

        p.ComputeWeightedMetrics();

        // weighted_units = 1000 + 5*500 + 1.25*200 + 0.1*5000 = 1000 + 2500 + 250 + 500 = 4250
        // input_cpt = 100 / 4250 = 0.0235294...
        // savings = 10000 * 0.0235294... = 235.29...
        Assert.NotNull(p.WeightedInputCpt);
        Assert.True(Math.Abs(p.WeightedInputCpt!.Value - (100.0 / 4250.0)) < 1e-6);

        Assert.NotNull(p.SavingsWeighted);
        Assert.True(Math.Abs(p.SavingsWeighted!.Value - 235.294) < 0.01);
    }

    // ===================== test_compute_weighted_metrics_zero_tokens =====================

    [Fact]
    public void ComputeWeightedMetrics_ZeroTokens_LeavesCptNull()
    {
        var p = NewPeriod("2026-01");
        p.CcCost = 100.0;
        p.CcInputTokens = 0;
        p.CcOutputTokens = 0;
        p.CcCacheCreateTokens = 0;
        p.CcCacheReadTokens = 0;
        p.RtkSavedTokens = 5000;

        p.ComputeWeightedMetrics();

        Assert.Null(p.WeightedInputCpt);
        Assert.Null(p.SavingsWeighted);
    }

    // ===================== test_compute_weighted_metrics_no_cache =====================

    [Fact]
    public void ComputeWeightedMetrics_NoCacheTokens_ComputesFromInputOutputOnly()
    {
        var p = NewPeriod("2026-01");
        p.CcCost = 60.0;
        p.CcInputTokens = 1000;
        p.CcOutputTokens = 1000;
        p.CcCacheCreateTokens = 0;
        p.CcCacheReadTokens = 0;
        p.RtkSavedTokens = 3000;

        p.ComputeWeightedMetrics();

        // weighted_units = 1000 + 5*1000 = 6000
        // input_cpt = 60 / 6000 = 0.01
        // savings = 3000 * 0.01 = 30
        Assert.NotNull(p.WeightedInputCpt);
        Assert.True(Math.Abs(p.WeightedInputCpt!.Value - 0.01) < 1e-6);

        Assert.NotNull(p.SavingsWeighted);
        Assert.True(Math.Abs(p.SavingsWeighted!.Value - 30.0) < 0.01);
    }

    // ===================== test_set_ccusage_stores_per_type_tokens =====================

    [Fact]
    public void SetCcusage_StoresPerTypeTokens()
    {
        var p = NewPeriod("2026-01");
        var metrics = new CcusageMetrics(1000, 500, 200, 3000, 4700, 50.0);

        p.SetCcusage(metrics);

        Assert.Equal(1000UL, p.CcInputTokens);
        Assert.Equal(500UL, p.CcOutputTokens);
        Assert.Equal(200UL, p.CcCacheCreateTokens);
        Assert.Equal(3000UL, p.CcCacheReadTokens);
        Assert.Equal(4700UL, p.CcTotalTokens);
        Assert.Equal(50.0, p.CcCost);
    }

    // ===================== test_compute_totals =====================

    [Fact]
    public void ComputeTotals_TwoPeriods_SumsAndAverages()
    {
        List<CcEconomicsCommand.PeriodEconomics> periods =
        [
            new()
            {
                Label = "2026-01",
                CcCost = 100.0,
                CcTotalTokens = 1_000_000,
                CcActiveTokens = 10_000,
                CcInputTokens = 5000,
                CcOutputTokens = 5000,
                CcCacheCreateTokens = 100,
                CcCacheReadTokens = 984_900,
                RtkCommands = 5,
                RtkSavedTokens = 2000,
                RtkSavingsPct = 50.0,
            },
            new()
            {
                Label = "2026-02",
                CcCost = 200.0,
                CcTotalTokens = 2_000_000,
                CcActiveTokens = 20_000,
                CcInputTokens = 10_000,
                CcOutputTokens = 10_000,
                CcCacheCreateTokens = 200,
                CcCacheReadTokens = 1_979_800,
                RtkCommands = 10,
                RtkSavedTokens = 3000,
                RtkSavingsPct = 60.0,
            },
        ];

        var totals = CcEconomicsCommand.ComputeTotals(periods);

        Assert.Equal(300.0, totals.CcCost);
        Assert.Equal(3_000_000UL, totals.CcTotalTokens);
        Assert.Equal(30_000UL, totals.CcActiveTokens);
        Assert.Equal(15_000UL, totals.CcInputTokens);
        Assert.Equal(15_000UL, totals.CcOutputTokens);
        Assert.Equal(15, totals.RtkCommands);
        Assert.Equal(5000UL, totals.RtkSavedTokens);
        Assert.Equal(55.0, totals.RtkAvgSavingsPct);

        Assert.NotNull(totals.WeightedInputCpt);
        Assert.NotNull(totals.SavingsWeighted);
        Assert.NotNull(totals.BlendedCpt);
        Assert.NotNull(totals.ActiveCpt);
    }
}
