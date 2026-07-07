using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// End-to-end tests for the <c>rtk init</c> verb entry (<see cref="InitCommand.Run"/>): project-scope
/// Claude Code dispatch, dry-run reporting, and the "not yet implemented" deferrals for out-of-scope
/// modes. Each test runs in a throwaway temp directory (as the process CWD) so no real project files
/// are touched; CWD changes are serialized via <see cref="CwdLock"/> since
/// <see cref="Directory.SetCurrentDirectory"/> is process-global.
/// </summary>
public sealed class InitCommandTests
{
    [Fact]
    public void DefaultMode_WritesClaudeMdAndFiltersTemplate()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run([]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(tmp.Root, "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(tmp.Root, ".rtk", "filters.toml")));
        Assert.Contains("Claude Code will use rtk in this project", console.Out.ToString());
    }

    [Fact]
    public void DefaultMode_IsIdempotent_SecondRunReportsUnchanged()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        InitCommand.Run([]);
        var claudeMdAfterFirst = File.ReadAllText(Path.Combine(tmp.Root, "CLAUDE.md"));

        using var console = new ConsoleCapture();
        var exit = InitCommand.Run([]);
        var claudeMdAfterSecond = File.ReadAllText(Path.Combine(tmp.Root, "CLAUDE.md"));

        Assert.Equal(0, exit);
        Assert.Equal(claudeMdAfterFirst, claudeMdAfterSecond);
        Assert.Contains("already up to date", console.Out.ToString());
    }

    [Fact]
    public void ClaudeMdMode_WritesClaudeMdOnly_NoFiltersTemplate()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        var exit = InitCommand.Run(["--claude-md"]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(tmp.Root, "CLAUDE.md")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Root, ".rtk")));
    }

    [Fact]
    public void HookOnlyMode_ProjectScope_WarnsAndWritesNothing()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--hook-only"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(tmp.Root, "CLAUDE.md")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Root, ".rtk")));
        Assert.Contains("only makes sense with --global", console.Error.ToString());
    }

    [Fact]
    public void DryRun_WritesNothing_AndPrintsFooter()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--dry-run"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(tmp.Root, "CLAUDE.md")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Root, ".rtk")));
        Assert.EndsWith("\n[dry-run] Nothing written.\n", console.Out.ToString());
    }

    [Fact]
    public void DryRunClaudeMd_ReportsWouldCreate()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--claude-md", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(tmp.Root, "CLAUDE.md")));
        Assert.Contains("[dry-run] would add rtk instructions to", console.Out.ToString());
    }

    // Note: "-g" (bare global default mode), "--show" (without --codex), "--codex", "--gemini",
    // "--copilot", "--opencode", and every "--agent <name>" value are all now implemented (see
    // GeminiInitTests.cs, CopilotInitTests.cs, PluginInitTests.cs, CursorInitTests.cs,
    // WorkspaceRulesInitTests.cs, HermesInitTests.cs, and InitGlobalCommandTests.cs/CodexInitTests.cs
    // for their dedicated, properly home/CWD-isolated coverage). The two dispatch-routing smoke tests
    // below only exercise cases that are safe to run without an env-var scope guard: Windsurf/Cline
    // write to the process CWD (already isolated by CwdGuard here), and the Cursor global-only guard
    // throws before touching any filesystem path at all.

    [Fact]
    public void AgentWindsurf_DispatchesToWorkspaceRulesInit()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--agent", "windsurf"]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(tmp.Root, ".windsurfrules")));
        Assert.Contains("RTK configured for Windsurf Cascade", console.Out.ToString());
    }

    [Fact]
    public void AgentCline_DispatchesToWorkspaceRulesInit()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--agent", "cline"]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(tmp.Root, ".clinerules")));
        Assert.Contains("RTK configured for Cline", console.Out.ToString());
    }

    [Fact]
    public void AgentCursor_WithoutGlobal_FailsWithExactRustMessage()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--agent", "cursor"]);

        Assert.Equal(1, exit);
        Assert.Equal("rtk: Cursor hooks are global-only. Use: rtk init -g --agent cursor\n", console.Error.ToString());
    }

    [Fact]
    public void Uninstall_WithoutGlobal_FailsWithExactRustMessage()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--uninstall"]);

        Assert.Equal(1, exit);
        Assert.Equal(
            "rtk: Uninstall only works with --global flag. For local projects, manually remove RTK from CLAUDE.md\n",
            console.Error.ToString());
    }

    [Fact]
    public void Uninstall_WithAgentCursor_WithoutGlobal_FailsWithExactRustMessage()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        // Rust's uninstall() checks cursor before the generic !global bail (init.rs:637-640) and
        // bails there with its own distinct message when !global (rather than dispatching into an
        // unimplemented body), so the exact Rust text is reproduced here.
        var exit = InitCommand.Run(["--uninstall", "--agent", "cursor"]);

        Assert.Equal(1, exit);
        Assert.Equal(
            "rtk: Cursor uninstall only works with --global flag\n",
            console.Error.ToString());
    }

    [Fact]
    public void Uninstall_WithAgentPi_ReportsNothingToRemove()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        // Rust dispatches --agent pi into uninstall_pi(global, ctx) unconditionally (init.rs:664-667),
        // regardless of --global. Local scope (".pi/extensions/rtk.ts") is CWD-relative, so this is
        // safe to run without an env-var scope guard; PiInit.Uninstall's fuller coverage (including
        // the global scope, which needs PI_CODING_AGENT_DIR isolation) lives in PluginInitTests.cs.
        var exit = InitCommand.Run(["--uninstall", "--agent", "pi"]);

        Assert.Equal(0, exit);
        Assert.Contains("RTK Pi extension was not installed (nothing to remove)", console.Out.ToString());
    }

    [Fact]
    public void OpencodeWithoutGlobal_FailsWithExactRustMessage()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(["--opencode"]);

        Assert.Equal(1, exit);
        Assert.Equal("rtk: OpenCode plugin is global-only. Use: rtk init -g --opencode\n", console.Error.ToString());
    }

    [Fact]
    public void UnrecognizedArgument_FailsLoud()
    {
        var exit = InitCommand.Run(["--not-a-real-flag"]);
        Assert.Equal(1, exit);
    }

}
