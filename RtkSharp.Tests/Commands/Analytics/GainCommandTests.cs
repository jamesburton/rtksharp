using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using RtkSharp.Commands.Analytics;
using RtkSharp.Core.Tracking;
using RtkSharp.Tests.Hooks;
using Xunit;

namespace RtkSharp.Tests.Commands.Analytics;

/// <summary>
/// Port of Rust <c>src/analytics/gain.rs</c>'s behavior via <see cref="GainCommand.Run"/> — no
/// dedicated Rust unit-test module exists for <c>gain.rs</c> itself (it has no
/// <c>#[cfg(test)] mod tests</c>), so these tests are original coverage written directly against the
/// Rust source's documented behavior (flag semantics, exact text, table widths, JSON/CSV shapes),
/// not a port of pre-existing Rust test cases.
/// </summary>
/// <remarks>
/// Every test that touches <c>RTK_DB_PATH</c> and/or <c>CLAUDE_CONFIG_DIR</c>/<c>RTK_CONFIG_DIR_OVERRIDE</c>
/// redirects both to a throwaway <see cref="TempDir"/> so the real host's tracking database and
/// <c>~/.claude</c> directory are never touched — reusing <see cref="GlobalScopeGuard"/> from
/// <c>InitTestSupport.cs</c> for the latter (same assembly, <c>internal</c> visibility). The whole
/// assembly runs with <c>DisableTestParallelization = true</c> (<c>AssemblyInfo.cs</c>), so a bare
/// save/restore of <c>RTK_DB_PATH</c> (via <see cref="DbPathGuard"/>) is sufficient — no additional
/// cross-class locking is required.
/// </remarks>
public sealed class GainCommandTests
{
    // -----------------------------------------------------------------------
    // No tracking data yet (default view only — gain.rs:79-83)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_NoData_DefaultView_PrintsNoTrackingDataMessage()
    {
        using var tmp = new TempDir();
        using var db = new DbPathGuard(Path.Combine(tmp.Root, "history.db"));
        using var claude = new GlobalScopeGuard(tmp); // Missing hook status also exercised implicitly (no data => early return before hook check).
        using var console = new ConsoleCapture();

        var exitCode = GainCommand.Run([]);

        Assert.Equal(0, exitCode);
        Assert.Equal("No tracking data yet.\nRun some rtk commands to start tracking savings.\n", console.Out.ToString());
    }

    [Fact]
    public void Run_NoData_DailyFlag_AlsoShowsNoTrackingDataMessage()
    {
        // Rust's `if summary.total_commands == 0 { ...; return Ok(()); }` guard (gain.rs:79-83) runs
        // unconditionally before the `if !daily && !weekly && !monthly && !all` default-view branch —
        // it is NOT skipped for --daily/--weekly/--monthly/--all. DisplayHelpers.PrintPeriodTable's own
        // "No {label} data available." guard is therefore only reachable via this same early return
        // path never firing, i.e. only when the *filtered* query the period table itself runs returns
        // rows for other periods but the requested one legitimately has none (not exercised by an
        // entirely-empty database, which this test covers).
        using var tmp = new TempDir();
        using var db = new DbPathGuard(Path.Combine(tmp.Root, "history.db"));
        using var claude = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exitCode = GainCommand.Run(["--daily"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("No tracking data yet.\nRun some rtk commands to start tracking savings.\n", console.Out.ToString());
    }

    // -----------------------------------------------------------------------
    // Default view — KPIs, efficiency meter, By Command table (gain.rs:85-233)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_DefaultView_PrintsExactKpiBlockAndByCommandTable()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using var claude = new GlobalScopeGuard(tmp); // no hooks dir contents => HookStatus.Missing; asserted separately below.
        SeedSchema(dbPath);

        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk git status", 100, 20, 80, 80.0, 500, "");
        InsertRow(dbPath, "2026-07-01T11:00:00.000000+00:00", "rtk cargo test", 5000, 600, 4400, 88.0, 19500, "");

        using var console = new ConsoleCapture();
        var exitCode = GainCommand.Run([]);
        var output = console.Out.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("RTK Token Savings (Global Scope)\n", output);
        Assert.Contains(new string('═', 60), output);
        Assert.Contains("Total commands:    2\n", output);
        Assert.Contains("Input tokens:      5.1K\n", output);
        Assert.Contains("Output tokens:     620\n", output);
        // total_saved = 4480, total_input = 5100 -> 87.84313725490196% (F1 => 87.8%)
        Assert.Contains("Tokens saved:      4.5K (87.8%)\n", output);
        Assert.Contains("Total exec time:   20.0s (avg 10.0s)\n", output);
        Assert.Contains("Efficiency meter: ", output);
        Assert.Contains("By Command\n", output);
        Assert.Contains("rtk cargo test", output);
        Assert.Contains("rtk git status", output);

        // Gain's own inline hook-status warning goes to stderr, not stdout (HookCheck.Status() called
        // directly, not MaybeWarn() — the two are intentionally distinct, see HookCheck.cs remarks).
        Assert.Contains("[warn] No hook installed — run `rtk init -g` for automatic token savings\n", console.Error.ToString());
    }

    [Fact]
    public void Run_DefaultView_HookOk_NoWarningLine()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using var claude = new EnvVarScope("CLAUDE_CONFIG_DIR", Path.Combine(tmp.Root, "does-not-exist"));
        SeedSchema(dbPath);
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk git status", 100, 20, 80, 80.0, 500, "");

        using var console = new ConsoleCapture();
        GainCommand.Run([]);

        Assert.Equal(string.Empty, console.Error.ToString());
    }

