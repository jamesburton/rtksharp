using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Js;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Parser;
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
    // VitestParser - ports vitest_cmd.rs's test_vitest_parser_json
    // -----------------------------------------------------------------------

    [Fact]
    public void VitestParser_JsonTier1_ParsesTotals()
    {
        const string json = /*lang=json,strict*/ """
        {
            "numTotalTests": 13,
            "numPassedTests": 13,
            "numFailedTests": 0,
            "numPendingTests": 0,
            "testResults": [],
            "startTime": 1000
        }
        """;

        var result = new VitestCommand.VitestParser().Parse(json);

        Assert.Equal(1, result.Tier);
        Assert.True(result.IsOk);
        var data = result.Unwrap();
        Assert.Equal(13, data.Total);
        Assert.Equal(13, data.Passed);
        Assert.Equal(0, data.Failed);
        Assert.Null(data.DurationMs);
    }

    /// <summary>
    /// Confirms the SAME parser correctly handles jest's <c>--json</c> output shape too - jest's JSON
    /// is structurally identical to vitest's (no framework-specific fields in
    /// <c>VitestJsonOutput</c>), which is the structural fact that justifies one shared parser for
    /// both frameworks.
    /// </summary>
    [Fact]
    public void VitestParser_JestJsonShape_ParsesIdentically()
    {
        const string jestJson = /*lang=json,strict*/ """
        {
            "numTotalTests": 5,
            "numPassedTests": 3,
            "numFailedTests": 2,
            "numPendingTests": 0,
            "testResults": [
                {
                    "name": "src/math.test.ts",
                    "assertionResults": [
                        { "fullName": "adds numbers", "status": "passed", "failureMessages": [] },
                        { "fullName": "subtracts numbers", "status": "failed", "failureMessages": ["Expected 2 to be 3"] }
                    ]
                }
            ]
        }
        """;

        var result = new VitestCommand.VitestParser().Parse(jestJson);

        Assert.Equal(1, result.Tier);
        var data = result.Unwrap();
        Assert.Equal(5, data.Total);
        Assert.Equal(3, data.Passed);
        Assert.Equal(2, data.Failed);
        var failure = Assert.Single(data.Failures);
        Assert.Equal("subtracts numbers", failure.TestName);
        Assert.Equal("src/math.test.ts", failure.FilePath);
        Assert.Equal("Expected 2 to be 3", failure.ErrorMessage);
    }

    // -----------------------------------------------------------------------
    // VitestParser - ports vitest_cmd.rs's test_vitest_parser_with_pnpm_prefix / _with_dotenv_prefix
    // -----------------------------------------------------------------------

    [Fact]
    public void VitestParser_PnpmBannerPrefix_StillParsesAsTier1()
    {
        const string input = """

        Scope: all 6 workspace projects
         WARN  deprecated inflight@1.0.6: This module is not supported

        {"numTotalTests": 13, "numPassedTests": 13, "numFailedTests": 0, "numPendingTests": 0, "testResults": [], "startTime": 1000}

        """;

        var result = new VitestCommand.VitestParser().Parse(input);

        Assert.Equal(1, result.Tier);
        var data = result.Unwrap();
        Assert.Equal(13, data.Total);
        Assert.Equal(13, data.Passed);
        Assert.Equal(0, data.Failed);
    }

    [Fact]
    public void VitestParser_DotenvBannerPrefix_StillParsesAsTier1()
    {
        const string input = """
        [dotenv] Loading environment variables from .env
        [dotenv] Injected 5 variables

        {"numTotalTests": 5, "numPassedTests": 4, "numFailedTests": 1, "numPendingTests": 0, "testResults": [], "startTime": 2000}

        """;

        var result = new VitestCommand.VitestParser().Parse(input);

        Assert.Equal(1, result.Tier);
        var data = result.Unwrap();
        Assert.Equal(5, data.Total);
        Assert.Equal(4, data.Passed);
        Assert.Equal(1, data.Failed);
        Assert.Null(data.DurationMs);
    }

    [Fact]
    public void VitestParser_NestedJsonWithAssertionResults_ParsesAsTier1()
    {
        const string input = """
        prefix text
        {"numTotalTests": 2, "numPassedTests": 2, "numFailedTests": 0, "numPendingTests": 0, "testResults": [{"name": "test.js", "assertionResults": [{"fullName": "nested test", "status": "passed", "failureMessages": []}]}], "startTime": 1000}

        """;

        var result = new VitestCommand.VitestParser().Parse(input);

        Assert.Equal(1, result.Tier);
        var data = result.Unwrap();
        Assert.Equal(2, data.Total);
        Assert.Equal(2, data.Passed);
    }

    // -----------------------------------------------------------------------
    // VitestParser - ports vitest_cmd.rs's test_vitest_parser_regex_fallback / _passthrough
    // -----------------------------------------------------------------------

    [Fact]
    public void VitestParser_RegexFallback_ParsesSummaryLines()
    {
        const string text = """

         Test Files  2 passed (2)
              Tests  13 passed (13)
           Duration  450ms
        """;

        var result = new VitestCommand.VitestParser().Parse(text);

        Assert.Equal(2, result.Tier);
        var data = result.Unwrap();
        Assert.Equal(13, data.Passed);
        Assert.Equal(0, data.Failed);
        Assert.Equal(450, data.DurationMs);
    }

    [Fact]
    public void VitestParser_InvalidInput_FallsBackToPassthrough()
    {
        const string invalid = "random output with no structure";

        var result = new VitestCommand.VitestParser().Parse(invalid);

        Assert.Equal(3, result.Tier);
        Assert.False(result.IsOk);
    }

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
    // render_test_output_with_hints - ports vitest_cmd.rs's
    // test_vitest_explicit_reporter_truncated_output_adds_recovery_hint
    // -----------------------------------------------------------------------

    [Fact]
    public void RenderTestOutputWithHints_TruncatedPassthrough_UsesForceHintNotTeeHint()
    {
        var output =
            string.Concat(Enumerable.Repeat(" ✓ constants/publicPaths.test.ts > public paths > keeps verbose case\n", 80))
            + " Test Files  1 passed (1)\n      Tests  80 passed (80)\n";

        var filtered = VitestCommand.FormatPassthroughOutputWithLimit(output, 200);

        var rendered = VitestCommand.RenderTestOutputWithHints(
            filtered,
            output,
            "vitest_run",
            exitCode: 0,
            forceHint: (raw, label) =>
            {
                Assert.Equal(output, raw);
                Assert.Equal("vitest_run", label);
                return "[full output: /tmp/vitest_run.log]";
            },
            teeHint: (_, _, _) => "[full output: wrong-path.log]");

        Assert.True(filtered.IsTruncated);
        Assert.Contains(OutputParserSupport.PassthroughMarker + " Output truncated", rendered, StringComparison.Ordinal);
        Assert.Contains("[full output: /tmp/vitest_run.log]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-path.log", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTestOutputWithHints_NonTruncated_UsesTeeHintNotForceHint()
    {
        var filtered = VitestCommand.FormatPassthroughOutputWithLimit("short output", 2000);

        var rendered = VitestCommand.RenderTestOutputWithHints(
            filtered,
            "short output",
            "vitest_run",
            exitCode: 1,
            forceHint: (_, _) => "[force hint - should not be used]",
            teeHint: (raw, label, code) =>
            {
                Assert.Equal("short output", raw);
                Assert.Equal("vitest_run", label);
                Assert.Equal(1, code);
                return "[full output: /tmp/vitest_run.log]";
            });

        Assert.False(filtered.IsTruncated);
        Assert.Contains("[full output: /tmp/vitest_run.log]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("force hint", rendered, StringComparison.Ordinal);
    }

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
