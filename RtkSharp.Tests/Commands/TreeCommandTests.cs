using RtkSharp.Commands.System;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="TreeCommand"/>. The pure output filter and <c>NoiseDirs</c> constant
/// moved to <c>RtkSharp.Filters.Commands.System.TreeFilters</c>/<c>SystemConstants</c> (see
/// <c>RtkSharp.Filters.Tests.Commands.System.TreeFiltersTests</c>) — this file now covers only
/// the missing-tool branch, exercised via the internal
/// <see cref="TreeCommand.RunAsync(string[], System.Func{string, bool})"/> overload with an
/// injected "not found" predicate, since a stock Windows install ships <c>tree.com</c> and would
/// otherwise never hit that branch. The exact install-hint text is asserted against the literal
/// transcribed from tree.rs's <c>anyhow::bail!</c>.
/// </summary>
public sealed class TreeCommandTests
{
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
