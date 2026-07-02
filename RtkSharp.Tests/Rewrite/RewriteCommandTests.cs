using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Rewrite;

public class RewriteCommandTests
{
    // NOTE: the task-4 brief's test table expected exit 0 for a plain rewrite (e.g. "git
    // status" -> exit 0). That is only correct when `~/.claude/settings.json` allow rules are
    // loaded and match. RtkSharp has no config-loading support (Task 3's Permissions port is
    // config-free by design), so Permissions.CheckCommand always falls through to
    // PermissionVerdict.Ask for every command. Per the brief's own "oracle wins" rule, the
    // Rust oracle probed with an isolated (empty HOME) config also returns exit 3 for plain
    // rewrites, confirming exit 3 (not 0) is correct here. Expectations below are corrected
    // accordingly.
    [Theory]
    [InlineData("git status", 3, "rtk git status")]
    [InlineData("echo hello", 1, "")]
    [InlineData("cd foo && git status", 3, "cd foo && rtk git status")]
    public void Evaluate_MatchesRustContract(string cmd, int expectedExit, string expectedOut)
    {
        var (exit, output) = RewriteCommand.Evaluate(cmd);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedOut, output);
    }
}
