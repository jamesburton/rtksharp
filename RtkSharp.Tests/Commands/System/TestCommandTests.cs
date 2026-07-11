using System.Text;
using Microsoft.Data.Sqlite;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="TestCommand"/>, the <c>rtk test</c> buffered test-summary runner. The
/// pure summarizing logic (<c>ExtractTestSummary</c>) moved to
/// <c>RtkSharp.Filters.Commands.System.TestFilters</c> - see
/// <c>RtkSharp.Filters.Tests.Commands.System.TestFiltersTests</c>. This file now covers only the
/// buffered-capture cap application. Faithful-port target: Rust <c>runner::run_test</c>
/// (<c>src/cmds/rust/runner.rs</c>:132-146).
/// </summary>
public sealed class TestCommandTests
{
    // -----------------------------------------------------------------------
    // Cap application (RAW_CAP soft warning for stdout, silent cap for stderr).
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyStdoutCap_UnderCap_ReturnsUnchanged_NoWarning()
    {
        var raw = "line one\nline two\n";
        using var console = new ConsoleErrorCapture();

        var (text, capped) = TestCommand.ApplyStdoutCap(raw, capBytes: 1_000_000);

        Assert.False(capped);
        Assert.Equal(raw, text);
        Assert.Equal(string.Empty, console.ToString());
    }

    [Fact]
    public void ApplyStdoutCap_OverCap_StopsAccumulating_PrintsExactWarning()
    {
        var raw = "aaaaaaaaaa\nbbbbbbbbbb\ncccccccccc\ndddddddddd\n";
        using var console = new ConsoleErrorCapture();

        var (text, capped) = TestCommand.ApplyStdoutCap(raw, capBytes: 15);

        Assert.True(capped);
        Assert.Contains("aaaaaaaaaa", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dddddddddd", text, StringComparison.Ordinal);
        Assert.Equal(
            "[rtk] warning: output exceeds 10 MiB — filter input truncated\n",
            console.ToString());
    }

    [Fact]
    public void ApplyStderrCapSilently_OverCap_StopsAccumulating_NoWarningPrinted()
    {
        var raw = "aaaaaaaaaa\nbbbbbbbbbb\ncccccccccc\ndddddddddd\n";
        using var console = new ConsoleErrorCapture();

        var text = TestCommand.ApplyStderrCapSilently(raw, capBytes: 15);

        Assert.Contains("aaaaaaaaaa", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dddddddddd", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, console.ToString());
    }

    [Fact]
    public void ApplyStdoutCap_EmptyInput_ReturnsEmpty_NotCapped()
    {
        var (text, capped) = TestCommand.ApplyStdoutCap(string.Empty, capBytes: 100);
        Assert.Equal(string.Empty, text);
        Assert.False(capped);
    }

    // -----------------------------------------------------------------------
    // RunAsync: end-to-end via injectable IProcessExecutor.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_JoinsTrailingArgsWithSpaces_AsTheShellCommand()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult("test result: ok. 1 passed", "", 0, TimeSpan.Zero, true, null, false));
        using var db = new TempTrackingDb();

        var exitCode = await TestCommand.RunAsync(["cargo", "test", "--", "--nocapture"], fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Contains("cargo test -- --nocapture", fake.CapturedRequest!.Arguments);
    }

    [Fact]
    public async Task RunAsync_SpawnFailure_ThrowsWithDetail()
    {
        var fake = new FakeProcessExecutor(
            new ExecutionResult("", "", 127, TimeSpan.Zero, false, "The system cannot find the file specified", false));

        using var console = new ConsoleErrorCapture();
        var exitCode = await TestCommand.RunAsync(["does-not-exist-xyz"], fake);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("rtk: Failed to run test:", console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PrintsExtractedSummary_NotRawOutput()
    {
        var fake = new FakeProcessExecutor(
            new ExecutionResult("test result: FAILED. 0 passed; 1 failed; 0 ignored\ntest foo ... FAILED", "", 1, TimeSpan.Zero, true, null, false));
        using var db = new TempTrackingDb();

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            var exitCode = await TestCommand.RunAsync(["cargo", "test"], fake);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("SUMMARY:", capture.ToString(), StringComparison.Ordinal);
        Assert.Contains("[FAIL] FAILURES:", capture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExitCodePropagatesFromExecutor()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult("", "", 5, TimeSpan.Zero, true, null, false));
        using var db = new TempTrackingDb();

        var exitCode = await TestCommand.RunAsync(["go", "test", "./..."], fake);
        Assert.Equal(5, exitCode);
    }

    [Fact]
    public async Task RunAsync_Tracks_UsingCombinedRawAndFilteredOutput()
    {
        var fake = new FakeProcessExecutor(
            new ExecutionResult("test result: ok. 1 passed; 0 failed", "", 0, TimeSpan.Zero, true, null, false));
        using var db = new TempTrackingDb();

        var marker = "test-track-test_" + Guid.NewGuid().ToString("N");
        var exitCode = await TestCommand.RunAsync(["cargo", "test", marker], fake);

        Assert.Equal(0, exitCode);

        var row = QueryRow(db.DbPath, $"rtk test cargo test {marker}");
        Assert.NotNull(row);
        // Rust's core::runner.rs run() tracks `cmd_label = format!("{} {}", tool_name, args_display)`
        // (i.e. "test {command}") as BOTH the original_cmd and (with "rtk " prepended) the rtk_cmd -
        // so original_cmd genuinely carries the "test " prefix too, byte-exact with the source.
        Assert.Equal($"test cargo test {marker}", row!.Value.OriginalCmd);
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

    private sealed class FakeProcessExecutor(ExecutionResult result) : IProcessExecutor
    {
        public ExecutionRequest? CapturedRequest { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
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

    private sealed class TempTrackingDb : IDisposable
    {
        private readonly string? _previousDbPath = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rtksharp-test-cmd-tests-" + Guid.NewGuid().ToString("N"));

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
