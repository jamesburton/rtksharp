using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// End-to-end and primitive-level tests for the Gemini CLI hook path (<c>rtk init -g --gemini</c>):
/// install (hook script + integrity hash + GEMINI.md + settings.json <c>BeforeTool</c> entry),
/// idempotent re-install, uninstall round-trip, and dry-run. <see cref="GeminiInit"/> is not yet
/// wired into <c>InitCommand</c>'s dispatch (that central wiring lands in a follow-up task), so these
/// tests call <see cref="GeminiInit.Run"/>/<see cref="GeminiInit.Uninstall"/> directly rather than
/// through <c>InitCommand.Run(string[])</c>. Global scope is isolated via
/// <see cref="GeminiScopeGuard"/> (a Gemini-specific analog of <see cref="CodexScopeGuard"/>) so no
/// real <c>~/.gemini</c> is ever touched. Oracle-derived expectations were captured against
/// <c>src/hooks/init.rs</c>'s <c>run_gemini</c>/<c>patch_gemini_settings</c>/<c>uninstall_gemini</c>
/// (init.rs:3599-3868).
/// </summary>
public sealed class GeminiInitTests
{
    [Fact]
    public void Install_WritesHookScript_GeminiMd_SettingsEntry_AndHashSidecar()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        using var console = new ConsoleCapture();

        GeminiInit.Run(global: true, hookOnly: false, PatchMode.Auto, InitContext.Default);

        var hookPath = Path.Combine(env.GeminiDir, "hooks", "rtk-hook-gemini.sh");
        var hashPath = Path.Combine(env.GeminiDir, "hooks", ".rtk-hook.sha256");
        var geminiMdPath = Path.Combine(env.GeminiDir, "GEMINI.md");
        var settingsPath = Path.Combine(env.GeminiDir, "settings.json");

        Assert.True(File.Exists(hookPath));
        Assert.Equal("#!/bin/bash\nexec rtk hook gemini\n", File.ReadAllText(hookPath));

        Assert.True(File.Exists(hashPath));
        Assert.Contains(Integrity.ComputeHash(hookPath), File.ReadAllText(hashPath));

        Assert.True(File.Exists(geminiMdPath));
        Assert.Equal(InitArtifacts.RtkSlim, File.ReadAllText(geminiMdPath));

        Assert.True(File.Exists(settingsPath));
        var settingsContent = File.ReadAllText(settingsPath);
        Assert.Contains("\"BeforeTool\"", settingsContent);
        Assert.Contains("\"run_shell_command\"", settingsContent);
        Assert.Contains(hookPath.Replace("\\", "\\\\"), settingsContent);

