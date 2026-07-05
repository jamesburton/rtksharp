using System.Text;
using Microsoft.Data.Sqlite;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="ErrCommand"/>, the <c>rtk err</c> streaming error-block filter wrapper.
/// Faithful-port target: Rust <c>runner::run_err</c> (<c>src/cmds/rust/runner.rs</c>:117-130) plus
/// its dispatch arm (<c>src/main.rs</c>:1728-1731).
/// </summary>
public sealed class ErrCommandTests
{
    [Fact]
    public async Task RunAsync_JoinsTrailingArgsWithSpaces_AsTheShellCommand()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw\n", "filtered\n", 0, true, null));
        using var db = new TempTrackingDb();

        var exitCode = await ErrCommand.RunAsync(["git", "status", "--short"], fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Contains("git status --short", fake.CapturedRequest!.Arguments);
    }

    [Fact]
    public async Task RunAsync_UsesErrorStreamFilter()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 0, true, null));
        using var db = new TempTrackingDb();

        await ErrCommand.RunAsync(["echo", "hi"], fake);

        Assert.IsType<ErrorStreamFilter>(fake.CapturedFilter);
    }

    [Fact]
    public async Task RunAsync_SpawnFailure_ThrowsWithDetail_TopLevelCatchPrintsRtkPrefixedError()
    {
        var fake = new FakeLineFilteringExecutor(
            new LineFilteringResult("", "", 127, false, "The system cannot find the file specified"));

        using var console = new ConsoleErrorCapture();
        var exitCode = await ErrCommand.RunAsync(["does-not-exist-xyz"], fake);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("rtk: Failed to run err:", console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExitCodePropagatesFromExecutor()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 7, true, null));
        using var db = new TempTrackingDb();

        var exitCode = await ErrCommand.RunAsync(["some", "command"], fake);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task RunAsync_Tracks_UsingRawAndFilteredFromExecutor()
    {
        var fake = new FakeLineFilteringExecutor(
            new LineFilteringResult("raw-content-for-tracking", "filtered-content", 0, true, null));
        using var db = new TempTrackingDb();

        var marker = "err-track-test_" + Guid.NewGuid().ToString("N");
        var exitCode = await ErrCommand.RunAsync(["echo", marker], fake);

        Assert.Equal(0, exitCode);

        var row = QueryRow(db.DbPath, $"rtk err echo {marker}");
        Assert.NotNull(row);
        // Rust's core::runner.rs run() tracks `cmd_label = format!("{} {}", tool_name, args_display)`
        // (i.e. "err {command}") as BOTH the original_cmd and (with "rtk " prepended) the rtk_cmd -
        // so original_cmd genuinely carries the "err " prefix too, byte-exact with the source.
        Assert.Equal($"err echo {marker}", row!.Value.OriginalCmd);

        var expectedInputTokens = Tracker.EstimateTokens("raw-content-for-tracking");
        var expectedOutputTokens = Tracker.EstimateTokens("filtered-content");
        Assert.Equal(expectedInputTokens, row.Value.InputTokens);
        Assert.Equal(expectedOutputTokens, row.Value.OutputTokens);
    }

    [Fact]
    public async Task RunAsync_VerbosityAboveZero_PrintsRunningLine()
    {
        using var guard = new VerbosityScope(1);
        using var db = new TempTrackingDb();
        using var console = new ConsoleErrorCapture();

        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 0, true, null));
        var exitCode = await ErrCommand.RunAsync(["echo", "hi"], fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("Running: echo hi", console.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Real end-to-end: "no errors"/"[FAIL]" outcomes via the actual LineFilteringExecutor +
    // ErrorStreamFilter, and Tee-to-disk behavior.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_RealSpawn_NoErrorOutput_PrintsOkMessage()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            var exitCode = await ErrCommand.RunAsync(["echo", "all good here"], executor: null);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("[ok] Command completed successfully (no errors)", capture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RealSpawn_ErrorLineStreamsLive()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            var exitCode = await ErrCommand.RunAsync(["echo", "error: something broke"], executor: null);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("error: something broke", capture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RealSpawn_FailureWithNoErrorPattern_TeesToDisk_PrintsRecoveryHint()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();
        using var teeDir = new TempTeeDir();

        // Output padded well past Tee's 500-char minimum, with no line matching any error pattern,
        // paired with a non-zero exit code so ErrorStreamFilter.OnExit emits [FAIL]+tail AND Tee's
        // own Failures-mode threshold is satisfied.
        var padding = new string('x', 600);

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            var exitCode = await ErrCommand.RunAsync(["echo", padding, "&", "exit", "9"], executor: null);
            Assert.Equal(9, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("[FAIL] Command failed (exit code: 9)", capture.ToString(), StringComparison.Ordinal);

        var teeFiles = Directory.GetFiles(teeDir.Path, "*.log");
        Assert.NotEmpty(teeFiles);
        Assert.Contains("[full output:", capture.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (string OriginalCmd, int InputTokens, int OutputTokens, long ExecTimeMs)? QueryRow(string dbPath, string rtkCmd)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT original_cmd, input_tokens, output_tokens, exec_time_ms FROM commands WHERE rtk_cmd = $rtkCmd";
        cmd.Parameters.AddWithValue("$rtkCmd", rtkCmd);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    private sealed class FakeLineFilteringExecutor(LineFilteringResult result) : ILineFilteringExecutor
    {
        public ExecutionRequest? CapturedRequest { get; private set; }
        public IStreamFilter? CapturedFilter { get; private set; }

        public ValueTask<LineFilteringResult> ExecuteAsync(
            ExecutionRequest request,
            IStreamFilter filter,
            CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            CapturedFilter = filter;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ConsoleErrorCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Error;
        private readonly StringWriter _capture = new();

        public ConsoleErrorCapture() => Console.SetError(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetError(_original);
    }

    private sealed class VerbosityScope : IDisposable
    {
        private readonly int _previous = RuntimeOptions.Verbosity;

        public VerbosityScope(int value) => RuntimeOptions.Verbosity = value;

        public void Dispose() => RuntimeOptions.Verbosity = _previous;
    }

    private sealed class TempTrackingDb : IDisposable
    {
        private readonly string? _previousDbPath = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rtksharp-err-cmd-tests-" + Guid.NewGuid().ToString("N"));

        public string DbPath { get; }

        public TempTrackingDb()
        {
            Directory.CreateDirectory(_root);
            DbPath = Path.Combine(_root, "history.db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, DbPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, _previousDbPath);
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class TempTeeDir : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("RTK_TEE_DIR");

        public string Path { get; } =
            global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "rtksharp-err-cmd-tee-" + Guid.NewGuid().ToString("N"));

        public TempTeeDir()
        {
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable("RTK_TEE_DIR", Path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("RTK_TEE_DIR", _previous);
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
