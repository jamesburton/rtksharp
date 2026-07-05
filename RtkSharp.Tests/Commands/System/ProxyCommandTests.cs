using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RtkSharp.Commands.System;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="ProxyCommand"/>, the <c>rtk proxy</c> live-streaming + capped-capture +
/// direct-<see cref="Tracker"/>-recording wrapper. Rust's <c>Commands::Proxy</c> arm
/// (<c>main.rs</c>:2333-2511) has no dedicated <c>#[cfg(test)]</c> module of its own (it is inline in
/// <c>main</c>'s dispatch, not a <c>cmds::</c> filter), so this suite is original coverage written
/// directly against the documented Rust behavior: the issue #388 single-raw-arg re-split special
/// case, the empty-args error, verbose-mode stderr logging, direct-Tracker tracking with identical
/// input/output strings, and exit-code propagation.
/// </summary>
public sealed class ProxyCommandTests
{
    // Note: no cross-test env-var lock is needed here - xUnit runs [Fact] methods within a single
    // test class sequentially by default (parallelization happens across test classes/collections,
    // not within one), matching RunCommandTests's/TrackerTests's established convention of mutating
    // RTK_DB_PATH via a scoped guard without any additional synchronization.

    // -----------------------------------------------------------------------
    // ResolveCommand - issue #388 single-raw-arg re-split vs multi-arg direct-use
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveCommand_SingleArgWithSpaces_ReSplitsRespectingQuoting()
    {
        var (cmdName, cmdArgs) = ProxyCommand.ResolveCommand(["echo hello world"]);

        Assert.Equal("echo", cmdName);
        Assert.Equal(["hello", "world"], cmdArgs);
    }

    [Fact]
    public void ResolveCommand_SingleArgWithQuotedSegment_PreservesQuotedSpaces()
    {
        // Mirrors main.rs's own documented example (main.rs:2349):
        // rtk proxy 'git log --format="%H %s"' -> cmd=git, args=["log", "--format=%H %s"]
        var (cmdName, cmdArgs) = ProxyCommand.ResolveCommand(["git log --format=\"%H %s\""]);

        Assert.Equal("git", cmdName);
        Assert.Equal(["log", "--format=%H %s"], cmdArgs);
    }

    [Fact]
    public void ResolveCommand_SingleArgNoSpaces_UsedAsBareCommandName_NoArgs()
    {
        var (cmdName, cmdArgs) = ProxyCommand.ResolveCommand(["echo"]);

        Assert.Equal("echo", cmdName);
        Assert.Empty(cmdArgs);
    }

    [Fact]
    public void ResolveCommand_MultipleArgs_UsedDirectly_NoReSplitAttempted()
    {
        // Even though "hello world" contains a space, it must NOT be re-split because more than one
        // raw arg was supplied - this asymmetry (single-arg re-split, multi-arg direct-use) is
        // deliberate per issue #388.
        var (cmdName, cmdArgs) = ProxyCommand.ResolveCommand(["echo", "hello world", "again"]);

        Assert.Equal("echo", cmdName);
        Assert.Equal(["hello world", "again"], cmdArgs);
    }

