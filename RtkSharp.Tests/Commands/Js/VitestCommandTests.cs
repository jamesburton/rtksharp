using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Js;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="VitestCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>vitest_cmd.rs</c> (shared JSON schema parsing for both vitest's <c>--reporter=json</c> and
/// jest's <c>--json</c> shapes, banner-stripped JSON extraction, regex-fallback summary/failure
/// parsing, effective-args construction, the vitest-only reporter-passthrough escape hatch, and the
/// tee-hint rendering split on truncation) plus original coverage proving the single most important
/// discriminating fact in this port: jest has NO equivalent escape hatch.
/// </summary>
public sealed class VitestCommandTests
{
    // -----------------------------------------------------------------------
    // VitestParser now lives in RtkSharp.Filters.Commands.Js.VitestFilters (Task 7 of the
    // filters-library extraction) - see VitestFiltersTests in RtkSharp.Filters.Tests.
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // build_vitest_effective_args - ports vitest_cmd.rs's
    // test_vitest_effective_args_inject_json_reporter_by_default / _preserve_explicit_reporter_*
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildVitestEffectiveArgs_NoExplicitReporter_InjectsJsonReporterAndStripsRunWatch()
    {
        var effective = VitestCommand.BuildVitestEffectiveArgs(["run", "constants.test.ts", "--watch"]);

        Assert.False(effective.Passthrough);
        Assert.Equal(["run", "--reporter=json", "constants.test.ts"], effective.Args);
    }

    [Fact]
    public void BuildVitestEffectiveArgs_ExplicitReporterEqualsForm_TriggersPassthroughUnmodified()
    {
        var effective = VitestCommand.BuildVitestEffectiveArgs(["constants.test.ts", "--reporter=verbose"]);

        Assert.True(effective.Passthrough);
        Assert.Equal(["run", "constants.test.ts", "--reporter=verbose"], effective.Args);
    }

    [Fact]
    public void BuildVitestEffectiveArgs_ExplicitReporterValueForm_TriggersPassthroughUnmodified()
    {
        var effective = VitestCommand.BuildVitestEffectiveArgs(["run", "constants.test.ts", "--reporter", "verbose"]);

        Assert.True(effective.Passthrough);
        Assert.Equal(["run", "constants.test.ts", "--reporter", "verbose"], effective.Args);
    }

    // -----------------------------------------------------------------------
    // format_test_output - ports vitest_cmd.rs's
    // test_vitest_explicit_reporter_keeps_verbose_output
    // -----------------------------------------------------------------------

    [Fact]
    public void FormatTestOutput_VitestExplicitReporterPassthrough_KeepsVerboseOutputUnfiltered()
    {
        const string output = """

         ✓ constants/publicPaths.test.ts > public paths > keeps docs path
         ✓ constants/publicPaths.test.ts > public paths > keeps app path

         Test Files  1 passed (1)
              Tests  2 passed (2)
           Duration  450ms
        """;

        var filtered = VitestCommand.FormatTestOutput("vitest", output, output, passthroughRequested: true, verbose: 0);

        Assert.Contains("keeps docs path", filtered.Text, StringComparison.Ordinal);
        Assert.Contains("keeps app path", filtered.Text, StringComparison.Ordinal);
        Assert.Contains("Tests  2 passed", filtered.Text, StringComparison.Ordinal);
        Assert.False(filtered.IsTruncated);
    }

    // -----------------------------------------------------------------------
    // RenderTestOutputWithHints now lives in RtkSharp.Filters.Commands.Js.VitestFilters (Task 7 of
    // the filters-library extraction) - see VitestFiltersTests in RtkSharp.Filters.Tests.
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // THE single most important discriminating test: jest has NO reporter-passthrough escape hatch.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Proves the vitest-only reporter-passthrough escape hatch does NOT exist for jest: an explicit
    /// <c>--reporter=X</c> flag passed to <c>rtk jest</c> must NEVER set <c>passthroughRequested</c>,
    /// and must be silently dropped from the args sent to the child process (not honored, not passed
    /// through) — jest always forces <c>--no-watch --json</c> and always goes through the shared
    /// parser/formatter. If jest were ever accidentally given vitest's escape hatch, this test fails.
    /// </summary>
    [Fact]
    public async Task Jest_ExplicitReporterFlag_NeverTriggersPassthrough_AndIsDroppedFromChildArgs()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        const string json = /*lang=json,strict*/ """
        {"numTotalTests": 1, "numPassedTests": 1, "numFailedTests": 0, "numPendingTests": 0, "testResults": []}
        """;

        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        // The user tries to pass an explicit reporter to jest, mirroring vitest's own escape-hatch
        // trigger exactly - it must have zero effect on jest. Uses the single-token `--reporter=X`
        // form deliberately: Rust's own jest arg-filtering loop (vitest_cmd.rs:234-245) is per-token
        // only (`arg.starts_with("--reporter")` on each token in isolation, never consuming a
        // following separate value token) - a two-token `--reporter verbose` would genuinely leave
        // "verbose" in Rust's own child args too, since the bare token "verbose" itself doesn't start
        // with "--reporter"/"--json"/"--watch" and isn't "run". The single-token form is what
        // unambiguously proves the drop-not-honor behavior without exercising that separate, correctly
        // preserved two-token quirk.
        var exitCode = await VitestCommand.RunTestAsync(TestFramework.Jest, ["--reporter=verbose"], verbose: 0, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.LastRequest);

