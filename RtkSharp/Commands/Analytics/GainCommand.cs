using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Hooks;

namespace RtkSharp.Commands.Analytics;

/// <summary>
/// Implements the <c>rtk gain</c> CLI verb: shows the user how many tokens RTK has saved them over
/// time. Faithful port of Rust <c>src/analytics/gain.rs</c> (<c>run</c> and every helper it calls).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-loud, not never-block.</b> Like <see cref="RtkSharp.Commands.System.ConfigCommand"/> and
/// <see cref="RtkSharp.Hooks.VerifyCommand"/>, this is a user-invoked one-shot command, not a
/// runtime hot path — the RTK-wide "never block the user" fallback pattern does not apply. A
/// <see cref="Tracker"/> construction failure is wrapped with the same context message Rust's
/// <c>.context("Failed to initialize tracking database")?</c> attaches (<c>gain.rs:31</c>) and
/// surfaces as <c>rtk: {message}</c> on stderr with exit code 1.
/// </para>
/// <para>
/// <b><c>RTK_META_COMMANDS</c> parity.</b> Rust's <c>main.rs</c> lists <c>"gain"</c> in
/// <c>RTK_META_COMMANDS</c> so a Clap parse failure shows Clap's own error instead of falling back to
/// raw shell execution (<c>main.rs:1170-1205</c>). RtkSharp has no Clap-equivalent parser or raw
/// passthrough fallback *inside* a registered command — <c>RtkProgram.RunAsync</c>'s
/// <see cref="RtkSharp.Cli.CommandRegistry"/> lookup already short-circuits the TOML-fallback/raw-passthrough
/// path entirely for any registered verb (see <c>Program.cs</c>'s dispatch order), so registering
/// <c>"gain"</c> there (done in <c>CommandRegistry</c>'s static constructor) achieves the same
/// never-falls-back-to-raw-execution guarantee structurally, without a separate meta-command list.
/// </para>
/// <para>
/// <b>Disclosed simplification: flag-parse error text.</b> Clap renders a multi-line usage banner
/// (full `Usage:` line, wrapped at terminal width, `For more information, try '--help'.` etc.) for
/// parse errors and <c>requires</c> violations (<c>--tier</c> without <c>--quota</c>, <c>--yes</c>
/// without <c>--reset</c>). Reproducing Clap's exact banner byte-for-byte is out of scope (it would
/// require re-implementing Clap's usage-string renderer for no behavioral benefit) — this port emits
/// a short, single-purpose <c>error: ...</c> message to stderr and exits 2 (Clap's own exit code for
/// usage errors), preserving the fail-fast *behavior* (nonzero exit, no raw-command fallback, no
/// partial output) without chasing Clap's exact prose. Ledgered in
/// <c>docs/parity/compatibility-ledger.md</c>.
/// </para>
/// </remarks>
public static class GainCommand
{
    /// <summary>Estimated Pro-tier monthly token quota, used as the base unit for all quota-tier multipliers (Rust <c>ESTIMATED_PRO_MONTHLY</c>, <c>gain.rs:276</c>).</summary>
    private const long EstimatedProMonthly = 6_000_000;

