using System.IO;
using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Rewrite;

/// <summary>
/// Tests for <see cref="PermissionRules"/>, the port of rtk's <c>load_permission_rules</c>
/// (<c>src/hooks/permissions.rs</c>). All cases pass an explicit <c>baseOverride</c> temp
/// directory so they never touch the developer's real <c>~/.claude/settings.json</c>.
/// </summary>
public sealed class PermissionRulesTests : IDisposable
{
    private readonly string _baseDir;

    public PermissionRulesTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "rtksharp-permrules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_baseDir, ".claude"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteSettings(string fileName, string json)
        => File.WriteAllText(Path.Combine(_baseDir, ".claude", fileName), json);

    [Fact]
    public void Load_NoFiles_ReturnsEmptyRuleSet()
    {
        var rules = PermissionRules.Load(_baseDir);
        Assert.Empty(rules.Deny);
        Assert.Empty(rules.Ask);
        Assert.Empty(rules.Allow);
    }

    [Fact]
    public void Load_ExtractsBashPatternsIntoDenyAskAllow()
    {
        WriteSettings("settings.json", """
        {
          "permissions": {
            "allow": ["Bash(git:*)", "Bash(dotnet build:*)", "Read(**/.env*)", "WebSearch"],
            "deny": ["Bash(rm -rf /)"],
            "ask": ["Bash(git push:*)"]
          }
        }
        """);

        var rules = PermissionRules.Load(_baseDir);

        Assert.Equal(["git:*", "dotnet build:*"], rules.Allow);
        Assert.Equal(["rm -rf /"], rules.Deny);
        Assert.Equal(["git push:*"], rules.Ask);
    }

    [Fact]
    public void Load_MergesSettingsAndSettingsLocal()
    {
        WriteSettings("settings.json", """
        { "permissions": { "allow": ["Bash(git:*)"] } }
        """);
        WriteSettings("settings.local.json", """
        { "permissions": { "allow": ["Bash(cargo:*)"], "deny": ["Bash(sudo:*)"] } }
        """);

        var rules = PermissionRules.Load(_baseDir);

        Assert.Equal(["git:*", "cargo:*"], rules.Allow);
        Assert.Equal(["sudo:*"], rules.Deny);
    }

    [Fact]
    public void Load_MalformedJson_SkippedWithoutThrowing()
    {
        WriteSettings("settings.json", "{ this is not valid json ");
        WriteSettings("settings.local.json", """
        { "permissions": { "allow": ["Bash(git:*)"] } }
        """);

        var rules = PermissionRules.Load(_baseDir);

        // The malformed file is skipped; the valid one still contributes.
        Assert.Equal(["git:*"], rules.Allow);
    }

    [Fact]
    public void Load_NoPermissionsKey_ReturnsEmpty()
    {
        WriteSettings("settings.json", """{ "model": "claude", "env": {} }""");

        var rules = PermissionRules.Load(_baseDir);

        Assert.Empty(rules.Allow);
        Assert.Empty(rules.Deny);
        Assert.Empty(rules.Ask);
    }

    [Fact]
    public void Load_NonBashRules_Ignored()
    {
        WriteSettings("settings.json", """
        {
          "permissions": {
            "allow": ["Read(**/.env*)", "WebSearch", "mcp__foo__bar", "Skill(gsd:plan)"]
          }
        }
        """);

        var rules = PermissionRules.Load(_baseDir);

        Assert.Empty(rules.Allow);
    }

    [Fact]
    public void LoadedRules_DriveCheckCommandToAllow()
    {
        WriteSettings("settings.json", """
        { "permissions": { "allow": ["Bash(git:*)"] } }
        """);

        var rules = PermissionRules.Load(_baseDir);

        Assert.Equal(
            PermissionVerdict.Allow,
            Permissions.CheckCommandWithRules("git status", rules.Deny, rules.Ask, rules.Allow));
    }

    [Theory]
    [InlineData("Bash(git:*)", "git:*")]
    [InlineData("Bash(git push --force)", "git push --force")]
    [InlineData("Bash(*)", "*")]
    [InlineData("Bash()", "")]
    [InlineData("Read(**/.env*)", "Read(**/.env*)")] // not Bash-wrapped → unchanged
    [InlineData("Bash(", "Bash(")] // no closing paren → unchanged
    public void ExtractBashPattern_MatchesRustSemantics(string rule, string expected)
    {
        Assert.Equal(expected, PermissionRules.ExtractBashPattern(rule));
    }
}
