using RtkSharp.Filters.Commands.Ruby;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Ruby;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/ruby/rake_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// subset covering <c>filter_minitest_output</c> and <c>parse_minitest_summary</c>. The
/// <c>select_runner</c>/<c>looks_like_test_path</c> dispatch-selection tests remain in
/// <c>RtkSharp.Tests.Commands.Ruby.RakeCommandTests</c> alongside <c>RakeCommand</c>.
/// </summary>
public sealed class RakeFiltersTests
{
    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public void FilterMinitestAllPass()
    {
        const string output = "Run options: --seed 12345\n\n" +
            "# Running:\n\n" +
            "........\n\n" +
            "Finished in 0.123456s, 64.8 runs/s, 72.9 assertions/s.\n\n" +
            "8 runs, 9 assertions, 0 failures, 0 errors, 0 skips";

        var result = RakeFilters.FilterMinitestOutput(output);
        Assert.Contains("ok rake test", result, StringComparison.Ordinal);
        Assert.Contains("8 runs", result, StringComparison.Ordinal);
        Assert.Contains("0 failures", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMinitestWithFailures()
    {
        const string output = "Run options: --seed 54321\n\n" +
            "# Running:\n\n" +
            "..F....\n\n" +
            "Finished in 0.234567s, 29.8 runs/s\n\n" +
            "  1) Failure:\n" +
            "TestSomething#test_that_fails [/path/to/test.rb:15]:\n" +
            "Expected: true\n" +
            "  Actual: false\n\n" +
            "7 runs, 7 assertions, 1 failures, 0 errors, 0 skips";

        var result = RakeFilters.FilterMinitestOutput(output);
        Assert.Contains("1 failures", result, StringComparison.Ordinal);
        Assert.Contains("test_that_fails", result, StringComparison.Ordinal);
        Assert.Contains("Expected: true", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMinitestWithErrors()
    {
        const string output = "Run options: --seed 99999\n\n" +
            "# Running:\n\n" +
            ".E....\n\n" +
            "Finished in 0.345678s, 17.4 runs/s\n\n" +
            "  1) Error:\n" +
            "TestOther#test_boom [/path/to/test.rb:42]:\n" +
            "RuntimeError: something went wrong\n" +
            "    /path/to/test.rb:42:in `test_boom'\n\n" +
            "6 runs, 5 assertions, 0 failures, 1 errors, 0 skips";

        var result = RakeFilters.FilterMinitestOutput(output);
        Assert.Contains("1 errors", result, StringComparison.Ordinal);
        Assert.Contains("test_boom", result, StringComparison.Ordinal);
        Assert.Contains("RuntimeError", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMinitestEmpty()
    {
        var result = RakeFilters.FilterMinitestOutput("");
        Assert.Contains("no tests ran", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMinitestSkip()
    {
        const string output = "Run options: --seed 11111\n\n" +
            "# Running:\n\n" +
            "..S..\n\n" +
            "Finished in 0.100000s, 50.0 runs/s\n\n" +
            "5 runs, 4 assertions, 0 failures, 0 errors, 1 skips";

        var result = RakeFilters.FilterMinitestOutput(output);
        Assert.Contains("ok rake test", result, StringComparison.Ordinal);
        Assert.Contains("1 skips", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenSavings()
    {
        var dots = string.Concat(Enumerable.Repeat("......................................................................\n", 20));
        var output = "Run options: --seed 12345\n\n" +
            "# Running:\n\n" +
            $"{dots}\n" +
            "Finished in 2.345678s, 213.4 runs/s, 428.7 assertions/s.\n\n" +
            "500 runs, 1003 assertions, 0 failures, 0 errors, 0 skips";

        var inputTokens = CountTokens(output);
        var result = RakeFilters.FilterMinitestOutput(output);
        var outputTokens = CountTokens(result);

        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);
        Assert.True(savings >= 80.0, $"Expected >= 80% savings, got {savings:F1}% (input: {inputTokens}, output: {outputTokens})");
    }

    [Fact]
    public void ParseMinitestSummary()
    {
        Assert.Equal((8, 9, 0, 0, 0), RakeFilters.ParseMinitestSummary("8 runs, 9 assertions, 0 failures, 0 errors, 0 skips"));
        Assert.Equal((5, 4, 1, 1, 2), RakeFilters.ParseMinitestSummary("5 runs, 4 assertions, 1 failures, 1 errors, 2 skips"));
        // minitest-reporters uses "tests" instead of "runs".
        Assert.Equal((57, 378, 0, 0, 0), RakeFilters.ParseMinitestSummary("57 tests, 378 assertions, 0 failures, 0 errors, 0 skips"));
    }

    [Fact]
    public void FilterMinitestMultipleFailures()
    {
        const string output = "Run options: --seed 77777\n\n" +
            "# Running:\n\n" +
            ".FF.E.\n\n" +
            "Finished in 0.500000s, 12.0 runs/s\n\n" +
            "  1) Failure:\n" +
            "TestFoo#test_alpha [/test.rb:10]:\n" +
            "Expected: 1\n" +
            "  Actual: 2\n\n" +
            "  2) Failure:\n" +
            "TestFoo#test_beta [/test.rb:20]:\n" +
            "Expected: \"hello\"\n" +
            "  Actual: \"world\"\n\n" +
            "  3) Error:\n" +
            "TestBar#test_gamma [/test.rb:30]:\n" +
            "NoMethodError: undefined method `blah'\n\n" +
            "6 runs, 5 assertions, 2 failures, 1 errors, 0 skips";

        var result = RakeFilters.FilterMinitestOutput(output);
        Assert.Contains("2 failures", result, StringComparison.Ordinal);
        Assert.Contains("1 errors", result, StringComparison.Ordinal);
        Assert.Contains("test_alpha", result, StringComparison.Ordinal);
        Assert.Contains("test_beta", result, StringComparison.Ordinal);
        Assert.Contains("test_gamma", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMinitestReportersFormat()
    {
        const string output = "Started with run options --seed 37764\n\n" +
            "Progress: |========================================|\n\n" +
            "Finished in 5.79938s\n" +
            "57 tests, 378 assertions, 0 failures, 0 errors, 0 skips";

        var result = RakeFilters.FilterMinitestOutput(output);
        Assert.Contains("ok rake test", result, StringComparison.Ordinal);
        Assert.Contains("57 runs", result, StringComparison.Ordinal);
        Assert.Contains("0 failures", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMinitestWithAnsi()
    {
        const string output = "\x1b[32mRun options: --seed 12345\x1b[0m\n\n" +
            "# Running:\n\n" +
            "\x1b[32m....\x1b[0m\n\n" +
            "Finished in 0.1s, 40.0 runs/s\n\n" +
            "4 runs, 4 assertions, 0 failures, 0 errors, 0 skips";

        var result = RakeFilters.FilterMinitestOutput(output);
        Assert.Contains("ok rake test", result, StringComparison.Ordinal);
        Assert.Contains("4 runs", result, StringComparison.Ordinal);
    }
}
