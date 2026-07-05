using System.Text;
using Microsoft.Data.Sqlite;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="TestCommand"/>, the <c>rtk test</c> buffered test-summary runner.
/// Faithful-port target: Rust <c>runner::run_test</c>/<c>extract_test_summary</c>
/// (<c>src/cmds/rust/runner.rs</c>:132-146, 181-280).
/// </summary>
public sealed class TestCommandTests
{
    // -----------------------------------------------------------------------
    // ExtractTestSummary: each ecosystem branch, plus the no-match fallback.
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractTestSummary_Cargo_SummaryLineAndFailures()
    {
        var output = string.Join('\n', new[]
        {
            "running 3 tests",
            "test foo::bar ... FAILED",
            "test foo::baz ... ok",
            "failures:",
            "    foo::bar",
            "",
            "test result: FAILED. 2 passed; 1 failed; 0 ignored"
        });

        var result = TestCommand.ExtractTestSummary(output, "cargo test");

        Assert.Contains("[FAIL] FAILURES:", result, StringComparison.Ordinal);
        Assert.Contains("test foo::bar ... FAILED", result, StringComparison.Ordinal);
        Assert.Contains("foo::bar", result, StringComparison.Ordinal); // indented failure detail line
        Assert.Contains("SUMMARY:", result, StringComparison.Ordinal);
        Assert.Contains("test result: FAILED. 2 passed; 1 failed; 0 ignored", result, StringComparison.Ordinal);

        // The "test result:" line itself must never be misclassified as a FAILED failure line.
        Assert.DoesNotContain("  test result: FAILED. 2 passed; 1 failed; 0 ignored\n  test foo::bar", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractTestSummary_Pytest_SummaryAndFailures()
    {
        var output = string.Join('\n', new[]
        {
            "===== test session starts =====",
            "FAILED test_mod.py::test_thing - AssertionError",
            "===== 1 failed, 2 passed in 0.12s ====="
        });

        var result = TestCommand.ExtractTestSummary(output, "pytest -v");

        Assert.Contains("[FAIL] FAILURES:", result, StringComparison.Ordinal);
        Assert.Contains("FAILED test_mod.py::test_thing - AssertionError", result, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:", result, StringComparison.Ordinal);
        Assert.Contains("===== 1 failed, 2 passed in 0.12s =====", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractTestSummary_Jest_SummaryAndFailures_NpmTestAlias()
    {
        var output = string.Join('\n', new[]
        {
            "  ✕ does the thing (5 ms)",
            "Test Suites: 1 failed, 1 total",
            "Tests:       1 failed, 2 passed, 3 total"
        });

        var result = TestCommand.ExtractTestSummary(output, "npm test");

        Assert.Contains("[FAIL] FAILURES:", result, StringComparison.Ordinal);
        Assert.Contains("✕ does the thing (5 ms)", result, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:", result, StringComparison.Ordinal);
        Assert.Contains("Test Suites: 1 failed, 1 total", result, StringComparison.Ordinal);
        Assert.Contains("Tests:       1 failed, 2 passed, 3 total", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractTestSummary_Jest_YarnTestAlias_AlsoDetected()
    {
        var output = "Tests:       3 passed, 3 total";
        var result = TestCommand.ExtractTestSummary(output, "yarn test");

        Assert.Contains("SUMMARY:", result, StringComparison.Ordinal);
        Assert.Contains("Tests:       3 passed, 3 total", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractTestSummary_Go_SummaryAndFailures()
    {
        var output = string.Join('\n', new[]
        {
            "--- FAIL: TestSomething (0.00s)",
            "FAIL",
            "FAIL\tmypackage\t0.003s"
        });

        var result = TestCommand.ExtractTestSummary(output, "go test ./...");

        Assert.Contains("[FAIL] FAILURES:", result, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:", result, StringComparison.Ordinal);
        Assert.Contains("--- FAIL: TestSomething (0.00s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractTestSummary_Go_OkPrefixedLine_GoesToSummary()
    {
        var output = "ok  \tmypackage\t0.003s";
        var result = TestCommand.ExtractTestSummary(output, "go test ./...");

        Assert.Contains("SUMMARY:", result, StringComparison.Ordinal);
        Assert.Contains("ok  \tmypackage\t0.003s", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[FAIL] FAILURES:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractTestSummary_NoEcosystemMatched_FallsBackToLastFiveLines()
    {
        var output = string.Join('\n', Enumerable.Range(1, 8).Select(i => $"line{i}"));

        var result = TestCommand.ExtractTestSummary(output, "make check");

        Assert.Contains("OUTPUT (last 5 lines):", result, StringComparison.Ordinal);
        Assert.DoesNotContain("line1\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("line3\n", result, StringComparison.Ordinal);
        for (var i = 4; i <= 8; i++)
        {
            Assert.Contains($"line{i}", result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExtractTestSummary_NoEcosystemMatched_EmptyLinesInFallbackAreSkipped()
    {
        var output = "line1\n\nline3\n";
        var result = TestCommand.ExtractTestSummary(output, "unknown-runner");

        Assert.Contains("OUTPUT (last 5 lines):", result, StringComparison.Ordinal);
        Assert.Contains("line1", result, StringComparison.Ordinal);
        Assert.Contains("line3", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Failure/summary-line capping at MAX_RUNNER_FAILURES (10) / MAX_RUNNER_LINES (20).
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractTestSummary_MoreThanTenFailures_CapsAndShowsOverflowMarker()
    {
        // Uses "pytest" rather than "cargo test" here: any command containing the literal
        // substring "cargo test" ALSO contains "go test" (because "cargo" itself ends in "go",
        // immediately followed by " test") - a genuine, faithful Rust-source substring-matching
        // quirk (see ExtractTestSummary_CargoCommandString_AlsoMatchesGoEcosystem below for a
        // dedicated regression test of that quirk). Using pytest here keeps this test focused on
        // the MAX_RUNNER_FAILURES=10 cap in isolation, matched by exactly one ecosystem.
        // Includes a genuine pytest summary line (" failed" substring) so the SUMMARY section is
        // populated and the "OUTPUT (last 5 lines)" fallback does NOT also fire (which would put
        // the last few FAILED lines back into the result via a different code path, muddying this
        // cap-focused assertion).
        var lines = new List<string>();
        for (var i = 1; i <= 15; i++)
        {
            lines.Add($"FAILED test_mod.py::test_t{i} - AssertionError");
        }

        lines.Add("===== 15 failed in 0.5s =====");

        var output = string.Join('\n', lines);
        var result = TestCommand.ExtractTestSummary(output, "pytest -v");

        Assert.Contains("test_t1 - AssertionError", result, StringComparison.Ordinal);
        Assert.Contains("test_t10 - AssertionError", result, StringComparison.Ordinal);
        Assert.DoesNotContain("test_t11 - AssertionError", result, StringComparison.Ordinal);
        Assert.Contains("... +5 more failures", result, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTPUT (last 5 lines)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractTestSummary_MoreThanTwentyFailureDetailLines_CapsAndShowsOverflowMarker()
    {
        // Cargo-only feature (failure-detail lines under a "failures:" header) - at least one
        // "FAILED" line is required to open the failures block at all (see the class remarks on
        // TestCommand for why a "cargo test" command also matches "go test" - harmless here since
        // failureLines/the "+N more" marker asserted below are populated only by the cargo branch).
        // Includes a "test result:" summary line so the SUMMARY section is populated and the
        // "OUTPUT (last 5 lines)" fallback does NOT also fire (which would reintroduce the tail
        // detail lines via a different code path, muddying this cap-focused assertion).
        var lines = new List<string> { "test dummy ... FAILED", "failures:" };
        for (var i = 1; i <= 25; i++)
        {
            lines.Add($"    detail{i}");
        }

        lines.Add("test result: FAILED. 0 passed; 1 failed; 0 ignored");

        var output = string.Join('\n', lines);
        var result = TestCommand.ExtractTestSummary(output, "cargo test");

        Assert.Contains("detail1", result, StringComparison.Ordinal);
        Assert.Contains("detail20", result, StringComparison.Ordinal);
        Assert.DoesNotContain("detail21", result, StringComparison.Ordinal);
        Assert.Contains("... +5 more\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTPUT (last 5 lines)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractTestSummary_CargoCommandString_AlsoMatchesGoEcosystem()
    {
        // Documents a genuine Rust-source substring-matching quirk (not a port defect): the literal
        // word "cargo" ends in "go", so any command containing "cargo test" also contains the
        // substring "go test" - both `command.contains("cargo test")` and
        // `command.contains("go test")` are true simultaneously, and Rust's extract_test_summary
        // applies BOTH ecosystems' rules to every line (each is a plain, independent `if`, not an
        // `else if`). One "FAILED" line therefore gets pushed into the failures list twice: once by
        // the cargo rule (contains "FAILED"), once by the go rule (contains "FAIL", a substring of
        // "FAILED") - two occurrences in the [FAIL] FAILURES: block. A third occurrence comes from
        // the "OUTPUT (last 5 lines)" fallback, which also fires here since no summary line ("test
        // result:"/go's ok|FAIL|--- prefix) was present. Preserved as-is per house rule, not "fixed"
        // for this port.
        var output = "test something ... FAILED";
        var result = TestCommand.ExtractTestSummary(output, "cargo test");

        var occurrences = result.Split("test something ... FAILED").Length - 1;
        Assert.Equal(3, occurrences);
    }

    [Fact]
    public void ExtractTestSummary_TenOrFewerFailures_NoOverflowMarker()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            lines.Add($"test t{i} ... FAILED");
        }

        var output = string.Join('\n', lines);
        var result = TestCommand.ExtractTestSummary(output, "cargo test");

        Assert.DoesNotContain("more failures", result, StringComparison.Ordinal);
    }

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
