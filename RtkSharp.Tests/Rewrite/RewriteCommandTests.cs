using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Rewrite;

public class RewriteCommandTests
{
    // These cases inject an EMPTY rule set explicitly (via the internal Evaluate overload) so the
    // contract is exercised deterministically, independent of the developer's real
    // ~/.claude/settings.json. With no rules loaded every rewritable command falls through to the
    // Ask verdict → exit 3 (Rust's evaluate() resolves the same way under an empty-config oracle).
    [Theory]
    [InlineData("git status", 3, "rtk git status")]
    [InlineData("echo hello", 1, "")]
    [InlineData("cd foo && git status", 3, "cd foo && rtk git status")]
    public void Evaluate_NoRules_MatchesRustContract(string cmd, int expectedExit, string expectedOut)
    {
        var (exit, output) = RewriteCommand.Evaluate(cmd, PermissionRuleSet.Empty);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedOut, output);
    }

    [Theory]
    // With a loaded allow rule matching the command, the verdict is Allow → exit 0 with the
    // rewritten stdout (this is the behaviour the real host settings' `Bash(git:*)` produces).
    [InlineData("git status", "git:*", 0, "rtk git status")]
    [InlineData("git status", "git status", 0, "rtk git status")]
    public void Evaluate_AllowRuleMatches_ExitsZero(string cmd, string allowPattern, int expectedExit, string expectedOut)
    {
        var rules = new PermissionRuleSet([], [], [allowPattern]);
        var (exit, output) = RewriteCommand.Evaluate(cmd, rules);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedOut, output);
    }

    [Fact]
    public void Evaluate_DenyRuleMatches_ExitsTwoWithEmptyOutput()
    {
        var rules = new PermissionRuleSet(["git push --force"], [], []);
        var (exit, output) = RewriteCommand.Evaluate("git push --force", rules);
        Assert.Equal(2, exit);
        Assert.Equal("", output);
    }
}
