using System.Globalization;
using global::System.Text.Json.Serialization;
using RtkSharp.Analytics;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;

namespace RtkSharp.Commands.Analytics;

/// <summary>
/// Implements the <c>rtk cc-economics</c> CLI verb: combines ccusage (Claude Code API spend) with
/// rtk's own tracking database (tokens saved) into a spend-vs-savings economics report. Faithful
/// port of Rust <c>src/analytics/cc_economics.rs</c> (<c>run</c> and every helper it calls).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>RTK_META_COMMANDS</c> parity.</b> Rust's <c>main.rs</c> lists <c>"cc-economics"</c> in
/// <c>RTK_META_COMMANDS</c> (<c>main.rs:1181</c>), so a Clap parse failure shows Clap's own error
/// instead of falling back to raw shell execution. Following the same convention already
/// established by <see cref="GainCommand"/> for other meta-commands, this port uses a hand-rolled
/// <see cref="CcEconomicsArgsException"/>-based parser (never
/// <see cref="RtkSharp.Cli.CommandArgumentParseException"/>, which is reserved for
/// PASSTHROUGH-classified commands) and prints a short <c>error: ...</c> message on stderr with
/// exit code 2.
/// </para>
/// <para>
/// <b>Weighted-input CPT is the primary metric; blended/active are legacy, verbose-gated.</b>
/// Mirrors Rust's doc header: <c>weighted_input_cpt</c>/<c>savings_weighted</c> are always computed
/// and always the metric printed in the default (non-verbose) table/summary view; the legacy
/// <c>blended_cpt</c>/<c>active_cpt</c> (and their savings) are computed unconditionally too (so
/// JSON/CSV export always includes them) but only ever printed in text mode when
/// <c>cli.verbose &gt; 0</c>.
/// </para>
/// </remarks>
public static class CcEconomicsCommand
{
    // API pricing ratios (verified Feb 2026, consistent across Claude models <=200K context).
    // Source: https://docs.anthropic.com/en/docs/about-claude/models (cc_economics.rs:17-21).
    private const double WeightOutput = 5.0;
    private const double WeightCacheCreate = 1.25;
    private const double WeightCacheRead = 0.1;

