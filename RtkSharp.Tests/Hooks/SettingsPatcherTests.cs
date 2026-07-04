using System.Text.Json.Nodes;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Unit tests for <see cref="SettingsPatcher"/>'s JSON deep-merge primitives, isolated from the
/// filesystem. Byte-exact expectations (indentation, line endings, no trailing newline) were
/// captured against <c>target/release/rtk.exe init -g --auto-patch</c> under a redirected
/// <c>CLAUDE_CONFIG_DIR</c>.
/// </summary>
public sealed class SettingsPatcherTests
{
    [Fact]
    public void InsertHookEntry_CreatesHooksStructureFromEmptyObject()
    {
        var root = new JsonObject();

        SettingsPatcher.InsertHookEntry(root, SettingsPatcher.ClaudeHookCommand);

        var serialized = SettingsPatcher.SerializePretty(root);
        Assert.Equal(
            "{\n" +
            "  \"hooks\": {\n" +
            "    \"PreToolUse\": [\n" +
            "      {\n" +
            "        \"matcher\": \"Bash\",\n" +
            "        \"hooks\": [\n" +
            "          {\n" +
            "            \"type\": \"command\",\n" +
            "            \"command\": \"rtk hook claude\"\n" +
            "          }\n" +
            "        ]\n" +
            "      }\n" +
            "    ]\n" +
            "  }\n" +
            "}",
            serialized);
    }

    [Fact]
    public void InsertHookEntry_PreservesExistingUnrelatedEntriesAndKeys()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Edit",
                    "hooks": [ { "type": "command", "command": "some-other-hook" } ]
                  }
                ]
              },
              "otherKey": "value"
            }
            """)!;

        SettingsPatcher.InsertHookEntry(root, SettingsPatcher.ClaudeHookCommand);

        Assert.True(SettingsPatcher.HookAlreadyPresent(root, SettingsPatcher.ClaudeHookCommand));
        var preToolUse = (JsonArray)root["hooks"]!["PreToolUse"]!;
        Assert.Equal(2, preToolUse.Count);
        Assert.Equal("Edit", preToolUse[0]!["matcher"]!.GetValue<string>());
        Assert.Equal("Bash", preToolUse[1]!["matcher"]!.GetValue<string>());
        Assert.Equal("value", root["otherKey"]!.GetValue<string>());
    }

    [Fact]
    public void InsertHookEntry_ThrowsWhenHooksValueIsNotAnObject()
    {
        var root = new JsonObject { ["hooks"] = "not-an-object" };

        var ex = Assert.Throws<InitAbortException>(() => SettingsPatcher.InsertHookEntry(root, SettingsPatcher.ClaudeHookCommand));
        Assert.Equal("hooks value is not an object", ex.Message);
    }

    [Fact]
    public void HookAlreadyPresent_MatchesLegacyScriptSubstring()
    {
        var root = (JsonObject)JsonNode.Parse("""
            { "hooks": { "PreToolUse": [ { "hooks": [ { "command": "/home/u/.claude/hooks/rtk-rewrite.sh" } ] } ] } }
            """)!;

        Assert.True(SettingsPatcher.HookAlreadyPresent(root, SettingsPatcher.ClaudeHookCommand));
    }

    [Fact]
    public void HookAlreadyPresent_FalseWhenNoHooksKey()
    {
        Assert.False(SettingsPatcher.HookAlreadyPresent(new JsonObject(), SettingsPatcher.ClaudeHookCommand));
    }

    [Fact]
    public void RemoveHookFromJson_RemovesMatchingEntryAndReturnsTrue()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {
              "hooks": {
                "PreToolUse": [
                  { "matcher": "Edit", "hooks": [ { "type": "command", "command": "some-other-hook" } ] },
                  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "rtk hook claude" } ] }
                ]
              }
            }
            """)!;

        var removed = SettingsPatcher.RemoveHookFromJson(root);

        Assert.True(removed);
        var preToolUse = (JsonArray)root["hooks"]!["PreToolUse"]!;
        Assert.Single(preToolUse);
        Assert.Equal("Edit", preToolUse[0]!["matcher"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveHookFromJson_ReturnsFalseWhenNothingMatches()
    {
        var root = (JsonObject)JsonNode.Parse("""
            { "hooks": { "PreToolUse": [ { "matcher": "Edit", "hooks": [ { "command": "some-other-hook" } ] } ] } }
            """)!;

        Assert.False(SettingsPatcher.RemoveHookFromJson(root));
    }

    [Fact]
    public void RemoveLegacyHookEntriesFromJson_PreservesEntryThatAlsoHasNewFormatHook()
    {
        // A single PreToolUse entry with TWO hooks: one legacy, one new-format. Rust's
        // dominated-by-legacy check requires ALL hooks in the entry to be legacy, so this mixed
        // entry must survive.
        var root = (JsonObject)JsonNode.Parse("""
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      { "type": "command", "command": "/home/u/.claude/hooks/rtk-rewrite.sh" },
                      { "type": "command", "command": "rtk hook claude" }
                    ]
                  }
                ]
              }
            }
            """)!;

        var removed = SettingsPatcher.RemoveLegacyHookEntriesFromJson(root);

        Assert.False(removed);
        Assert.Single((JsonArray)root["hooks"]!["PreToolUse"]!);
    }

    [Fact]
    public void RemoveLegacyHookEntriesFromJson_RemovesEntryFullyDominatedByLegacyScript()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {
              "hooks": {
                "PreToolUse": [
                  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "/home/u/.claude/hooks/rtk-rewrite.sh" } ] }
                ]
              }
            }
            """)!;

        var removed = SettingsPatcher.RemoveLegacyHookEntriesFromJson(root);

        Assert.True(removed);
        Assert.Empty((JsonArray)root["hooks"]!["PreToolUse"]!);
    }

    [Fact]
    public void ParseSettingsObject_ThrowsOnMalformedJson()
    {
        Assert.Throws<InitAbortException>(() => SettingsPatcher.ParseSettingsObject("{ not json", "settings.json"));
    }

    [Fact]
    public void ResolveClaudeDir_HonorsClaudeConfigDirEnvVar()
    {
        // CLAUDE_CONFIG_DIR is process-global, like the CWD guarded elsewhere in this suite by
        // InitTestSupport.CwdLock — serialize via the equivalent env-var lock so this can't race
        // with GlobalScopeGuard-based tests in another test class (a different xUnit collection,
        // and therefore eligible to run in parallel with this one).
        lock (InitTestSupport.EnvLock)
        {
            var previous = System.Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            try
            {
                System.Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "/tmp/rtk-claude-override");
                Assert.Equal("/tmp/rtk-claude-override", SettingsPatcher.ResolveClaudeDir());
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
            }
        }
    }
}