    // -----------------------------------------------------------------------
    // Empty-args guard (main.rs:2339-2343)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_EmptyArgs_PrintsExactErrorMessage_ReturnsOne()
    {
        using var console = new ConsoleErrorCapture();

        var exitCode = await ProxyCommand.RunAsync([], executor: null);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "rtk: proxy requires a command to execute\nUsage: rtk proxy <command> [args...]\n",
            console.ToString());
    }

    // -----------------------------------------------------------------------
    // Verbose-mode stderr logging (main.rs:2368-2370) - only extra output proxy ever emits
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_VerbosityAboveZero_PrintsProxyModeLine()
    {
        using var guard = new VerbosityScope(1);
        using var db = new TempTrackingDb();
        using var console = new ConsoleErrorCapture();

        var fake = new FakeStreamingExecutor(new ExecutionResult("out", "", 0, TimeSpan.Zero, true, null, false));
        var exitCode = await ProxyCommand.RunAsync(["echo", "hi"], fake);

        Assert.Equal(0, exitCode);
        Assert.Equal("Proxy mode: echo hi\n", console.ToString());
    }

    [Fact]
    public async Task RunAsync_VerbosityZero_PrintsNothingToStderr()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();
        using var console = new ConsoleErrorCapture();

        var fake = new FakeStreamingExecutor(new ExecutionResult("out", "", 0, TimeSpan.Zero, true, null, false));
        var exitCode = await ProxyCommand.RunAsync(["echo", "hi"], fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, console.ToString());
    }

    // -----------------------------------------------------------------------
    // Tracking: identical input/output strings, non-zero elapsed time (main.rs:2502-2508)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_Success_RecordsIdenticalInputOutput_TokensMatchExpectedFullOutput()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var fake = new FakeStreamingExecutor(
            new ExecutionResult("stdout-part", "stderr-part", 0, TimeSpan.FromMilliseconds(1), true, null, false));

        var marker = "proxy-track-test_" + Guid.NewGuid().ToString("N");
        var exitCode = await ProxyCommand.RunAsync(["echo", marker], fake);

        Assert.Equal(0, exitCode);

        var row = QueryRow(db.DbPath, $"rtk proxy echo {marker}");
        Assert.NotNull(row);

        // Exact Rust concatenation: format!("{}{}", stdout, stderr) - no separator - is what both
        // timer.Track's `input` and `output` parameters receive, so their token estimates must
        // both equal EstimateTokens("stdout-part" + "stderr-part").
        var expectedTokens = Tracker.EstimateTokens("stdout-part" + "stderr-part");
        Assert.Equal(expectedTokens, row!.Value.InputTokens);
        Assert.Equal(expectedTokens, row.Value.OutputTokens);
    }

    [Fact]
    public async Task RunAsync_Success_TracksSameTokenCountForInputAndOutput_ZeroSavingsByDesign()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var fake = new FakeStreamingExecutor(
            new ExecutionResult("stdout-part", "stderr-part", 0, TimeSpan.FromMilliseconds(1), true, null, false));

        var marker = "proxy-savings-test_" + Guid.NewGuid().ToString("N");
        var exitCode = await ProxyCommand.RunAsync(["echo", marker], fake);

        Assert.Equal(0, exitCode);

        var row = QueryRow(db.DbPath, $"rtk proxy echo {marker}");
        Assert.NotNull(row);

        // Both input and output are the SAME full_output string, so token estimates (and hence
        // saved_tokens) must be identical - zero savings by design, since proxy never filters.
        Assert.Equal(row!.Value.InputTokens, row.Value.OutputTokens);
        Assert.True(row.Value.ExecTimeMs >= 0);
    }

    [Fact]
    public async Task RunAsync_Success_OriginalCmdAndRtkCmd_ExactRustFormatting()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var fake = new FakeStreamingExecutor(new ExecutionResult("", "", 0, TimeSpan.Zero, true, null, false));

        var marker = "proxy-format-test_" + Guid.NewGuid().ToString("N");
        var exitCode = await ProxyCommand.RunAsync(["echo", marker, "arg2"], fake);

        Assert.Equal(0, exitCode);

        var expectedRtkCmd = $"rtk proxy echo {marker} arg2";
        var row = QueryRow(db.DbPath, expectedRtkCmd);
        Assert.NotNull(row);
        Assert.Equal($"echo {marker} arg2", row!.Value.OriginalCmd);
    }

    [Fact]
    public async Task RunAsync_Success_NoArgsAfterCmdName_TrailingSpaceInTrackedStrings()
    {
        // format!("{} {}", cmd_name, cmd_args.join(" ")) always inserts a space even when cmd_args is
        // empty (yielding a trailing space) - verify this Rust quirk is replicated verbatim.
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var fake = new FakeStreamingExecutor(new ExecutionResult("", "", 0, TimeSpan.Zero, true, null, false));

        var marker = "proxy-trailing-space-test_" + Guid.NewGuid().ToString("N");
        var exitCode = await ProxyCommand.RunAsync([marker], fake);

        Assert.Equal(0, exitCode);

        var expectedRtkCmd = $"rtk proxy {marker} ";
        var row = QueryRow(db.DbPath, expectedRtkCmd);
        Assert.NotNull(row);
        Assert.Equal($"{marker} ", row!.Value.OriginalCmd);
    }

    // -----------------------------------------------------------------------
    // Exit-code propagation: success and non-zero exit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_RealSpawn_SuccessExitsZero()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var exitCode = await ProxyCommand.RunAsync(["cmd", "/C", "exit 0"], executor: null);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_RealSpawn_NonZeroExitCode_PropagatesExactly()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var exitCode = await ProxyCommand.RunAsync(["cmd", "/C", "exit 3"], executor: null);
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task RunAsync_SpawnFailure_PrintsRtkPrefixedError_ReturnsOne()
    {
        var fake = new FakeStreamingExecutor(
            new ExecutionResult("", "", 127, TimeSpan.Zero, false, "The system cannot find the file specified", false));

        using var console = new ConsoleErrorCapture();
        var exitCode = await ProxyCommand.RunAsync(["does-not-exist-xyz"], fake);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("rtk: Failed to execute command: does-not-exist-xyz", console.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Live passthrough visible via real StreamingExecutor (not just captured)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_RealSpawn_LivePassthroughReachesRedirectedConsole()
    {
        using var guard = new VerbosityScope(0);
        using var db = new TempTrackingDb();

        var originalOut = Console.Out;
        var capture = new StringWriter();
        Console.SetOut(capture);
        try
        {
            var exitCode = await ProxyCommand.RunAsync(["cmd", "/C", "echo live-passthrough-marker"], executor: null);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("live-passthrough-marker", capture.ToString(), StringComparison.Ordinal);
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

    private sealed class FakeStreamingExecutor(ExecutionResult result) : IStreamingExecutor
    {
        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    /// <summary>Captures <see cref="Console.Error"/> output for the lifetime of the instance.</summary>
    private sealed class ConsoleErrorCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Error;
        private readonly StringWriter _capture = new();

        public ConsoleErrorCapture() => Console.SetError(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetError(_original);
    }

    /// <summary>Saves and restores <see cref="RtkSharp.Core.RuntimeOptions.Verbosity"/> for the lifetime of the instance.</summary>
    private sealed class VerbosityScope : IDisposable
    {
        private readonly int _previous = RtkSharp.Core.RuntimeOptions.Verbosity;

        public VerbosityScope(int value) => RtkSharp.Core.RuntimeOptions.Verbosity = value;

        public void Dispose() => RtkSharp.Core.RuntimeOptions.Verbosity = _previous;
    }

    /// <summary>Points <c>RTK_DB_PATH</c> at a fresh throwaway SQLite file for the lifetime of the instance.</summary>
    private sealed class TempTrackingDb : IDisposable
    {
        private readonly string? _previousDbPath = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rtksharp-proxy-cmd-tests-" + Guid.NewGuid().ToString("N"));

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
}
