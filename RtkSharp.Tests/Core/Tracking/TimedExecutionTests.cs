using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using RtkSharp.Core.Tracking;
using Xunit;

namespace RtkSharp.Tests.Core.Tracking;

/// <summary>
/// Tests for <see cref="TimedExecution"/>, the direct-<see cref="Tracker"/>-backed timing wrapper.
/// Faithful port of the behavior described by Rust's <c>TimedExecution</c>
/// (<c>tracking.rs:1306-1399</c>) — Rust has no dedicated unit tests for this struct itself (its
/// doctests are <c>no_run</c>), so this suite exercises the documented contract directly: elapsed
/// time + token estimation land in the persisted row via <c>Tracker::record</c>, and all tracking
/// failures are swallowed silently.
/// </summary>
/// <remarks>
/// Follows <see cref="TrackerTests"/>'s established conventions: <c>RTK_DB_PATH</c>-based hermetic
/// testing via a private <c>EnvVarScope</c>, and a private <c>TempDir</c> for throwaway database
/// files.
/// </remarks>
public sealed class TimedExecutionTests
{
    private static readonly object EnvLock = new();

    // -----------------------------------------------------------------------
    // Track — persists a row via a temp-DB-backed Tracker, elapsed time + token estimates land correctly
    // -----------------------------------------------------------------------

    [Fact]
    public void Track_PersistsRow_ViaRtkDbPathEnvVar_WithCorrectTokenEstimates()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            using var dir = new TempDir();
            var dbPath = Path.Combine(dir.Root, "history.db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, dbPath);

            var rtkCmd = "rtk timed-execution-test_" + Guid.NewGuid().ToString("N");
            const string input = "abcde"; // 5 bytes -> ceil(5/4) = 2 tokens
            const string output = "abcd"; // 4 bytes -> ceil(4/4) = 1 token

            var timer = TimedExecution.Start();
            timer.Track("original cmd", rtkCmd, input, output);

            var row = QueryRow(dbPath, rtkCmd);
            Assert.NotNull(row);
            Assert.Equal(2, row!.Value.InputTokens);
            Assert.Equal(1, row.Value.OutputTokens);
            Assert.True(row.Value.ExecTimeMs >= 0, "Elapsed time must be non-negative.");
        }
    }

    [Fact]
    public void Track_SeededDelay_RecordsExecTimeMsAtLeastTheDelay()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            using var dir = new TempDir();
            var dbPath = Path.Combine(dir.Root, "history.db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, dbPath);

            var rtkCmd = "rtk timed-execution-delay-test_" + Guid.NewGuid().ToString("N");

            var timer = TimedExecution.Start();
            Thread.Sleep(30);
            timer.Track("original cmd", rtkCmd, "input", "output");

            var row = QueryRow(dbPath, rtkCmd);
            Assert.NotNull(row);

            // Allow generous slack for CI scheduling jitter - only asserting the seeded delay was
            // actually captured, not measuring precise timer accuracy.
            Assert.True(row!.Value.ExecTimeMs >= 15, $"Expected exec_time_ms >= 15 after a 30ms sleep, got {row.Value.ExecTimeMs}.");
        }
    }

    // -----------------------------------------------------------------------
    // TrackPassthrough — zero input/output tokens, non-zero elapsed time
    // -----------------------------------------------------------------------

    [Fact]
    public void TrackPassthrough_RecordsZeroTokens_ButNonZeroElapsedTime()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            using var dir = new TempDir();
            var dbPath = Path.Combine(dir.Root, "history.db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, dbPath);

            var rtkCmd = "rtk timed-execution-passthrough-test_" + Guid.NewGuid().ToString("N");

            var timer = TimedExecution.Start();
            Thread.Sleep(15);
            timer.TrackPassthrough("git tag --list", rtkCmd);

            var row = QueryRow(dbPath, rtkCmd);
            Assert.NotNull(row);
            Assert.Equal(0, row!.Value.InputTokens);
            Assert.Equal(0, row.Value.OutputTokens);
            Assert.True(row.Value.ExecTimeMs > 0, $"Expected exec_time_ms > 0 after a 15ms sleep, got {row.Value.ExecTimeMs}.");
        }
    }

    // -----------------------------------------------------------------------
    // Tracker construction failure is silently swallowed - no exception escapes Track()/TrackPassthrough()
    // -----------------------------------------------------------------------

    [Fact]
    public void Track_TrackerConstructionFails_SwallowsSilently_NoExceptionEscapes()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            using var blocker = new BlockingFile();

            // The parent directory component ("<blockerFile>/sub") can never be created because
            // <blockerFile> is itself a plain file, not a directory - Directory.CreateDirectory
            // inside Tracker's constructor must throw (IOException on Windows), which Track() must
            // swallow rather than let escape.
            var invalidDbPath = Path.Combine(blocker.FilePath, "sub", "history.db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, invalidDbPath);

            var timer = TimedExecution.Start();
            var exception = Record.Exception(() => timer.Track("cmd", "rtk cmd", "input", "output"));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void TrackPassthrough_TrackerConstructionFails_SwallowsSilently_NoExceptionEscapes()
    {
        lock (EnvLock)
        {
            using var guard = new EnvVarScope();
            using var blocker = new BlockingFile();

            var invalidDbPath = Path.Combine(blocker.FilePath, "sub", "history.db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, invalidDbPath);

            var timer = TimedExecution.Start();
            var exception = Record.Exception(() => timer.TrackPassthrough("cmd", "rtk cmd"));

            Assert.Null(exception);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (int InputTokens, int OutputTokens, long ExecTimeMs)? QueryRow(string dbPath, string rtkCmd)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT input_tokens, output_tokens, exec_time_ms FROM commands WHERE rtk_cmd = $rtkCmd";
        cmd.Parameters.AddWithValue("$rtkCmd", rtkCmd);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2));
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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rtksharp-timedexec-tests-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// A plain file (not a directory) used to force <c>Directory.CreateDirectory</c> to fail when a
    /// test builds a target DB path with a path segment underneath it - a file cannot contain
    /// subdirectories.
    /// </summary>
    private sealed class BlockingFile : IDisposable
    {
        public string FilePath { get; } = Path.Combine(Path.GetTempPath(), "rtksharp-timedexec-blocker-" + Guid.NewGuid().ToString("N"));

        public BlockingFile() => File.WriteAllText(FilePath, "blocker");

        public void Dispose()
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
