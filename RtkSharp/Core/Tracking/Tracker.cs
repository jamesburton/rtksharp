using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using RtkSharp.Hooks;

namespace RtkSharp.Core.Tracking;

/// <summary>
/// Main tracking interface for recording and querying command history. Faithful port of Rust
/// <c>Tracker</c> (<c>src/core/tracking.rs:91-93</c>). This task (Phase 5 Task 1) ports the
/// constructor's database-path resolution, schema creation, and idempotent migrations only —
/// <c>record</c>/<c>get_summary</c>/etc. are ported in Task 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Database location.</b> Resolved by <see cref="ResolveDbPath"/> with the same three-tier
/// priority as Rust's <c>get_db_path()</c> (<c>tracking.rs:1220-1236</c>): the <c>RTK_DB_PATH</c>
/// environment variable (verbatim, highest priority) → <c>Config.LoadOrDefault().Tracking.DatabasePath</c>
/// → a platform-specific default, <c>{data_dir}/rtk/history.db</c>. The default branch reuses
/// <see cref="TrustCommand.ResolveDataDir"/> directly (rather than re-implementing the same
/// Windows/macOS/Linux <c>dirs::data_local_dir()</c>-equivalent switch a second time) so it honors
/// the exact same <see cref="TrustCommand.DataDirOverrideEnvVar"/> (<c>RTK_DATA_DIR_OVERRIDE</c>)
/// test/parity escape hatch Phase 4 established — this is a distinct, lower-priority override from
/// <c>RTK_DB_PATH</c>: the latter replaces the whole resolved path verbatim regardless of platform,
/// while <c>RTK_DATA_DIR_OVERRIDE</c> only ever affects the <em>default</em> branch's data-directory
/// component, and is never consulted at all once <c>RTK_DB_PATH</c> or a configured
/// <c>database_path</c> is present.
/// </para>
/// <para>
/// <b>Db filename is <c>history.db</c>, not <c>tracking.db</c>.</b> A stale Rust doc-comment
/// (<c>tracking.rs:9</c>) claims <c>~/.local/share/rtk/tracking.db</c>, but the actual
/// <c>HISTORY_DB</c> constant — and Rust's own <c>test_db_path_env_and_default</c>
/// (<c>tracking.rs:1565</c>), which asserts the default path ends with <c>rtk/history.db</c> — say
/// otherwise. This port trusts the code and its test, not the comment.
/// </para>
/// <para>
/// <b>Non-fatal pragmas.</b> <c>PRAGMA journal_mode=WAL;</c>/<c>PRAGMA busy_timeout=5000;</c> are each
/// executed with all errors swallowed, matching Rust's <c>let _ = conn.execute_batch(...)</c>
/// (<c>tracking.rs:258-261</c>) — some filesystems (e.g. NFS mounts) don't support WAL, and that must
/// never block tracking from working at all.
/// </para>
/// <para>
/// <b>Idempotent migrations.</b> The two <c>ALTER TABLE ... ADD COLUMN</c> statements
/// (<c>exec_time_ms</c>, <c>project_path</c>) and the one-time <c>project_path</c> NULL-normalization
/// pass are safe to run on every construction: SQLite raises a "duplicate column name" error on a
/// repeat <c>ALTER TABLE ADD COLUMN</c>, which this port swallows exactly like Rust's
/// <c>let _ = conn.execute(...)</c> (<c>tracking.rs:282-290</c>); the NULL-normalization guard first
/// checks <c>SELECT EXISTS(...)</c> before running the <c>UPDATE</c>, so a second construction against
/// an already-normalized database is a cheap no-op read, not a redundant write.
/// </para>
/// </remarks>
public sealed class Tracker : IDisposable
{
    /// <summary>
    /// Highest-priority database-path override, consulted verbatim (no further resolution — even a
    /// relative path is used as-is). Port of Rust's <c>RTK_DB_PATH</c> check
    /// (<c>tracking.rs:1222-1224</c>). Distinct from <see cref="TrustCommand.DataDirOverrideEnvVar"/>:
    /// this one replaces the whole path, the other only redirects the default branch's data directory.
    /// </summary>
    internal const string DbPathEnvVar = "RTK_DB_PATH";

    private readonly SqliteConnection _connection;

    /// <summary>
    /// Opens or creates the tracking database at the resolved platform-specific location (see
    /// <see cref="ResolveDbPath"/>), creating parent directories as needed, and ensures the schema
    /// (tables, indexes, migrations) is present and up to date.
    /// </summary>
    /// <exception cref="IOException">Parent directories could not be created.</exception>
    /// <exception cref="SqliteException">The database could not be opened, or schema creation failed.</exception>
    public Tracker()
        : this(ResolveDbPath())
    {
    }