    // -----------------------------------------------------------------------
    // --graph (ASCII graph — gain.rs:447-475)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_GraphFlag_RendersAsciiBarsScaledToMax()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using var claude = new EnvVarScope("CLAUDE_CONFIG_DIR", Path.Combine(tmp.Root, "does-not-exist"));
        SeedSchema(dbPath);

        // Day 1: saved 80 (half of max) -> bar_len = floor(80/160*40) = 20.
        // Day 2: saved 160 (max) -> bar_len = 40 (full width).
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk a", 100, 20, 80, 80.0, 500, "");
        InsertRow(dbPath, "2026-07-02T10:00:00.000000+00:00", "rtk b", 200, 40, 160, 80.0, 500, "");

        using var console = new ConsoleCapture();
        GainCommand.Run(["--graph"]);
        var output = console.Out.ToString();

        Assert.Contains("Daily Savings (last 30 days)\n", output);
        Assert.Contains("07-01 │" + new string('█', 20) + new string(' ', 20) + " 80\n", output);
        Assert.Contains("07-02 │" + new string('█', 40) + " 160\n", output);
    }

    // -----------------------------------------------------------------------
    // --history (Recent Commands — gain.rs:242-273)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_HistoryFlag_ShowsSignGlyphsAtThresholds()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using var claude = new EnvVarScope("CLAUDE_CONFIG_DIR", Path.Combine(tmp.Root, "does-not-exist"));
        SeedSchema(dbPath);

        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk high", 100, 10, 90, 90.0, 100, ""); // >=70 -> ▲
        InsertRow(dbPath, "2026-07-02T10:00:00.000000+00:00", "rtk mid", 100, 60, 40, 40.0, 100, "");  // >=30 -> ■
        InsertRow(dbPath, "2026-07-03T10:00:00.000000+00:00", "rtk low", 100, 90, 10, 10.0, 100, "");  // <30 -> •

        using var console = new ConsoleCapture();
        GainCommand.Run(["--history"]);
        var output = console.Out.ToString();

        Assert.Contains("Recent Commands\n", output);
        Assert.Contains("▲ rtk high", output);
        Assert.Contains("■ rtk mid", output);
        Assert.Contains("• rtk low", output);
    }

    [Fact]
    public void Run_HistoryFlag_LongCommandName_TruncatesAt22PlusEllipsis()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using var claude = new EnvVarScope("CLAUDE_CONFIG_DIR", Path.Combine(tmp.Root, "does-not-exist"));
        SeedSchema(dbPath);

        var longCmd = "rtk this-is-a-very-long-command-name-indeed";
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", longCmd, 100, 10, 90, 90.0, 100, "");

        using var console = new ConsoleCapture();
        GainCommand.Run(["--history"]);
        var output = console.Out.ToString();

        Assert.Contains(longCmd[..22] + "...", output);
        Assert.DoesNotContain(longCmd, output);
    }

    // -----------------------------------------------------------------------
    // --quota (tier table + fallback — gain.rs:275-299)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(null, "Max 20x ($200/mo)", 120_000_000)]
    [InlineData("pro", "Pro ($20/mo)", 6_000_000)]
    [InlineData("5x", "Max 5x ($100/mo)", 30_000_000)]
    [InlineData("20x", "Max 20x ($200/mo)", 120_000_000)]
    [InlineData("bogus", "Pro ($20/mo)", 6_000_000)] // invalid tier silently falls back to "pro".
    public void Run_QuotaFlag_TierSelection(string? tierArgValue, string expectedTierName, long expectedQuotaTokens)
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using var claude = new EnvVarScope("CLAUDE_CONFIG_DIR", Path.Combine(tmp.Root, "does-not-exist"));
        SeedSchema(dbPath);
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk a", 100, 20, 80, 80.0, 500, "");

        var args = tierArgValue is null ? new[] { "--quota" } : new[] { "--quota", "--tier", tierArgValue };

        using var console = new ConsoleCapture();
        var exitCode = GainCommand.Run(args);
        var output = console.Out.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("Monthly Quota Analysis\n", output);
        Assert.Contains($"Subscription tier: {expectedTierName}\n", output);
        Assert.Contains($"Estimated monthly quota: {RtkSharp.Core.Utils.FormatTokens(expectedQuotaTokens)}\n", output);
        Assert.Contains("Note: Heuristic estimate based on ~44K tokens/5h (Pro baseline)\n", output);
        Assert.Contains("      Actual limits use rolling 5-hour windows, not monthly caps.\n", output);
    }

    [Fact]
    public void Run_TierWithoutQuota_FailsWithRequiresError()
    {
        using var tmp = new TempDir();
        using var db = new DbPathGuard(Path.Combine(tmp.Root, "history.db"));
        using var console = new ConsoleCapture();

        var exitCode = GainCommand.Run(["--tier", "5x"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("--quota", console.Error.ToString());
        Assert.Equal(string.Empty, console.Out.ToString());
    }

    [Fact]
    public void Run_YesWithoutReset_FailsWithRequiresError()
    {
        using var tmp = new TempDir();
        using var db = new DbPathGuard(Path.Combine(tmp.Root, "history.db"));
        using var console = new ConsoleCapture();

        var exitCode = GainCommand.Run(["--yes"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("--reset", console.Error.ToString());
    }

    // -----------------------------------------------------------------------
    // --daily / --weekly / --monthly / --all dispatch (gain.rs:304-316)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_AllFlag_PrintsAllThreePeriodTables()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using var claude = new EnvVarScope("CLAUDE_CONFIG_DIR", Path.Combine(tmp.Root, "does-not-exist"));
        SeedSchema(dbPath);
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk a", 100, 20, 80, 80.0, 500, "");

        using var console = new ConsoleCapture();
        var exitCode = GainCommand.Run(["--all"]);
        var output = console.Out.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("Daily Breakdown", output);
        Assert.Contains("Weekly Breakdown", output);
        Assert.Contains("Monthly Breakdown", output);
    }

    // -----------------------------------------------------------------------
    // --format json (gain.rs:498-563)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_FormatJson_SummaryOnly_ExactShape()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        SeedSchema(dbPath);
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk a", 100, 20, 80, 80.0, 500, "");

        using var console = new ConsoleCapture();
        var exitCode = GainCommand.Run(["--format", "json"]);

        Assert.Equal(0, exitCode);
        var expected =
            "{\n" +
            "  \"summary\": {\n" +
            "    \"total_commands\": 1,\n" +
            "    \"total_input\": 100,\n" +
            "    \"total_output\": 20,\n" +
            "    \"total_saved\": 80,\n" +
            "    \"avg_savings_pct\": 80.0,\n" +
            "    \"total_time_ms\": 500,\n" +
            "    \"avg_time_ms\": 500\n" +
            "  }\n" +
            "}\n";
        Assert.Equal(expected, console.Out.ToString());
    }

    [Fact]
    public void Run_FormatJson_WithDaily_IncludesDailyArray_OmitsWeeklyMonthly()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        SeedSchema(dbPath);
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk a", 100, 20, 80, 80.0, 500, "");

        using var console = new ConsoleCapture();
        GainCommand.Run(["--format", "json", "--daily"]);
        var output = console.Out.ToString();

        Assert.Contains("\"daily\": [", output);
        Assert.DoesNotContain("\"weekly\"", output);
        Assert.DoesNotContain("\"monthly\"", output);
        Assert.Contains("\"date\": \"2026-07-01\"", output);
        Assert.Contains("\"input_tokens\": 100", output);
        Assert.Contains("\"savings_pct\": 80.0", output);
    }

    // -----------------------------------------------------------------------
    // --format csv (gain.rs:565-636)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_FormatCsv_All_ThreeSectionsWithBlankLinesBetween_NoTrailingBlankAfterLast()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        SeedSchema(dbPath);
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk a", 100, 20, 80, 80.0, 500, "");

        using var console = new ConsoleCapture();
        var exitCode = GainCommand.Run(["--format", "csv", "--all"]);
        var output = console.Out.ToString();

        Assert.Equal(0, exitCode);
        var expected =
            "# Daily Data\n" +
            "date,commands,input_tokens,output_tokens,saved_tokens,savings_pct,total_time_ms,avg_time_ms\n" +
            "2026-07-01,1,100,20,80,80.00,500,500\n" +
            "\n" +
            "# Weekly Data\n" +
            "week_start,week_end,commands,input_tokens,output_tokens,saved_tokens,savings_pct,total_time_ms,avg_time_ms\n" +
            "2026-06-29,2026-07-05,1,100,20,80,80.00,500,500\n" +
            "\n" +
            "# Monthly Data\n" +
            "month,commands,input_tokens,output_tokens,saved_tokens,savings_pct,total_time_ms,avg_time_ms\n" +
            "2026-07,1,100,20,80,80.00,500,500\n";
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Run_FormatCsv_MonthlyOnly_NoTrailingBlankLine()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        SeedSchema(dbPath);
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk a", 100, 20, 80, 80.0, 500, "");

        using var console = new ConsoleCapture();
        GainCommand.Run(["--format", "csv", "--monthly"]);
        var output = console.Out.ToString();

        Assert.False(output.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.EndsWith("2026-07,1,100,20,80,80.00,500,500\n", output);
    }

    // -----------------------------------------------------------------------
    // --failures (gain.rs:687-741)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_Failures_ZeroFailures_PrintsNoFailuresMessage()
    {
        using var tmp = new TempDir();
        using var db = new DbPathGuard(Path.Combine(tmp.Root, "history.db"));
        using var console = new ConsoleCapture();

        var exitCode = GainCommand.Run(["--failures"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "No parse failures recorded.\nThis means all commands parsed successfully (or fallback hasn't triggered yet).\n",
            console.Out.ToString());
    }

    [Fact]
    public void Run_Failures_WithData_PrintsKpisAndSections()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using (var tracker = new Tracker(dbPath))
        {
            tracker.RecordParseFailure("git -C /weird status", "unrecognized subcommand", true);
            tracker.RecordParseFailure("git -C /weird status", "unrecognized subcommand", true);
            tracker.RecordParseFailure("totally bogus cmd", "parse error", false);
        }

        using var console = new ConsoleCapture();
        var exitCode = GainCommand.Run(["--failures"]);
        var output = console.Out.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("RTK Parse Failures\n", output);
        Assert.Contains("Total failures:    3\n", output);
        Assert.Contains($"Recovery rate:     {(200.0 / 3.0).ToString("F1", CultureInfo.InvariantCulture)}%\n", output);
        Assert.Contains("Top Commands (by frequency)\n", output);
        Assert.Contains("2x  git -C /weird status", output);
        Assert.Contains("Recent Failures (last 10)\n", output);
        Assert.Contains("[ok]", output);
        Assert.Contains("[FAIL]", output);
    }

    [Fact]
    public void Run_Failures_LongCommands_TruncateAtDifferingThresholds()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        var longCmd = new string('x', 60);
        using (var tracker = new Tracker(dbPath))
        {
            for (var i = 0; i < 3; i++)
            {
                tracker.RecordParseFailure(longCmd, "err", false);
            }
        }

        using var console = new ConsoleCapture();
        GainCommand.Run(["--failures"]);
        var output = console.Out.ToString();

        // Top Commands: truncate at 47 chars + "..." when raw length > 50.
        Assert.Contains(longCmd[..47] + "...", output);
        // Recent Failures: truncate at 37 chars + "..." when raw length > 40 (a *different* row than
        // the Top Commands one, so both truncated forms must appear).
        Assert.Contains(longCmd[..37] + "...", output);
    }

    // -----------------------------------------------------------------------
    // --reset (gain.rs:34-44, 745-764)
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_Reset_NonInteractive_NoYes_DefaultsToNo_PrintsAborted()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using (var tracker = new Tracker(dbPath))
        {
            tracker.Record("git status", "rtk git status", 100, 20, 500);
        }

        using var console = new ConsoleCapture();
        var exitCode = GainCommand.Run(["--reset"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("Aborted.\n", console.Out.ToString());
        Assert.Contains("(non-interactive mode, defaulting to N)", console.Error.ToString());

        // Data must NOT have been cleared.
        using var verifyTracker = new Tracker(dbPath);
        Assert.Equal(1, verifyTracker.GetSummary().TotalCommands);
    }

    [Fact]
    public void Run_Reset_WithYes_SkipsPromptAndClearsData()
    {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Root, "history.db");
        using var db = new DbPathGuard(dbPath);
        using (var tracker = new Tracker(dbPath))
        {
            tracker.Record("git status", "rtk git status", 100, 20, 500);
        }

        using var console = new ConsoleCapture();
        var exitCode = GainCommand.Run(["--reset", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Token savings stats reset to zero.", console.Out.ToString());

        using var verifyTracker = new Tracker(dbPath);
        Assert.Equal(0, verifyTracker.GetSummary().TotalCommands);
    }

    // -----------------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------------

    private static void SeedSchema(string dbPath)
    {
        using var tracker = new Tracker(dbPath);
    }

    private static void InsertRow(
        string dbPath,
        string timestamp,
        string rtkCmd,
        int inputTokens,
        int outputTokens,
        int savedTokens,
        double savingsPct,
        long execTimeMs,
        string projectPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO commands (timestamp, original_cmd, rtk_cmd, project_path, input_tokens, output_tokens, saved_tokens, savings_pct, exec_time_ms)
            VALUES ($ts, $orig, $rtk, $pp, $it, $ot, $st, $pct, $et)
            """;
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$orig", "orig");
        cmd.Parameters.AddWithValue("$rtk", rtkCmd);
        cmd.Parameters.AddWithValue("$pp", projectPath);
        cmd.Parameters.AddWithValue("$it", inputTokens);
        cmd.Parameters.AddWithValue("$ot", outputTokens);
        cmd.Parameters.AddWithValue("$st", savedTokens);
        cmd.Parameters.AddWithValue("$pct", savingsPct);
        cmd.Parameters.AddWithValue("$et", execTimeMs);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Redirects <c>RTK_DB_PATH</c> for the instance's lifetime, restoring the previous value on disposal.</summary>
    private sealed class DbPathGuard : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);

        public DbPathGuard(string dbPath) => Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, dbPath);

        public void Dispose() => Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, _previous);
    }

    /// <summary>Redirects a single named environment variable for the instance's lifetime.</summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
