using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using RtkSharp.Core.Tracking;
using Xunit;

namespace RtkSharp.Tests.Core.Tracking;

/// <summary>
/// Port of the schema/migration/path-resolution subset of Rust's <c>src/core/tracking.rs</c> test
/// module (<c>tracking.rs:1422-1689</c>) covered by Phase 5 Task 1 (constructor, schema, migrations,
/// <c>estimate_tokens</c>). Record/query method tests (<c>record</c>, <c>get_summary</c>, etc.) are
/// deferred to Task 2's own test additions.
/// </summary>
/// <remarks>
/// All tests that touch process-global environment variables (<c>RTK_DB_PATH</c>,
/// <c>RTK_DATA_DIR_OVERRIDE</c>, <c>RTK_CONFIG_DIR_OVERRIDE</c>) serialize via <see cref="EnvLock"/>,
/// mirroring Rust's own <c>ENV_LOCK</c> mutex in <c>test_db_path_env_and_default</c>
/// (<c>tracking.rs:1553-1555</c>) and RtkSharp's established <c>InitTestSupport.EnterEnvLock</c>/
/// <c>ExitEnvLock</c> pattern (this class uses its own separate lock object rather than that one,
/// since it serializes a disjoint set of environment variables).
/// </remarks>
public sealed class TrackerTests
{
    private static readonly object EnvLock = new();

    // -----------------------------------------------------------------------
    // estimate_tokens
    // -----------------------------------------------------------------------

    [Fact]
    public void EstimateTokens_Ascii_MatchesRustDoctestExamples()
    {
        // Mirrors the Rust doctest at tracking.rs:1276-1283 exactly.
        Assert.Equal(0, Tracker.EstimateTokens(""));
        Assert.Equal(1, Tracker.EstimateTokens("abcd")); // 4 bytes = 1 token
        Assert.Equal(2, Tracker.EstimateTokens("abcde")); // 5 bytes = ceil(1.25) = 2
        Assert.Equal(3, Tracker.EstimateTokens("hello world")); // 11 bytes = ceil(2.75) = 3
    }

    [Fact]
    public void EstimateTokens_Ascii_MatchesRustUnitTestExamples()
    {
        // Mirrors tracking.rs:1427-1434 (test_estimate_tokens).
        Assert.Equal(0, Tracker.EstimateTokens(""));
        Assert.Equal(1, Tracker.EstimateTokens("abcd"));
        Assert.Equal(2, Tracker.EstimateTokens("abcde"));
        Assert.Equal(1, Tracker.EstimateTokens("a"));
        Assert.Equal(2, Tracker.EstimateTokens("12345678"));
    }

    [Fact]
    public void EstimateTokens_MultiByteUtf8_CountsUtf8BytesNotUtf16UnitsOrRunes()
    {
        // A single emoji is 4 bytes in UTF-8, 2 UTF-16 code units in .NET's `string.Length`, and 1
        // Unicode scalar value ("rune"). Rust's `str::len()` (which `estimate_tokens` uses) counts
        // UTF-8 bytes, so this must estimate ceil(4/4) = 1, NOT ceil(2/4) = 1 coincidentally matching
        // via .Length, NOR ceil(1/4) = 1 coincidentally matching via a rune count either — use a
        // repeated sequence to disambiguate all three candidate semantics from each other.
        const string threeEmoji = "\U0001F600\U0001F600\U0001F600"; // U+1F600 GRINNING FACE
        Assert.Equal(4 * 3, Encoding.UTF8.GetByteCount(threeEmoji)); // sanity: 12 UTF-8 bytes
        Assert.Equal(2 * 3, threeEmoji.Length); // sanity: 6 UTF-16 code units (surrogate pairs)
        Assert.Equal(3, ExactRuneCount(threeEmoji)); // sanity: 3 Unicode scalar values

        // Byte-count semantics: ceil(12 / 4.0) = 3.
        Assert.Equal(3, Tracker.EstimateTokens(threeEmoji));

        // A CJK character is 3 bytes in UTF-8 but 1 UTF-16 code unit AND 1 rune, so byte-count and
        // the other two semantics diverge here too.
        const string fourCjk = "日本語版"; // 4 characters, 3 bytes each = 12 UTF-8 bytes
        Assert.Equal(12, Encoding.UTF8.GetByteCount(fourCjk));
        Assert.Equal(4, fourCjk.Length);
        Assert.Equal(3, Tracker.EstimateTokens(fourCjk)); // ceil(12/4.0) = 3, not ceil(4/4.0) = 1
    }