    /// <summary>The 58-dash rule used under every default-view section header (<c>gain.rs:237,246,288</c>).</summary>
    private const string ThinRule = "──────────────────────────────────────────────────────────";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new SerdeDoubleConverter() },
    };

    /// <summary>
    /// The source-generated context bound to <see cref="JsonOptions"/> (naming policy, indent, and
    /// the custom float converter all apply to its generated metadata) — used instead of
    /// <c>JsonSerializer.Serialize&lt;T&gt;(value, JsonSerializerOptions)</c>'s generic reflection-based
    /// overload so <c>rtk gain --format json</c> works under <c>PublishAot=true</c> without trimming
    /// warnings.
    /// </summary>
    private static readonly GainJsonContext JsonContext = new(JsonOptions);

    /// <summary>
    /// Runs <c>rtk gain</c> with the given arguments (the remainder after the <c>gain</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>gain</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        try
        {
            return RunCore(args);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    private static int RunCore(string[] args)
    {
        GainArgs parsed;
        try
        {
            parsed = GainArgs.Parse(args);
        }
        catch (GainArgsException ex)
        {
            Console.Error.Write(ex.Message + "\n");
            return 2;
        }

        Tracker tracker;
        try
        {
            tracker = new Tracker();
        }
        catch (Exception ex)
        {
            // Mirrors Rust's `Tracker::new().context("Failed to initialize tracking database")?`.
            throw new IOException($"Failed to initialize tracking database: {ex.Message}", ex);
        }

        using (tracker)
        {
            var projectScope = ResolveProjectScope(parsed.Project);

            if (parsed.Reset)
            {
                if (!parsed.Yes && !ConfirmReset())
                {
                    Print("Aborted.\n");
                    return 0;
                }

                tracker.ResetAll();
                Print(Styled("Token savings stats reset to zero.", true) + "\n");
                return 0;
            }

            if (parsed.Failures)
            {
                return ShowFailures(tracker);
            }

            if (parsed.Format == "json")
            {
                return ExportJson(tracker, parsed.Daily, parsed.Weekly, parsed.Monthly, parsed.All, projectScope);
            }

            if (parsed.Format == "csv")
            {
                return ExportCsv(tracker, parsed.Daily, parsed.Weekly, parsed.Monthly, parsed.All, projectScope);
            }

            // Any other format value (including typos) silently falls through to the text view,
            // matching Rust's `match format { "json" => ..., "csv" => ..., _ => {} }` (gain.rs:51-73).
            var summary = tracker.GetSummaryFiltered(projectScope);
            if (summary.TotalCommands == 0)
            {
                Print("No tracking data yet.\n");
                Print("Run some rtk commands to start tracking savings.\n");
                return 0;
            }

            if (!parsed.Daily && !parsed.Weekly && !parsed.Monthly && !parsed.All)
            {
                return PrintDefaultView(tracker, summary, projectScope, parsed);
            }

            if (parsed.All || parsed.Daily)
            {
                Print(DisplayHelpers.PrintPeriodTable(tracker.GetAllDaysFiltered(projectScope)));
            }

            if (parsed.All || parsed.Weekly)
            {
                Print(DisplayHelpers.PrintPeriodTable(tracker.GetByWeekFiltered(projectScope)));
            }

            if (parsed.All || parsed.Monthly)
            {
                Print(DisplayHelpers.PrintPeriodTable(tracker.GetByMonthFiltered(projectScope)));
            }

            return 0;
        }
    }

    // -----------------------------------------------------------------------
    // Default summary view (gain.rs:85-302)
    // -----------------------------------------------------------------------

    private static int PrintDefaultView(Tracker tracker, GainSummary summary, string? projectScope, GainArgs args)
    {
        var title = projectScope is not null
            ? "RTK Token Savings (Project Scope)"
            : "RTK Token Savings (Global Scope)";
        Print(Styled(title, true) + "\n");
        Print(new string('═', 60) + "\n");
        if (projectScope is not null)
        {
            Print($"Scope: {ShortenPath(projectScope)}\n");
        }

        Print("\n");

        PrintKpi("Total commands", summary.TotalCommands.ToString(CultureInfo.InvariantCulture));
        PrintKpi("Input tokens", Utils.FormatTokens(summary.TotalInput));
        PrintKpi("Output tokens", Utils.FormatTokens(summary.TotalOutput));
        PrintKpi(
            "Tokens saved",
            $"{Utils.FormatTokens(summary.TotalSaved)} ({summary.AvgSavingsPct.ToString("F1", CultureInfo.InvariantCulture)}%)");
        PrintKpi(
            "Total exec time",
            $"{DisplayHelpers.FormatDuration(summary.TotalTimeMs)} (avg {DisplayHelpers.FormatDuration(summary.AvgTimeMs)})");
        PrintEfficiencyMeter(summary.AvgSavingsPct);
        Print("\n");

        // Warn about hook issues that silently kill savings (stderr, not stdout). Calls
        // HookCheck.Status() directly, NOT HookCheck.MaybeWarn() — this is gain's own, separately
        // formatted, "[warn] ..."-prefixed warning (gain.rs:125-137), distinct from the rate-limited
        // startup warning in RtkProgram.RunAsync (which is explicitly skipped for the "gain" verb).
        switch (HookCheck.Status())
        {
            case HookStatus.Missing:
                Console.Error.Write(
                    StyledWarn("[warn] No hook installed — run `rtk init -g` for automatic token savings") + "\n");
                Console.Error.Write("\n");
                break;
            case HookStatus.Outdated:
                Console.Error.Write(StyledWarn("[warn] Hook outdated — run `rtk init -g` to update") + "\n");
                Console.Error.Write("\n");
                break;
            case HookStatus.Ok:
            default:
                break;
        }

        // check_rtk_disabled_bypass() (gain.rs:641-685) is intentionally NOT ported: it scans recent
        // Claude Code session transcripts via the entirely-unported `discover` module
        // (ClaudeProvider/cmd_has_rtk_disabled_prefix). Per the Phase 5 plan's Scope Decision, this is
        // a disclosed, narrow omission — Rust's own contract for this helper is "best-effort, silent
        // on any error" (a missing/unreadable session directory already yields None there too), so
        // never emitting this one warning line is in-contract, not a behavior change. See
        // docs/parity/compatibility-ledger.md.
        if (summary.ByCommand.Count > 0)
        {
            PrintByCommandTable(summary.ByCommand);
        }

        if (args.Graph && summary.ByDay.Count > 0)
        {
            Print(Styled("Daily Savings (last 30 days)", true) + "\n");
            Print(ThinRule + "\n");
            Print(PrintAsciiGraph(summary.ByDay));
            Print("\n");
        }

        if (args.History)
        {
            var recent = tracker.GetRecentFiltered(10, projectScope);
            if (recent.Count > 0)
            {
                Print(Styled("Recent Commands", true) + "\n");
                Print(ThinRule + "\n");
                foreach (var rec in recent)
                {
                    var time = rec.Timestamp.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                    var cmdShort = rec.RtkCmd.Length > 25 ? rec.RtkCmd[..22] + "..." : rec.RtkCmd;
                    var sign = rec.SavingsPct >= 70.0 ? "▲" : rec.SavingsPct >= 30.0 ? "■" : "•";
                    Print(
                        $"{time} {sign} {cmdShort.PadRight(25)} -{rec.SavingsPct.ToString("F0", CultureInfo.InvariantCulture)}% ({Utils.FormatTokens(rec.SavedTokens)})\n");
                }

                Print("\n");
            }
        }

        if (args.Quota)
        {
            var (quotaTokens, tierName) = args.Tier switch
            {
                "pro" => (EstimatedProMonthly, "Pro ($20/mo)"),
                "5x" => (EstimatedProMonthly * 5, "Max 5x ($100/mo)"),
                "20x" => (EstimatedProMonthly * 20, "Max 20x ($200/mo)"),
                _ => (EstimatedProMonthly, "Pro ($20/mo)"), // silent fallback — a deliberate Rust quirk (gain.rs:282), not fixed here.
            };

            var quotaPct = summary.TotalSaved / (double)quotaTokens * 100.0;

            Print(Styled("Monthly Quota Analysis", true) + "\n");
            Print(ThinRule + "\n");
            PrintKpi("Subscription tier", tierName);
            PrintKpi("Estimated monthly quota", Utils.FormatTokens(quotaTokens));
            PrintKpi("Tokens saved (lifetime)", Utils.FormatTokens(summary.TotalSaved));
            PrintKpi("Quota preserved", $"{quotaPct.ToString("F1", CultureInfo.InvariantCulture)}%");
            Print("\n");
            Print("Note: Heuristic estimate based on ~44K tokens/5h (Pro baseline)\n");
            Print("      Actual limits use rolling 5-hour windows, not monthly caps.\n");
        }

        return 0;
    }

    private static void PrintByCommandTable(IReadOnlyList<CommandStat> byCommand)
    {
        const int cmdWidth = 24;
        const int impactWidth = 10;

        var countWidth = Math.Max(byCommand.Max(c => c.Count.ToString(CultureInfo.InvariantCulture).Length), 5);
        var savedWidth = Math.Max(byCommand.Max(c => Utils.FormatTokens(c.SavedTokens).Length), 5);
        var timeWidth = Math.Max(byCommand.Max(c => DisplayHelpers.FormatDuration(c.AvgTimeMs).Length), 6);

        var tableWidth = 3 + 2 + cmdWidth + 2 + countWidth + 2 + savedWidth + 2 + 6 + 2 + timeWidth + 2 + impactWidth;

        Print(Styled("By Command", true) + "\n");
        Print(new string('─', tableWidth) + "\n");
        Print(
            "#".PadLeft(3) + "  " + "Command".PadRight(cmdWidth) + "  " + "Count".PadLeft(countWidth) + "  "
            + "Saved".PadLeft(savedWidth) + "  " + "Avg%".PadLeft(6) + "  " + "Time".PadLeft(timeWidth) + "  "
            + "Impact".PadRight(impactWidth) + "\n");
        Print(new string('─', tableWidth) + "\n");

        var maxSaved = byCommand.Max(c => c.SavedTokens);

        for (var idx = 0; idx < byCommand.Count; idx++)
        {
            var c = byCommand[idx];
            var rowIdx = (idx + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2) + ".";
            var cmdCell = StyleCommandCell(TruncateForColumn(c.Command, cmdWidth));
            var countCell = c.Count.ToString(CultureInfo.InvariantCulture).PadLeft(countWidth);
            var savedCell = Utils.FormatTokens(c.SavedTokens).PadLeft(savedWidth);
            var pctPlain = (c.AvgSavingsPct.ToString("F1", CultureInfo.InvariantCulture) + "%").PadLeft(6);
            var pctCell = ColorizePctCell(c.AvgSavingsPct, pctPlain);
            var timeCell = DisplayHelpers.FormatDuration(c.AvgTimeMs).PadLeft(timeWidth);
            var impact = MiniBar(c.SavedTokens, maxSaved, impactWidth);
            Print($"{rowIdx}  {cmdCell}  {countCell}  {savedCell}  {pctCell}  {timeCell}  {impact}\n");
        }

        Print(new string('─', tableWidth) + "\n");
        Print("\n");
    }

    /// <summary>
    /// Renders the "Daily Savings" ASCII bar-chart section. Faithful port of Rust
    /// <c>print_ascii_graph</c> (<c>gain.rs:447-475</c>): a fixed 40-character-wide bar per day,
    /// scaled linearly against the maximum day's saved-token count (truncated toward zero, not
    /// rounded — <c>as usize</c> on a non-negative <c>f64</c>), followed by right-padding spaces to
    /// fill the remaining bar width, then a literal <c>│</c> gutter character and the formatted
    /// token count.
    /// </summary>
    /// <param name="data">The day-by-saved-tokens series, oldest first (empty guard is the caller's responsibility).</param>
    /// <returns>The rendered graph lines, each newline-terminated.</returns>
    private static string PrintAsciiGraph(IReadOnlyList<DaySavings> data)
    {
        if (data.Count == 0)
        {
            return string.Empty;
        }

        var maxVal = data.Max(d => d.SavedTokens);
        const int width = 40;

        var sb = new StringBuilder();
        foreach (var entry in data)
        {
            var dateShort = entry.Date.Length >= 10 ? entry.Date.Substring(5, 5) : entry.Date;
            var barLen = maxVal > 0 ? (int)(entry.SavedTokens / (double)maxVal * width) : 0;
            var bar = new string('█', barLen);
            var spaces = new string(' ', width - barLen);
            sb.Append(dateShort).Append(" │").Append(bar).Append(spaces).Append(' ')
                .Append(Utils.FormatTokens(entry.SavedTokens)).Append('\n');
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // --failures view (gain.rs:687-741)
    // -----------------------------------------------------------------------

    private static int ShowFailures(Tracker tracker)
    {
        var summary = tracker.GetParseFailureSummary();

        if (summary.Total == 0)
        {
            Print("No parse failures recorded.\n");
            Print("This means all commands parsed successfully (or fallback hasn't triggered yet).\n");
            return 0;
        }

        Print(Styled("RTK Parse Failures", true) + "\n");
        Print(new string('═', 60) + "\n");
        Print("\n");

        PrintKpi("Total failures", summary.Total.ToString(CultureInfo.InvariantCulture));
        PrintKpi("Recovery rate", $"{summary.RecoveryRate.ToString("F1", CultureInfo.InvariantCulture)}%");
        Print("\n");

        if (summary.TopCommands.Count > 0)
        {
            Print(Styled("Top Commands (by frequency)", true) + "\n");
            Print(new string('─', 60) + "\n");
            foreach (var (cmd, count) in summary.TopCommands)
            {
                var display = cmd.Length > 50 ? cmd[..47] + "..." : cmd;
                Print($"  {count.ToString(CultureInfo.InvariantCulture).PadLeft(4)}x  {display}\n");
            }

            Print("\n");
        }

        if (summary.Recent.Count > 0)
        {
            Print(Styled("Recent Failures (last 10)", true) + "\n");
            Print(new string('─', 60) + "\n");
            foreach (var rec in summary.Recent)
            {
                var tsShort = rec.Timestamp.Length >= 16 ? rec.Timestamp[..16] : rec.Timestamp;
                var status = rec.FallbackSucceeded ? "ok" : "FAIL";
                var cmdDisplay = rec.RawCommand.Length > 40 ? rec.RawCommand[..37] + "..." : rec.RawCommand;
                Print($"  {tsShort} [{status}] {cmdDisplay}\n");
            }

            Print("\n");
        }

        return 0;
    }

    // -----------------------------------------------------------------------
    // --format json / csv (gain.rs:498-636)
    // -----------------------------------------------------------------------

    private static int ExportJson(Tracker tracker, bool daily, bool weekly, bool monthly, bool all, string? projectScope)
    {
        var summary = tracker.GetSummaryFiltered(projectScope);

        var export = new ExportData
        {
            Summary = new ExportSummary
            {
                TotalCommands = summary.TotalCommands,
                TotalInput = summary.TotalInput,
                TotalOutput = summary.TotalOutput,
                TotalSaved = summary.TotalSaved,
                AvgSavingsPct = summary.AvgSavingsPct,
                TotalTimeMs = summary.TotalTimeMs,
                AvgTimeMs = summary.AvgTimeMs,
            },
            Daily = all || daily ? tracker.GetAllDaysFiltered(projectScope) : null,
            Weekly = all || weekly ? tracker.GetByWeekFiltered(projectScope) : null,
            Monthly = all || monthly ? tracker.GetByMonthFiltered(projectScope) : null,
        };

        // Utf8JsonWriter's indented mode emits Environment.NewLine between elements (CRLF on
        // Windows); normalize to "\n" per the house "\n"-only stdout rule (serde_json never emits
        // CRLF either, so this is a pure normalization, not a behavior change).
        var json = JsonSerializer.Serialize(export, JsonContext.ExportData).Replace("\r\n", "\n", StringComparison.Ordinal);
        Print(json + "\n");
        return 0;
    }

    private static int ExportCsv(Tracker tracker, bool daily, bool weekly, bool monthly, bool all, string? projectScope)
    {
        if (all || daily)
        {
            var days = tracker.GetAllDaysFiltered(projectScope);
            Print("# Daily Data\n");
            Print("date,commands,input_tokens,output_tokens,saved_tokens,savings_pct,total_time_ms,avg_time_ms\n");
            foreach (var day in days)
            {
                Print(
                    $"{day.Date},{day.Commands},{day.InputTokens},{day.OutputTokens},{day.SavedTokens},{day.SavingsPct.ToString("F2", CultureInfo.InvariantCulture)},{day.TotalTimeMs},{day.AvgTimeMs}\n");
            }

            Print("\n");
        }

        if (all || weekly)
        {
            var weeks = tracker.GetByWeekFiltered(projectScope);
            Print("# Weekly Data\n");
            Print(
                "week_start,week_end,commands,input_tokens,output_tokens,saved_tokens,savings_pct,total_time_ms,avg_time_ms\n");
            foreach (var week in weeks)
            {
                Print(
                    $"{week.WeekStart},{week.WeekEnd},{week.Commands},{week.InputTokens},{week.OutputTokens},{week.SavedTokens},{week.SavingsPct.ToString("F2", CultureInfo.InvariantCulture)},{week.TotalTimeMs},{week.AvgTimeMs}\n");
            }

            Print("\n");
        }

        if (all || monthly)
        {
            var months = tracker.GetByMonthFiltered(projectScope);
            Print("# Monthly Data\n");
            Print("month,commands,input_tokens,output_tokens,saved_tokens,savings_pct,total_time_ms,avg_time_ms\n");
            foreach (var month in months)
            {
                Print(
                    $"{month.Month},{month.Commands},{month.InputTokens},{month.OutputTokens},{month.SavedTokens},{month.SavingsPct.ToString("F2", CultureInfo.InvariantCulture)},{month.TotalTimeMs},{month.AvgTimeMs}\n");
            }
        }

        return 0;
    }

    // -----------------------------------------------------------------------
    // --reset flow (gain.rs:34-44, 745-764)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prompts the user (on stderr) to confirm a destructive reset, defaulting to No when stdin is
    /// not a terminal (piped/redirected). Faithful port of Rust <c>confirm_reset()</c>
    /// (<c>gain.rs:745-764</c>).
    /// </summary>
    private static bool ConfirmReset()
    {
        Console.Error.Write("This will permanently delete all tracking data. Continue? [y/N] ");

        if (Console.IsInputRedirected)
        {
            Console.Error.Write("(non-interactive mode, defaulting to N)\n");
            return false;
        }

        var line = Console.In.ReadLine() ?? string.Empty;
        var response = line.Trim().ToLowerInvariant();
        return response is "y" or "yes";
    }

    // -----------------------------------------------------------------------
    // Shared helpers (gain.rs:320-445)
    // -----------------------------------------------------------------------

    private static void Print(string text) => Console.Out.Write(text);

    private static bool IsTty() => !Console.IsOutputRedirected;

    private static string Wrap(string text, string codes) => $"\x1b[{codes}m{text}\x1b[0m";

    /// <summary>Bold+green when the terminal check passes; plain text otherwise. Port of Rust <c>styled()</c> (<c>gain.rs:322-332</c>).</summary>
    private static string Styled(string text, bool strong) => strong && IsTty() ? Wrap(text, "1;32") : text;

    /// <summary>Plain yellow (no bold) — used only by the hook-status warning lines. Port of the <c>.yellow()</c> calls at <c>gain.rs:130,137</c>.</summary>
    private static string StyledWarn(string text) => IsTty() ? Wrap(text, "33") : text;

    /// <summary>Port of Rust <c>colorize_pct_cell</c> (<c>gain.rs:339-351</c>): green/yellow/red (all bold) at the 70%/40% thresholds.</summary>
    private static string ColorizePctCell(double pct, string padded)
    {
        if (!IsTty())
        {
            return padded;
        }

        if (pct >= 70.0)
        {
            return Wrap(padded, "1;32");
        }

        return pct >= 40.0 ? Wrap(padded, "1;33") : Wrap(padded, "1;31");
    }

    /// <summary>
    /// Truncates (Unicode-scalar-aware, not UTF-16-code-unit-aware) or pads text to fit a fixed
    /// column width, ellipsizing with <c>"..."</c> when it overflows. Faithful port of Rust
    /// <c>truncate_for_column</c> (<c>gain.rs:353-368</c>), which counts/slices by <c>chars()</c>
    /// (Unicode scalar values).
    /// </summary>
    private static string TruncateForColumn(string text, int width)
    {
        if (width == 0)
        {
            return string.Empty;
        }

        var runes = text.EnumerateRunes().Select(r => r.ToString()).ToList();
        if (runes.Count <= width)
        {
            return text + new string(' ', width - runes.Count);
        }

        if (width <= 3)
        {
            return string.Concat(runes.Take(width));
        }

        return string.Concat(runes.Take(width - 3)) + "...";
    }

    /// <summary>Bright-cyan+bold command-name styling. Port of Rust <c>style_command_cell</c> (<c>gain.rs:370-376</c>).</summary>
    private static string StyleCommandCell(string cmd) => IsTty() ? Wrap(cmd, "1;96") : cmd;

    /// <summary>Proportional block-bar mini-chart. Port of Rust <c>mini_bar</c> (<c>gain.rs:378-392</c>).</summary>
    private static string MiniBar(long value, long max, int width)
    {
        if (max <= 0 || width == 0)
        {
            return string.Empty;
        }

        var filled = Math.Min((int)Math.Round(value / (double)max * width, MidpointRounding.AwayFromZero), width);
        var bar = new string('█', filled) + new string('░', width - filled);
        return IsTty() ? Wrap(bar, "36") : bar;
    }

    /// <summary>24-char efficiency meter with a green/yellow/red-colored percentage. Port of Rust <c>print_efficiency_meter</c> (<c>gain.rs:394-412</c>).</summary>
    private static void PrintEfficiencyMeter(double pct)
    {
        const int width = 24;
        var filled = Math.Min((int)Math.Round(pct / 100.0 * width, MidpointRounding.AwayFromZero), width);
        var meter = new string('█', filled) + new string('░', width - filled);

        if (IsTty())
        {
            var pctStr = pct.ToString("F1", CultureInfo.InvariantCulture) + "%";
            var coloredPct = pct >= 70.0 ? Wrap(pctStr, "1;32") : pct >= 40.0 ? Wrap(pctStr, "1;33") : Wrap(pctStr, "1;31");
            Print($"Efficiency meter: {Wrap(meter, "32")} {coloredPct}\n");
        }
        else
        {
            Print($"Efficiency meter: {meter} {pct.ToString("F1", CultureInfo.InvariantCulture)}%\n");
        }
    }

    /// <summary>Left-pads a KPI label to 18 characters (including the trailing colon). Port of Rust <c>print_kpi</c> (<c>gain.rs:334-337</c>).</summary>
    private static void PrintKpi(string label, string value) => Print($"{(label + ":").PadRight(18)} {value}\n");

    /// <summary>
    /// Resolves the <c>--project</c> scope to the canonicalized current working directory, or
    /// <see langword="null"/> for global scope. Faithful port of Rust <c>resolve_project_scope</c>
    /// (<c>gain.rs:414-422</c>), reusing the same <see cref="Path.GetFullPath(string)"/>-based
    /// canonicalization <see cref="Tracker"/>'s own <c>current_project_path_string</c> uses (Task 2),
    /// so a <c>--project</c>-scoped query matches exactly the <c>project_path</c> values
    /// <see cref="Tracker.Record"/> stores for commands run from the same directory.
    /// </summary>
    private static string? ResolveProjectScope(bool project)
    {
        if (!project)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Directory.GetCurrentDirectory());
        }
        catch (Exception)
        {
            return Directory.GetCurrentDirectory();
        }
    }

    /// <summary>
    /// Shortens a long absolute path for display: paths of 4 or fewer components print in full;
    /// longer paths abbreviate to <c>{root}/.../{parent}/{last}</c>. Faithful port of Rust
    /// <c>shorten_path</c> (<c>gain.rs:424-445</c>).
    /// </summary>
    /// <remarks>
    /// <b>Disclosed simplification.</b> Rust's <c>Path::components()</c> iterates platform-specific
    /// path components (on Windows: a distinct <c>Prefix</c> component like <c>C:</c>, then
    /// <c>RootDir</c>, then each <c>Normal</c> segment — further complicated by
    /// <c>resolve_project_scope</c>'s <c>canonicalize()</c> call, which on Windows prepends the
    /// verbatim <c>\\?\</c> prefix). This port instead splits on <see cref="Path.GetPathRoot(string)"/>
    /// plus a simple <c>/</c>/<c>\</c> segment split, which agrees with Rust's component count and
    /// output for ordinary project paths (the overwhelmingly common case) without replicating the
    /// verbatim-path/prefix-component edge cases exactly. Ledgered in
    /// <c>docs/parity/compatibility-ledger.md</c>.
    /// </remarks>
    private static string ShortenPath(string path)
    {
        var comps = SplitPathComponents(path);
        if (comps.Count <= 4)
        {
            return path;
        }

        var root = comps[0];
        return string.IsNullOrEmpty(root) || root == "/"
            ? $"/.../{comps[^2]}/{comps[^1]}"
            : $"{root}/.../{comps[^2]}/{comps[^1]}";
    }

    private static List<string> SplitPathComponents(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var rest = path[root.Length..];
        var segments = rest.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        var comps = new List<string>();
        var trimmedRoot = root.TrimEnd('/', '\\');
        if (!string.IsNullOrEmpty(trimmedRoot))
        {
            comps.Add(trimmedRoot);
        }
        else if (!string.IsNullOrEmpty(root))
        {
            comps.Add(root);
        }

        comps.AddRange(segments);
        return comps;
    }

    // -----------------------------------------------------------------------
    // JSON export DTOs (gain.rs:498-518)
    // -----------------------------------------------------------------------

    internal sealed class ExportData
    {
        [JsonPropertyName("summary")]
        public required ExportSummary Summary { get; init; }

        [JsonPropertyName("daily")]
        public IReadOnlyList<DayStats>? Daily { get; init; }

        [JsonPropertyName("weekly")]
        public IReadOnlyList<WeekStats>? Weekly { get; init; }

        [JsonPropertyName("monthly")]
        public IReadOnlyList<MonthStats>? Monthly { get; init; }
    }

    internal sealed class ExportSummary
    {
        [JsonPropertyName("total_commands")]
        public required int TotalCommands { get; init; }

        [JsonPropertyName("total_input")]
        public required long TotalInput { get; init; }

        [JsonPropertyName("total_output")]
        public required long TotalOutput { get; init; }

        [JsonPropertyName("total_saved")]
        public required long TotalSaved { get; init; }

        [JsonPropertyName("avg_savings_pct")]
        public required double AvgSavingsPct { get; init; }

        [JsonPropertyName("total_time_ms")]
        public required long TotalTimeMs { get; init; }

        [JsonPropertyName("avg_time_ms")]
        public required long AvgTimeMs { get; init; }
    }

    /// <summary>
    /// Forces every <see cref="double"/> to serialize the way Rust's <c>serde_json</c> (backed by
    /// the <c>ryu</c> float formatter) does: the shortest round-trippable decimal representation,
    /// always including a decimal point — e.g. an exact whole number like <c>60.0</c> serializes as
    /// <c>"60.0"</c>, never bare <c>"60"</c> (which is what <see cref="System.Text.Json"/>'s built-in
    /// <see cref="double"/> writer produces for whole-number doubles by default).
    /// </summary>
    private sealed class SerdeDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
            writer.WriteRawValue(FormatSerdeFloat(value));

        private static string FormatSerdeFloat(double value)
        {
            var s = value.ToString(CultureInfo.InvariantCulture);
            return s.IndexOfAny(['.', 'e', 'E']) < 0 ? s + ".0" : s;
        }
    }

    // -----------------------------------------------------------------------
    // Flag parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parsed <c>rtk gain</c> flags. Hand-rolled port of the Clap-derived <c>Commands::Gain</c> arg
    /// struct (<c>main.rs:402-442</c>) — see the disclosed simplification note on
    /// <see cref="GainCommand"/> regarding parse-error text fidelity, and note that bundled
    /// short-boolean-flags (e.g. Clap's implicit <c>-pg</c> for <c>-p -g</c>) are not supported;
    /// each flag must be passed separately.
    /// </summary>
    private sealed class GainArgs
    {
        public bool Project { get; private set; }

        public bool Graph { get; private set; }

        public bool History { get; private set; }

        public bool Quota { get; private set; }

        public string Tier { get; private set; } = "20x";

        public bool Daily { get; private set; }

        public bool Weekly { get; private set; }

        public bool Monthly { get; private set; }

        public bool All { get; private set; }

        public string Format { get; private set; } = "text";

        public bool Failures { get; private set; }

        public bool Reset { get; private set; }

        public bool Yes { get; private set; }

        public static GainArgs Parse(string[] args)
        {
            var result = new GainArgs();
            var tierExplicit = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-p" or "--project":
                        result.Project = true;
                        break;
                    case "-g" or "--graph":
                        result.Graph = true;
                        break;
                    case "-H" or "--history":
                        result.History = true;
                        break;
                    case "-q" or "--quota":
                        result.Quota = true;
                        break;
                    case "-t" or "--tier":
                        result.Tier = RequireValue(args, ref i, arg);
                        tierExplicit = true;
                        break;
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
                    case "-F" or "--failures":
                        result.Failures = true;
                        break;
                    case "--reset":
                        result.Reset = true;
                        break;
                    case "--yes":
                        result.Yes = true;
                        break;
                    default:
                        if (arg.StartsWith("--tier=", StringComparison.Ordinal))
                        {
                            result.Tier = arg["--tier=".Length..];
                            tierExplicit = true;
                        }
                        else if (arg.StartsWith("--format=", StringComparison.Ordinal))
                        {
                            result.Format = arg["--format=".Length..];
                        }
                        else
                        {
                            throw new GainArgsException($"error: unexpected argument '{arg}' found");
                        }

                        break;
                }
            }

            if (tierExplicit && !result.Quota)
            {
                throw new GainArgsException(
                    "error: the following required arguments were not provided:\n  --quota\n\nUsage: rtk gain --quota --tier <TIER>\n\nFor more information, try '--help'.");
            }

            if (result.Yes && !result.Reset)
            {
                throw new GainArgsException(
                    "error: the following required arguments were not provided:\n  --reset\n\nUsage: rtk gain --reset --yes\n\nFor more information, try '--help'.");
            }

            return result;
        }

        private static string RequireValue(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
            {
                throw new GainArgsException($"error: a value is required for '{flag}' but none was supplied");
            }

            i++;
            return args[i];
        }
    }

    private sealed class GainArgsException(string message) : Exception(message);
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <c>rtk gain --format json</c>'s export
/// shape, avoiding the reflection-based serialization <see cref="JsonSerializer"/> falls back to
/// otherwise — required because <c>RtkSharp.csproj</c> sets <c>PublishAot=true</c> (mirrors the
/// AOT-safe source-generation approach <c>Config.cs</c> already uses for Tomlyn).
/// </summary>
[JsonSerializable(typeof(GainCommand.ExportData))]
[JsonSerializable(typeof(GainCommand.ExportSummary))]
[JsonSerializable(typeof(DayStats))]
[JsonSerializable(typeof(WeekStats))]
[JsonSerializable(typeof(MonthStats))]
internal sealed partial class GainJsonContext : JsonSerializerContext;
