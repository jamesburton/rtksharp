using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Tests for the GitHub Copilot instructions-file + hook-config path
/// (<c>rtk init --copilot</c> / <c>rtk init --copilot --global</c>): project- and global-scope
/// install/uninstall round-trips, idempotent re-install, and dry-run. Exercises
/// <see cref="CopilotInit"/> directly (via its <c>internal</c> <c>*At</c> overloads) rather than
/// through <see cref="InitCommand"/>, since Copilot dispatch is not yet wired into
/// <c>InitCommand.cs</c> (central wiring lands in a follow-up task — see
/// <c>docs/superpowers/plans/2026-07-03-phase9b-init-hooks.md</c>). Oracle-derived expectations were
/// captured against <c>src/hooks/init.rs</c>'s <c>run_copilot</c>/<c>run_copilot_global</c>/
/// <c>uninstall_copilot</c>/<c>uninstall_copilot_global</c> and their own unit tests
/// (init.rs:6446-6800).
/// </summary>
public sealed class CopilotInitTests
{
    [Fact]
    public void ProjectScope_Install_WritesInstructionsAndHookConfig()
    {
        using var tmp = new TempDir();

        CopilotInit.RunProjectAt(tmp.Root, InitContext.Default);

        var instructionsPath = Path.Combine(tmp.Root, ".github", "copilot-instructions.md");
        var hookPath = Path.Combine(tmp.Root, ".github", "hooks", "rtk-rewrite.json");

        Assert.True(File.Exists(instructionsPath));
        Assert.True(File.Exists(hookPath));
        Assert.Equal(CopilotInit.CopilotInstructions, File.ReadAllText(instructionsPath));
        Assert.Equal(CopilotInit.CopilotHookJson, File.ReadAllText(hookPath));
    }

    [Fact]
    public void ProjectScope_Install_PrintsExpectedSuccessMessage()
    {
        using var tmp = new TempDir();
        using var console = new ConsoleCapture();

        CopilotInit.RunProjectAt(tmp.Root, InitContext.Default);

        var stdout = console.Out.ToString();
        Assert.Contains("GitHub Copilot integration installed (project-scoped).", stdout);
        Assert.Contains("Works with VS Code Copilot Chat (transparent rewrite)", stdout);
        Assert.Contains("and Copilot CLI (deny-with-suggestion).", stdout);
        Assert.Contains("Restart your IDE or Copilot CLI session to activate.", stdout);
    }

    [Fact]
    public void ProjectScope_Install_PreservesExistingInstructionsContent()
    {
        using var tmp = new TempDir();
        var githubDir = Path.Combine(tmp.Root, ".github");
        Directory.CreateDirectory(githubDir);
        var instructionsPath = Path.Combine(githubDir, "copilot-instructions.md");
        File.WriteAllText(instructionsPath, "# Team Copilot rules\n\nDo the thing.\n");

        CopilotInit.RunProjectAt(tmp.Root, InitContext.Default);

        var content = File.ReadAllText(instructionsPath);
        Assert.Contains("# Team Copilot rules", content);
        Assert.Contains("Do the thing.", content);
        Assert.Contains(InitArtifacts.RtkBlockStart, content);
    }

    [Fact]
    public void ProjectScope_ReRun_IsIdempotent()
    {
        using var tmp = new TempDir();

        CopilotInit.RunProjectAt(tmp.Root, InitContext.Default);
        var instructionsPath = Path.Combine(tmp.Root, ".github", "copilot-instructions.md");
        var hookPath = Path.Combine(tmp.Root, ".github", "hooks", "rtk-rewrite.json");
        var instructionsAfterFirst = File.ReadAllText(instructionsPath);
        var hookAfterFirst = File.ReadAllText(hookPath);

        using var console = new ConsoleCapture();
        CopilotInit.RunProjectAt(tmp.Root, InitContext.Default);

        Assert.Equal(instructionsAfterFirst, File.ReadAllText(instructionsPath));
        Assert.Equal(hookAfterFirst, File.ReadAllText(hookPath));
        Assert.Contains("Copilot instructions already up to date", console.Out.ToString());
    }

    [Fact]
    public void ProjectScope_DryRun_WritesNothing_AndPrintsFooter()
    {
        using var tmp = new TempDir();
        using var console = new ConsoleCapture();

        CopilotInit.RunProjectAt(tmp.Root, new InitContext(DryRun: true));

        Assert.False(File.Exists(Path.Combine(tmp.Root, ".github", "copilot-instructions.md")));
        Assert.False(File.Exists(Path.Combine(tmp.Root, ".github", "hooks", "rtk-rewrite.json")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Root, ".github")));

        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would add Copilot instructions to", stdout);
        Assert.Contains("[dry-run] would create Copilot hook config", stdout);
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void ProjectScope_UninstallRoundTrip_RemovesHookAndBlock()
    {
        using var tmp = new TempDir();
        CopilotInit.RunProjectAt(tmp.Root, InitContext.Default);

        using var console = new ConsoleCapture();
        var removed = CopilotInit.UninstallProjectAt(tmp.Root, InitContext.Default);

        Assert.Equal(2, removed.Count);
        Assert.False(File.Exists(Path.Combine(tmp.Root, ".github", "hooks", "rtk-rewrite.json")));

        var instructionsPath = Path.Combine(tmp.Root, ".github", "copilot-instructions.md");
        Assert.True(File.Exists(instructionsPath));
        Assert.DoesNotContain(InitArtifacts.RtkBlockStart, File.ReadAllText(instructionsPath));
    }

