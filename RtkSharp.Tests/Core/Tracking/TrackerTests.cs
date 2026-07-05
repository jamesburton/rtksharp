using System;
using System.IO;
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
/// (<c>tracking.rs:1553-1555</c>) and RtkSharp's established <c>InitTestSupport.EnvLock</c> pattern.
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
}
