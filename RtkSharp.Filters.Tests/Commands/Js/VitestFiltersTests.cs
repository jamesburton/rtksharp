using System;
using System.Linq;
using RtkSharp.Filters.Commands.Js;
using RtkSharp.Parser;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="VitestFilters"/>, moved from
/// <c>RtkSharp.Tests.Commands.Js.VitestCommandTests</c> (Task 7 of the filters-library extraction) —
/// ported directly from Rust's own <c>#[cfg(test)]</c> module in <c>vitest_cmd.rs</c> (shared JSON
/// schema parsing for both vitest's <c>--reporter=json</c> and jest's <c>--json</c> shapes,
/// banner-stripped JSON extraction, regex-fallback summary/failure parsing, and the tee-hint
/// rendering split on truncation), plus new direct coverage of <see cref="VitestFilters.FormatVitestSummary"/>
/// — the newly-carved-out pure half of <c>VitestCommand</c>'s <c>LogFullAndFormat</c>/
/// <c>LogDegradedAndFormat</c> (Task 7's fused-method split).
/// </summary>
public sealed class VitestFiltersTests
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

        var result = new VitestFilters.VitestParser().Parse(json);

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

        var result = new VitestFilters.VitestParser().Parse(jestJson);

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

        var result = new VitestFilters.VitestParser().Parse(input);

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

        var result = new VitestFilters.VitestParser().Parse(input);

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

        var result = new VitestFilters.VitestParser().Parse(input);

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

        var result = new VitestFilters.VitestParser().Parse(text);

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

        var result = new VitestFilters.VitestParser().Parse(invalid);

        Assert.Equal(3, result.Tier);
        Assert.False(result.IsOk);
    }

    // -----------------------------------------------------------------------
    // FormatVitestSummary - the newly-carved-out pure half of VitestCommand's
    // LogFullAndFormat/LogDegradedAndFormat (Task 7 fused-method split). Both original call sites
    // reduced to the identical formatting expression once the diagnostic half was split out - this
    // is direct coverage of that shared pure boundary, complementing the existing end-to-end
    // Vitest_DefaultInvocation_ForcesJsonReporter_ParsesAndFormats test that remains in
    // RtkSharp.Tests (which exercises the Console-diagnostic half + full dispatch).
    // -----------------------------------------------------------------------

    [Fact]
    public void FormatVitestSummary_DelegatesToSharedTokenFormatter()
    {
        var data = new TestResult
        {
            Total = 3,
            Passed = 2,
            Failed = 1,
            Skipped = 0,
            DurationMs = 100,
            Failures = [],
        };

        var result = VitestFilters.FormatVitestSummary(data, FormatMode.Compact);

        Assert.Equal(((ITokenFormatter)data).Format(FormatMode.Compact), result.Text);
        Assert.False(result.IsTruncated);
        Assert.Contains("PASS (2) FAIL (1)", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatVitestSummary_AlwaysReturnsUntruncatedResult()
    {
        var data = new TestResult { Total = 1, Passed = 1, Failed = 0, Skipped = 0, DurationMs = null, Failures = [] };

        var result = VitestFilters.FormatVitestSummary(data, FormatMode.Verbose);

        Assert.False(result.IsTruncated);
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

        var filtered = VitestFilters.FormatPassthroughOutputWithLimit(output, 200);

        var rendered = VitestFilters.RenderTestOutputWithHints(
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
        var filtered = VitestFilters.FormatPassthroughOutputWithLimit("short output", 2000);

        var rendered = VitestFilters.RenderTestOutputWithHints(
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
}
