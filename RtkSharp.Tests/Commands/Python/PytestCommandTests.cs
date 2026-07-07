using System;
using RtkSharp.Commands.Python;
using Xunit;

namespace RtkSharp.Tests.Commands.Python;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/python/pytest_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (<c>filter_pytest_output</c>/<c>parse_summary_line</c>). Rust's own oracle never unit-tests
/// <c>pytest_cmd::run</c> itself (it spawns a real subprocess), so no dispatch-level test is added
/// here either — only the pure filter functions are covered, mirroring <c>CargoCommandTests</c>'s
/// precedent for cargo's non-injectable buffered subcommands.
/// </summary>
public sealed class PytestCommandTests
{
    [Fact]
    public void FilterPytestOutput_AllPass_ShowsPassedCount()
    {
        const string output = """
            === test session starts ===
            platform darwin -- Python 3.11.0
            collected 5 items

            tests/test_foo.py .....                                            [100%]

            === 5 passed in 0.50s ===
            """;

        var result = PytestFilters.FilterPytestOutput(output);
        Assert.Contains("Pytest", result, StringComparison.Ordinal);
        Assert.Contains("5 passed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPytestOutput_WithFailures_ShowsFailureDetail()
    {
        const string output = """
            === test session starts ===
            collected 5 items

            tests/test_foo.py ..F..                                            [100%]

            === FAILURES ===
            ___ test_something ___

                def test_something():
            >       assert False
            E       assert False

            tests/test_foo.py:10: AssertionError

            === short test summary info ===
            FAILED tests/test_foo.py::test_something - assert False
            === 4 passed, 1 failed in 0.50s ===
            """;

        var result = PytestFilters.FilterPytestOutput(output);
        Assert.Contains("4 passed, 1 failed", result, StringComparison.Ordinal);
        Assert.Contains("test_something", result, StringComparison.Ordinal);
        Assert.Contains("assert False", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPytestOutput_MultipleFailures_ShowsEachFailure()
    {
        const string output = """
            === test session starts ===
            collected 3 items

            tests/test_foo.py FFF                                              [100%]

            === FAILURES ===
            ___ test_one ___
            E   AssertionError: expected 5

            ___ test_two ___
            E   ValueError: invalid value

            === short test summary info ===
            FAILED tests/test_foo.py::test_one - AssertionError: expected 5
            FAILED tests/test_foo.py::test_two - ValueError: invalid value
            FAILED tests/test_foo.py::test_three - KeyError
            === 3 failed in 0.20s ===
            """;

        var result = PytestFilters.FilterPytestOutput(output);
        Assert.Contains("3 failed", result, StringComparison.Ordinal);
        Assert.Contains("test_one", result, StringComparison.Ordinal);
        Assert.Contains("test_two", result, StringComparison.Ordinal);
        Assert.Contains("expected 5", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPytestOutput_NoTests_ReportsNoTestsCollected()
    {
        const string output = """
            === test session starts ===
            collected 0 items

            === no tests ran in 0.00s ===
            """;

        var result = PytestFilters.FilterPytestOutput(output);
        Assert.Contains("No tests collected", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSummaryLine_SimplePassed()
    {
        var c = PytestFilters.ParseSummaryLine("=== 5 passed in 0.50s ===");
        Assert.Equal((5, 0, 0), (c.Passed, c.Failed, c.Skipped));
    }

    [Fact]
    public void ParseSummaryLine_PassedAndFailed()
    {
        var c = PytestFilters.ParseSummaryLine("=== 4 passed, 1 failed in 0.50s ===");
        Assert.Equal((4, 1, 0), (c.Passed, c.Failed, c.Skipped));
    }

    [Fact]
    public void ParseSummaryLine_PassedFailedSkipped()
    {
        var c = PytestFilters.ParseSummaryLine("=== 3 passed, 1 failed, 2 skipped in 1.0s ===");
        Assert.Equal((3, 1, 2), (c.Passed, c.Failed, c.Skipped));
    }

    [Fact]
    public void ParseSummaryLine_WithXfailXpass()
    {
        var c = PytestFilters.ParseSummaryLine("=== 2 passed, 1 failed, 2 xfailed, 1 xpassed in 1.0s ===");
        Assert.Equal((2, 1, 2, 1), (c.Passed, c.Failed, c.Xfailed, c.Xpassed));
    }

    [Fact]
    public void FilterPytestOutput_XfailCapsAndEmitsTeeHint()
    {
        var lines = "=== test session starts ===\ncollected 30 items\n\n";
        lines += "test_x.py " + new string('x', 15);
        lines += "\n\n=== short test summary info ===\n";
        for (var i = 0; i < 15; i++)
        {
            lines += $"XFAIL test_x.py::test_case_{i} - known issue #{i}\n";
        }

        lines += "=== 0 passed, 15 xfailed in 0.05s ===\n";

        var result = PytestFilters.FilterPytestOutput(lines);
        var xfailSection = result.Contains("Expected-failure outcomes:", StringComparison.Ordinal)
            ? result[(result.IndexOf("Expected-failure outcomes:", StringComparison.Ordinal) + "Expected-failure outcomes:".Length)..]
            : "";
        var listed = xfailSection.Split('\n').Count(l => l.Trim().StartsWith("XFAIL", StringComparison.Ordinal));

        Assert.True(listed <= 10, $"MAX_XFAIL cap not enforced: listed {listed}");
        Assert.Contains("… +5 more", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPytestOutput_XfailAndXpass_ShowsBoth()
    {
        const string output = """
            === test session starts ===
            collected 5 items

            test_math.py ..xxX                                                 [100%]

            === short test summary info ===
            XFAIL test_math.py::test_division_by_zero - known bug in division
            XFAIL test_math.py::test_float_precision - float precision issue — bug #42
            XPASS test_math.py::test_unexpected_pass - this should fail but currently passes
            === 2 passed, 2 xfailed, 1 xpassed in 0.05s ===
            """;

        var result = PytestFilters.FilterPytestOutput(output);
        Assert.Contains("xfailed", result, StringComparison.Ordinal);
        Assert.Contains("xpassed", result, StringComparison.Ordinal);
        Assert.Contains("XPASS", result, StringComparison.Ordinal);
        Assert.Contains("float precision", result, StringComparison.Ordinal);
        Assert.Contains("test_division_by_zero", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPytestOutput_QuietModeFailures_ShowsActualCounts()
    {
        // In -q mode, the final summary line has NO === wrapper. This was causing "No tests
        // collected" to be reported incorrectly.
        const string output = """
            === test session starts ===
            platform linux -- Python 3.12.11, pytest-8.1.0
            collected 1705 items

            .......F.......

            === FAILURES ===
            ___ test_something ___

            E   AssertionError: expected True

            === short test summary info ===
            FAILED tests/test_foo.py::test_something - AssertionError
            5 failed, 1698 passed, 2 skipped in 108.89s
            """;

        var result = PytestFilters.FilterPytestOutput(output);
        Assert.DoesNotContain("No tests collected", result, StringComparison.Ordinal);
        Assert.True(
            result.Contains("1698", StringComparison.Ordinal) || result.Contains("5 failed", StringComparison.Ordinal),
            $"Should show actual test counts. Got: {result}");
    }

    [Fact]
    public void FilterPytestOutput_OnlySkipped_DoesNotReportNoTestsCollected()
    {
        const string output = """
            === test session starts ===
            collected 3 items

            === 3 skipped in 0.10s ===
            """;

        var result = PytestFilters.FilterPytestOutput(output);
        Assert.DoesNotContain("No tests collected", result, StringComparison.Ordinal);
    }
}