        // --no-watch and --json are always forced; --reporter=verbose must be ABSENT from the child's
        // argument vector (dropped, not honored) - this is what "no passthrough escape hatch" means at
        // the wire level.
        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("--no-watch", childArgs);
        Assert.Contains("--json", childArgs);
        Assert.DoesNotContain("--reporter=verbose", childArgs);
        Assert.DoesNotContain("--reporter", childArgs);

        // And the rendered output went through the parser/formatter (PASS/FAIL summary), NOT raw JSON
        // passthrough - if passthrough had incorrectly triggered, the raw JSON blob would appear
        // verbatim in stdout instead of the formatted "PASS (1) FAIL (0)" summary.
        var printed = stdout.ToString();
        Assert.Contains("PASS (1) FAIL (0)", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("numTotalTests", printed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror-image control case: the SAME explicit <c>--reporter=X</c> flag passed to <c>rtk
    /// vitest</c> DOES trigger passthrough (raw output, unfiltered), proving the escape hatch is real
    /// and vitest-specific rather than universally absent.
    /// </summary>
    [Fact]
    public async Task Vitest_ExplicitReporterFlag_TriggersPassthrough_RawOutputUnfiltered()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        const string verboseReporterOutput = """
         ✓ constants/publicPaths.test.ts > public paths > keeps docs path

         Test Files  1 passed (1)
              Tests  1 passed (1)
        """;

        var fake = new FakeProcessExecutor(new ExecutionResult(verboseReporterOutput, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await VitestCommand.RunTestAsync(TestFramework.Vitest, ["--reporter=verbose"], verbose: 0, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.LastRequest);

        // Vitest's own effective-args builder passes the explicit reporter straight through, unlike jest.
        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("--reporter=verbose", childArgs);

        var printed = stdout.ToString();
        Assert.Contains("keeps docs path", printed, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // End-to-end: default vitest invocation forces --reporter=json and parses through the shared formatter.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Vitest_DefaultInvocation_ForcesJsonReporter_ParsesAndFormats()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        const string json = /*lang=json,strict*/ """
        {"numTotalTests": 3, "numPassedTests": 2, "numFailedTests": 1, "numPendingTests": 0, "testResults": []}
        """;

        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 1, TimeSpan.Zero, true, null, false));

        var exitCode = await VitestCommand.RunTestAsync(TestFramework.Vitest, [], verbose: 0, fake);

        // Non-zero child exit code propagates directly.
        Assert.Equal(1, exitCode);

        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("run", childArgs);
        Assert.Contains("--reporter=json", childArgs);

        Assert.Contains("PASS (2) FAIL (1)", stdout.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Test doubles (mirrors PnpmCommandTests/NpmCommandTests conventions exactly).
    // -----------------------------------------------------------------------

    private sealed class FakeProcessExecutor(ExecutionResult result) : IProcessExecutor
    {
        public ExecutionRequest? LastRequest { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Captures <see cref="Console.Out"/> output for the lifetime of the instance.</summary>
    private sealed class ConsoleOutCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Out;
        private readonly StringWriter _capture = new();

        public ConsoleOutCapture() => Console.SetOut(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetOut(_original);
    }

    /// <summary>Points <c>RTK_DB_PATH</c> at a fresh throwaway SQLite file for the lifetime of the instance.</summary>
    private sealed class TempTrackingDb : IDisposable
    {
        private readonly string? _previousDbPath = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "rtksharp-vitest-cmd-tests-" + Guid.NewGuid().ToString("N"));

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
