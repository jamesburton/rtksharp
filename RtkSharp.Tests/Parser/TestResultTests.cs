using RtkSharp.Parser;
using Xunit;

namespace RtkSharp.Tests.Parser;

/// <summary>
/// Tests for <see cref="TestResult"/>'s <see cref="ITokenFormatter"/> rendering. Ports Rust's
/// <c>test_dependency_state_plain_listing_shows_packages</c> (no — see
/// <c>DependencyStateTests</c>), <c>test_compact_shows_full_error_message</c>,
/// <c>test_compact_summary_line_is_concise</c>, <c>test_compact_all_pass_is_one_line</c>, and
/// <c>test_compact_single_line_error_no_trailing_noise</c> (<c>src/parser/formatter.rs:262-320</c>),
/// plus new coverage for <see cref="FormatMode.Verbose"/>/<see cref="FormatMode.Ultra"/> and
/// <see cref="FormatModeExtensions.FromVerbosity"/> per the task brief.
/// </summary>
public sealed class TestResultTests
{
    private static TestFailure MakeFailure(string name, string error) => new()
    {
        TestName = name,
        FilePath = "tests/e2e.spec.ts",
        ErrorMessage = error,
    };

    private static TestResult MakeResult(int passed, IReadOnlyList<TestFailure> failures) => new()
    {
        Total = passed + failures.Count,
        Passed = passed,
        Failed = failures.Count,
        Skipped = 0,
        DurationMs = 1500,
        Failures = failures,
    };

    [Fact]
    public void FormatCompact_PreservesFullErrorMessage_NotClippedToTwoLines()
    {
        const string error = "Error: expect(locator).toHaveText(expected)\n\nExpected: 'Submit'\nReceived: 'Loading'\n\nCall log:\n  - waiting for getByRole('button', { name: 'Submit' })";
        var result = MakeResult(5, [MakeFailure("should click submit", error)]);

        var output = result.FormatCompact();

        Assert.Contains("Expected: 'Submit'", output);
        Assert.Contains("Received: 'Loading'", output);
        Assert.Contains("Call log:", output);
    }

    [Fact]
    public void FormatCompact_SummaryLineIsConcise()
    {
        var result = MakeResult(28, [MakeFailure("test", "some error")]);
        var firstLine = result.FormatCompact().Split('\n')[0];
        Assert.Contains("28", firstLine);
        Assert.Contains("1", firstLine);
    }

    [Fact]
    public void FormatCompact_AllPass_IsCompact()
    {
        var result = MakeResult(10, []);
        var lineCount = result.FormatCompact().Split('\n').Length;
        Assert.True(lineCount <= 3, $"Expected <= 3 lines, got {lineCount}");
    }

    [Fact]
    public void FormatCompact_SingleLineError_NoTrailingNoise()
    {
        var result = MakeResult(0, [MakeFailure("should work", "Timeout exceeded")]);
        Assert.Contains("Timeout exceeded", result.FormatCompact());
    }

    [Fact]
    public void FormatCompact_SkippedTests_AreSurfaced()
    {
        var result = new TestResult { Total = 10, Passed = 8, Failed = 0, Skipped = 2 };
        Assert.Contains("skipped (2)", result.FormatCompact());
    }

    [Fact]
    public void FormatCompact_MoreThanFiveFailures_ShowsOverflowMarker()
    {
        var failures = Enumerable.Range(1, 8).Select(i => MakeFailure($"test{i}", "err")).ToList();
        var result = MakeResult(0, failures);

        var output = result.FormatCompact();

        Assert.Contains("... +3 more failures", output);
        // Only the first five are individually rendered.
        Assert.Contains("5. test5", output);
        Assert.DoesNotContain("6. test6", output);
    }

    [Fact]
    public void FormatCompact_WithDuration_ShowsTimeLine()
    {
        var result = MakeResult(1, []);
        Assert.Contains("Time: 1500ms", result.FormatCompact());
    }

    [Fact]
    public void FormatVerbose_ShowsFullSummaryAndFailureDetails()
    {
        var result = MakeResult(3, [MakeFailure("should pass", "boom")]);

        var output = result.FormatVerbose();

        Assert.Contains("Tests: 3 passed, 1 failed, 0 skipped (total: 4)", output);
        Assert.Contains("should pass (tests/e2e.spec.ts)", output);
        Assert.Contains("boom", output);
        Assert.Contains("Duration: 1500ms", output);
    }

    [Fact]
    public void FormatVerbose_IncludesStackTracePreview_UpToThreeLines()
    {
        var failure = new TestFailure
        {
            TestName = "t",
            FilePath = "f.ts",
            ErrorMessage = "err",
            StackTrace = "line1\nline2\nline3\nline4\nline5",
        };
        var result = MakeResult(0, [failure]);

        var output = result.FormatVerbose();

        Assert.Contains("line1", output);
        Assert.Contains("line3", output);
        Assert.DoesNotContain("line4", output);
    }

    [Fact]
    public void FormatUltra_RendersSymbolicSummary()
    {
        var result = MakeResult(4, [MakeFailure("t", "e")]);
        Assert.Equal("[ok]4 [x]1 [skip]0 (1500ms)", result.FormatUltra());
    }

    [Fact]
    public void FormatUltra_UnknownDuration_RendersZero()
    {
        var result = new TestResult { Total = 1, Passed = 1, Failed = 0, Skipped = 0 };
        Assert.Equal("[ok]1 [x]0 [skip]0 (0ms)", result.FormatUltra());
    }

    [Theory]
    [InlineData(FormatMode.Compact)]
    [InlineData(FormatMode.Verbose)]
    [InlineData(FormatMode.Ultra)]
    public void Format_DispatchesToCorrectPerModeRenderer(FormatMode mode)
    {
        var result = MakeResult(2, [MakeFailure("t", "e")]);
        var expected = mode switch
        {
            FormatMode.Compact => result.FormatCompact(),
            FormatMode.Verbose => result.FormatVerbose(),
            FormatMode.Ultra => result.FormatUltra(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        Assert.Equal(expected, ((ITokenFormatter)result).Format(mode));
    }

    [Theory]
    [InlineData(0, FormatMode.Compact)]
    [InlineData(1, FormatMode.Verbose)]
    [InlineData(2, FormatMode.Ultra)]
    [InlineData(9, FormatMode.Ultra)]
    public void FromVerbosity_MapsNumericLevelToFormatMode(byte verbosity, FormatMode expected)
    {
        Assert.Equal(expected, FormatModeExtensions.FromVerbosity(verbosity));
    }
}