    [Fact]
    public void ProjectScope_Uninstall_PreservesUserInstructionsContent()
    {
        using var tmp = new TempDir();
        CopilotInit.RunProjectAt(tmp.Root, InitContext.Default);
        var instructionsPath = Path.Combine(tmp.Root, ".github", "copilot-instructions.md");
        var withUserContent = "# Team Copilot rules\n\n" + File.ReadAllText(instructionsPath);
        File.WriteAllText(instructionsPath, withUserContent);

        CopilotInit.UninstallProjectAt(tmp.Root, InitContext.Default);

        var content = File.ReadAllText(instructionsPath);
        Assert.Contains("# Team Copilot rules", content);
        Assert.DoesNotContain(InitArtifacts.RtkBlockStart, content);
    }

    [Fact]
    public void ProjectScope_Uninstall_WhenNothingInstalled_ReportsNothingToRemove()
    {
        using var tmp = new TempDir();

        var removed = CopilotInit.UninstallProjectAt(tmp.Root, InitContext.Default);

        Assert.Empty(removed);
    }

    [Fact]
    public void ProjectScope_Uninstall_DryRun_KeepsFiles()
    {
        using var tmp = new TempDir();
        CopilotInit.RunProjectAt(tmp.Root, InitContext.Default);

        using var console = new ConsoleCapture();
        CopilotInit.UninstallProjectAt(tmp.Root, new InitContext(DryRun: true));

        Assert.True(File.Exists(Path.Combine(tmp.Root, ".github", "hooks", "rtk-rewrite.json")));
        var instructionsPath = Path.Combine(tmp.Root, ".github", "copilot-instructions.md");
        Assert.Contains(InitArtifacts.RtkBlockStart, File.ReadAllText(instructionsPath));

        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would remove hook config:", stdout);
        Assert.Contains("[dry-run] would remove rtk-instructions block from", stdout);
    }

    [Fact]
    public void GlobalScope_Install_WritesDirectlyUnderCopilotDir_NoGithubJoin()
    {
        using var tmp = new TempDir();
        var copilotDir = Path.Combine(tmp.Root, ".copilot");

        CopilotInit.RunGlobalAt(copilotDir, InitContext.Default);

        var instructionsPath = Path.Combine(copilotDir, "copilot-instructions.md");
        var hookPath = Path.Combine(copilotDir, "hooks", "rtk-rewrite.json");
        Assert.True(File.Exists(instructionsPath));
        Assert.True(File.Exists(hookPath));
        Assert.False(Directory.Exists(Path.Combine(copilotDir, ".github")));
    }

    [Fact]
    public void GlobalScope_Install_PrintsExpectedSuccessMessage()
    {
        using var tmp = new TempDir();
        var copilotDir = Path.Combine(tmp.Root, ".copilot");
        using var console = new ConsoleCapture();

        CopilotInit.RunGlobalAt(copilotDir, InitContext.Default);

        var stdout = console.Out.ToString();
        Assert.Contains("GitHub Copilot global integration installed (user-scoped).", stdout);
        Assert.Contains("Applies to all Copilot CLI sessions on this machine.", stdout);
        Assert.Contains("Restart your Copilot CLI session to activate.", stdout);
    }

    [Fact]
    public void GlobalScope_UsesCopilotHomeEnvVar_WhenSet()
    {
        using var tmp = new TempDir();
        using var env = new CopilotScopeGuard(tmp);

        Assert.Equal(env.CopilotDir, CopilotInit.CopilotUserDir());
    }

    [Fact]
    public void GlobalScope_UninstallRoundTrip_RemovesHookAndBlock()
    {
        using var tmp = new TempDir();
        var copilotDir = Path.Combine(tmp.Root, ".copilot");
        CopilotInit.RunGlobalAt(copilotDir, InitContext.Default);

        var removed = CopilotInit.UninstallGlobalAt(copilotDir, InitContext.Default);

        Assert.Equal(2, removed.Count);
        Assert.False(File.Exists(Path.Combine(copilotDir, "hooks", "rtk-rewrite.json")));
        var instructionsPath = Path.Combine(copilotDir, "copilot-instructions.md");
        Assert.True(File.Exists(instructionsPath));
        Assert.DoesNotContain(InitArtifacts.RtkBlockStart, File.ReadAllText(instructionsPath));
    }

    [Fact]
    public void GlobalScope_Uninstall_WhenNothingInstalled_ReportsNothingToRemove()
    {
        using var tmp = new TempDir();
        using var env = new CopilotScopeGuard(tmp);
        using var console = new ConsoleCapture();

        CopilotInit.UninstallCopilotGlobal(InitContext.Default);

        Assert.Contains("RTK global Copilot support was not installed (nothing to remove)", console.Out.ToString());
    }

    [Fact]
    public void GlobalScope_Uninstall_DryRun_KeepsFiles()
    {
        using var tmp = new TempDir();
        var copilotDir = Path.Combine(tmp.Root, ".copilot");
        CopilotInit.RunGlobalAt(copilotDir, InitContext.Default);

        using var console = new ConsoleCapture();
        CopilotInit.UninstallGlobalAt(copilotDir, new InitContext(DryRun: true));

        Assert.True(File.Exists(Path.Combine(copilotDir, "hooks", "rtk-rewrite.json")));
        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would remove hook config:", stdout);
    }
}