    /// <summary>
    /// Opens or creates the tracking database at an explicit path, bypassing <see cref="ResolveDbPath"/>
    /// entirely. Exists so tests can target a throwaway file directly without mutating process-global
    /// environment variables for every case (path-resolution priority itself is still covered via
    /// <see cref="ResolveDbPath"/> unit tests). Rust has no equivalent constructor overload — its tests
    /// instead set <c>RTK_DB_PATH</c> before calling the parameterless <c>Tracker::new()</c> — but an
    /// explicit-path constructor is a strictly more direct, less environment-mutating way to achieve
    /// the same schema/migration test coverage in C#, so it does not introduce any behavior Rust
    /// lacks; <see cref="ResolveDbPath"/>'s own priority order is still exercised independently.
    /// </summary>
    /// <param name="dbPath">The SQLite database file path to open or create.</param>
    internal Tracker(string dbPath)
    {
        var parent = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        // Non-fatal: NFS/read-only filesystems may not support WAL. Matches Rust's `let _ = ...`.
        TryExecute("PRAGMA journal_mode=WAL;");
        TryExecute("PRAGMA busy_timeout=5000;");

        InitSchema();
    }

    /// <summary>
    /// Estimate token count from text using the ~4-bytes-per-token heuristic. Faithful port of Rust
    /// <c>estimate_tokens</c> (<c>tracking.rs:1284-1287</c>): <c>ceil(text.len() / 4.0)</c>.
    /// </summary>
    /// <remarks>
    /// <b>Byte length, not char count.</b> Rust's <c>str::len()</c> returns the string's UTF-8
    /// <em>byte</em> length, not its Unicode scalar value (char) count — despite the misleading
    /// <c>// ~4 chars per token</c> comment directly above it in the Rust source. This port therefore
    /// counts UTF-8 bytes via <see cref="Encoding.UTF8.GetByteCount(string)"/>, not
    /// <c>text.Length</c> (.NET UTF-16 code units — wrong on two counts: wrong unit, and wrong
    /// encoding) and not a Unicode-scalar-value ("rune") count via <c>System.Globalization.StringInfo</c>
    /// or <c>Rune</c> enumeration either. For pure-ASCII text all three approaches agree (1 byte = 1
    /// UTF-16 unit = 1 rune), but they diverge for any multi-byte UTF-8 input (e.g. a 4-byte-UTF-8/
    /// 2-UTF-16-unit/1-rune emoji counts as 4 by this method, 2 by <c>text.Length</c>, and 1 by a rune
    /// count) — verified against a byte-for-byte reading of the actual Rust source, not assumed from a
    /// paraphrased description of it.
    /// </remarks>
    /// <param name="text">The text to estimate a token count for.</param>
    /// <returns>The estimated token count.</returns>
    public static int EstimateTokens(string text)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        return (int)Math.Ceiling(byteCount / 4.0);
    }

    /// <summary>
    /// Resolves the tracking database path with the same three-tier priority as Rust's
    /// <c>get_db_path()</c> (<c>tracking.rs:1220-1236</c>): <c>RTK_DB_PATH</c> env var (verbatim) →
    /// <c>Config.LoadOrDefault().Tracking.DatabasePath</c> → <c>{data_dir}/rtk/history.db</c>.
    /// </summary>
    /// <returns>The resolved database file path.</returns>
    internal static string ResolveDbPath()
    {
        var envPath = Environment.GetEnvironmentVariable(DbPathEnvVar);
        if (!string.IsNullOrEmpty(envPath))
        {
            return envPath;
        }

        var config = Config.LoadOrDefault();
        if (!string.IsNullOrEmpty(config.Tracking.DatabasePath))
        {
            return config.Tracking.DatabasePath;
        }

        // Default branch only: reuses TrustCommand's data-dir resolution (and its
        // RTK_DATA_DIR_OVERRIDE test/parity escape hatch) rather than re-implementing the same
        // platform switch a second time.
        return Path.Combine(TrustCommand.ResolveDataDir(), TrackingConstants.RtkDataDir, TrackingConstants.HistoryDb);
    }

    /// <summary>
    /// Creates the <c>commands</c>/<c>parse_failures</c> tables and their indexes if absent, then runs
    /// the idempotent column migrations and one-time NULL-normalization pass. Faithful port of the
    /// schema portion of Rust's <c>Tracker::new()</c> (<c>tracking.rs:262-324</c>).
    /// </summary>
    private void InitSchema()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS commands (
                id INTEGER PRIMARY KEY,
                timestamp TEXT NOT NULL,
                original_cmd TEXT NOT NULL,
                rtk_cmd TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                saved_tokens INTEGER NOT NULL,
                savings_pct REAL NOT NULL
            )
            """);

        Execute("CREATE INDEX IF NOT EXISTS idx_timestamp ON commands(timestamp)");

        // Migration: add exec_time_ms column if it doesn't exist. Non-fatal on repeat runs — SQLite
        // raises "duplicate column name" if it's already present.
        TryExecute("ALTER TABLE commands ADD COLUMN exec_time_ms INTEGER DEFAULT 0");

        // Migration: add project_path column with DEFAULT '' for new rows.
        TryExecute("ALTER TABLE commands ADD COLUMN project_path TEXT DEFAULT ''");

        // One-time migration: normalize NULLs from pre-default schema. Guarded by an EXISTS check so
        // a second construction against an already-normalized database is a cheap read, not a
        // redundant write.
        bool hasNulls;
        using (var checkCmd = _connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT EXISTS(SELECT 1 FROM commands WHERE project_path IS NULL)";
            hasNulls = Convert.ToInt64(checkCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }

        if (hasNulls)
        {
            TryExecute("UPDATE commands SET project_path = '' WHERE project_path IS NULL");
        }

        // Index for fast project-scoped gain queries.
        Execute("CREATE INDEX IF NOT EXISTS idx_project_path_timestamp ON commands(project_path, timestamp)");

        Execute(
            """
            CREATE TABLE IF NOT EXISTS parse_failures (
                id INTEGER PRIMARY KEY,
                timestamp TEXT NOT NULL,
                raw_command TEXT NOT NULL,
                error_message TEXT NOT NULL,
                fallback_succeeded INTEGER NOT NULL DEFAULT 0
            )
            """);

        Execute("CREATE INDEX IF NOT EXISTS idx_pf_timestamp ON parse_failures(timestamp)");
    }

    // -----------------------------------------------------------------------
    // Record / cleanup / reset
    // -----------------------------------------------------------------------

    /// <summary>
    /// Records a command execution with token counts and timing, then runs <see cref="CleanupOld"/>.
    /// Faithful port of Rust <c>Tracker::record</c> (<c>tracking.rs:402-437</c>).
    /// </summary>
    /// <param name="originalCmd">The standard command (e.g., <c>"ls -la"</c>).</param>
    /// <param name="rtkCmd">The RTK command used (e.g., <c>"rtk ls"</c>).</param>
    /// <param name="inputTokens">Estimated tokens from the standard command's output.</param>
    /// <param name="outputTokens">Actual tokens from RTK's filtered output.</param>
    /// <param name="execTimeMs">Execution time in milliseconds.</param>
    public void Record(string originalCmd, string rtkCmd, int inputTokens, int outputTokens, long execTimeMs)
    {
        // Rust's `saturating_sub`: never goes negative.
        var saved = Math.Max(0, inputTokens - outputTokens);
        var pct = inputTokens > 0 ? saved / (double)inputTokens * 100.0 : 0.0;
        var projectPath = CurrentProjectPathString();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO commands (timestamp, original_cmd, rtk_cmd, project_path, input_tokens, output_tokens, saved_tokens, savings_pct, exec_time_ms)
            VALUES ($ts, $originalCmd, $rtkCmd, $projectPath, $inputTokens, $outputTokens, $saved, $pct, $execTimeMs)
            """;
        cmd.Parameters.AddWithValue("$ts", RfcTimestampUtcNow());
        cmd.Parameters.AddWithValue("$originalCmd", originalCmd);
        cmd.Parameters.AddWithValue("$rtkCmd", rtkCmd);
        cmd.Parameters.AddWithValue("$projectPath", projectPath);
        cmd.Parameters.AddWithValue("$inputTokens", inputTokens);
        cmd.Parameters.AddWithValue("$outputTokens", outputTokens);
        cmd.Parameters.AddWithValue("$saved", saved);
        cmd.Parameters.AddWithValue("$pct", pct);
        cmd.Parameters.AddWithValue("$execTimeMs", execTimeMs);
        cmd.ExecuteNonQuery();

        CleanupOld();
    }

    /// <summary>
    /// Deletes <c>commands</c>/<c>parse_failures</c> rows older than <c>now - 90 days</c>. Faithful port
    /// of Rust <c>cleanup_old</c> (<c>tracking.rs:439-450</c>).
    /// </summary>
    /// <remarks>
    /// <b>Hardcoded 90-day cutoff — a deliberate, disclosed Rust quirk, not a bug.</b>
    /// <see cref="TrackingConstants.DefaultHistoryDays"/> is used verbatim here, never
    /// <c>config.tracking.history_days</c> (despite that field existing and being wired through
    /// TOML config in both languages) — Rust's own <c>cleanup_old()</c> never reads it either. This
    /// port replicates that behavior rather than "fixing" it.
    /// </remarks>
    private void CleanupOld()
    {
        var cutoff = RfcTimestamp(DateTimeOffset.UtcNow.AddDays(-TrackingConstants.DefaultHistoryDays));

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM commands WHERE timestamp < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM parse_failures WHERE timestamp < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes all tracked data (<c>commands</c> + <c>parse_failures</c>) in a single transaction,
    /// resetting all stats to zero. Faithful port of Rust <c>reset_all</c> (<c>tracking.rs:452-463</c>).
    /// </summary>
    public void ResetAll()
    {
        using var transaction = _connection.BeginTransaction();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM commands";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM parse_failures";
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // -----------------------------------------------------------------------
    // Parse failures
    // -----------------------------------------------------------------------

    /// <summary>
    /// Records a parse failure for analytics, then runs <see cref="CleanupOld"/>. Faithful port of
    /// Rust <c>record_parse_failure</c> (<c>tracking.rs:466-484</c>).
    /// </summary>
    /// <param name="rawCommand">The raw command line that failed to parse.</param>
    /// <param name="errorMessage">The parse error message.</param>
    /// <param name="fallbackSucceeded">Whether the raw-passthrough fallback succeeded.</param>
    public void RecordParseFailure(string rawCommand, string errorMessage, bool fallbackSucceeded)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO parse_failures (timestamp, raw_command, error_message, fallback_succeeded)
            VALUES ($ts, $rawCommand, $errorMessage, $fallbackSucceeded)
            """;
        cmd.Parameters.AddWithValue("$ts", RfcTimestampUtcNow());
        cmd.Parameters.AddWithValue("$rawCommand", rawCommand);
        cmd.Parameters.AddWithValue("$errorMessage", errorMessage);
        cmd.Parameters.AddWithValue("$fallbackSucceeded", fallbackSucceeded ? 1 : 0);
        cmd.ExecuteNonQuery();

        CleanupOld();
    }

    /// <summary>
    /// Gets the parse failure summary for <c>rtk gain --failures</c>. Faithful port of Rust
    /// <c>get_parse_failure_summary</c> (<c>tracking.rs:487-542</c>).
    /// </summary>
    /// <returns>The aggregated parse failure summary.</returns>
    public ParseFailureSummary GetParseFailureSummary()
    {
        var total = (int)ScalarInt64("SELECT COUNT(*) FROM parse_failures");
        var succeeded = (int)ScalarInt64("SELECT COUNT(*) FROM parse_failures WHERE fallback_succeeded = 1");
        var recoveryRate = total > 0 ? succeeded / (double)total * 100.0 : 0.0;

        var topCommands = new List<(string Command, int Count)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT raw_command, COUNT(*) as cnt
                FROM parse_failures
                GROUP BY raw_command
                ORDER BY cnt DESC
                LIMIT 10
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                topCommands.Add((reader.GetString(0), (int)reader.GetInt64(1)));
            }
        }

        var recent = new List<ParseFailureRecord>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT timestamp, raw_command, error_message, fallback_succeeded
                FROM parse_failures
                ORDER BY timestamp DESC
                LIMIT 10
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                recent.Add(new ParseFailureRecord
                {
                    Timestamp = reader.GetString(0),
                    RawCommand = reader.GetString(1),
                    ErrorMessage = reader.GetString(2),
                    FallbackSucceeded = reader.GetInt64(3) != 0,
                });
            }
        }

        return new ParseFailureSummary
        {
            Total = total,
            RecoveryRate = recoveryRate,
            TopCommands = topCommands,
            Recent = recent,
        };
    }

    // -----------------------------------------------------------------------
    // Summary
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets overall summary statistics across all recorded commands. Faithful port of Rust
    /// <c>get_summary</c> (<c>tracking.rs:564-566</c>), delegating to <see cref="GetSummaryFiltered"/>
    /// with no project filter.
    /// </summary>
    /// <returns>The aggregated summary.</returns>
    public GainSummary GetSummary() => GetSummaryFiltered(null);

    /// <summary>
    /// Gets summary statistics filtered by project path. Faithful port of Rust
    /// <c>get_summary_filtered</c> (<c>tracking.rs:572-631</c>). When <paramref name="projectPath"/> is
    /// non-null, matches the exact working directory or any subdirectory (prefix match with path
    /// separator).
    /// </summary>
    /// <param name="projectPath">The canonicalized project path to filter by, or <c>null</c> for all projects.</param>
    /// <returns>The aggregated summary.</returns>
    public GainSummary GetSummaryFiltered(string? projectPath)
    {
        var totalCommands = 0;
        var totalInput = 0;
        var totalOutput = 0;
        var totalSaved = 0;
        long totalTimeMs = 0;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT input_tokens, output_tokens, saved_tokens, exec_time_ms
                FROM commands
                WHERE ($exact IS NULL OR project_path = $exact OR project_path GLOB $glob)
                """;
            AddProjectFilterParams(cmd, projectPath);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                totalCommands++;
                totalInput += (int)reader.GetInt64(0);
                totalOutput += (int)reader.GetInt64(1);
                totalSaved += (int)reader.GetInt64(2);
                totalTimeMs += reader.GetInt64(3);
            }
        }

        var avgSavingsPct = totalInput > 0 ? totalSaved / (double)totalInput * 100.0 : 0.0;
        var avgTimeMs = totalCommands > 0 ? totalTimeMs / totalCommands : 0;

        return new GainSummary
        {
            TotalCommands = totalCommands,
            TotalInput = totalInput,
            TotalOutput = totalOutput,
            TotalSaved = totalSaved,
            AvgSavingsPct = avgSavingsPct,
            TotalTimeMs = totalTimeMs,
            AvgTimeMs = avgTimeMs,
            ByCommand = GetByCommand(projectPath),
            ByDay = GetByDay(projectPath),
        };
    }

    /// <summary>
    /// Gets the top 10 commands by tokens saved. Faithful port of Rust's private <c>get_by_command</c>
    /// (<c>tracking.rs:633-659</c>).
    /// </summary>
    /// <param name="projectPath">The canonicalized project path to filter by, or <c>null</c> for all projects.</param>
    /// <returns>Up to 10 command stats, ordered by tokens saved (descending).</returns>
    private IReadOnlyList<CommandStat> GetByCommand(string? projectPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT rtk_cmd, COUNT(*), SUM(saved_tokens), AVG(savings_pct), AVG(exec_time_ms)
            FROM commands
            WHERE ($exact IS NULL OR project_path = $exact OR project_path GLOB $glob)
            GROUP BY rtk_cmd
            ORDER BY SUM(saved_tokens) DESC
            LIMIT 10
            """;
        AddProjectFilterParams(cmd, projectPath);

        var result = new List<CommandStat>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CommandStat
            {
                Command = reader.GetString(0),
                Count = (int)reader.GetInt64(1),
                SavedTokens = (int)reader.GetInt64(2),
                AvgSavingsPct = reader.GetDouble(3),
                AvgTimeMs = (long)reader.GetDouble(4),
            });
        }

        return result;
    }

    /// <summary>
    /// Gets the last 30 days of activity, ordered oldest to newest. Faithful port of Rust's private
    /// <c>get_by_day</c> (<c>tracking.rs:661-683</c>): queried newest-first with a 30-row limit, then
    /// reversed to chronological order.
    /// </summary>
    /// <param name="projectPath">The canonicalized project path to filter by, or <c>null</c> for all projects.</param>
    /// <returns>Up to 30 days of savings totals, oldest first.</returns>
    private IReadOnlyList<DaySavings> GetByDay(string? projectPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT DATE(timestamp), SUM(saved_tokens)
            FROM commands
            WHERE ($exact IS NULL OR project_path = $exact OR project_path GLOB $glob)
            GROUP BY DATE(timestamp)
            ORDER BY DATE(timestamp) DESC
            LIMIT 30
            """;
        AddProjectFilterParams(cmd, projectPath);

        var result = new List<DaySavings>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                result.Add(new DaySavings { Date = reader.GetString(0), SavedTokens = (int)reader.GetInt64(1) });
            }
        }

        result.Reverse();
        return result;
    }

    // -----------------------------------------------------------------------
    // Day / week / month period stats
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets daily statistics for all recorded days, ordered chronologically (oldest first). Faithful
    /// port of Rust <c>get_all_days</c> (<c>tracking.rs:703-705</c>), delegating to
    /// <see cref="GetAllDaysFiltered"/> with no project filter.
    /// </summary>
    /// <returns>One <see cref="DayStats"/> per day.</returns>
    public IReadOnlyList<DayStats> GetAllDays() => GetAllDaysFiltered(null);

    /// <summary>
    /// Gets daily statistics filtered by project path, ordered chronologically (oldest first).
    /// Faithful port of Rust <c>get_all_days_filtered</c> (<c>tracking.rs:708-756</c>).
    /// </summary>
    /// <remarks>
    /// Rust queries newest-first (<c>ORDER BY DATE(timestamp) DESC</c>) then reverses the result
    /// vector. Since <c>DATE(timestamp)</c> is the sole sort key and every row in a
    /// <c>GROUP BY DATE(timestamp)</c> result set has a unique date, an ascending query
    /// (<c>ORDER BY DATE(timestamp) ASC</c>) is equivalent with no secondary-key ordering
    /// difference — used directly here instead of a query-then-reverse round trip.
    /// </remarks>
    /// <param name="projectPath">The canonicalized project path to filter by, or <c>null</c> for all projects.</param>
    /// <returns>One <see cref="DayStats"/> per day.</returns>
    public IReadOnlyList<DayStats> GetAllDaysFiltered(string? projectPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                DATE(timestamp) as date,
                COUNT(*) as commands,
                SUM(input_tokens) as input,
                SUM(output_tokens) as output,
                SUM(saved_tokens) as saved,
                SUM(exec_time_ms) as total_time
            FROM commands
            WHERE ($exact IS NULL OR project_path = $exact OR project_path GLOB $glob)
            GROUP BY DATE(timestamp)
            ORDER BY DATE(timestamp) ASC
            """;
        AddProjectFilterParams(cmd, projectPath);

        var result = new List<DayStats>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var input = (int)reader.GetInt64(2);
            var output = (int)reader.GetInt64(3);
            var saved = (int)reader.GetInt64(4);
            var commands = (int)reader.GetInt64(1);
            var totalTime = reader.GetInt64(5);
            var savingsPct = input > 0 ? saved / (double)input * 100.0 : 0.0;
            var avgTimeMs = commands > 0 ? totalTime / commands : 0;

            result.Add(new DayStats
            {
                Date = reader.GetString(0),
                Commands = commands,
                InputTokens = input,
                OutputTokens = output,
                SavedTokens = saved,
                SavingsPct = savingsPct,
                TotalTimeMs = totalTime,
                AvgTimeMs = avgTimeMs,
            });
        }

        return result;
    }

    /// <summary>
    /// Gets weekly statistics grouped by week, ordered chronologically (oldest first). Weeks run
    /// Monday through Sunday, per SQLite's <c>'weekday 0'</c> modifier. Faithful port of Rust
    /// <c>get_by_week</c> (<c>tracking.rs:776-778</c>), delegating to <see cref="GetByWeekFiltered"/>
    /// with no project filter.
    /// </summary>
    /// <returns>One <see cref="WeekStats"/> per week.</returns>
    public IReadOnlyList<WeekStats> GetByWeek() => GetByWeekFiltered(null);

    /// <summary>
    /// Gets weekly statistics filtered by project path, ordered chronologically (oldest first).
    /// Faithful port of Rust <c>get_by_week_filtered</c> (<c>tracking.rs:781-831</c>).
    /// </summary>
    /// <remarks>
    /// Week boundaries use SQLite's <c>DATE(timestamp, 'weekday 0', '-6 days')</c> (week start) and
    /// <c>DATE(timestamp, 'weekday 0')</c> (week end) modifiers verbatim, exactly as Rust does — raw
    /// SQL, not client-side date math, to guarantee identical semantics. <c>'weekday 0'</c> means
    /// "the next Sunday on/after this date (or today, if today is already Sunday)"; subtracting 6
    /// days from that yields the preceding Monday. See the same ASC-vs-DESC-then-reverse equivalence
    /// note as <see cref="GetAllDaysFiltered"/> — <c>week_start</c> is the sole <c>GROUP BY</c> key here.
    /// </remarks>
    /// <param name="projectPath">The canonicalized project path to filter by, or <c>null</c> for all projects.</param>
    /// <returns>One <see cref="WeekStats"/> per week.</returns>
    public IReadOnlyList<WeekStats> GetByWeekFiltered(string? projectPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                DATE(timestamp, 'weekday 0', '-6 days') as week_start,
                DATE(timestamp, 'weekday 0') as week_end,
                COUNT(*) as commands,
                SUM(input_tokens) as input,
                SUM(output_tokens) as output,
                SUM(saved_tokens) as saved,
                SUM(exec_time_ms) as total_time
            FROM commands
            WHERE ($exact IS NULL OR project_path = $exact OR project_path GLOB $glob)
            GROUP BY week_start
            ORDER BY week_start ASC
            """;
        AddProjectFilterParams(cmd, projectPath);

        var result = new List<WeekStats>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var input = (int)reader.GetInt64(3);
            var output = (int)reader.GetInt64(4);
            var saved = (int)reader.GetInt64(5);
            var commands = (int)reader.GetInt64(2);
            var totalTime = reader.GetInt64(6);
            var savingsPct = input > 0 ? saved / (double)input * 100.0 : 0.0;
            var avgTimeMs = commands > 0 ? totalTime / commands : 0;

            result.Add(new WeekStats
            {
                WeekStart = reader.GetString(0),
                WeekEnd = reader.GetString(1),
                Commands = commands,
                InputTokens = input,
                OutputTokens = output,
                SavedTokens = saved,
                SavingsPct = savingsPct,
                TotalTimeMs = totalTime,
                AvgTimeMs = avgTimeMs,
            });
        }

        return result;
    }

    /// <summary>
    /// Gets monthly statistics grouped by month (<c>YYYY-MM</c>), ordered chronologically (oldest
    /// first). Faithful port of Rust <c>get_by_month</c> (<c>tracking.rs:851-853</c>), delegating to
    /// <see cref="GetByMonthFiltered"/> with no project filter.
    /// </summary>
    /// <returns>One <see cref="MonthStats"/> per month.</returns>
    public IReadOnlyList<MonthStats> GetByMonth() => GetByMonthFiltered(null);

    /// <summary>
    /// Gets monthly statistics filtered by project path, ordered chronologically (oldest first).
    /// Faithful port of Rust <c>get_by_month_filtered</c> (<c>tracking.rs:856-904</c>).
    /// </summary>
    /// <param name="projectPath">The canonicalized project path to filter by, or <c>null</c> for all projects.</param>
    /// <returns>One <see cref="MonthStats"/> per month.</returns>
    public IReadOnlyList<MonthStats> GetByMonthFiltered(string? projectPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                strftime('%Y-%m', timestamp) as month,
                COUNT(*) as commands,
                SUM(input_tokens) as input,
                SUM(output_tokens) as output,
                SUM(saved_tokens) as saved,
                SUM(exec_time_ms) as total_time
            FROM commands
            WHERE ($exact IS NULL OR project_path = $exact OR project_path GLOB $glob)
            GROUP BY month
            ORDER BY month ASC
            """;
        AddProjectFilterParams(cmd, projectPath);

        var result = new List<MonthStats>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var input = (int)reader.GetInt64(2);
            var output = (int)reader.GetInt64(3);
            var saved = (int)reader.GetInt64(4);
            var commands = (int)reader.GetInt64(1);
            var totalTime = reader.GetInt64(5);
            var savingsPct = input > 0 ? saved / (double)input * 100.0 : 0.0;
            var avgTimeMs = commands > 0 ? totalTime / commands : 0;

            result.Add(new MonthStats
            {
                Month = reader.GetString(0),
                Commands = commands,
                InputTokens = input,
                OutputTokens = output,
                SavedTokens = saved,
                SavingsPct = savingsPct,
                TotalTimeMs = totalTime,
                AvgTimeMs = avgTimeMs,
            });
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Recent history
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets recent command history, newest first. Faithful port of Rust <c>get_recent</c>
    /// (<c>tracking.rs:928-930</c>), delegating to <see cref="GetRecentFiltered"/> with no project
    /// filter.
    /// </summary>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <returns>Up to <paramref name="limit"/> most recent command records.</returns>
    public IReadOnlyList<CommandRecord> GetRecent(int limit) => GetRecentFiltered(limit, null);

    /// <summary>
    /// Gets recent command history filtered by project path, newest first. Faithful port of Rust
    /// <c>get_recent_filtered</c> (<c>tracking.rs:933-962</c>).
    /// </summary>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="projectPath">The canonicalized project path to filter by, or <c>null</c> for all projects.</param>
    /// <returns>Up to <paramref name="limit"/> most recent command records.</returns>
    public IReadOnlyList<CommandRecord> GetRecentFiltered(int limit, string? projectPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT timestamp, rtk_cmd, saved_tokens, savings_pct
            FROM commands
            WHERE ($exact IS NULL OR project_path = $exact OR project_path GLOB $glob)
            ORDER BY timestamp DESC
            LIMIT $limit
            """;
        AddProjectFilterParams(cmd, projectPath);
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<CommandRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var timestampText = reader.GetString(0);
            var timestamp = DateTimeOffset.TryParse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            result.Add(new CommandRecord
            {
                Timestamp = timestamp,
                RtkCmd = reader.GetString(1),
                SavedTokens = (int)reader.GetInt64(2),
                SavingsPct = reader.GetDouble(3),
            });
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Project path filtering helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the SQL filter params for project-scoped queries: an exact match and a GLOB prefix
    /// pattern. Faithful port of Rust <c>project_filter_params</c> (<c>tracking.rs:54-62</c>).
    /// </summary>
    /// <remarks>
    /// <b>GLOB, not LIKE.</b> SQLite's <c>GLOB</c> operator uses Unix-shell-style wildcards
    /// (<c>*</c>/<c>?</c>), is case-sensitive, and treats <c>_</c> and <c>%</c> as ordinary literal
    /// characters — unlike <c>LIKE</c>, where <c>_</c> matches any single character and <c>%</c>
    /// matches any run of characters. Using <c>LIKE</c> here would silently misinterpret real
    /// project directory names containing underscores (e.g. <c>my_project</c>) as wildcard patterns.
    /// </remarks>
    /// <param name="projectPath">The project path to filter by, or <c>null</c> for no filter.</param>
    /// <returns>A tuple of (exact-match value, GLOB prefix pattern), both <c>null</c> when <paramref name="projectPath"/> is <c>null</c>.</returns>
    private static (string? Exact, string? Glob) ProjectFilterParams(string? projectPath) =>
        projectPath is null
            ? (null, null)
            : (projectPath, $"{projectPath}{Path.DirectorySeparatorChar}*");

    /// <summary>Adds the <c>$exact</c>/<c>$glob</c> parameters produced by <see cref="ProjectFilterParams"/> to a command.</summary>
    /// <param name="cmd">The command to add parameters to.</param>
    /// <param name="projectPath">The project path to filter by, or <c>null</c> for no filter.</param>
    private static void AddProjectFilterParams(SqliteCommand cmd, string? projectPath)
    {
        var (exact, glob) = ProjectFilterParams(projectPath);
        cmd.Parameters.AddWithValue("$exact", (object?)exact ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$glob", (object?)glob ?? DBNull.Value);
    }

    /// <summary>
    /// Gets the canonicalized project path string for the current working directory, or an empty
    /// string on any failure. Faithful port of Rust <c>current_project_path_string</c>
    /// (<c>tracking.rs:42-49</c>).
    /// </summary>
    /// <returns>The canonicalized current working directory, or <c>""</c> if it could not be resolved.</returns>
    private static string CurrentProjectPathString()
    {
        try
        {
            return Path.GetFullPath(Directory.GetCurrentDirectory());
        }
        catch (Exception)
        {
            // Best-effort, matching Rust's `.ok()`-chained fallback to an empty string on any failure
            // (current_dir() or canonicalize() erroring) — never block command execution over this.
            return string.Empty;
        }
    }

    /// <summary>Builds an RFC-3339 UTC timestamp string for "now". Mirrors Rust's <c>Utc::now().to_rfc3339()</c>.</summary>
    /// <returns>The formatted timestamp.</returns>
    private static string RfcTimestampUtcNow() => RfcTimestamp(DateTimeOffset.UtcNow);

    /// <summary>Formats a <see cref="DateTimeOffset"/> as an RFC-3339 timestamp string.</summary>
    /// <param name="value">The instant to format.</param>
    /// <returns>The formatted timestamp.</returns>
    private static string RfcTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    /// <summary>Executes a scalar <c>COUNT</c>-style query and returns the result as an <see cref="long"/>.</summary>
    /// <param name="sql">The scalar SQL query to execute (no parameters).</param>
    /// <returns>The scalar result.</returns>
    private long ScalarInt64(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Executes a non-query statement, propagating any failure. Used for schema-critical DDL.</summary>
    /// <param name="sql">The SQL statement to execute.</param>
    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Executes a non-query statement, swallowing any <see cref="SqliteException"/>. Matches Rust's
    /// <c>let _ = conn.execute(...)</c> non-fatal pattern used for pragmas and idempotent migrations.
    /// </summary>
    /// <param name="sql">The SQL statement to execute.</param>
    private void TryExecute(string sql)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Non-fatal by design — see remarks on the pragma calls and column migrations above.
        }
    }

    /// <summary>Closes the underlying SQLite connection.</summary>
    public void Dispose() => _connection.Dispose();
}
