using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// End-to-end and primitive-level tests for the Codex CLI instruction-mode path
/// (<c>rtk init --codex</c> / <c>rtk init -g --codex</c>): project- and global-scope install,
/// idempotency, dry-run, the <c>AGENTS.md</c> reference upsert (added/already-present/migrated/stale
/// block), uninstall round-trip, and <c>--show --codex</c>. Global-scope tests isolate
/// <c>CODEX_HOME</c> via <see cref="CodexScopeGuard"/> so no real <c>~/.codex</c> is ever touched.
/// Oracle-derived expectations were captured against <c>src/hooks/init.rs</c>'s <c>run_codex_mode</c>/
/// <c>uninstall_codex</c>/<c>show_codex_config</c> and their unit tests.
/// </summary>
public sealed class CodexInitTests
{
    [Fact]
    public void ProjectScope_DefaultInit_WritesRtkMdAndAgentsMdReference()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--codex"]);

        Assert.Equal(0, exit);
        var rtkMdPath = Path.Combine(tmp.Root, "RTK.md");
        var agentsMdPath = Path.Combine(tmp.Root, "AGENTS.md");
        Assert.True(File.Exists(rtkMdPath));
        Assert.Equal(CodexInit.RtkSlimCodex, File.ReadAllText(rtkMdPath));
        Assert.Equal("@RTK.md\n", File.ReadAllText(agentsMdPath));

