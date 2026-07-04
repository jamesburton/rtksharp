using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// End-to-end tests for the global-scope <c>rtk init -g</c> Claude Code path: default mode,
/// <c>--hook-only</c>, <c>--claude-md</c>, <c>--uninstall</c>, and <c>--show</c>. Each test isolates
/// both the resolved Claude config directory (<c>CLAUDE_CONFIG_DIR</c>) and the user-global config
/// root (<c>RTK_CONFIG_DIR_OVERRIDE</c>) via <see cref="GlobalScopeGuard"/> so no real
/// <c>~/.claude</c> or <c>%APPDATA%/rtk</c> is ever touched. Oracle-derived expectations were
/// captured against <c>target/release/rtk.exe init -g</c> under a redirected <c>CLAUDE_CONFIG_DIR</c>.
/// </summary>
public sealed class InitGlobalCommandTests
{
    [Fact]
    public void DefaultMode_AutoPatch_CreatesSettingsJsonFromEmpty()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--global", "--auto-patch"]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "RTK.md")));
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "CLAUDE.md")));

        var settingsPath = Path.Combine(env.ClaudeDir, "settings.json");
        Assert.True(File.Exists(settingsPath));
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
            File.ReadAllText(settingsPath));
        Assert.False(File.Exists(settingsPath + ".bak"));

        var stdout = console.Out.ToString();
        Assert.Contains("RTK hook registered (global).", stdout);
        Assert.Contains("settings.json: hook added", stdout);
        Assert.Contains("filters:", stdout);
        Assert.True(File.Exists(Path.Combine(env.ConfigDir, "rtk", "filters.toml")));
    }

    [Fact]
    public void DefaultMode_AutoPatch_DeepMergesPreservingExistingHooksAndKeys()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        var settingsPath = Path.Combine(env.ClaudeDir, "settings.json");
        File.WriteAllText(settingsPath, """
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
            """);

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["--global", "--auto-patch"]);

        Assert.Equal(0, exit);
        var content = File.ReadAllText(settingsPath);
        Assert.Contains("\"some-other-hook\"", content);
        Assert.Contains("\"rtk hook claude\"", content);
        Assert.Contains("\"otherKey\": \"value\"", content);
        Assert.True(File.Exists(settingsPath + ".bak"));

        var backup = File.ReadAllText(settingsPath + ".bak");
        Assert.Contains("some-other-hook", backup);
        Assert.DoesNotContain("rtk hook claude", backup);
    }

    [Fact]
    public void DefaultMode_AutoPatch_SecondRunReportsAlreadyPresent()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);

        InitCommand.Run(["--global", "--auto-patch"]);
        var settingsPath = Path.Combine(env.ClaudeDir, "settings.json");
        var afterFirst = File.ReadAllText(settingsPath);

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["--global", "--auto-patch"]);
        var afterSecond = File.ReadAllText(settingsPath);

        Assert.Equal(0, exit);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Contains("settings.json: hook already present", console.Out.ToString());
    }

    [Fact]
    public void DefaultMode_NoPatch_PrintsManualInstructionsAndWritesNoSettings()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--global", "--no-patch"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "settings.json")));

        var stdout = console.Out.ToString();
        Assert.Contains($"MANUAL STEP: Add this to {Path.Combine(env.ClaudeDir, "settings.json")}:", stdout);
        Assert.Contains("\"command\": \"rtk hook claude\"", stdout);
        Assert.Contains("Then restart Claude Code. Test with: git status", stdout);
    }

    [Fact]
    public void DefaultMode_AskMode_NonInteractiveDefaultsToDeclinedManualInstructions()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        // Neither --auto-patch nor --no-patch => PatchMode.Ask. The xUnit test host has no attached
        // interactive console, so Console.IsInputRedirected is true and the prompt must default to
        // "No" without blocking — matching Rust's `!io::stdin().is_terminal()` early return.
        var exit = InitCommand.Run(["--global"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "settings.json")));
        Assert.Contains("non-interactive mode, defaulting to N", console.Error.ToString());
        Assert.Contains("MANUAL STEP", console.Out.ToString());
    }

    [Fact]
    public void DryRunGlobal_WritesNothingAndPrintsExactFooter()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--global", "--auto-patch", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "RTK.md")));
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "CLAUDE.md")));
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(env.ConfigDir, "rtk", "filters.toml")));

        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would create RTK.md", stdout);
        Assert.Contains("[dry-run] would patch settings.json", stdout);
        Assert.Contains("[dry-run] would create global filters template", stdout);
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void HookOnlyMode_Global_RegistersHookWithoutRtkMd()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--global", "--hook-only", "--auto-patch"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "RTK.md")));
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "settings.json")));
        Assert.Contains("RTK hook registered (hook-only mode).", console.Out.ToString());
        Assert.Contains("No RTK.md created.", console.Out.ToString());
    }

    [Fact]
    public void ClaudeMdMode_Global_WritesFullBlockToResolvedClaudeDir()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--global", "--claude-md"]);

        Assert.Equal(0, exit);
        var claudeMdPath = Path.Combine(env.ClaudeDir, "CLAUDE.md");
        Assert.True(File.Exists(claudeMdPath));
        Assert.Contains("<!-- rtk-instructions", File.ReadAllText(claudeMdPath));
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "settings.json")));
        Assert.Contains("Claude Code will now use rtk in all sessions", console.Out.ToString());
    }

    [Fact]
    public void Uninstall_RoundTrip_RemovesEverythingAndReportsCorrectly()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);

        InitCommand.Run(["--global", "--auto-patch"]);
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "RTK.md")));
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "settings.json")));

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["--global", "--uninstall"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "RTK.md")));
        // CLAUDE.md contained only the @RTK.md reference, so it becomes empty after cleanup and is deleted.
        Assert.False(File.Exists(Path.Combine(env.ClaudeDir, "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "settings.json")));
        Assert.Equal("{\n  \"hooks\": {\n    \"PreToolUse\": []\n  }\n}", File.ReadAllText(Path.Combine(env.ClaudeDir, "settings.json")));

        var stdout = console.Out.ToString();
        Assert.Contains("RTK uninstalled:", stdout);
        Assert.Contains("RTK.md:", stdout);
        Assert.Contains("CLAUDE.md: removed (was empty after cleanup)", stdout);
        Assert.Contains("settings.json: removed RTK hook entry", stdout);
        Assert.Contains("Restart Claude Code, OpenCode, and Cursor (if used) to apply changes.", stdout);

        // Second uninstall: nothing left to remove.
        using var console2 = new ConsoleCapture();
        var exit2 = InitCommand.Run(["--global", "--uninstall"]);
        Assert.Equal(0, exit2);
        Assert.Contains("RTK was not installed (nothing to remove)", console2.Out.ToString());
    }

    [Fact]
    public void Uninstall_DryRun_ReportsWithoutWritingAndPrintsFooter()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        InitCommand.Run(["--global", "--auto-patch"]);
        var settingsBefore = File.ReadAllText(Path.Combine(env.ClaudeDir, "settings.json"));

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["--global", "--uninstall", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "RTK.md")));
        Assert.True(File.Exists(Path.Combine(env.ClaudeDir, "CLAUDE.md")));
        Assert.Equal(settingsBefore, File.ReadAllText(Path.Combine(env.ClaudeDir, "settings.json")));

        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would uninstall RTK:", stdout);
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void Show_EmptyState_ReportsAllArtifactsNotFound()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--global", "--show"]);

        Assert.Equal(0, exit);
        var stdout = console.Out.ToString();
        Assert.Equal(
            "rtk Configuration:\n" +
            "\n" +
            "[--] Hook: not found\n" +
            "[--] RTK.md: not found\n" +
            "[--] Global (~/.claude/CLAUDE.md): not found\n" +
            "[--] Local (./CLAUDE.md): not found\n" +
            "[--] settings.json: not found\n" +
            "[--] OpenCode: plugin not found\n" +
            "[--] Cursor hook: not found\n" +
            "\n" +
            "Usage:\n" +
            "  rtk init              # Full injection into local CLAUDE.md\n" +
            "  rtk init -g           # Hook + RTK.md + @RTK.md + settings.json (recommended)\n" +
            "  rtk init -g --auto-patch    # Same as above but no prompt\n" +
            "  rtk init -g --no-patch      # Skip settings.json (manual setup)\n" +
            "  rtk init -g --uninstall     # Remove all RTK artifacts\n" +
            "  rtk init -g --claude-md     # Legacy: full injection into ~/.claude/CLAUDE.md\n" +
            "  rtk init -g --hook-only     # Hook only, no RTK.md\n" +
            "  rtk init --codex            # Configure local AGENTS.md + RTK.md\n" +
            "  rtk init -g --codex         # Configure $CODEX_HOME/AGENTS.md + $CODEX_HOME/RTK.md (or ~/.codex/)\n" +
            "  rtk init -g --opencode      # OpenCode plugin only\n" +
            "  rtk init -g --agent cursor  # Install Cursor Agent hooks\n",
            stdout);
    }

    [Fact]
    public void Show_AfterDefaultInit_ReportsInstalledStatus()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var cwd = new CwdGuard(tmp.Root);

        InitCommand.Run(["--global", "--auto-patch"]);

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["--global", "--show"]);

        Assert.Equal(0, exit);
        var stdout = console.Out.ToString();
        Assert.Contains("[ok] Hook: rtk hook claude (native binary command)", stdout);
        Assert.Contains("[ok] RTK.md:", stdout);
        Assert.Contains("(slim mode)", stdout);
        Assert.Contains("[ok] Global (~/.claude/CLAUDE.md): @RTK.md reference", stdout);
        Assert.Contains("[--] Local (./CLAUDE.md): not found", stdout);
        Assert.Contains("[ok] settings.json: RTK hook configured", stdout);
        Assert.Contains("[--] OpenCode: plugin not found", stdout);
        Assert.Contains("[--] Cursor hook: not found", stdout);
    }

    [Fact]
    public void Show_CodexCombo_FailsLoudAsNotYetImplemented()
    {
        using var tmp = new TempDir();
        using var env = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--show", "--codex"]);

        Assert.Equal(1, exit);
        Assert.Contains("not yet implemented", console.Error.ToString());
    }
}