    /// <summary>
    /// Runs <c>rtk cc-economics</c> with the given arguments (the remainder after the
    /// <c>cc-economics</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>cc-economics</c>.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await RunCoreAsync(args, RuntimeOptions.Verbosity).ConfigureAwait(false);
        }
        catch (CcEconomicsArgsException ex)
        {
            Console.Error.Write(ex.Message + "\n");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args, int verbose)
    {
        var parsed = CcEconomicsArgs.Parse(args);

        Tracker tracker;
        try
        {
            tracker = new Tracker();
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to initialize tracking database: {ex.Message}", ex);
        }

        using (tracker)
        {
            return parsed.Format switch
            {
                "json" => await ExportJsonAsync(tracker, parsed.Daily, parsed.Weekly, parsed.Monthly, parsed.All)
                    .ConfigureAwait(false),
                "csv" => await ExportCsvAsync(tracker, parsed.Daily, parsed.Weekly, parsed.Monthly, parsed.All)
                    .ConfigureAwait(false),
                _ => await DisplayTextAsync(tracker, parsed.Daily, parsed.Weekly, parsed.Monthly, parsed.All, verbose)
                    .ConfigureAwait(false),
            };
        }
    }

    // -----------------------------------------------------------------------
    // Types (cc_economics.rs:25-178)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Per-period spend-vs-savings economics. Faithful port of Rust <c>PeriodEconomics</c>
    /// (<c>cc_economics.rs:25-49</c>) including its mutating helper methods.
    /// </summary>
    internal sealed class PeriodEconomics
    {
        public required string Label { get; init; }

        public double? CcCost { get; set; }

        public ulong? CcTotalTokens { get; set; }

        public ulong? CcActiveTokens { get; set; }

        public ulong? CcInputTokens { get; set; }

        public ulong? CcOutputTokens { get; set; }

        public ulong? CcCacheCreateTokens { get; set; }

        public ulong? CcCacheReadTokens { get; set; }

        public int? RtkCommands { get; set; }

        public ulong? RtkSavedTokens { get; set; }

        public double? RtkSavingsPct { get; set; }

        public double? WeightedInputCpt { get; set; }

        public double? SavingsWeighted { get; set; }

        public double? BlendedCpt { get; set; }

        public double? ActiveCpt { get; set; }

        public double? SavingsBlended { get; set; }

        public double? SavingsActive { get; set; }

        public static PeriodEconomics New(string label) => new() { Label = label };

        /// <summary>Faithful port of <c>set_ccusage</c> (<c>cc_economics.rs:74-87</c>).</summary>
        public void SetCcusage(CcusageMetrics metrics)
        {
            CcCost = metrics.TotalCost;
            CcTotalTokens = (ulong)metrics.TotalTokens;
            CcInputTokens = (ulong)metrics.InputTokens;
            CcOutputTokens = (ulong)metrics.OutputTokens;
            CcCacheCreateTokens = (ulong)metrics.CacheCreationTokens;
            CcCacheReadTokens = (ulong)metrics.CacheReadTokens;
            CcActiveTokens = (ulong)(metrics.InputTokens + metrics.OutputTokens);
        }

        /// <summary>Faithful port of <c>set_rtk_from_day</c> (<c>cc_economics.rs:89-93</c>).</summary>
        public void SetRtkFromDay(DayStats stats)
        {
            RtkCommands = stats.Commands;
            RtkSavedTokens = (ulong)stats.SavedTokens;
            RtkSavingsPct = stats.SavingsPct;
        }

        /// <summary>Faithful port of <c>set_rtk_from_week</c> (<c>cc_economics.rs:95-99</c>).</summary>
        public void SetRtkFromWeek(WeekStats stats)
        {
            RtkCommands = stats.Commands;
            RtkSavedTokens = (ulong)stats.SavedTokens;
            RtkSavingsPct = stats.SavingsPct;
        }

        /// <summary>Faithful port of <c>set_rtk_from_month</c> (<c>cc_economics.rs:101-111</c>).</summary>
        public void SetRtkFromMonth(MonthStats stats)
        {
            RtkCommands = stats.Commands;
            RtkSavedTokens = (ulong)stats.SavedTokens;
            var denom = stats.SavedTokens + stats.InputTokens + stats.OutputTokens;
            RtkSavingsPct = denom > 0 ? stats.SavedTokens / (double)denom * 100.0 : 0.0;
        }

        /// <summary>
        /// Derives the primary weighted-input CPT and its savings figure from API price ratios.
        /// Faithful port of <c>compute_weighted_metrics</c> (<c>cc_economics.rs:113-137</c>).
        /// </summary>
        public void ComputeWeightedMetrics()
        {
            if (CcCost is not { } cost || RtkSavedTokens is not { } saved)
            {
                return;
            }

            if (CcInputTokens is not { } input || CcOutputTokens is not { } output
                || CcCacheCreateTokens is not { } cacheCreate || CcCacheReadTokens is not { } cacheRead)
            {
                return;
            }

            var weightedUnits = input + (WeightOutput * output) + (WeightCacheCreate * cacheCreate)
                + (WeightCacheRead * cacheRead);

            if (weightedUnits > 0.0)
            {
                var inputCpt = cost / weightedUnits;
                WeightedInputCpt = inputCpt;
                SavingsWeighted = saved * inputCpt;
            }
        }

        /// <summary>
        /// Derives the legacy blended/active CPT metrics (verbose-mode-only display, but always
        /// computed so JSON/CSV export always includes them). Faithful port of
        /// <c>compute_dual_metrics</c> (<c>cc_economics.rs:139-157</c>).
        /// </summary>
        public void ComputeDualMetrics()
        {
            if (CcCost is not { } cost || RtkSavedTokens is not { } saved)
            {
                return;
            }

            if (CcTotalTokens is { } total && total > 0)
            {
                BlendedCpt = cost / total;
                SavingsBlended = saved * (cost / total);
            }

            if (CcActiveTokens is { } active && active > 0)
            {
                ActiveCpt = cost / active;
                SavingsActive = saved * (cost / active);
            }
        }
    }

    /// <summary>Aggregate totals across all periods in a view. Faithful port of Rust <c>Totals</c> (<c>cc_economics.rs:160-178</c>).</summary>
    internal sealed class Totals
    {
        public double CcCost { get; set; }

        public ulong CcTotalTokens { get; set; }

        public ulong CcActiveTokens { get; set; }

        public ulong CcInputTokens { get; set; }

        public ulong CcOutputTokens { get; set; }

        public ulong CcCacheCreateTokens { get; set; }

        public ulong CcCacheReadTokens { get; set; }

        public int RtkCommands { get; set; }

        public ulong RtkSavedTokens { get; set; }

        public double RtkAvgSavingsPct { get; set; }

        public double? WeightedInputCpt { get; set; }

        public double? SavingsWeighted { get; set; }

        public double? BlendedCpt { get; set; }

        public double? ActiveCpt { get; set; }

        public double? SavingsBlended { get; set; }

        public double? SavingsActive { get; set; }
    }

    // -----------------------------------------------------------------------
    // Merge logic (cc_economics.rs:201-296)
    // -----------------------------------------------------------------------

    /// <summary>Faithful port of <c>merge_daily</c> (<c>cc_economics.rs:201-229</c>).</summary>
    internal static List<PeriodEconomics> MergeDaily(IReadOnlyList<CcusagePeriod>? cc, IReadOnlyList<DayStats> rtk)
    {
        var map = new Dictionary<string, PeriodEconomics>(StringComparer.Ordinal);

        if (cc is not null)
        {
            foreach (var entry in cc)
            {
                GetOrAdd(map, entry.Key).SetCcusage(entry.Metrics);
            }
        }

        foreach (var entry in rtk)
        {
            GetOrAdd(map, entry.Date).SetRtkFromDay(entry);
        }

        return FinalizeMerge(map);
    }

    /// <summary>Faithful port of <c>merge_weekly</c> (<c>cc_economics.rs:231-267</c>).</summary>
    internal static List<PeriodEconomics> MergeWeekly(IReadOnlyList<CcusagePeriod>? cc, IReadOnlyList<WeekStats> rtk)
    {
        var map = new Dictionary<string, PeriodEconomics>(StringComparer.Ordinal);

        if (cc is not null)
        {
            foreach (var entry in cc)
            {
                GetOrAdd(map, entry.Key).SetCcusage(entry.Metrics);
            }
        }

        foreach (var entry in rtk)
        {
            var mondayKey = ConvertSaturdayToMonday(entry.WeekStart);
            if (mondayKey is null)
            {
                Console.Error.Write($"[warn] Invalid week_start format: {entry.WeekStart}\n");
                continue;
            }

            GetOrAdd(map, mondayKey).SetRtkFromWeek(entry);
        }

        return FinalizeMerge(map);
    }

    /// <summary>Faithful port of <c>merge_monthly</c> (<c>cc_economics.rs:269-296</c>).</summary>
    internal static List<PeriodEconomics> MergeMonthly(IReadOnlyList<CcusagePeriod>? cc, IReadOnlyList<MonthStats> rtk)
    {
        var map = new Dictionary<string, PeriodEconomics>(StringComparer.Ordinal);

        if (cc is not null)
        {
            foreach (var entry in cc)
            {
                GetOrAdd(map, entry.Key).SetCcusage(entry.Metrics);
            }
        }

        foreach (var entry in rtk)
        {
            GetOrAdd(map, entry.Month).SetRtkFromMonth(entry);
        }

        return FinalizeMerge(map);
    }

    private static PeriodEconomics GetOrAdd(Dictionary<string, PeriodEconomics> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            value = PeriodEconomics.New(key);
            map[key] = value;
        }

        return value;
    }

    private static List<PeriodEconomics> FinalizeMerge(Dictionary<string, PeriodEconomics> map)
    {
        var result = map.Values.ToList();
        foreach (var period in result)
        {
            period.ComputeWeightedMetrics();
            period.ComputeDualMetrics();
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers (cc_economics.rs:298-396)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a legacy Saturday <c>week_start</c> (rtk tracking) to the ISO Monday key ccusage
    /// uses. Faithful port of <c>convert_saturday_to_monday</c> (<c>cc_economics.rs:302-310</c>):
    /// Saturday + 2 days = Monday.
    /// </summary>
    /// <param name="saturday">The Saturday-keyed date string (<c>YYYY-MM-DD</c>).</param>
    /// <returns>The Monday-keyed date string, or <see langword="null"/> if <paramref name="saturday"/> doesn't parse.</returns>
    internal static string? ConvertSaturdayToMonday(string saturday)
    {
        if (!DateOnly.TryParseExact(saturday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var satDate))
        {
            return null;
        }

        var monday = satDate.AddDays(2);
        return monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Faithful port of <c>compute_totals</c> (<c>cc_economics.rs:312-396</c>).</summary>
    internal static Totals ComputeTotals(IReadOnlyList<PeriodEconomics> periods)
    {
        var totals = new Totals();
        double pctSum = 0;
        var pctCount = 0;

        foreach (var p in periods)
        {
            if (p.CcCost is { } cost)
            {
                totals.CcCost += cost;
            }

            if (p.CcTotalTokens is { } total)
            {
                totals.CcTotalTokens += total;
            }

            if (p.CcActiveTokens is { } active)
            {
                totals.CcActiveTokens += active;
            }

            if (p.CcInputTokens is { } input)
            {
                totals.CcInputTokens += input;
            }

            if (p.CcOutputTokens is { } output)
            {
                totals.CcOutputTokens += output;
            }

            if (p.CcCacheCreateTokens is { } cacheCreate)
            {
                totals.CcCacheCreateTokens += cacheCreate;
            }

            if (p.CcCacheReadTokens is { } cacheRead)
            {
                totals.CcCacheReadTokens += cacheRead;
            }

            if (p.RtkCommands is { } cmds)
            {
                totals.RtkCommands += cmds;
            }

            if (p.RtkSavedTokens is { } saved)
            {
                totals.RtkSavedTokens += saved;
            }

            if (p.RtkSavingsPct is { } pct)
            {
                pctSum += pct;
                pctCount++;
            }
        }

        if (pctCount > 0)
        {
            totals.RtkAvgSavingsPct = pctSum / pctCount;
        }

        var weightedUnits = totals.CcInputTokens + (WeightOutput * totals.CcOutputTokens)
            + (WeightCacheCreate * totals.CcCacheCreateTokens) + (WeightCacheRead * totals.CcCacheReadTokens);

        if (weightedUnits > 0.0)
        {
            var inputCpt = totals.CcCost / weightedUnits;
            totals.WeightedInputCpt = inputCpt;
            totals.SavingsWeighted = totals.RtkSavedTokens * inputCpt;
        }

        if (totals.CcTotalTokens > 0)
        {
            totals.BlendedCpt = totals.CcCost / totals.CcTotalTokens;
            totals.SavingsBlended = totals.RtkSavedTokens * totals.BlendedCpt.Value;
        }

        if (totals.CcActiveTokens > 0)
        {
            totals.ActiveCpt = totals.CcCost / totals.CcActiveTokens;
            totals.SavingsActive = totals.RtkSavedTokens * totals.ActiveCpt.Value;
        }

        return totals;
    }

    // -----------------------------------------------------------------------
    // Display (cc_economics.rs:400-659)
    // -----------------------------------------------------------------------

    private static async Task<int> DisplayTextAsync(
        Tracker tracker, bool daily, bool weekly, bool monthly, bool all, int verbose)
    {
        if (!daily && !weekly && !monthly && !all)
        {
            await DisplaySummaryAsync(tracker, verbose).ConfigureAwait(false);
            return 0;
        }

        if (all || daily)
        {
            await DisplayDailyAsync(tracker, verbose).ConfigureAwait(false);
        }

        if (all || weekly)
        {
            await DisplayWeeklyAsync(tracker, verbose).ConfigureAwait(false);
        }

        if (all || monthly)
        {
            await DisplayMonthlyAsync(tracker, verbose).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task DisplaySummaryAsync(Tracker tracker, int verbose)
    {
        var ccMonthly = await FetchMonthlyOrThrowAsync().ConfigureAwait(false);
        var rtkMonthly = tracker.GetByMonth();
        var periods = MergeMonthly(ccMonthly, rtkMonthly);

        if (periods.Count == 0)
        {
            Console.Out.Write("No data available. Run some rtk commands to start tracking.\n");
            return;
        }

        var totals = ComputeTotals(periods);

        Console.Out.Write("[cost] Claude Code Economics\n");
        Console.Out.Write(new string('═', 56) + "\n\n");

        Console.Out.Write($"  Spent (ccusage):              {FormatUsd(totals.CcCost)}\n");
        Console.Out.Write("  Token breakdown:\n");
        Console.Out.Write($"    Input:                      {Utils.FormatTokens((long)totals.CcInputTokens)}\n");
        Console.Out.Write($"    Output:                     {Utils.FormatTokens((long)totals.CcOutputTokens)}\n");
        Console.Out.Write($"    Cache writes:               {Utils.FormatTokens((long)totals.CcCacheCreateTokens)}\n");
        Console.Out.Write($"    Cache reads:                {Utils.FormatTokens((long)totals.CcCacheReadTokens)}\n\n");

        Console.Out.Write($"  RTK commands:                 {totals.RtkCommands}\n");
        Console.Out.Write($"  Tokens saved:                 {Utils.FormatTokens((long)totals.RtkSavedTokens)}\n\n");

        Console.Out.Write("  Estimated Savings:\n");
        Console.Out.Write("  ┌───────────────────────────────────────────────────┐\n");

        if (totals.SavingsWeighted is { } weightedSavings)
        {
            var weightedPct = totals.CcCost > 0.0 ? weightedSavings / totals.CcCost * 100.0 : 0.0;
            Console.Out.Write(
                $"  │ Input token pricing:   {FormatUsd(weightedSavings).TrimEnd()}  ({weightedPct.ToString("F1", CultureInfo.InvariantCulture)}%)           │\n");
            if (totals.WeightedInputCpt is { } inputCpt)
            {
                Console.Out.Write($"  │ Derived input CPT:     {FormatCpt(inputCpt)}               │\n");
            }
        }
        else
        {
            Console.Out.Write("  │ Input token pricing:   —                         │\n");
        }

        Console.Out.Write("  └───────────────────────────────────────────────────┘\n\n");

        Console.Out.Write("  How it works:\n");
        Console.Out.Write("  RTK compresses CLI outputs before they enter Claude's context.\n");
        Console.Out.Write("  Savings derived using API price ratios (out=5x, cache_w=1.25x, cache_r=0.1x).\n\n");

        if (verbose > 0)
        {
            Console.Out.Write("  Legacy metrics (reference only):\n");
            if (totals.SavingsActive is { } activeSavings)
            {
                var activePct = totals.CcCost > 0.0 ? activeSavings / totals.CcCost * 100.0 : 0.0;
                Console.Out.Write(
                    $"    Active (OVERESTIMATES):  {FormatUsd(activeSavings)}  ({activePct.ToString("F1", CultureInfo.InvariantCulture)}%)\n");
            }

            if (totals.SavingsBlended is { } blendedSavings)
            {
                var blendedPct = totals.CcCost > 0.0 ? blendedSavings / totals.CcCost * 100.0 : 0.0;
                Console.Out.Write(
                    $"    Blended (UNDERESTIMATES): {FormatUsd(blendedSavings)}  ({blendedPct.ToString("F2", CultureInfo.InvariantCulture)}%)\n");
            }

            Console.Out.Write("  Note: Saved tokens estimated via chars/4 heuristic, not exact tokenizer.\n\n");
        }
    }

    private static async Task DisplayDailyAsync(Tracker tracker, int verbose)
    {
        var ccDaily = await FetchDailyOrThrowAsync().ConfigureAwait(false);
        var rtkDaily = tracker.GetAllDays();
        var periods = MergeDaily(ccDaily, rtkDaily);

        Console.Out.Write("Daily Economics\n");
        Console.Out.Write(new string('═', 56) + "\n");
        PrintPeriodTable(periods, verbose);
    }

    private static async Task DisplayWeeklyAsync(Tracker tracker, int verbose)
    {
        var ccWeekly = await FetchWeeklyOrThrowAsync().ConfigureAwait(false);
        var rtkWeekly = tracker.GetByWeek();
        var periods = MergeWeekly(ccWeekly, rtkWeekly);

        Console.Out.Write("Weekly Economics\n");
        Console.Out.Write(new string('═', 56) + "\n");
        PrintPeriodTable(periods, verbose);
    }

    private static async Task DisplayMonthlyAsync(Tracker tracker, int verbose)
    {
        var ccMonthly = await FetchMonthlyOrThrowAsync().ConfigureAwait(false);
        var rtkMonthly = tracker.GetByMonth();
        var periods = MergeMonthly(ccMonthly, rtkMonthly);

        Console.Out.Write("Monthly Economics\n");
        Console.Out.Write(new string('═', 56) + "\n");
        PrintPeriodTable(periods, verbose);
    }

    private static void PrintPeriodTable(IReadOnlyList<PeriodEconomics> periods, int verbose)
    {
        Console.Out.Write("\n");

        if (verbose > 0)
        {
            Console.Out.Write(
                $"{"Period",-12} {"Spent",10} {"Saved",10} {"Savings",10} {"Active$",10} {"Blended$",12} {"RTK Cmds",12}\n");
            Console.Out.Write($"{new string('-', 12)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 12)} {new string('-', 12)}\n");

            foreach (var p in periods)
            {
                var spent = p.CcCost is { } c ? FormatUsd(c) : "—";
                var saved = p.RtkSavedTokens is { } s ? Utils.FormatTokens((long)s) : "—";
                var weighted = p.SavingsWeighted is { } w ? FormatUsd(w) : "—";
                var active = p.SavingsActive is { } a ? FormatUsd(a) : "—";
                var blended = p.SavingsBlended is { } b ? FormatUsd(b) : "—";
                var cmds = p.RtkCommands is { } cm ? cm.ToString(CultureInfo.InvariantCulture) : "—";

                Console.Out.Write($"{p.Label,-12} {spent,10} {saved,10} {weighted,10} {active,10} {blended,12} {cmds,12}\n");
            }
        }
        else
        {
            Console.Out.Write($"{"Period",-12} {"Spent",10} {"Saved",10} {"Savings",10} {"RTK Cmds",12}\n");
            Console.Out.Write($"{new string('-', 12)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 12)}\n");

            foreach (var p in periods)
            {
                var spent = p.CcCost is { } c ? FormatUsd(c) : "—";
                var saved = p.RtkSavedTokens is { } s ? Utils.FormatTokens((long)s) : "—";
                var weighted = p.SavingsWeighted is { } w ? FormatUsd(w) : "—";
                var cmds = p.RtkCommands is { } cm ? cm.ToString(CultureInfo.InvariantCulture) : "—";

                Console.Out.Write($"{p.Label,-12} {spent,10} {saved,10} {weighted,10} {cmds,12}\n");
            }
        }

        Console.Out.Write("\n");
    }

    // -----------------------------------------------------------------------
    // Export (cc_economics.rs:663-825)
    // -----------------------------------------------------------------------

    private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly CcEconomicsJsonContext JsonContext = new(JsonOptions);

    private static async Task<int> ExportJsonAsync(Tracker tracker, bool daily, bool weekly, bool monthly, bool all)
    {
        var export = new EconomicsExport();

        if (all || daily)
        {
            var cc = await FetchDailyOrThrowAsync().ConfigureAwait(false);
            var rtk = tracker.GetAllDays();
            export.Daily = MergeDaily(cc, rtk).Select(ToDto).ToList();
        }

        if (all || weekly)
        {
            var cc = await FetchWeeklyOrThrowAsync().ConfigureAwait(false);
            var rtk = tracker.GetByWeek();
            export.Weekly = MergeWeekly(cc, rtk).Select(ToDto).ToList();
        }

        if (all || monthly)
        {
            var cc = await FetchMonthlyOrThrowAsync().ConfigureAwait(false);
            var rtk = tracker.GetByMonth();
            var periods = MergeMonthly(cc, rtk);
            export.Totals = ToDto(ComputeTotals(periods));
            export.Monthly = periods.Select(ToDto).ToList();
        }

        var json = global::System.Text.Json.JsonSerializer.Serialize(export, JsonContext.EconomicsExport)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Console.Out.Write(json + "\n");
        return 0;
    }

    private static async Task<int> ExportCsvAsync(Tracker tracker, bool daily, bool weekly, bool monthly, bool all)
    {
        Console.Out.Write(
            "period,spent,input_tokens,output_tokens,cache_create,cache_read,active_tokens,total_tokens,saved_tokens,weighted_savings,active_savings,blended_savings,rtk_commands\n");

        if (all || daily)
        {
            var cc = await FetchDailyOrThrowAsync().ConfigureAwait(false);
            var rtk = tracker.GetAllDays();
            foreach (var p in MergeDaily(cc, rtk))
            {
                PrintCsvRow(p);
            }
        }

        if (all || weekly)
        {
            var cc = await FetchWeeklyOrThrowAsync().ConfigureAwait(false);
            var rtk = tracker.GetByWeek();
            foreach (var p in MergeWeekly(cc, rtk))
            {
                PrintCsvRow(p);
            }
        }

        if (all || monthly)
        {
            var cc = await FetchMonthlyOrThrowAsync().ConfigureAwait(false);
            var rtk = tracker.GetByMonth();
            foreach (var p in MergeMonthly(cc, rtk))
            {
                PrintCsvRow(p);
            }
        }

        return 0;
    }

    private static void PrintCsvRow(PeriodEconomics p)
    {
        var spent = p.CcCost is { } c ? c.ToString("F4", CultureInfo.InvariantCulture) : string.Empty;
        var input = p.CcInputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var output = p.CcOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var cacheCreate = p.CcCacheCreateTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var cacheRead = p.CcCacheReadTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var active = p.CcActiveTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var total = p.CcTotalTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var saved = p.RtkSavedTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var weightedSavings = p.SavingsWeighted is { } w ? w.ToString("F4", CultureInfo.InvariantCulture) : string.Empty;
        var activeSavings = p.SavingsActive is { } a ? a.ToString("F4", CultureInfo.InvariantCulture) : string.Empty;
        var blendedSavings = p.SavingsBlended is { } b ? b.ToString("F4", CultureInfo.InvariantCulture) : string.Empty;
        var cmds = p.RtkCommands?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        Console.Out.Write(
            $"{p.Label},{spent},{input},{output},{cacheCreate},{cacheRead},{active},{total},{saved},{weightedSavings},{activeSavings},{blendedSavings},{cmds}\n");
    }

    // -----------------------------------------------------------------------
    // Number formatting (src/core/utils.rs:78-136, kept local to avoid touching shared Utils.cs)
    // -----------------------------------------------------------------------

    /// <summary>Faithful port of Rust <c>format_usd</c> (<c>src/core/utils.rs:104-113</c>).</summary>
    private static string FormatUsd(double amount)
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount))
        {
            return "$0.00";
        }

        return amount >= 0.01
            ? "$" + amount.ToString("F2", CultureInfo.InvariantCulture)
            : "$" + amount.ToString("F4", CultureInfo.InvariantCulture);
    }

    /// <summary>Faithful port of Rust <c>format_cpt</c> (<c>src/core/utils.rs:130-136</c>).</summary>
    private static string FormatCpt(double cpt)
    {
        if (double.IsNaN(cpt) || double.IsInfinity(cpt) || cpt <= 0.0)
        {
            return "$0.00/MTok";
        }

        var perMillion = cpt * 1_000_000.0;
        return "$" + perMillion.ToString("F2", CultureInfo.InvariantCulture) + "/MTok";
    }

    // -----------------------------------------------------------------------
    // ccusage fetch wrappers (adds Rust's `.context(...)` messages)
    // -----------------------------------------------------------------------

    private static async Task<IReadOnlyList<CcusagePeriod>?> FetchDailyOrThrowAsync()
    {
        try
        {
            return await Ccusage.FetchAsync(Granularity.Daily).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to fetch ccusage daily data: {ex.Message}", ex);
        }
    }

    private static async Task<IReadOnlyList<CcusagePeriod>?> FetchWeeklyOrThrowAsync()
    {
        try
        {
            return await Ccusage.FetchAsync(Granularity.Weekly).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to fetch ccusage weekly data: {ex.Message}", ex);
        }
    }

    private static async Task<IReadOnlyList<CcusagePeriod>?> FetchMonthlyOrThrowAsync()
    {
        try
        {
            return await Ccusage.FetchAsync(Granularity.Monthly).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to fetch ccusage monthly data: {ex.Message}", ex);
        }
    }

    // -----------------------------------------------------------------------
    // JSON export DTOs
    // -----------------------------------------------------------------------

    private static PeriodEconomicsDto ToDto(PeriodEconomics p) => new()
    {
        Label = p.Label,
        CcCost = p.CcCost,
        CcTotalTokens = p.CcTotalTokens,
        CcActiveTokens = p.CcActiveTokens,
        CcInputTokens = p.CcInputTokens,
        CcOutputTokens = p.CcOutputTokens,
        CcCacheCreateTokens = p.CcCacheCreateTokens,
        CcCacheReadTokens = p.CcCacheReadTokens,
        RtkCommands = p.RtkCommands,
        RtkSavedTokens = p.RtkSavedTokens,
        RtkSavingsPct = p.RtkSavingsPct,
        WeightedInputCpt = p.WeightedInputCpt,
        SavingsWeighted = p.SavingsWeighted,
        BlendedCpt = p.BlendedCpt,
        ActiveCpt = p.ActiveCpt,
        SavingsBlended = p.SavingsBlended,
        SavingsActive = p.SavingsActive,
    };

    private static TotalsDto ToDto(Totals t) => new()
    {
        CcCost = t.CcCost,
        CcTotalTokens = t.CcTotalTokens,
        CcActiveTokens = t.CcActiveTokens,
        CcInputTokens = t.CcInputTokens,
        CcOutputTokens = t.CcOutputTokens,
        CcCacheCreateTokens = t.CcCacheCreateTokens,
        CcCacheReadTokens = t.CcCacheReadTokens,
        RtkCommands = t.RtkCommands,
        RtkSavedTokens = t.RtkSavedTokens,
        RtkAvgSavingsPct = t.RtkAvgSavingsPct,
        WeightedInputCpt = t.WeightedInputCpt,
        SavingsWeighted = t.SavingsWeighted,
        BlendedCpt = t.BlendedCpt,
        ActiveCpt = t.ActiveCpt,
        SavingsBlended = t.SavingsBlended,
        SavingsActive = t.SavingsActive,
    };

    internal sealed class EconomicsExport
    {
        [JsonPropertyName("daily")]
        public List<PeriodEconomicsDto>? Daily { get; set; }

        [JsonPropertyName("weekly")]
        public List<PeriodEconomicsDto>? Weekly { get; set; }

        [JsonPropertyName("monthly")]
        public List<PeriodEconomicsDto>? Monthly { get; set; }

        [JsonPropertyName("totals")]
        public TotalsDto? Totals { get; set; }
    }

    internal sealed class PeriodEconomicsDto
    {
        [JsonPropertyName("label")]
        public required string Label { get; set; }

        [JsonPropertyName("cc_cost")]
        public double? CcCost { get; set; }

        [JsonPropertyName("cc_total_tokens")]
        public ulong? CcTotalTokens { get; set; }

        [JsonPropertyName("cc_active_tokens")]
        public ulong? CcActiveTokens { get; set; }

        [JsonPropertyName("cc_input_tokens")]
        public ulong? CcInputTokens { get; set; }

        [JsonPropertyName("cc_output_tokens")]
        public ulong? CcOutputTokens { get; set; }

        [JsonPropertyName("cc_cache_create_tokens")]
        public ulong? CcCacheCreateTokens { get; set; }

        [JsonPropertyName("cc_cache_read_tokens")]
        public ulong? CcCacheReadTokens { get; set; }

        [JsonPropertyName("rtk_commands")]
        public int? RtkCommands { get; set; }

        [JsonPropertyName("rtk_saved_tokens")]
        public ulong? RtkSavedTokens { get; set; }

        [JsonPropertyName("rtk_savings_pct")]
        public double? RtkSavingsPct { get; set; }

        [JsonPropertyName("weighted_input_cpt")]
        public double? WeightedInputCpt { get; set; }

        [JsonPropertyName("savings_weighted")]
        public double? SavingsWeighted { get; set; }

        [JsonPropertyName("blended_cpt")]
        public double? BlendedCpt { get; set; }

        [JsonPropertyName("active_cpt")]
        public double? ActiveCpt { get; set; }

        [JsonPropertyName("savings_blended")]
        public double? SavingsBlended { get; set; }

        [JsonPropertyName("savings_active")]
        public double? SavingsActive { get; set; }
    }

    internal sealed class TotalsDto
    {
        [JsonPropertyName("cc_cost")]
        public double CcCost { get; set; }

        [JsonPropertyName("cc_total_tokens")]
        public ulong CcTotalTokens { get; set; }

        [JsonPropertyName("cc_active_tokens")]
        public ulong CcActiveTokens { get; set; }

        [JsonPropertyName("cc_input_tokens")]
        public ulong CcInputTokens { get; set; }

        [JsonPropertyName("cc_output_tokens")]
        public ulong CcOutputTokens { get; set; }

        [JsonPropertyName("cc_cache_create_tokens")]
        public ulong CcCacheCreateTokens { get; set; }

        [JsonPropertyName("cc_cache_read_tokens")]
        public ulong CcCacheReadTokens { get; set; }

        [JsonPropertyName("rtk_commands")]
        public int RtkCommands { get; set; }

        [JsonPropertyName("rtk_saved_tokens")]
        public ulong RtkSavedTokens { get; set; }

        [JsonPropertyName("rtk_avg_savings_pct")]
        public double RtkAvgSavingsPct { get; set; }

        [JsonPropertyName("weighted_input_cpt")]
        public double? WeightedInputCpt { get; set; }

        [JsonPropertyName("savings_weighted")]
        public double? SavingsWeighted { get; set; }

        [JsonPropertyName("blended_cpt")]
        public double? BlendedCpt { get; set; }

        [JsonPropertyName("active_cpt")]
        public double? ActiveCpt { get; set; }

        [JsonPropertyName("savings_blended")]
        public double? SavingsBlended { get; set; }

        [JsonPropertyName("savings_active")]
        public double? SavingsActive { get; set; }
    }

    // -----------------------------------------------------------------------
    // Flag parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parsed <c>rtk cc-economics</c> flags. Hand-rolled port of the Clap-derived
    /// <c>Commands::CcEconomics</c> arg struct (<c>main.rs:445-461</c>), following the same
    /// hand-rolled-parser convention <see cref="GainCommand"/> established for other
    /// <c>RTK_META_COMMANDS</c>.
    /// </summary>
    private sealed class CcEconomicsArgs
    {
        public bool Daily { get; private set; }

        public bool Weekly { get; private set; }

        public bool Monthly { get; private set; }

        public bool All { get; private set; }

        public string Format { get; private set; } = "text";

        public static CcEconomicsArgs Parse(string[] args)
        {
            var result = new CcEconomicsArgs();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-d" or "--daily":
                        result.Daily = true;
                        break;
                    case "-w" or "--weekly":
                        result.Weekly = true;
                        break;
                    case "-m" or "--monthly":
                        result.Monthly = true;
                        break;
                    case "-a" or "--all":
                        result.All = true;
                        break;
                    case "-f" or "--format":
                        result.Format = RequireValue(args, ref i, arg);
                        break;
                    default:
                        if (arg.StartsWith("--format=", StringComparison.Ordinal))
                        {
                            result.Format = arg["--format=".Length..];
                        }
                        else
                        {
                            throw new CcEconomicsArgsException($"error: unexpected argument '{arg}' found");
                        }

                        break;
                }
            }

            return result;
        }

        private static string RequireValue(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
            {
                throw new CcEconomicsArgsException($"error: a value is required for '{flag}' but none was supplied");
            }

            i++;
            return args[i];
        }
    }

    private sealed class CcEconomicsArgsException(string message) : Exception(message);
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <c>rtk cc-economics --format json</c>'s
/// export shape — required because <c>RtkSharp.csproj</c> sets <c>PublishAot=true</c>.
/// </summary>
[JsonSerializable(typeof(CcEconomicsCommand.EconomicsExport))]
[JsonSerializable(typeof(CcEconomicsCommand.PeriodEconomicsDto))]
[JsonSerializable(typeof(CcEconomicsCommand.TotalsDto))]
internal sealed partial class CcEconomicsJsonContext : JsonSerializerContext;
