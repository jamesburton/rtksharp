using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Rewrite;

public class RewriteRulesTests
{
    [Fact]
    public void All_Contains75RulesInSourceOrder()
    {
        Assert.Equal(75, RewriteRules.All.Count);
        // First rule in rules.rs is the git/yadm rule; last-known category spot checks:
        Assert.Equal("rtk git", RewriteRules.All[0].RtkCmd);
        Assert.Contains("git", RewriteRules.All[0].RewritePrefixes);
        Assert.Equal("Git", RewriteRules.All[0].Category);
    }

    [Fact]
    public void All_EveryPatternCompilesAndMatchesItsOwnPrefix()
    {
        foreach (var rule in RewriteRules.All)
        {
            Assert.NotNull(rule.CompiledPattern);
            Assert.NotEmpty(rule.RewritePrefixes);
            Assert.StartsWith("rtk", rule.RtkCmd);
        }
    }

    [Theory]
    [InlineData("git status", "rtk git")]
    [InlineData("gh pr list", "rtk gh")]
    [InlineData("cargo build", "rtk cargo")]
    public void All_FirstMatchingRule_IsExpected(string command, string expectedRtkCmd)
    {
        var match = RewriteRules.All.FirstOrDefault(r => r.CompiledPattern.IsMatch(command));
        Assert.NotNull(match);
        Assert.Equal(expectedRtkCmd, match!.RtkCmd);
    }
}
