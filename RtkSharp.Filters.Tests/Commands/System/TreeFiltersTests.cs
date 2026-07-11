using System.Linq;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="TreeFilters"/> and <see cref="SystemConstants"/>. The output filter is a
/// pure function of raw <c>tree</c> output, so it is tested directly against the fixtures ported
/// from <c>src/cmds/system/tree.rs</c>'s own <c>#[test]</c> cases. Moved from
/// <c>RtkSharp.Tests.Commands.TreeCommandTests</c> when the underlying pure methods moved from
/// <c>RtkSharp.Commands.System.TreeCommand</c> to <see cref="TreeFilters"/> (Task 14 of the
/// filters-library extraction). <c>RunAsync</c> and the missing-tool error path are not pure and
/// remain in <c>TreeCommand</c>.
/// </summary>
public sealed class TreeFiltersTests
{
    // ---- FilterTreeOutput: ported from tree.rs #[test] fixtures ----

    [Fact]
    public void FilterTreeOutput_RemovesSummary()
    {
        var input = ".\n├── src\n│   └── main.rs\n└── Cargo.toml\n\n2 directories, 3 files\n";
        var output = TreeFilters.FilterTreeOutput(input);
        Assert.DoesNotContain("directories", output);
        Assert.DoesNotContain("files", output);
        Assert.Contains("main.rs", output);
        Assert.Contains("Cargo.toml", output);
    }

    [Fact]
    public void FilterTreeOutput_PreservesStructure()
    {
        var input = ".\n├── src\n│   ├── main.rs\n│   └── lib.rs\n└── tests\n    └── test.rs\n";
        var output = TreeFilters.FilterTreeOutput(input);
        Assert.Contains("├──", output);
        Assert.Contains("│", output);
        Assert.Contains("└──", output);
        Assert.Contains("main.rs", output);
        Assert.Contains("test.rs", output);
    }

    [Fact]
    public void FilterTreeOutput_HandlesEmpty() =>
        Assert.Equal("\n", TreeFilters.FilterTreeOutput(""));

    [Fact]
    public void FilterTreeOutput_RemovesTrailingEmptyLines()
    {
        var input = ".\n├── file.txt\n\n\n";
        var output = TreeFilters.FilterTreeOutput(input);
        // Root + file.txt + final newline == two '\n'.
        Assert.Equal(2, output.Count(c => c == '\n'));
    }

    [Theory]
    [InlineData(".\n└── file.txt\n\n0 directories, 1 file\n", "1 file")]
    [InlineData(".\n└── file.txt\n\n1 directory, 0 files\n", "1 directory")]
    [InlineData(".\n└── file.txt\n\n10 directories, 25 files\n", "25 files")]
    public void FilterTreeOutput_RemovesSummaryVariations(string input, string summaryFragment)
    {
        var output = TreeFilters.FilterTreeOutput(input);
        Assert.DoesNotContain(summaryFragment, output);
        Assert.Contains("file.txt", output);
    }

    [Fact]
    public void NoiseDirs_ContainsExpectedPatterns()
    {
        // Ported from tree.rs's test_noise_dirs_constant.
        Assert.Contains("node_modules", SystemConstants.NoiseDirs);
        Assert.Contains(".git", SystemConstants.NoiseDirs);
        Assert.Contains("target", SystemConstants.NoiseDirs);
        Assert.Contains("__pycache__", SystemConstants.NoiseDirs);
        Assert.Contains(".next", SystemConstants.NoiseDirs);
        Assert.Contains("dist", SystemConstants.NoiseDirs);
        Assert.Contains("build", SystemConstants.NoiseDirs);
    }
}
