using System;
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
