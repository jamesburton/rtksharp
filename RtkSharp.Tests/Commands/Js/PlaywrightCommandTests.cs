using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Js;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PlaywrightCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c>
/// module in <c>playwright_cmd.rs</c> (real JSON schema with nested <c>describe</c>-block recursion,
/// the truncated-not-rounded float duration, the minutes-supporting regex fallback, and failure
/// message defaulting) plus original coverage proving the two structural distinctions from
/// <see cref="VitestCommand"/>: no reporter-passthrough escape hatch, and an unconditional tee-hint.
/// </summary>
public sealed class PlaywrightCommandTests
{
    // -----------------------------------------------------------------------
    // PlaywrightParser - ports playwright_cmd.rs's test_playwright_parser_json
    // (nested describe-block recursion: 2 levels of suites)
    // -----------------------------------------------------------------------

    [Fact]
    public void PlaywrightParser_JsonTier1_NestedDescribeBlockRecursion_ParsesThroughTwoLevels()
    {
        // "auth" (level 1, no specs of its own) -> "login.spec.ts" (level 2, has the actual spec).
        // Proves collect_test_results recurses into suite.suites rather than only reading top-level specs.
        const string json = /*lang=json,strict*/ """
        {
            "config": {},
            "stats": {
                "startTime": "2026-01-01T00:00:00.000Z",
                "expected": 1,
                "unexpected": 0,
                "skipped": 0,
                "flaky": 0,
                "duration": 7300.5
            },
            "suites": [
                {
                    "title": "auth",
                    "specs": [],
                    "suites": [
                        {
                            "title": "login.spec.ts",
                            "specs": [
                                {
                                    "title": "should login",
                                    "ok": true,
                                    "tests": [
                                        {
                                            "status": "expected",
                                            "results": [{"status": "passed", "errors": [], "duration": 2300}]
                                        }
                                    ]
                                }
                            ],
                            "suites": []
                        }
                    ]
                }
            ],
            "errors": []
        }
        """;

        var result = new PlaywrightCommand.PlaywrightParser().Parse(json);

        Assert.Equal(1, result.Tier);
        Assert.True(result.IsOk);

        var data = result.Unwrap();
        Assert.Equal(1, data.Total);
        Assert.Equal(1, data.Passed);
        Assert.Equal(0, data.Failed);
        Assert.Equal(7300, data.DurationMs);
    }

    // -----------------------------------------------------------------------
    // PlaywrightParser - ports playwright_cmd.rs's test_playwright_parser_json_float_duration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Exact reproduction of Rust's own float-duration test: real Playwright output uses a float
    /// duration (e.g. <c>3519.7039999999997</c>), which must TRUNCATE (not round) to <c>3519</c> ms -
    /// a naive <c>Math.Round</c> would incorrectly yield <c>3520</c>.
    /// </summary>
    [Fact]
    public void PlaywrightParser_JsonTier1_FloatDuration_TruncatesNotRounds()
    {
        const string json = /*lang=json,strict*/ """
        {
            "stats": {
                "startTime": "2026-02-18T10:17:53.187Z",
                "expected": 4,
                "unexpected": 0,
                "skipped": 0,
                "flaky": 0,
                "duration": 3519.7039999999997
            },
            "suites": [],
            "errors": []
        }
        """;

        var result = new PlaywrightCommand.PlaywrightParser().Parse(json);

        Assert.Equal(1, result.Tier);
        Assert.True(result.IsOk);

        var data = result.Unwrap();
        Assert.Equal(4, data.Passed);
        Assert.Equal(3519, data.DurationMs);
        Assert.NotEqual(3520, data.DurationMs);
    }

    // -----------------------------------------------------------------------
    // PlaywrightParser - ports playwright_cmd.rs's test_playwright_parser_json_with_failure
    // -----------------------------------------------------------------------

