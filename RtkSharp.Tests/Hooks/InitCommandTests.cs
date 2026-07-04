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

    [Theory]
    [InlineData(new object[] { new[] { "--gemini" } })]
    [InlineData(new object[] { new[] { "--codex" } })]
    [InlineData(new object[] { new[] { "--copilot" } })]
    [InlineData(new object[] { new[] { "--agent", "cursor" } })]
    [InlineData(new object[] { new[] { "--agent", "windsurf" } })]
    [InlineData(new object[] { new[] { "--agent", "pi" } })]
    [InlineData(new object[] { new[] { "-g" } })]
    [InlineData(new object[] { new[] { "--show" } })]
    public void OutOfScopeModes_FailLoudWithDeferredMessage(string[] args)
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = InitCommand.Run(args);

        Assert.Equal(1, exit);
        Assert.Contains("not yet implemented", console.Error.ToString());
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