        var stdout = console.Out.ToString();
        Assert.Contains("Gemini CLI hook installed (global).", stdout);
        Assert.Contains($"Hook: {hookPath}", stdout);
        Assert.Contains($"GEMINI.md: {geminiMdPath}", stdout);
        Assert.Contains("Restart Gemini CLI. Test with: git status", stdout);
    }

    [Fact]
    public void Install_HookOnly_SkipsGeminiMd()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        using var console = new ConsoleCapture();

        GeminiInit.Run(global: true, hookOnly: true, PatchMode.Auto, InitContext.Default);

        Assert.False(File.Exists(Path.Combine(env.GeminiDir, "GEMINI.md")));
        Assert.True(File.Exists(Path.Combine(env.GeminiDir, "hooks", "rtk-hook-gemini.sh")));

        var stdout = console.Out.ToString();
        Assert.DoesNotContain("GEMINI.md:", stdout);
    }

    [Fact]
    public void Install_NotGlobal_ThrowsExactRustMessage()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);

        var ex = Assert.Throws<InitAbortException>(() =>
            GeminiInit.Run(global: false, hookOnly: false, PatchMode.Auto, InitContext.Default));

        Assert.Equal("Gemini support is global-only. Use: rtk init -g --gemini", ex.Message);
    }

    [Fact]
    public void Install_ReRun_IsIdempotent_ReportsAlreadyPresentAndDoesNotDuplicateSettingsEntry()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);

        GeminiInit.Run(global: true, hookOnly: false, PatchMode.Auto, InitContext.Default);
        var settingsPath = Path.Combine(env.GeminiDir, "settings.json");
        var settingsAfterFirst = File.ReadAllText(settingsPath);

        using var console = new ConsoleCapture();
        GeminiInit.Run(global: true, hookOnly: false, PatchMode.Auto, new InitContext(Verbose: 1));
        var settingsAfterSecond = File.ReadAllText(settingsPath);

        Assert.Equal(settingsAfterFirst, settingsAfterSecond);
        Assert.Contains("Gemini settings.json already has RTK hook", console.Error.ToString());
    }

    [Fact]
    public void Install_DryRun_WritesNothing_AndPrintsFooter()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        using var console = new ConsoleCapture();

        GeminiInit.Run(global: true, hookOnly: false, PatchMode.Auto, new InitContext(DryRun: true));

        Assert.False(Directory.Exists(Path.Combine(env.GeminiDir, "hooks")));
        Assert.False(File.Exists(Path.Combine(env.GeminiDir, "GEMINI.md")));
        Assert.False(File.Exists(Path.Combine(env.GeminiDir, "settings.json")));

        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would create Gemini hook", stdout);
        Assert.Contains("[dry-run] would create GEMINI.md", stdout);
        Assert.Contains("[dry-run] would patch Gemini settings.json", stdout);
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void Uninstall_RemovesHookScript_GeminiMd_AndSettingsEntry()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);

        GeminiInit.Run(global: true, hookOnly: false, PatchMode.Auto, InitContext.Default);

        using var console = new ConsoleCapture();
        GeminiInit.Uninstall(InitContext.Default);

        var hookPath = Path.Combine(env.GeminiDir, "hooks", "rtk-hook-gemini.sh");
        var geminiMdPath = Path.Combine(env.GeminiDir, "GEMINI.md");
        var settingsPath = Path.Combine(env.GeminiDir, "settings.json");

        Assert.False(File.Exists(hookPath));
        Assert.False(File.Exists(geminiMdPath));
        Assert.True(File.Exists(settingsPath));

        var settingsContent = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("run_shell_command", settingsContent);

        var stdout = console.Out.ToString();
        Assert.Contains("RTK uninstalled (Gemini):", stdout);
        Assert.Contains($"Gemini hook: {hookPath}", stdout);
        Assert.Contains($"GEMINI.md: {geminiMdPath}", stdout);
        Assert.Contains("Gemini settings.json: removed RTK hook entry", stdout);
        Assert.Contains("Restart Gemini CLI to apply changes.", stdout);
    }

    [Fact]
    public void Uninstall_NothingInstalled_ReportsNothingToRemove()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        using var console = new ConsoleCapture();

        GeminiInit.Uninstall(InitContext.Default);

        Assert.Equal("RTK Gemini support was not installed (nothing to remove)\n", console.Out.ToString());
    }

    [Fact]
    public void Uninstall_DryRun_WritesNothing_AndPrintsFooter()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);

        GeminiInit.Run(global: true, hookOnly: false, PatchMode.Auto, InitContext.Default);

        using var console = new ConsoleCapture();
        GeminiInit.Uninstall(new InitContext(DryRun: true));

        var hookPath = Path.Combine(env.GeminiDir, "hooks", "rtk-hook-gemini.sh");
        var geminiMdPath = Path.Combine(env.GeminiDir, "GEMINI.md");

        Assert.True(File.Exists(hookPath));
        Assert.True(File.Exists(geminiMdPath));

        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would uninstall RTK (Gemini):", stdout);
        Assert.Contains($"[dry-run] would remove Gemini hook: {hookPath}", stdout);
        Assert.Contains($"[dry-run] would remove GEMINI.md: {geminiMdPath}", stdout);
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void PatchMode_Skip_PrintsManualInstructions_AndWritesNoSettingsEntry()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        using var console = new ConsoleCapture();

        GeminiInit.Run(global: true, hookOnly: true, PatchMode.Skip, InitContext.Default);

        Assert.False(File.Exists(Path.Combine(env.GeminiDir, "settings.json")));
        var stdout = console.Out.ToString();
        Assert.Contains("Manual setup needed: add RTK hook to", stdout);
        Assert.Contains("https://github.com/rtk-ai/rtk#gemini-cli", stdout);
    }

    [Fact]
    public void PatchMode_Ask_DeclinedAnswer_DoesNotAppendHookEntry()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        using var console = new ConsoleCapture();
        var previousIn = Console.In;

        try
        {
            // Explicitly supply "n" via a StringReader rather than relying on real stdin (which
            // would block indefinitely under a test host with an attached, non-redirected console).
            Console.SetIn(new StringReader("n\n"));
            GeminiInit.Run(global: true, hookOnly: true, PatchMode.Ask, InitContext.Default);
        }
        finally
        {
            Console.SetIn(previousIn);
        }

        var settingsPath = Path.Combine(env.GeminiDir, "settings.json");
        if (File.Exists(settingsPath))
        {
            Assert.DoesNotContain("run_shell_command", File.ReadAllText(settingsPath));
        }

        Assert.Contains("Skipped. Add hook manually later.", console.Out.ToString());
    }

    [Fact]
    public void PatchMode_Ask_AcceptedAnswer_AppendsHookEntry()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        var previousIn = Console.In;

        try
        {
            Console.SetIn(new StringReader("y\n"));
            GeminiInit.Run(global: true, hookOnly: true, PatchMode.Ask, InitContext.Default);
        }
        finally
        {
            Console.SetIn(previousIn);
        }

        var settingsPath = Path.Combine(env.GeminiDir, "settings.json");
        Assert.True(File.Exists(settingsPath));
        Assert.Contains("run_shell_command", File.ReadAllText(settingsPath));
    }

    [Fact]
    public void PatchGeminiSettings_PreservesExistingUnrelatedSettings()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        Directory.CreateDirectory(env.GeminiDir);
        File.WriteAllText(Path.Combine(env.GeminiDir, "settings.json"), "{\n  \"theme\": \"dark\"\n}");

        GeminiInit.Run(global: true, hookOnly: true, PatchMode.Auto, InitContext.Default);

        var content = File.ReadAllText(Path.Combine(env.GeminiDir, "settings.json"));
        Assert.Contains("\"theme\": \"dark\"", content);
        Assert.Contains("\"BeforeTool\"", content);
    }

    [Fact]
    public void PatchGeminiSettings_MalformedJson_FallsBackToEmptyObject()
    {
        using var tmp = new TempDir();
        using var env = new GeminiScopeGuard(tmp);
        Directory.CreateDirectory(env.GeminiDir);
        File.WriteAllText(Path.Combine(env.GeminiDir, "settings.json"), "{ not valid json");

        GeminiInit.Run(global: true, hookOnly: true, PatchMode.Auto, InitContext.Default);

        var content = File.ReadAllText(Path.Combine(env.GeminiDir, "settings.json"));
        Assert.Contains("\"BeforeTool\"", content);
    }
}

/// <summary>
/// Redirects <see cref="GeminiInit.GeminiDirOverrideEnvVar"/> so Gemini-mode global-scope tests never
/// touch the real <c>~/.gemini</c> directory on the machine running the tests. Rust's
/// <c>resolve_gemini_dir</c> has no such override (see that constant's remarks); this is a
/// test/parity-only escape hatch analogous to <see cref="CodexScopeGuard"/>.
/// </summary>
internal sealed class GeminiScopeGuard : IDisposable
{
    private readonly string? _previous;

    /// <summary>The throwaway directory standing in for <c>~/.gemini</c>.</summary>
    public string GeminiDir { get; }

    public GeminiScopeGuard(TempDir tmp)
    {
        InitTestSupport.EnterEnvLock();

        GeminiDir = Path.Combine(tmp.Root, ".gemini");

        _previous = Environment.GetEnvironmentVariable(GeminiInit.GeminiDirOverrideEnvVar);
        Environment.SetEnvironmentVariable(GeminiInit.GeminiDirOverrideEnvVar, GeminiDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GeminiInit.GeminiDirOverrideEnvVar, _previous);
        InitTestSupport.ExitEnvLock();
    }
}