    [Fact]
    public void PlaywrightParser_JsonTier1_WithFailure_ExtractsFirstErrorMessage()
    {
        const string json = /*lang=json,strict*/ """
        {
            "stats": {
                "expected": 0,
                "unexpected": 1,
                "skipped": 0,
                "duration": 1500.0
            },
            "suites": [
                {
                    "title": "my.spec.ts",
                    "specs": [
                        {
                            "title": "should work",
                            "ok": false,
                            "tests": [
                                {
                                    "status": "unexpected",
                                    "results": [
                                        {
                                            "status": "failed",
                                            "errors": [{"message": "Expected true to be false"}],
                                            "duration": 500
                                        }
                                    ]
                                }
                            ]
                        }
                    ],
                    "suites": []
                }
            ],
            "errors": []
        }
        """;

        var result = new PlaywrightCommand.PlaywrightParser().Parse(json);

        Assert.Equal(1, result.Tier);
        Assert.True(result.IsOk);

        var data = result.Unwrap();
        Assert.Equal(1, data.Failed);
        var failure = Assert.Single(data.Failures);
        Assert.Equal("should work", failure.TestName);
        Assert.Equal("Expected true to be false", failure.ErrorMessage);
    }

    /// <summary>
    /// Ports Rust's own defaulting behavior: when a failed spec has no <c>errors</c> array entries
    /// anywhere in its results, the error message defaults to <c>"Test failed"</c> rather than being
    /// empty or throwing.
    /// </summary>
    [Fact]
    public void PlaywrightParser_JsonTier1_FailureWithNoErrorEntries_DefaultsToTestFailed()
    {
        const string json = /*lang=json,strict*/ """
        {
            "stats": {
                "expected": 0,
                "unexpected": 1,
                "skipped": 0,
                "duration": 100.0
            },
            "suites": [
                {
                    "title": "no-error.spec.ts",
                    "specs": [
                        {
                            "title": "times out silently",
                            "ok": false,
                            "tests": [
                                {
                                    "status": "unexpected",
                                    "results": [
                                        { "status": "timedOut", "errors": [] }
                                    ]
                                }
                            ]
                        }
                    ],
                    "suites": []
                }
            ],
            "errors": []
        }
        """;

        var result = new PlaywrightCommand.PlaywrightParser().Parse(json);

        var data = result.Unwrap();
        var failure = Assert.Single(data.Failures);
        Assert.Equal("Test failed", failure.ErrorMessage);
    }

    // -----------------------------------------------------------------------
    // PlaywrightParser - ports playwright_cmd.rs's test_playwright_parser_regex_fallback
    // -----------------------------------------------------------------------

    [Fact]
    public void PlaywrightParser_RegexFallback_ParsesSummaryCounts()
    {
        const string text = "3 passed (7.3s)";

        var result = new PlaywrightCommand.PlaywrightParser().Parse(text);

        Assert.Equal(2, result.Tier);
        Assert.True(result.IsOk);

        var data = result.Unwrap();
        Assert.Equal(3, data.Passed);
        Assert.Equal(0, data.Failed);
        Assert.Equal(7300, data.DurationMs);
    }

    /// <summary>
    /// Confirms the minutes unit (<c>m</c>) is supported by playwright's duration regex - unlike
    /// vitest/tsc, which only recognize <c>ms</c>/<c>s</c>.
    /// </summary>
    [Fact]
    public void PlaywrightParser_RegexFallback_MinutesDurationUnit_ConvertsToMilliseconds()
    {
        const string text = "12 passed (1.5m)";

        var result = new PlaywrightCommand.PlaywrightParser().Parse(text);

        Assert.Equal(2, result.Tier);
        var data = result.Unwrap();
        Assert.Equal(12, data.Passed);
        Assert.Equal(90_000, data.DurationMs); // 1.5 * 60_000
    }

    // -----------------------------------------------------------------------
    // PlaywrightParser - ports playwright_cmd.rs's test_playwright_parser_passthrough
    // -----------------------------------------------------------------------

    [Fact]
    public void PlaywrightParser_InvalidInput_FallsBackToPassthrough()
    {
        const string invalid = "random output";

        var result = new PlaywrightCommand.PlaywrightParser().Parse(invalid);

        Assert.Equal(3, result.Tier);
        Assert.False(result.IsOk);
    }

