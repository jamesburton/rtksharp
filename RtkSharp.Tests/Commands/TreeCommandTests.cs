using RtkSharp.Commands.System;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="TreeCommand"/>. The output filter is a pure function of raw <c>tree</c>
/// output, so it is tested directly against the fixtures ported from
/// <c>src/cmds/system/tree.rs</c>'s own <c>#[test]</c> cases. The missing-tool branch is exercised
/// via the internal <see cref="TreeCommand.RunAsync(string[], System.Func{string, bool})"/> overload
/// with an injected "not found" predicate, since a stock Windows install ships <c>tree.com</c> and
/// would otherwise never hit that branch. The exact install-hint text is asserted against the
/// literal transcribed from tree.rs's <c>anyhow::bail!</c>.
/// </summary>
public sealed class TreeCommandTests
{
    // ---- FilterTreeOutput: ported from tree.rs #[test] fixtures ----

    [Fact]
    public void FilterTreeOutput_RemovesSummary()
    {
        var input = ".\n├── src\n│   └── main.rs\n└── Cargo.toml\n\n2 directories, 3 files\n";
        var output = TreeCommand.FilterTreeOutput(input);
        Assert.DoesNotContain("directories", output);
        Assert.DoesNotContain("files", output);
        Assert.Contains("main.rs", output);
        Assert.Contains("Cargo.toml", output);
    }

    [Fact]
    public void FilterTreeOutput_PreservesStructure()
    {
        var input = ".\n├── src\n│   ├── main.rs\n│   └── lib.rs\n└── tests\n    └── test.rs\n";
        var output = TreeCommand.FilterTreeOutput(input);
        Assert.Contains("├──", output);
        Assert.Contains("│", output);
        Assert.Contains("└──", output);
        Assert.Contains("main.rs", output);
        Assert.Contains("test.rs", output);
    }

    [Fact]
    public void FilterTreeOutput_HandlesEmpty() =>
        Assert.Equal("\n", TreeCommand.FilterTreeOutput(""));

    [Fact]
    public void FilterTreeOutput_RemovesTrailingEmptyLines()
    {
        var input = ".\n├── file.txt\n\n\n";
        var output = TreeCommand.FilterTreeOutput(input);
        // Root + file.txt + final newline == two '\n'.
        Assert.Equal(2, output.Count(c => c == '\n'));
    }

    [Theory]
    [InlineData(".\n└── file.txt\n\n0 directories, 1 file\n", "1 file")]
    [InlineData(".\n└── file.txt\n\n1 directory, 0 files\n", "1 directory")]
    [InlineData(".\n└── file.txt\n\n10 directories, 25 files\n", "25 files")]
    public void FilterTreeOutput_RemovesSummaryVariations(string input, string summaryFragment)
    {
        var output = TreeCommand.FilterTreeOutput(input);
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

    // ---- Missing-tool error path ----

    [Fact]
    public void ToolNotFoundMessage_MatchesRustSource() =>
        Assert.Equal(
            "tree command not found. Install it first:\n" +
            "- macOS: brew install tree\n" +
            "- Ubuntu/Debian: sudo apt install tree\n" +
            "- Fedora/RHEL: sudo dnf install tree\n" +
            "- Arch: sudo pacman -S tree",
            TreeCommand.ToolNotFoundMessage);

    [Collection("Console")]
    public sealed class MissingTool
    {
        [Fact]
        public async Task RunAsync_TreeMissing_PrintsHintAndReturnsOne()
        {
            var origErr = Console.Error;
            var se = new StringWriter();
            try
            {
                Console.SetError(se);
                var exit = await TreeCommand.RunAsync(["-L", "2"], _ => false);
                Assert.Equal(1, exit);
            }
            finally
            {
                Console.SetError(origErr);
            }

            var err = se.ToString();
            Assert.StartsWith("rtk: tree command not found. Install it first:", err);
            Assert.Contains("- macOS: brew install tree", err);
            Assert.Contains("- Arch: sudo pacman -S tree", err);
        }
    }
}
