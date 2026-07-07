using System.IO;
using System.Text.Json.Nodes;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Unit tests for <see cref="CursorInit"/>'s Cursor <c>hooks.json</c> deep-merge primitives and
/// path-parameterized patch/remove operations. Mirrors the pure-JSON unit tests in Rust
/// <c>src/hooks/init.rs</c> (init.rs:5584-5864: <c>test_cursor_hook_already_present_*</c>,
/// <c>test_insert_cursor_hook_*</c>, <c>test_remove_cursor_hook_*</c>,
/// <c>test_remove_legacy_cursor_entries_*</c>), plus filesystem-level tests of
/// <see cref="CursorInit.PatchHooksJsonAt"/>, <see cref="CursorInit.RemoveLegacyHooksJsonEntriesAt"/>,
/// and <see cref="CursorInit.RemoveHookFromHooksJsonAt"/> against a temp directory.
/// </summary>
/// <remarks>
/// <see cref="CursorInit.RunCursor"/> and <see cref="CursorInit.UninstallCursor"/> themselves are not
/// exercised end-to-end here: like Rust's own <c>install_cursor_hooks</c>/<c>remove_cursor_hooks</c>
/// (which the Rust test suite also never drives directly — see init.rs's test module), they resolve
/// the Cursor config directory strictly via the real home directory with no environment-variable
/// override (<see cref="CursorInit.ResolveCursorDir"/> doc remarks), so nothing below touches the
/// machine's real <c>~/.cursor</c>. The path-parameterized helpers cover the same read/patch/backup/
/// write cycle those entry points delegate to.
/// </remarks>
public sealed class CursorInitTests
{
    // ─── JSON-level pure function tests (mirrors init.rs:5584-5864) ───

    [Fact]
    public void HookAlreadyPresent_MatchesLegacyScriptSubstring()
    {
        var root = (JsonObject)JsonNode.Parse("""
            { "version": 1, "hooks": { "preToolUse": [{ "command": "./hooks/rtk-rewrite.sh", "matcher": "Shell" }] } }
            """)!;

        Assert.True(CursorInit.HookAlreadyPresent(root));
    }

    [Fact]
    public void HookAlreadyPresent_MatchesNewCommand()
    {
        var root = (JsonObject)JsonNode.Parse($$"""
            { "version": 1, "hooks": { "preToolUse": [{ "command": "{{CursorInit.CursorHookCommand}}", "matcher": "Shell" }] } }
            """)!;

        Assert.True(CursorInit.HookAlreadyPresent(root));
    }

    [Fact]
    public void HookAlreadyPresent_FalseWhenEmpty()
    {
        var root = (JsonObject)JsonNode.Parse("""{ "version": 1 }""")!;

        Assert.False(CursorInit.HookAlreadyPresent(root));
    }

    [Fact]
    public void HookAlreadyPresent_FalseForUnrelatedHook()
    {
        var root = (JsonObject)JsonNode.Parse("""
            { "version": 1, "hooks": { "preToolUse": [{ "command": "./hooks/some-other-hook.sh", "matcher": "Shell" }] } }
            """)!;

        Assert.False(CursorInit.HookAlreadyPresent(root));
    }

    [Fact]
    public void InsertHookEntry_CreatesStructureFromVersionOnly()
    {
        var root = (JsonObject)JsonNode.Parse("""{ "version": 1 }""")!;

        CursorInit.InsertHookEntry(root);

        var hooks = (JsonArray)root["hooks"]!["preToolUse"]!;
        Assert.Single(hooks);
        Assert.Equal(CursorInit.CursorHookCommand, hooks[0]!["command"]!.GetValue<string>());
        Assert.Equal("Shell", hooks[0]!["matcher"]!.GetValue<string>());
        Assert.Equal(1, root["version"]!.GetValue<int>());
    }

    [Fact]
    public void InsertHookEntry_FromEmptyObject_AddsVersion()
    {
        var root = new JsonObject();

        CursorInit.InsertHookEntry(root);

        Assert.Equal(1, root["version"]!.GetValue<int>());
        var hooks = (JsonArray)root["hooks"]!["preToolUse"]!;
        Assert.Single(hooks);
    }