    private static int ExactRuneCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    // -----------------------------------------------------------------------
    // Path resolution priority
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveDbPath_EnvVarWins_OverConfigAndDefault()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            var customPath = Path.Combine(Path.GetTempPath(), "rtksharp_tracker_test_" + Guid.NewGuid().ToString("N") + ".db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, customPath);

            var resolved = Tracker.ResolveDbPath();

            Assert.Equal(customPath, resolved);
        }
    }

    [Fact]
    public void ResolveDbPath_FallsBackToDefault_WhenNoEnvVarOrConfig()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            using var configDir = new TempDir();
            Environment.SetEnvironmentVariable("RTK_CONFIG_DIR_OVERRIDE", configDir.Root); // no config.toml present -> defaults

            var resolved = Tracker.ResolveDbPath();

            Assert.EndsWith(Path.Combine("rtk", "history.db"), resolved);
        }
    }

    [Fact]
    public void ResolveDbPath_ConfigDatabasePath_WinsOverDefault_WhenNoEnvVar()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            using var configDir = new TempDir();
            Environment.SetEnvironmentVariable("RTK_CONFIG_DIR_OVERRIDE", configDir.Root);

            var configuredDbPath = Path.Combine(Path.GetTempPath(), "rtksharp_configured_" + Guid.NewGuid().ToString("N") + ".db").Replace('\\', '/');
            var configTomlPath = Path.Combine(configDir.Root, "rtk", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(configTomlPath)!);
            File.WriteAllText(
                configTomlPath,
                $"""
                [tracking]
                enabled = true
                history_days = 90
                database_path = "{configuredDbPath}"
                """);

            var resolved = Tracker.ResolveDbPath();

            Assert.Equal(configuredDbPath, resolved.Replace('\\', '/'));
        }
    }

    [Fact]
    public void ResolveDbPath_DataDirOverride_HonoredOnlyForDefaultBranch_NotWhenDbPathSet()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            using var overrideDir = new TempDir();
            Environment.SetEnvironmentVariable("RTK_DATA_DIR_OVERRIDE", overrideDir.Root);

            // RTK_DB_PATH set: RTK_DATA_DIR_OVERRIDE must be ignored entirely.
            var explicitPath = Path.Combine(Path.GetTempPath(), "rtksharp_explicit_" + Guid.NewGuid().ToString("N") + ".db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, explicitPath);

            var resolvedWithDbPath = Tracker.ResolveDbPath();
            Assert.Equal(explicitPath, resolvedWithDbPath);
            Assert.DoesNotContain(overrideDir.Root, resolvedWithDbPath);

            // RTK_DB_PATH unset, no config: RTK_DATA_DIR_OVERRIDE must be honored for the default branch.
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, null);
            using var configDir = new TempDir();
            Environment.SetEnvironmentVariable("RTK_CONFIG_DIR_OVERRIDE", configDir.Root);

            var resolvedDefault = Tracker.ResolveDbPath();
            Assert.Equal(Path.Combine(overrideDir.Root, "rtk", "history.db"), resolvedDefault);
        }
    }

    // -----------------------------------------------------------------------
    // Schema creation + migration idempotency
    // -----------------------------------------------------------------------

    [Fact]
    public void Construction_CreatesSchema_QueryableImmediately()
    {
        using var temp = new TempDir();
        var dbPath = Path.Combine(temp.Root, "history.db");

        using var tracker = new Tracker(dbPath);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        AssertTableExists(connection, "commands");
        AssertTableExists(connection, "parse_failures");
        AssertColumnExists(connection, "commands", "exec_time_ms");
        AssertColumnExists(connection, "commands", "project_path");
    }

    [Fact]
    public void Construction_Twice_AgainstSameFile_IsIdempotent_NoErrors()
    {
        using var temp = new TempDir();
        var dbPath = Path.Combine(temp.Root, "history.db");

        using (var first = new Tracker(dbPath))
        {
            // no-op, just exercise construction
        }

        // Second construction re-runs every migration (ALTER TABLE ADD COLUMN, NULL-normalization
        // EXISTS check) against the same file; must not throw.
        var exception = Record.Exception(() =>
        {
            using var second = new Tracker(dbPath);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Construction_Repeated_ThreeTimes_StillIdempotent_AndTablesUsable()
    {
        using var temp = new TempDir();
        var dbPath = Path.Combine(temp.Root, "history.db");

        for (var i = 0; i < 3; i++)
        {
            using var tracker = new Tracker(dbPath);
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO commands (timestamp, original_cmd, rtk_cmd, input_tokens, output_tokens, saved_tokens, savings_pct, exec_time_ms, project_path) VALUES ('2026-01-01T00:00:00Z', 'ls', 'rtk ls', 100, 20, 80, 80.0, 5, '')";
        var affected = cmd.ExecuteNonQuery();
        Assert.Equal(1, affected);
    }

    private static void AssertTableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", tableName);
        var result = cmd.ExecuteScalar();
        Assert.NotNull(result);
    }

    private static void AssertColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        var found = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        Assert.True(found, $"Expected column '{columnName}' on table '{tableName}'.");
    }

    /// <summary>Saves and restores every env var this suite touches, resetting them after each test.</summary>
    private sealed class EnvVarScope : IDisposable
    {
        private static readonly string[] Vars = ["RTK_DB_PATH", "RTK_DATA_DIR_OVERRIDE", "RTK_CONFIG_DIR_OVERRIDE"];
        private readonly string?[] _previous;

        public EnvVarScope()
        {
            _previous = new string?[Vars.Length];
            for (var i = 0; i < Vars.Length; i++)
            {
                _previous[i] = Environment.GetEnvironmentVariable(Vars[i]);
                Environment.SetEnvironmentVariable(Vars[i], null);
            }
        }

        public void Dispose()
        {
            for (var i = 0; i < Vars.Length; i++)
            {
                Environment.SetEnvironmentVariable(Vars[i], _previous[i]);
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rtksharp-tracker-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    // =========================================================================
    // Task 2: record / query methods
    // =========================================================================

    private static Tracker NewTracker(TempDir dir) => new(Path.Combine(dir.Root, "history.db"));

    /// <summary>Inserts a row directly via raw SQL, bypassing <see cref="Tracker.Record"/>, so tests can
    /// control <c>timestamp</c>/<c>project_path</c> precisely (Rust's own tests rely on wall-clock
    /// <c>Utc::now()</c> for record() and instead seed via a real DB file for aggregate-math tests;
    /// here we seed directly for determinism).</summary>
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

    // -----------------------------------------------------------------------
    // record() — saturating_sub + pct math (tracking.rs:402-437, tests at 1449-1513)
    // -----------------------------------------------------------------------

    [Fact]
    public void Record_ComputesSavedTokensAndSavingsPct()
    {
        using var dir = new TempDir();
        using var tracker = NewTracker(dir);
        var testCmd = "rtk git status test_" + Guid.NewGuid().ToString("N");

        tracker.Record("git status", testCmd, 100, 20, 50);

        var recent = tracker.GetRecent(10);
        var record = Assert.Single(recent, r => r.RtkCmd == testCmd);
        Assert.Equal(80, record.SavedTokens);
        Assert.Equal(80.0, record.SavingsPct);
    }

    [Fact]
    public void Record_ZeroInputTokens_ZeroPct_NoDivideByZero()
    {
        // Mirrors tracking.rs:1471-1513 (test_track_passthrough_no_dilution): 0 input / 0 output
        // must not divide by zero and must record 0% savings, not diluting other stats.
        using var dir = new TempDir();
        using var tracker = NewTracker(dir);
        var testCmd = "rtk passthrough test_" + Guid.NewGuid().ToString("N");

        tracker.Record("cmd", testCmd, 0, 0, 5);

        var recent = tracker.GetRecent(10);
        var record = Assert.Single(recent, r => r.RtkCmd == testCmd);
        Assert.Equal(0, record.SavedTokens);
        Assert.Equal(0.0, record.SavingsPct);
    }

    [Fact]
    public void Record_OutputExceedsInput_SaturatesToZero_NotNegative()
    {
        // Rust's saturating_sub: an (erroneous) output > input measurement must clamp saved to 0,
        // never go negative.
        using var dir = new TempDir();
        using var tracker = NewTracker(dir);
        var testCmd = "rtk weird test_" + Guid.NewGuid().ToString("N");

        tracker.Record("cmd", testCmd, 10, 50, 1);

        var recent = tracker.GetRecent(10);
        var record = Assert.Single(recent, r => r.RtkCmd == testCmd);
        Assert.Equal(0, record.SavedTokens);
    }

    // -----------------------------------------------------------------------
    // reset_all (tracking.rs:452-463, test at 1650-1688)
    // -----------------------------------------------------------------------

    [Fact]
    public void ResetAll_ClearsBothCommandsAndParseFailuresTables()
    {
        using var dir = new TempDir();
        using var tracker = NewTracker(dir);

        tracker.Record("git status", "rtk git status reset_test", 100, 20, 50);
        tracker.RecordParseFailure("bad_cmd_reset_test", "parse error", false);

        tracker.ResetAll();

        var summary = tracker.GetSummary();
        Assert.Equal(0, summary.TotalCommands);

        var failures = tracker.GetParseFailureSummary();
        Assert.Equal(0, failures.Total);
    }

    // -----------------------------------------------------------------------
    // record_parse_failure / get_parse_failure_summary (tracking.rs:465-542, tests at 1609-1648)
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseFailure_RecordAndSummary_RoundTrips()
    {
        using var dir = new TempDir();
        using var tracker = NewTracker(dir);
        var testCmd = "git -C /path status test_" + Guid.NewGuid().ToString("N");

        tracker.RecordParseFailure(testCmd, "unrecognized subcommand", true);

        var summary = tracker.GetParseFailureSummary();
        Assert.True(summary.Total >= 1);
        Assert.Contains(summary.Recent, r => r.RawCommand == testCmd);
    }

    [Fact]
    public void ParseFailure_RecoveryRate_ComputedFromSucceededOverTotal()
    {
        using var dir = new TempDir();
        using var tracker = NewTracker(dir);

        // 2 successes, 1 failure -> recovery rate is exactly 2/3 * 100 on a fresh DB.
        tracker.RecordParseFailure("cmd_ok1", "err", true);
        tracker.RecordParseFailure("cmd_ok2", "err", true);
        tracker.RecordParseFailure("cmd_fail", "err", false);

        var summary = tracker.GetParseFailureSummary();
        Assert.Equal(3, summary.Total);
        Assert.Equal(200.0 / 3.0, summary.RecoveryRate, precision: 10);
    }

    // -----------------------------------------------------------------------
    // get_summary_filtered aggregate math (tracking.rs:572-631)
    // -----------------------------------------------------------------------

    [Fact]
    public void GetSummary_AggregatesAcrossRows_AvgPctIsTotalSavedOverTotalInput_NotMeanOfPerRowPct()
    {
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        using (var seed = NewTracker(dir))
        {
            // no-op, just create schema
        }

        // Row 1: 100 in / 20 out -> saved 80, pct 80%.
        // Row 2: 50 in / 40 out -> saved 10, pct 20%.
        // Naive mean-of-per-row-pct would be (80+20)/2 = 50%.
        // Correct (Rust) semantics: total_saved / total_input = 90/150 = 60%.
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk cmd1", 100, 20, 80, 80.0, 10, "");
        InsertRow(dbPath, "2026-07-01T11:00:00.000000+00:00", "rtk cmd2", 50, 40, 10, 20.0, 30, "");

        using var tracker = new Tracker(dbPath);
        var summary = tracker.GetSummary();

        Assert.Equal(2, summary.TotalCommands);
        Assert.Equal(150, summary.TotalInput);
        Assert.Equal(60, summary.TotalOutput);
        Assert.Equal(90, summary.TotalSaved);
        Assert.Equal(60.0, summary.AvgSavingsPct, precision: 10);
        Assert.Equal(40, summary.TotalTimeMs);
        Assert.Equal(20, summary.AvgTimeMs); // 40 / 2 commands, integer division
    }

    [Fact]
    public void GetSummary_ByCommand_TopByTokensSaved_Descending()
    {
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        using (var seed = NewTracker(dir))
        {
        }

        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk small", 100, 90, 10, 10.0, 5, "");
        InsertRow(dbPath, "2026-07-01T11:00:00.000000+00:00", "rtk big", 100, 10, 90, 90.0, 5, "");

        using var tracker = new Tracker(dbPath);
        var summary = tracker.GetSummary();

        Assert.Equal("rtk big", summary.ByCommand[0].Command);
        Assert.Equal(90, summary.ByCommand[0].SavedTokens);
        Assert.Equal("rtk small", summary.ByCommand[1].Command);
    }

    // -----------------------------------------------------------------------
    // Week-boundary arithmetic (tracking.rs:781-831) — verified empirically against SQLite itself,
    // not hand-computed, per the phase plan's explicit caution about 'weekday 0' semantics.
    // -----------------------------------------------------------------------

    [Fact]
    public void GetByWeekFiltered_KnownWednesday_ProducesExactSqliteWeekBoundaries()
    {
        // 2026-07-01 is a Wednesday (confirmed via `sqlite3 :memory: "SELECT strftime('%w','2026-07-01')"`
        // -> 3). SQLite's own 'weekday 0'/'weekday 0','-6 days' modifiers against this timestamp
        // produce week_start=2026-06-29 (Monday) and week_end=2026-07-05 (Sunday) — confirmed by
        // direct sqlite3 CLI query, not hand-derived, per the phase plan's caution against assuming
        // ISO-week semantics.
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        using (var seed = NewTracker(dir))
        {
        }

        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk wed cmd", 100, 20, 80, 80.0, 10, "");

        using var tracker = new Tracker(dbPath);
        var weeks = tracker.GetByWeek();

        var week = Assert.Single(weeks);
        Assert.Equal("2026-06-29", week.WeekStart);
        Assert.Equal("2026-07-05", week.WeekEnd);
        Assert.Equal(1, week.Commands);
        Assert.Equal(80.0, week.SavingsPct, precision: 10);
    }

    [Fact]
    public void GetByWeekFiltered_SundayTimestamp_WeekEndIsSameDay()
    {
        // 2026-07-05 is itself a Sunday (confirmed via sqlite3 strftime('%w', ...) -> 0). 'weekday 0'
        // on an already-Sunday date resolves to that same date, per the phase plan's explicit note
        // ("or today if already Sunday").
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        using (var seed = NewTracker(dir))
        {
        }

        InsertRow(dbPath, "2026-07-05T10:00:00.000000+00:00", "rtk sun cmd", 100, 20, 80, 80.0, 10, "");

        using var tracker = new Tracker(dbPath);
        var weeks = tracker.GetByWeek();

        var week = Assert.Single(weeks);
        Assert.Equal("2026-06-29", week.WeekStart);
        Assert.Equal("2026-07-05", week.WeekEnd);
    }

    [Fact]
    public void GetAllDaysFiltered_And_GetByMonthFiltered_OrderedChronologically()
    {
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        using (var seed = NewTracker(dir))
        {
        }

        InsertRow(dbPath, "2026-06-15T10:00:00.000000+00:00", "rtk a", 10, 5, 5, 50.0, 1, "");
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk b", 10, 5, 5, 50.0, 1, "");
        InsertRow(dbPath, "2026-06-20T10:00:00.000000+00:00", "rtk c", 10, 5, 5, 50.0, 1, "");

        using var tracker = new Tracker(dbPath);
        var days = tracker.GetAllDays();
        Assert.Equal(["2026-06-15", "2026-06-20", "2026-07-01"], days.Select(d => d.Date).ToArray());

        var months = tracker.GetByMonth();
        Assert.Equal(["2026-06", "2026-07"], months.Select(m => m.Month).ToArray());
        Assert.Equal(2, months[0].Commands); // June: 2 rows
        Assert.Equal(1, months[1].Commands); // July: 1 row
    }

    // -----------------------------------------------------------------------
    // get_recent_filtered / project scoping (tracking.rs:933-962)
    // -----------------------------------------------------------------------

    [Fact]
    public void GetRecentFiltered_ExactProjectMatch_IncludesOnlyThatProject()
    {
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        using (var seed = NewTracker(dir))
        {
        }

        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk in project", 10, 5, 5, 50.0, 1, "/home/user/project");
        InsertRow(dbPath, "2026-07-01T11:00:00.000000+00:00", "rtk elsewhere", 10, 5, 5, 50.0, 1, "/home/user/other");

        using var tracker = new Tracker(dbPath);
        var recent = tracker.GetRecentFiltered(10, "/home/user/project");

        var record = Assert.Single(recent);
        Assert.Equal("rtk in project", record.RtkCmd);
    }

    [Fact]
    public void GetRecentFiltered_Subdirectory_MatchesViaGlobPrefix()
    {
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        using (var seed = NewTracker(dir))
        {
        }

        var sep = Path.DirectorySeparatorChar;
        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk subdir", 10, 5, 5, 50.0, 1, $"/home/user/project{sep}sub");
        InsertRow(dbPath, "2026-07-01T11:00:00.000000+00:00", "rtk sibling", 10, 5, 5, 50.0, 1, "/home/user/project-sibling");

        using var tracker = new Tracker(dbPath);
        var recent = tracker.GetRecentFiltered(10, "/home/user/project");

        // Only the true subdirectory (separator-prefixed) must match — a same-prefix sibling
        // directory name ("project-sibling") must NOT match, proving the GLOB pattern anchors on
        // the path separator rather than doing a naive string-prefix match.
        var record = Assert.Single(recent);
        Assert.Equal("rtk subdir", record.RtkCmd);
    }

    [Fact]
    public void GetRecentFiltered_NullProjectPath_IncludesAllProjects()
    {
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        using (var seed = NewTracker(dir))
        {
        }

        InsertRow(dbPath, "2026-07-01T10:00:00.000000+00:00", "rtk one", 10, 5, 5, 50.0, 1, "/home/user/project");
        InsertRow(dbPath, "2026-07-01T11:00:00.000000+00:00", "rtk two", 10, 5, 5, 50.0, 1, "/home/user/other");

        using var tracker = new Tracker(dbPath);
        var recent = tracker.GetRecentFiltered(10, null);

        Assert.Equal(2, recent.Count);
    }

    // -----------------------------------------------------------------------
    // project_filter_params — GLOB-safety (tracking.rs:1571-1607, ported verbatim via reflection
    // since the method is private in both languages)
    // -----------------------------------------------------------------------

    private static (string? Exact, string? Glob) InvokeProjectFilterParams(string? projectPath)
    {
        var method = typeof(Tracker).GetMethod("ProjectFilterParams", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ProjectFilterParams method not found via reflection.");
        var result = method.Invoke(null, [projectPath]);
        var tupleType = result!.GetType();
        var exact = (string?)tupleType.GetField("Item1")!.GetValue(result);
        var glob = (string?)tupleType.GetField("Item2")!.GetValue(result);
        return (exact, glob);
    }

    [Fact]
    public void ProjectFilterParams_UsesGlobPatternWithStarWildcard()
    {
        // Mirrors tracking.rs:1572-1584 (test_project_filter_params_glob_pattern).
        var (exact, glob) = InvokeProjectFilterParams("/home/user/project");

        Assert.Equal("/home/user/project", exact);
        Assert.NotNull(glob);
        Assert.EndsWith("*", glob);
        Assert.DoesNotContain('%', glob);
        Assert.Equal($"/home/user/project{Path.DirectorySeparatorChar}*", glob);
    }

    [Fact]
    public void ProjectFilterParams_NoneInput_ReturnsNoneForBoth()
    {
        // Mirrors tracking.rs:1587-1592 (test_project_filter_params_none).
        var (exact, glob) = InvokeProjectFilterParams(null);

        Assert.Null(exact);
        Assert.Null(glob);
    }

    [Fact]
    public void ProjectFilterParams_UnderscoreInPath_PreservedLiterally_NotTreatedAsGlobWildcard()
    {
        // Mirrors tracking.rs:1595-1607 (test_project_filter_params_underscore_safe). Unlike LIKE,
        // where '_' matches any single character, GLOB treats '_' as a literal character — so a real
        // path segment like "my_project" must survive unescaped in the pattern.
        var (exact, glob) = InvokeProjectFilterParams("/home/user/my_project");

        Assert.Equal("/home/user/my_project", exact);
        Assert.Contains("my_project", glob);
        Assert.Equal($"/home/user/my_project{Path.DirectorySeparatorChar}*", glob);
    }

    [Fact]
    public void ProjectFilterParams_AsteriskAndQuestionMarkInPath_StillProduceValidPattern()
    {
        // Not present verbatim in the Rust test module but a natural extension of the GLOB-safety
        // concern: a path segment that happens to contain GLOB metacharacters itself would still be
        // appended as literal text by this function (no escaping) — documenting this as a known,
        // narrow edge case (matches Rust: project_filter_params does not escape metacharacters in the
        // input path itself, only guarantees the *pattern it appends* uses GLOB-safe syntax).
        var (exact, glob) = InvokeProjectFilterParams("/home/user/weird*project?");

        Assert.Equal("/home/user/weird*project?", exact);
        Assert.Equal($"/home/user/weird*project?{Path.DirectorySeparatorChar}*", glob);
    }

    // -----------------------------------------------------------------------
    // estimate_tokens already covered in Task 1's suite above.
    // -----------------------------------------------------------------------
}
