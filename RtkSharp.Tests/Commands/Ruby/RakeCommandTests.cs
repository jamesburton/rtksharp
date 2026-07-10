using RtkSharp.Commands.Ruby;
using Xunit;

namespace RtkSharp.Tests.Commands.Ruby;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/ruby/rake_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// subset covering <c>select_runner</c> and <c>looks_like_test_path</c> (dispatch-level selection
/// logic that stays in <see cref="RakeCommand"/>). The <c>filter_minitest_output</c>/
/// <c>parse_minitest_summary</c> tests moved to
/// <c>RtkSharp.Filters.Tests.Commands.Ruby.RakeFiltersTests</c> alongside
/// <c>RtkSharp.Filters.Commands.Ruby.RakeFilters</c> (Task 11).
/// </summary>
public sealed class RakeCommandTests
{
    // ── select_runner tests ──────────────────────────────────────────────────

    private static string[] Args(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void SelectRunnerSingleFileUsesRake()
    {
        var (tool, _) = RakeCommand.SelectRunner(Args("test TEST=test/models/post_test.rb"));
        Assert.Equal("rake", tool);
    }

    [Fact]
    public void SelectRunnerNoFilesUsesRake()
    {
        var (tool, _) = RakeCommand.SelectRunner(Args("test"));
        Assert.Equal("rake", tool);
    }

    [Fact]
    public void SelectRunnerMultipleFilesUsesRails()
    {
        var (tool, a) = RakeCommand.SelectRunner(Args("test test/models/post_test.rb test/models/user_test.rb"));
        Assert.Equal("rails", tool);
        Assert.Equal(Args("test test/models/post_test.rb test/models/user_test.rb"), a);
    }

    [Fact]
    public void SelectRunnerLineNumberUsesRails()
    {
        var (tool, _) = RakeCommand.SelectRunner(Args("test test/models/post_test.rb:15"));
        Assert.Equal("rails", tool);
    }

    [Fact]
    public void SelectRunnerMultipleWithLineNumbers()
    {
        var (tool, _) = RakeCommand.SelectRunner(Args("test test/models/post_test.rb:15 test/models/user_test.rb:30"));
        Assert.Equal("rails", tool);
    }

    [Fact]
    public void SelectRunnerNonTestSubcommandUsesRake()
    {
        var (tool, _) = RakeCommand.SelectRunner(Args("db:migrate"));
        Assert.Equal("rake", tool);
    }

    [Fact]
    public void SelectRunnerSinglePositionalFileUsesRails()
    {
        var (tool, _) = RakeCommand.SelectRunner(Args("test test/models/post_test.rb"));
        Assert.Equal("rails", tool);
    }

    [Fact]
    public void SelectRunnerFlagsNotCountedAsFiles()
    {
        var (tool, _) = RakeCommand.SelectRunner(Args("test --verbose --seed 12345"));
        Assert.Equal("rake", tool);
    }

    [Fact]
    public void LooksLikeTestPath()
    {
        Assert.True(RakeCommand.LooksLikeTestPath("test/models/post_test.rb"));
        Assert.True(RakeCommand.LooksLikeTestPath("test/models/post_test.rb:15"));
        Assert.True(RakeCommand.LooksLikeTestPath("spec/models/post_spec.rb"));
        Assert.True(RakeCommand.LooksLikeTestPath("my_file.rb"));
        Assert.False(RakeCommand.LooksLikeTestPath("--verbose"));
        Assert.False(RakeCommand.LooksLikeTestPath("12345"));
    }
}
