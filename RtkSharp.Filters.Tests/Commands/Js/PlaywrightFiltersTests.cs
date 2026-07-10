using System;
using RtkSharp.Filters.Commands.Js;
using RtkSharp.Parser;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PlaywrightFilters"/>, moved from
/// <c>RtkSharp.Tests.Commands.Js.PlaywrightCommandTests</c> (Task 7 of the filters-library
/// extraction) — ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>playwright_cmd.rs</c> (real JSON schema with nested <c>describe</c>-block recursion, the
/// truncated-not-rounded float duration, the minutes-supporting regex fallback, failure message
/// defaulting, and the unconditional tee-hint), plus new direct coverage of
/// <see cref="PlaywrightFilters.FormatFullResults"/>/<see cref="PlaywrightFilters.FormatDegradedResults"/>
/// — the newly-carved-out pure halves of <c>PlaywrightCommand</c>'s <c>LogFullAndFormat</c>/
/// <c>LogDegradedAndFormat</c> (Task 7's fused-method split).
/// </summary>
public sealed class PlaywrightFiltersTests
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

        var result = new PlaywrightFilters.PlaywrightParser().Parse(json);

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

        var result = new PlaywrightFilters.PlaywrightParser().Parse(json);

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

        var result = new PlaywrightFilters.PlaywrightParser().Parse(json);

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

        var result = new PlaywrightFilters.PlaywrightParser().Parse(json);

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

        var result = new PlaywrightFilters.PlaywrightParser().Parse(text);

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

        var result = new PlaywrightFilters.PlaywrightParser().Parse(text);

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

        var result = new PlaywrightFilters.PlaywrightParser().Parse(invalid);

        Assert.Equal(3, result.Tier);
        Assert.False(result.IsOk);
    }

    // -----------------------------------------------------------------------
    // RenderOutput - the unconditional tee-hint, discriminated against VitestFilters' conditional
    // (truncated-vs-not) hint-selection logic.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Proves the tee-hint is appended UNCONDITIONALLY: a single hint strategy is always invoked,
    /// regardless of any truncation state - there is no <c>FormattedTestOutput.IsTruncated</c>-style
    /// branch here the way <see cref="VitestFilters.RenderTestOutputWithHints"/> has (which picks
    /// between a "force" hint and a "conditional" tee hint based on whether its own passthrough
    /// output was truncated). This is the single most important distinction from vitest's rendering.
    /// </summary>
    [Fact]
    public void RenderOutput_AlwaysInvokesTeeHintStrategy_RegardlessOfContent()
    {
        var invoked = false;

        var rendered = PlaywrightFilters.RenderOutput(
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
        var rendered = PlaywrightFilters.RenderOutput(
            "PASS (1) FAIL (0)",
            "short",
            exitCode: 0,
            teeHint: (_, _, _) => null);

        Assert.Equal("PASS (1) FAIL (0)", rendered);
    }

    // -----------------------------------------------------------------------
    // FormatFullResults/FormatDegradedResults - the newly-carved-out pure halves of
    // PlaywrightCommand's LogFullAndFormat/LogDegradedAndFormat (Task 7 fused-method split). Direct
    // coverage of these pure boundaries, complementing the existing end-to-end
    // PlaywrightTest_DefaultInvocation_ForcesJsonReporter_ParsesAndPropagatesExitCode test that
    // remains in RtkSharp.Tests (which exercises the Console-diagnostic half + full dispatch).
    // -----------------------------------------------------------------------

    [Fact]
    public void FormatFullResults_DelegatesToSharedTokenFormatter()
    {
        var data = new TestResult { Total = 2, Passed = 2, Failed = 0, Skipped = 0, DurationMs = 500, Failures = [] };

        var result = PlaywrightFilters.FormatFullResults(data, FormatMode.Compact);

        Assert.Equal(((ITokenFormatter)data).Format(FormatMode.Compact), result);
        Assert.Contains("PASS (2) FAIL (0)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDegradedResults_DelegatesToSharedTokenFormatter()
    {
        var data = new TestResult { Total = 15, Passed = 12, Failed = 0, Skipped = 3, DurationMs = 7300, Failures = [] };

        var result = PlaywrightFilters.FormatDegradedResults(data, FormatMode.Compact);

        Assert.Equal(((ITokenFormatter)data).Format(FormatMode.Compact), result);
        Assert.Contains("PASS (12) FAIL (0)", result, StringComparison.Ordinal);
    }
}