        var stdout = console.Out.ToString();
        Assert.Contains("RTK configured for Codex CLI.", stdout);
        Assert.Contains("AGENTS.md: @RTK.md reference added", stdout);
        // Project scope prints the bare relative path (Rust's `PathBuf::from("AGENTS.md")`), never
        // resolved to an absolute path.
        Assert.Contains("Codex project instructions path: AGENTS.md", stdout);
    }

    [Fact]
    public void ProjectScope_ReRun_IsIdempotent_ReportsAlreadyPresent()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        InitCommand.Run(["--codex"]);
        var agentsMdAfterFirst = File.ReadAllText(Path.Combine(tmp.Root, "AGENTS.md"));

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["--codex"]);
        var agentsMdAfterSecond = File.ReadAllText(Path.Combine(tmp.Root, "AGENTS.md"));

        Assert.Equal(0, exit);
        Assert.Equal(agentsMdAfterFirst, agentsMdAfterSecond);
        Assert.Contains("AGENTS.md: @RTK.md reference already present", console.Out.ToString());
    }

    [Fact]
    public void ProjectScope_DryRun_WritesNothing_AndPrintsFooter()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--codex", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(tmp.Root, "RTK.md")));
        Assert.False(File.Exists(Path.Combine(tmp.Root, "AGENTS.md")));

        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would create RTK.md", stdout);
        Assert.Contains("[dry-run] would add @RTK.md reference to AGENTS.md", stdout);
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void ProjectScope_PreservesExistingAgentsMdContent()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        File.WriteAllText(Path.Combine(tmp.Root, "AGENTS.md"), "# Team rules\n\nDo the thing.\n");

        var exit = InitCommand.Run(["--codex"]);

        Assert.Equal(0, exit);
        var content = File.ReadAllText(Path.Combine(tmp.Root, "AGENTS.md"));
        Assert.Equal("# Team rules\n\nDo the thing.\n\n@RTK.md\n", content);
    }

    [Theory]
    [InlineData("--opencode", "--codex cannot be combined with --opencode")]
    [InlineData("--claude-md", "--codex cannot be combined with --claude-md")]
    [InlineData("--hook-only", "--codex cannot be combined with --hook-only")]
    [InlineData("--auto-patch", "--codex cannot be combined with --auto-patch")]
    [InlineData("--no-patch", "--codex cannot be combined with --no-patch")]
    public void Codex_CombinedWithIncompatibleFlag_FailsWithExactRustMessage(string flag, string expectedMessage)
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--codex", flag]);

        Assert.Equal(1, exit);
        Assert.Equal($"rtk: {expectedMessage}\n", console.Error.ToString());
    }

    [Fact]
    public void GlobalScope_DefaultInit_WritesAbsoluteReferenceToCodexHome()
    {
        using var tmp = new TempDir();
        using var env = new CodexScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["-g", "--codex"]);

        Assert.Equal(0, exit);
        var rtkMdPath = Path.Combine(env.CodexDir, "RTK.md");
        var agentsMdPath = Path.Combine(env.CodexDir, "AGENTS.md");
        Assert.True(File.Exists(rtkMdPath));
        Assert.Equal(CodexInit.RtkSlimCodex, File.ReadAllText(rtkMdPath));

        var expectedRef = $"@{rtkMdPath}";
        Assert.Equal($"{expectedRef}\n", File.ReadAllText(agentsMdPath));

        var stdout = console.Out.ToString();
        Assert.Contains($"AGENTS.md: {expectedRef} reference added", stdout);
        Assert.Contains($"Codex global instructions path: {agentsMdPath}", stdout);
    }

    [Fact]
    public void GlobalScope_ReRun_IsIdempotent()
    {
        using var tmp = new TempDir();
        using var env = new CodexScopeGuard(tmp);

        InitCommand.Run(["-g", "--codex"]);
        var agentsMdAfterFirst = File.ReadAllText(Path.Combine(env.CodexDir, "AGENTS.md"));

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["-g", "--codex"]);
        var agentsMdAfterSecond = File.ReadAllText(Path.Combine(env.CodexDir, "AGENTS.md"));

        Assert.Equal(0, exit);
        Assert.Equal(agentsMdAfterFirst, agentsMdAfterSecond);
        Assert.Contains("reference already present", console.Out.ToString());
    }

    [Fact]
    public void GlobalScope_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        using var env = new CodexScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["-g", "--codex", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(env.CodexDir, "RTK.md")));
        Assert.False(File.Exists(Path.Combine(env.CodexDir, "AGENTS.md")));
        Assert.EndsWith("\n[dry-run] Nothing written.\n", console.Out.ToString());
    }

    [Fact]
    public void GlobalScope_MigratesBareReferenceToAbsolute()
    {
        using var tmp = new TempDir();
        using var env = new CodexScopeGuard(tmp);
        Directory.CreateDirectory(env.CodexDir);
        File.WriteAllText(Path.Combine(env.CodexDir, "AGENTS.md"), "# Notes\n\n@RTK.md\n");

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["-g", "--codex"]);

        Assert.Equal(0, exit);
        var expectedRef = $"@{Path.Combine(env.CodexDir, "RTK.md")}";
        Assert.Equal($"# Notes\n\n{expectedRef}\n", File.ReadAllText(Path.Combine(env.CodexDir, "AGENTS.md")));
        Assert.Contains($"AGENTS.md: {expectedRef} reference added", console.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_Uninstall_RoundTrip_RemovesEverything()
    {
        using var tmp = new TempDir();
        using var env = new CodexScopeGuard(tmp);

        InitCommand.Run(["-g", "--codex"]);
        Assert.True(File.Exists(Path.Combine(env.CodexDir, "RTK.md")));
        Assert.True(File.Exists(Path.Combine(env.CodexDir, "AGENTS.md")));

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["-g", "--codex", "--uninstall"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(env.CodexDir, "RTK.md")));
        // Unlike Claude's uninstall (which deletes CLAUDE.md if it becomes empty after cleanup),
        // Rust's uninstall_codex_at never deletes AGENTS.md itself — it only rewrites the file with
        // the reference line stripped, even if that leaves it empty.
        Assert.True(File.Exists(Path.Combine(env.CodexDir, "AGENTS.md")));
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(env.CodexDir, "AGENTS.md")));

        var stdout = console.Out.ToString();
        Assert.Contains("RTK uninstalled for Codex CLI:", stdout);
        Assert.Contains("RTK.md:", stdout);
        Assert.Contains("AGENTS.md: removed @RTK.md reference", stdout);

        using var console2 = new ConsoleCapture();
        var exit2 = InitCommand.Run(["-g", "--codex", "--uninstall"]);
        Assert.Equal(0, exit2);
        Assert.Contains("RTK was not installed for Codex CLI (nothing to remove)", console2.Out.ToString());
    }

    [Fact]
    public void GlobalScope_Uninstall_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        using var env = new CodexScopeGuard(tmp);

        InitCommand.Run(["-g", "--codex"]);
        var rtkMdBefore = File.ReadAllText(Path.Combine(env.CodexDir, "RTK.md"));
        var agentsMdBefore = File.ReadAllText(Path.Combine(env.CodexDir, "AGENTS.md"));

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["-g", "--codex", "--uninstall", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.Equal(rtkMdBefore, File.ReadAllText(Path.Combine(env.CodexDir, "RTK.md")));
        Assert.Equal(agentsMdBefore, File.ReadAllText(Path.Combine(env.CodexDir, "AGENTS.md")));

        var stdout = console.Out.ToString();
        Assert.Contains("[dry-run] would uninstall RTK for Codex CLI:", stdout);
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void Uninstall_Codex_WithoutGlobal_FailsWithExactRustMessage()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--codex", "--uninstall"]);

        Assert.Equal(1, exit);
        Assert.Equal(
            "rtk: Uninstall only works with --global flag. For local projects, manually remove RTK from AGENTS.md\n",
            console.Error.ToString());
    }

    [Fact]
    public void Show_Codex_EmptyState_ReportsAllArtifactsNotFound()
    {
        using var tmp = new TempDir();
        using var env = new CodexScopeGuard(tmp);
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--show", "--codex"]);

        Assert.Equal(0, exit);
        Assert.Equal(
            "rtk Configuration (Codex CLI):\n" +
            "\n" +
            "[--] Global RTK.md: not found\n" +
            "[--] Global AGENTS.md: not found\n" +
            "[--] Local RTK.md: not found\n" +
            "[--] Local AGENTS.md: not found\n" +
            "\n" +
            "Usage:\n" +
            "  rtk init --codex              # Configure local AGENTS.md + RTK.md\n" +
            "  rtk init -g --codex           # Configure $CODEX_HOME/AGENTS.md + $CODEX_HOME/RTK.md (or ~/.codex/)\n" +
            "  rtk init -g --codex --uninstall  # Remove global Codex RTK artifacts\n",
            console.Out.ToString());
    }

    [Fact]
    public void Show_Codex_AfterGlobalInit_ReportsInstalledStatus()
    {
        using var tmp = new TempDir();
        using var env = new CodexScopeGuard(tmp);
        using var cwd = new CwdGuard(tmp.Root);

        InitCommand.Run(["-g", "--codex"]);

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run(["--show", "--codex"]);

        Assert.Equal(0, exit);
        var stdout = console.Out.ToString();
        Assert.Contains("[ok] Global RTK.md:", stdout);
        Assert.Contains("[ok] Global AGENTS.md: RTK.md reference", stdout);
        Assert.Contains("[--] Local RTK.md: not found", stdout);
        Assert.Contains("[--] Local AGENTS.md: not found", stdout);
    }

    // ===================== primitive-level tests (InitArtifacts.RemoveRtkBlock reuse) =====================

    [Fact]
    public void UninstallCodexAt_RemovesStaleInlineBlock_ButPreservesUserContent()
    {
        using var tmp = new TempDir();
        var codexDir = tmp.Root;
        var agentsMd = Path.Combine(codexDir, "AGENTS.md");
        var rtkMd = Path.Combine(codexDir, "RTK.md");

        File.WriteAllText(
            agentsMd,
            $"# Team rules\n\n{InitArtifacts.RtkBlockStart} v2 -->\nOLD RTK STUFF\n{InitArtifacts.RtkBlockEnd}\n\nMore content");
        File.WriteAllText(rtkMd, "codex config");

        var removed = CodexInit.UninstallCodexAt(codexDir, InitContext.Default);

        var content = File.ReadAllText(agentsMd);
        Assert.DoesNotContain("OLD RTK STUFF", content);
        Assert.Contains("# Team rules", content);
        Assert.Contains("More content", content);
        Assert.Contains(removed, r => r.Contains("rtk-instructions block"));
    }

    [Fact]
    public void UninstallCodexAt_IsIdempotent()
    {
        using var tmp = new TempDir();
        var codexDir = tmp.Root;
        var agentsMd = Path.Combine(codexDir, "AGENTS.md");
        var rtkMd = Path.Combine(codexDir, "RTK.md");

        File.WriteAllText(agentsMd, "# Team rules\n\n@RTK.md\n");
        File.WriteAllText(rtkMd, "codex config");

        var removedFirst = CodexInit.UninstallCodexAt(codexDir, InitContext.Default);
        var removedSecond = CodexInit.UninstallCodexAt(codexDir, InitContext.Default);

        Assert.Equal(2, removedFirst.Count);
        Assert.Empty(removedSecond);
        Assert.False(File.Exists(rtkMd));

        var content = File.ReadAllText(agentsMd);
        Assert.DoesNotContain("@RTK.md", content);
        Assert.Contains("# Team rules", content);
    }

    [Fact]
    public void UninstallCodexAt_RemovesAbsoluteReference()
    {
        using var tmp = new TempDir();
        var codexDir = tmp.Root;
        var agentsMd = Path.Combine(codexDir, "AGENTS.md");
        var rtkMd = Path.Combine(codexDir, "RTK.md");
        var absoluteRef = CodexInit.CodexRtkMdRef(codexDir);

        File.WriteAllText(agentsMd, $"# Team rules\n\n{absoluteRef}\n");
        File.WriteAllText(rtkMd, "codex config");

        var removed = CodexInit.UninstallCodexAt(codexDir, InitContext.Default);

        Assert.Equal(2, removed.Count);
        var content = File.ReadAllText(agentsMd);
        Assert.DoesNotContain(absoluteRef, content);
        Assert.Contains("# Team rules", content);
    }

    [Fact]
    public void ResolveCodexDirFrom_PrefersCodexHome_AndIgnoresEmptyValue()
    {
        var codexHome = Path.Combine("tmp", "custom-codex-home");
        var homeDir = Path.Combine("tmp", "home");

        var preferred = CodexInit.ResolveCodexDirFrom(codexHome, homeDir);
        var emptyFallsBack = CodexInit.ResolveCodexDirFrom(string.Empty, homeDir);
        var missingFallsBack = CodexInit.ResolveCodexDirFrom(null, homeDir);

        Assert.Equal(codexHome, preferred);
        Assert.Equal(Path.Combine(homeDir, ".codex"), emptyFallsBack);
        Assert.Equal(Path.Combine(homeDir, ".codex"), missingFallsBack);
    }
}