    [Fact]
    public void InsertHookEntry_PreservesExistingEntriesAndSiblingHookKinds()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {
              "version": 1,
              "hooks": {
                "preToolUse": [ { "command": "./hooks/other.sh", "matcher": "Shell" } ],
                "afterFileEdit": [ { "command": "./hooks/format.sh" } ]
              }
            }
            """)!;

        CursorInit.InsertHookEntry(root);

        var preToolUse = (JsonArray)root["hooks"]!["preToolUse"]!;
        Assert.Equal(2, preToolUse.Count);
        Assert.Equal("./hooks/other.sh", preToolUse[0]!["command"]!.GetValue<string>());
        Assert.Equal(CursorInit.CursorHookCommand, preToolUse[1]!["command"]!.GetValue<string>());
        Assert.True(root["hooks"]!["afterFileEdit"] is JsonArray);
    }

    [Fact]
    public void InsertHookEntry_ThrowsWhenHooksValueIsNotAnObject()
    {
        var root = new JsonObject { ["hooks"] = "not-an-object" };

        var ex = Assert.Throws<InitAbortException>(() => CursorInit.InsertHookEntry(root));
        Assert.Equal("hooks value is not an object", ex.Message);
    }

    [Fact]
    public void InsertHookEntry_ThrowsWhenPreToolUseValueIsNotAnArray()
    {
        var root = new JsonObject { ["hooks"] = new JsonObject { ["preToolUse"] = "not-an-array" } };

        var ex = Assert.Throws<InitAbortException>(() => CursorInit.InsertHookEntry(root));
        Assert.Equal("preToolUse value is not an array", ex.Message);
    }

    [Fact]
    public void RemoveHookFromJson_RemovesLegacyScriptEntry()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {
              "version": 1,
              "hooks": { "preToolUse": [
                { "command": "./hooks/other.sh", "matcher": "Shell" },
                { "command": "./hooks/rtk-rewrite.sh", "matcher": "Shell" }
              ] }
            }
            """)!;

        Assert.True(CursorInit.RemoveHookFromJson(root));

        var hooks = (JsonArray)root["hooks"]!["preToolUse"]!;
        Assert.Single(hooks);
        Assert.Equal("./hooks/other.sh", hooks[0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveHookFromJson_RemovesNewCommandEntry()
    {
        var root = (JsonObject)JsonNode.Parse($$"""
            {
              "version": 1,
              "hooks": { "preToolUse": [
                { "command": "./hooks/other.sh", "matcher": "Shell" },
                { "command": "{{CursorInit.CursorHookCommand}}", "matcher": "Shell" }
              ] }
            }
            """)!;

        Assert.True(CursorInit.RemoveHookFromJson(root));

        var hooks = (JsonArray)root["hooks"]!["preToolUse"]!;
        Assert.Single(hooks);
        Assert.Equal("./hooks/other.sh", hooks[0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveHookFromJson_FalseWhenNotPresent()
    {
        var root = (JsonObject)JsonNode.Parse("""
            { "version": 1, "hooks": { "preToolUse": [ { "command": "./hooks/other.sh", "matcher": "Shell" } ] } }
            """)!;

        Assert.False(CursorInit.RemoveHookFromJson(root));
    }

    [Fact]
    public void RemoveLegacyEntriesFromJson_StripsOldScript()
    {
        var root = (JsonObject)JsonNode.Parse("""
            { "version": 1, "hooks": { "preToolUse": [ { "command": "./hooks/rtk-rewrite.sh", "matcher": "Shell" } ] } }
            """)!;

        Assert.True(CursorInit.RemoveLegacyEntriesFromJson(root));

        Assert.Empty((JsonArray)root["hooks"]!["preToolUse"]!);
    }

    [Fact]
    public void RemoveLegacyEntriesFromJson_PreservesNewCommand()
    {
        var root = (JsonObject)JsonNode.Parse($$"""
            {
              "version": 1,
              "hooks": { "preToolUse": [
                { "command": "./hooks/rtk-rewrite.sh", "matcher": "Shell" },
                { "command": "{{CursorInit.CursorHookCommand}}", "matcher": "Shell" }
              ] }
            }
            """)!;

        Assert.True(CursorInit.RemoveLegacyEntriesFromJson(root));

        var arr = (JsonArray)root["hooks"]!["preToolUse"]!;
        Assert.Single(arr);
        Assert.Equal(CursorInit.CursorHookCommand, arr[0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveLegacyEntriesFromJson_NoopWhenNoLegacyEntries()
    {
        var root = (JsonObject)JsonNode.Parse($$"""
            { "version": 1, "hooks": { "preToolUse": [ { "command": "{{CursorInit.CursorHookCommand}}", "matcher": "Shell" } ] } }
            """)!;

        Assert.False(CursorInit.RemoveLegacyEntriesFromJson(root));
        Assert.Single((JsonArray)root["hooks"]!["preToolUse"]!);
    }

    // ─── Path-parameterized patch/remove tests (filesystem, temp dir) ───

    [Fact]
    public void PatchHooksJsonAt_CreatesFromMissingFile()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");

        var patched = CursorInit.PatchHooksJsonAt(path, InitContext.Default);

        Assert.True(patched);
        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains($"\"command\": \"{CursorInit.CursorHookCommand}\"", content);
        Assert.Contains("\"matcher\": \"Shell\"", content);
        Assert.Contains("\"version\": 1", content);
        Assert.False(File.Exists(path + ".bak"), "no backup should be made when the file did not previously exist");
    }

    [Fact]
    public void PatchHooksJsonAt_CreatesFromEmptyFile()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        File.WriteAllText(path, string.Empty);

        var patched = CursorInit.PatchHooksJsonAt(path, InitContext.Default);

        Assert.True(patched);
        var content = File.ReadAllText(path);
        Assert.Contains(CursorInit.CursorHookCommand, content);
    }

    [Fact]
    public void PatchHooksJsonAt_IdempotentRePatchAddsNoDuplicate()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");

        Assert.True(CursorInit.PatchHooksJsonAt(path, InitContext.Default));
        var firstPatch = File.ReadAllText(path);

        var patchedAgain = CursorInit.PatchHooksJsonAt(path, InitContext.Default);

        Assert.False(patchedAgain);
        Assert.Equal(firstPatch, File.ReadAllText(path));

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var hooks = (JsonArray)root["hooks"]!["preToolUse"]!;
        Assert.Single(hooks);
    }

    [Fact]
    public void PatchHooksJsonAt_DeepMergesPreservingExistingEntriesAndBacksUp()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [ { "command": "./hooks/other.sh", "matcher": "Shell" } ],
                "afterFileEdit": [ { "command": "./hooks/format.sh" } ]
              }
            }
            """);

        var patched = CursorInit.PatchHooksJsonAt(path, InitContext.Default);

        Assert.True(patched);
        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains("./hooks/other.sh", File.ReadAllText(path + ".bak"));

        var content = File.ReadAllText(path);
        Assert.Contains("./hooks/other.sh", content);
        Assert.Contains("./hooks/format.sh", content);
        Assert.Contains(CursorInit.CursorHookCommand, content);
    }

    [Fact]
    public void PatchHooksJsonAt_DryRunWritesNothing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        using var console = new ConsoleCapture();

        var patched = CursorInit.PatchHooksJsonAt(path, new InitContext(Verbose: 0, DryRun: true));

        Assert.True(patched);
        Assert.False(File.Exists(path), "dry-run must not create hooks.json");
        Assert.Contains("[dry-run] would patch Cursor hooks.json", console.Out.ToString());
    }

    [Fact]
    public void PatchHooksJsonAt_DryRunOnExistingFileLeavesItUnchanged()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        const string original = """{ "version": 1 }""";
        File.WriteAllText(path, original);

        var patched = CursorInit.PatchHooksJsonAt(path, new InitContext(Verbose: 0, DryRun: true));

        Assert.True(patched);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void PatchHooksJsonAt_ThrowsOnMalformedJson()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        File.WriteAllText(path, "{ not valid json");

        Assert.Throws<InitAbortException>(() => CursorInit.PatchHooksJsonAt(path, InitContext.Default));
    }

    [Fact]
    public void RemoveLegacyHooksJsonEntriesAt_MigratesOldScriptEntry()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        File.WriteAllText(path, """
            { "version": 1, "hooks": { "preToolUse": [ { "command": "./hooks/rtk-rewrite.sh", "matcher": "Shell" } ] } }
            """);

        CursorInit.RemoveLegacyHooksJsonEntriesAt(path, InitContext.Default);

        // Rust's remove_legacy_cursor_hooks_json_entries (init.rs:3148) does not back up before
        // writing — unlike patch_cursor_hooks_json and the uninstall-time RTK-entry removal, both of
        // which do. This asymmetry is faithfully preserved.
        Assert.False(File.Exists(path + ".bak"));
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Empty((JsonArray)root["hooks"]!["preToolUse"]!);
    }

    [Fact]
    public void RemoveLegacyHooksJsonEntriesAt_NoopWhenFileMissing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");

        CursorInit.RemoveLegacyHooksJsonEntriesAt(path, InitContext.Default);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RemoveLegacyHooksJsonEntriesAt_DryRunWritesNothing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        const string original = """
            { "version": 1, "hooks": { "preToolUse": [ { "command": "./hooks/rtk-rewrite.sh", "matcher": "Shell" } ] } }
            """;
        File.WriteAllText(path, original);

        CursorInit.RemoveLegacyHooksJsonEntriesAt(path, new InitContext(Verbose: 0, DryRun: true));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    // ─── Uninstall round-trip (path-parameterized) ───

    [Fact]
    public void InstallThenUninstall_RoundTripRemovesEntryAndBacksUp()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");

        Assert.True(CursorInit.PatchHooksJsonAt(path, InitContext.Default));
        Assert.Contains(CursorInit.CursorHookCommand, File.ReadAllText(path));

        var removed = CursorInit.RemoveHookFromHooksJsonAt(path, InitContext.Default);

        Assert.True(removed);
        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains(CursorInit.CursorHookCommand, File.ReadAllText(path + ".bak"));

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.DoesNotContain(
            (JsonArray)root["hooks"]!["preToolUse"]!,
            entry => entry!["command"]!.GetValue<string>() == CursorInit.CursorHookCommand);
    }

    [Fact]
    public void RemoveHookFromHooksJsonAt_FalseWhenFileMissing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");

        Assert.False(CursorInit.RemoveHookFromHooksJsonAt(path, InitContext.Default));
    }

    [Fact]
    public void RemoveHookFromHooksJsonAt_FalseWhenNoRtkEntry()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        File.WriteAllText(path, """
            { "version": 1, "hooks": { "preToolUse": [ { "command": "./hooks/other.sh", "matcher": "Shell" } ] } }
            """);

        Assert.False(CursorInit.RemoveHookFromHooksJsonAt(path, InitContext.Default));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void RemoveHookFromHooksJsonAt_DryRunWritesNothing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        Assert.True(CursorInit.PatchHooksJsonAt(path, InitContext.Default));
        var original = File.ReadAllText(path);

        var removed = CursorInit.RemoveHookFromHooksJsonAt(path, new InitContext(Verbose: 0, DryRun: true));

        Assert.True(removed);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void RemoveHookFromHooksJsonAt_MalformedJsonIsSkippedNotThrown()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "hooks.json");
        File.WriteAllText(path, "{ not valid json");

        Assert.False(CursorInit.RemoveHookFromHooksJsonAt(path, InitContext.Default));
    }

    // ─── ResolveCursorDir ───

    [Fact]
    public void ResolveCursorDir_ReturnsHomeSlashDotCursor()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        var dir = CursorInit.ResolveCursorDir();

        Assert.Equal(Path.Combine(home, ".cursor"), dir);
    }
}