    // -----------------------------------------------------------------------
    // RenderOutput - the unconditional tee-hint, discriminated against VitestCommand's conditional
    // (truncated-vs-not) hint-selection logic.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Proves the tee-hint is appended UNCONDITIONALLY: a single hint strategy is always invoked,
    /// regardless of any truncation state - there is no <c>FormattedTestOutput.IsTruncated</c>-style
    /// branch here the way <see cref="VitestCommand.RenderTestOutputWithHints"/> has (which picks
    /// between a "force" hint and a "conditional" tee hint based on whether its own passthrough
    /// output was truncated). This is the single most important distinction from vitest's rendering.
    /// </summary>
    [Fact]
    public void RenderOutput_AlwaysInvokesTeeHintStrategy_RegardlessOfContent()
    {
        var invoked = false;

        var rendered = PlaywrightCommand.RenderOutput(
            "PASS (1) FAIL (0)",
            "raw output, not truncated, short",
            exitCode: 0,
            teeHint: (raw, label, code) =>
            {
                invoked = true;
                Assert.Equal("raw output, not truncated, short", raw);
                Assert.Equal("playwright", label);
                Assert.Equal(0, code);
                return "[full output: /tmp/playwright.log]";
            });

        Assert.True(invoked);
        Assert.Contains("[full output: /tmp/playwright.log]", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hint strategy returning null (e.g. the real <see cref="RtkSharp.Core.Tee.TeeAndHint"/>
    /// declining to tee because output is too small or teeing is disabled) still results in a clean,
    /// unmodified render - the strategy is called every time, but its own internal gating (size
    /// threshold, exit code mode, <c>RTK_TEE=0</c>) is untouched by this call site.
    /// </summary>
    [Fact]
    public void RenderOutput_HintStrategyDeclines_ReturnsFilteredTextUnmodified()
    {
        var rendered = PlaywrightCommand.RenderOutput(
            "PASS (1) FAIL (0)",
            "short",
            exitCode: 0,
            teeHint: (_, _, _) => null);

        Assert.Equal("PASS (1) FAIL (0)", rendered);
    }

    // -----------------------------------------------------------------------
    // End-to-end: no reporter-passthrough escape hatch for `playwright test`.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Proves playwright has NO reporter-passthrough escape hatch (unlike vitest): an explicit
    /// <c>--reporter=X</c> flag passed to <c>rtk playwright test</c> is always stripped/overridden,
    /// never honored - the child always receives <c>--reporter=json</c> and never the user's flag.
    /// </summary>
    [Fact]
    public async Task PlaywrightTest_ExplicitReporterFlag_AlwaysStrippedAndOverridden()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        const string json = /*lang=json,strict*/ """
        {"stats": {"expected": 1, "unexpected": 0, "skipped": 0, "duration": 10.0}, "suites": [], "errors": []}
        """;

        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PlaywrightCommand.RunAsync(["test", "--reporter=list"], verbose: 0, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.LastRequest);

        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("--reporter=json", childArgs);
        Assert.DoesNotContain("--reporter=list", childArgs);

        var printed = stdout.ToString();
        Assert.Contains("PASS (1) FAIL (0)", printed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-<c>test</c> subcommands (e.g. <c>playwright install</c>, <c>playwright show-report</c>)
    /// pass all args through untouched - no <c>--reporter=json</c> injection, no stripping.
    /// </summary>
    [Fact]
    public async Task PlaywrightNonTestSubcommand_PassesArgsThroughUnmodified()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult("Downloading browsers...", string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PlaywrightCommand.RunAsync(["install", "--with-deps", "chromium"], verbose: 0, fake);

        Assert.Equal(0, exitCode);
        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("install", childArgs);
        Assert.Contains("--with-deps", childArgs);
        Assert.Contains("chromium", childArgs);
        Assert.DoesNotContain("--reporter=json", childArgs);
    }

    /// <summary>
    /// End-to-end: a default <c>playwright test</c> invocation forces <c>--reporter=json</c>,
    /// propagates a non-zero exit code, and formats through the shared <c>TestResult</c> formatter.
    /// </summary>
    [Fact]
    public async Task PlaywrightTest_DefaultInvocation_ForcesJsonReporter_ParsesAndPropagatesExitCode()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        const string json = /*lang=json,strict*/ """
        {"stats": {"expected": 2, "unexpected": 1, "skipped": 0, "duration": 500.0}, "suites": [], "errors": []}
        """;

        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 1, TimeSpan.Zero, true, null, false));

        var exitCode = await PlaywrightCommand.RunAsync(["test"], verbose: 0, fake);

        Assert.Equal(1, exitCode);

        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("test", childArgs);
        Assert.Contains("--reporter=json", childArgs);

        Assert.Contains("PASS (2) FAIL (1)", stdout.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Test doubles (mirrors VitestCommandTests/PnpmCommandTests conventions exactly).
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
            Path.Combine(Path.GetTempPath(), "rtksharp-playwright-cmd-tests-" + Guid.NewGuid().ToString("N"));

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
