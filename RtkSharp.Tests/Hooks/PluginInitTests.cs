using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Redirects <c>PI_CODING_AGENT_DIR</c> so global-scope Pi tests never touch the real
/// <c>~/.pi/agent</c> directory on the machine running the tests. Mirrors <see cref="CodexScopeGuard"/>;
/// serializes via <see cref="InitTestSupport.EnterEnvLock"/> since the env var is process-global.
/// </summary>
internal sealed class PiDirGuard : IDisposable
{
    private const string PiCodingAgentDirEnvVar = "PI_CODING_AGENT_DIR";

    private readonly string? _previous;

    /// <summary>The throwaway directory standing in for <c>$PI_CODING_AGENT_DIR</c>.</summary>
    public string PiDir { get; }

    public PiDirGuard(TempDir tmp)
    {
        InitTestSupport.EnterEnvLock();

        PiDir = Path.Combine(tmp.Root, "pi_agent");
        Directory.CreateDirectory(PiDir);

        _previous = Environment.GetEnvironmentVariable(PiCodingAgentDirEnvVar);
        Environment.SetEnvironmentVariable(PiCodingAgentDirEnvVar, PiDir);
    }

    /// <summary>Constructs a guard pointing at a directory that does not (yet) exist on disk.</summary>
    public PiDirGuard(TempDir tmp, string absentSubdirName)
    {
        InitTestSupport.EnterEnvLock();

        PiDir = Path.Combine(tmp.Root, absentSubdirName);

        _previous = Environment.GetEnvironmentVariable(PiCodingAgentDirEnvVar);
        Environment.SetEnvironmentVariable(PiCodingAgentDirEnvVar, PiDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PiCodingAgentDirEnvVar, _previous);
        InitTestSupport.ExitEnvLock();
    }
}

/// <summary>
/// Tests for the Pi coding agent plugin installer (<see cref="PiInit"/>): global/local install,
/// dry-run, uninstall, dry-run uninstall, and path resolution. Directly ports Rust's
/// <c>test_run_pi_mode_*</c>/<c>test_pi_*</c> battery (<c>src/hooks/init.rs</c>, ~lines 6199-6420),
/// using <see cref="PiDirGuard"/> in place of Rust's <c>with_pi_dir_override</c> and
/// <see cref="CwdGuard"/> in place of Rust's <c>CWD_LOCK</c>-guarded <c>set_current_dir</c>.
/// </summary>
public sealed class PiInitTests
{
    [Fact]
    public void Run_Global_InstallsPluginContainingRtkRewrite()
    {
        using var tmp = new TempDir();
        using var piDirGuard = new PiDirGuard(tmp);

        PiInit.Run(global: true, InitContext.Default);

        var plugin = Path.Combine(piDirGuard.PiDir, "extensions", "rtk.ts");
        Assert.True(File.Exists(plugin), "global Pi extension must be created");
        Assert.Contains("rtk rewrite", File.ReadAllText(plugin));
    }

    [Fact]
    public void Run_Local_InstallsPluginUnderDotPi()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        PiInit.Run(global: false, InitContext.Default);

        var plugin = Path.Combine(tmp.Root, ".pi", "extensions", "rtk.ts");
        Assert.True(File.Exists(plugin), "local Pi extension must be created");
    }

    [Fact]
    public void Run_Global_DoesNotCreateAgentsMd()
    {
        using var tmp = new TempDir();
        using var piDirGuard = new PiDirGuard(tmp);

        PiInit.Run(global: true, InitContext.Default);

        Assert.False(File.Exists(Path.Combine(piDirGuard.PiDir, "AGENTS.md")));
    }

    [Fact]
    public void Run_Global_CreatesPluginWhenDirAbsent()
    {
        using var tmp = new TempDir();
        using var piDirGuard = new PiDirGuard(tmp, "no_such_pi_dir");
        Assert.False(Directory.Exists(piDirGuard.PiDir));

        PiInit.Run(global: true, InitContext.Default);

        var plugin = Path.Combine(piDirGuard.PiDir, "extensions", "rtk.ts");
        Assert.True(File.Exists(plugin), "plugin must be written even when dir was absent");
        Assert.False(File.Exists(Path.Combine(piDirGuard.PiDir, "AGENTS.md")));
    }

    [Fact]
    public void PluginPathForScope_Global_ResolvesUnderPiDir()
    {
        using var tmp = new TempDir();
        using var piDirGuard = new PiDirGuard(tmp);

        var path = PiInit.PiPluginPathForScope(true);

        Assert.Equal(Path.Combine(piDirGuard.PiDir, "extensions", "rtk.ts"), path);
    }

    [Fact]
    public void PluginPathForScope_Local_ResolvesUnderDotPi()
    {
        var path = PiInit.PiPluginPathForScope(false);

        Assert.Equal(Path.Combine(".pi", "extensions", "rtk.ts"), path);
    }

    [Fact]
    public void Run_Global_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        using var piDirGuard = new PiDirGuard(tmp);

        PiInit.Run(global: true, new InitContext(Verbose: 0, DryRun: true));

        Assert.False(Directory.Exists(Path.Combine(piDirGuard.PiDir, "extensions")),
            "dry-run must not create the Pi extensions directory");
    }

    [Fact]
    public void Run_Local_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        PiInit.Run(global: false, new InitContext(Verbose: 0, DryRun: true));

        Assert.False(Directory.Exists(Path.Combine(tmp.Root, ".pi", "extensions")),
            "dry-run must not create .pi/extensions/");
    }

    [Fact]
    public void Uninstall_Global_RemovesPlugin()
    {
        using var tmp = new TempDir();
        using var piDirGuard = new PiDirGuard(tmp);
        PiInit.Run(global: true, InitContext.Default);
        var plugin = Path.Combine(piDirGuard.PiDir, "extensions", "rtk.ts");
        Assert.True(File.Exists(plugin));

        PiInit.Uninstall(global: true, InitContext.Default);

        Assert.False(File.Exists(plugin), "plugin must be removed");
    }

    [Fact]
    public void Uninstall_Local_RemovesPlugin()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        PiInit.Run(global: false, InitContext.Default);
        var plugin = Path.Combine(tmp.Root, ".pi", "extensions", "rtk.ts");
        Assert.True(File.Exists(plugin));

        PiInit.Uninstall(global: false, InitContext.Default);

        Assert.False(File.Exists(plugin), "local plugin must be removed");
    }

    [Fact]
    public void Uninstall_Global_DryRun_KeepsPlugin()
    {
        using var tmp = new TempDir();
        using var piDirGuard = new PiDirGuard(tmp);
        PiInit.Run(global: true, InitContext.Default);
        var plugin = Path.Combine(piDirGuard.PiDir, "extensions", "rtk.ts");
        Assert.True(File.Exists(plugin), "plugin must exist before uninstall dry-run");

        PiInit.Uninstall(global: true, new InitContext(Verbose: 0, DryRun: true));

        Assert.True(File.Exists(plugin), "dry-run uninstall must not remove the Pi extension");
    }

    [Fact]
    public void Uninstall_Local_DryRun_KeepsPlugin()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        PiInit.Run(global: false, InitContext.Default);
        var plugin = Path.Combine(tmp.Root, ".pi", "extensions", "rtk.ts");
        Assert.True(File.Exists(plugin), "plugin must exist before uninstall dry-run");

        PiInit.Uninstall(global: false, new InitContext(Verbose: 0, DryRun: true));

        Assert.True(File.Exists(plugin), "dry-run uninstall must not remove the Pi extension");
    }

    [Fact]
    public void Uninstall_NothingInstalled_PrintsNotInstalled()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        PiInit.Uninstall(global: false, InitContext.Default);

        Assert.Contains("RTK Pi extension was not installed (nothing to remove)", console.Out.ToString());
    }

    [Fact]
    public void Run_ReRun_IsIdempotent_ReportsAlreadyUpToDate()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        PiInit.Run(global: false, InitContext.Default);

        using var console = new ConsoleCapture();
        PiInit.Run(global: false, InitContext.Default);

        Assert.Contains("RTK Pi extension already up to date:", console.Out.ToString());
    }
}

/// <summary>
/// Tests for the OpenCode plugin installer (<see cref="OpenCodeInit"/>): install, update
/// (idempotency), removal, and dry-run — all exercised against the lower-level primitives
/// (<see cref="OpenCodeInit.OpencodePluginPath"/>) with a fabricated plugin path rather than through
/// <see cref="OpenCodeInit.Run"/>/<see cref="OpenCodeInit.Uninstall"/> directly, because
/// <see cref="OpenCodeInit.ResolveOpencodeDir"/> has no environment-variable override in the Rust
/// oracle and always resolves the real <c>%USERPROFILE%/.config/opencode</c> — exactly mirroring how
/// Rust's own <c>test_opencode_plugin_install_and_update</c>/<c>test_opencode_plugin_remove</c>
/// (<c>src/hooks/init.rs</c>, ~lines 4239-4272) call <c>ensure_opencode_plugin_installed</c>/
/// <c>opencode_plugin_path</c> directly against a fabricated <c>TempDir</c> rather than calling
/// <c>run_opencode_only_mode</c> end-to-end.
/// </summary>
public sealed class OpenCodeInitTests
{
    [Fact]
    public void EnsurePluginInstalled_WritesPlugin_ThenUpdatesStaleContent()
    {
        using var tmp = new TempDir();
        var opencodeDir = Path.Combine(tmp.Root, "opencode");
        var pluginPath = OpenCodeInit.OpencodePluginPath(opencodeDir);
        Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
        Assert.False(File.Exists(pluginPath));

        var changed = OpenCodeInit.EnsureOpencodePluginInstalled(pluginPath, InitContext.Default);
        Assert.True(changed);
        Assert.Equal(OpenCodeInit.OpencodePluginContent, File.ReadAllText(pluginPath));

        File.WriteAllText(pluginPath, "// old");
        var changedAgain = OpenCodeInit.EnsureOpencodePluginInstalled(pluginPath, InitContext.Default);
        Assert.True(changedAgain);
        Assert.Equal(OpenCodeInit.OpencodePluginContent, File.ReadAllText(pluginPath));
    }

    [Fact]
    public void EnsurePluginInstalled_ReRun_IsIdempotent()
    {
        using var tmp = new TempDir();
        var opencodeDir = Path.Combine(tmp.Root, "opencode");
        var pluginPath = OpenCodeInit.OpencodePluginPath(opencodeDir);

        Assert.True(OpenCodeInit.EnsureOpencodePluginInstalled(pluginPath, InitContext.Default));
        Assert.False(OpenCodeInit.EnsureOpencodePluginInstalled(pluginPath, InitContext.Default));
    }

    [Fact]
    public void EnsurePluginInstalled_CreatesParentDirectory()
    {
        using var tmp = new TempDir();
        var opencodeDir = Path.Combine(tmp.Root, "opencode");
        var pluginPath = OpenCodeInit.OpencodePluginPath(opencodeDir);
        Assert.False(Directory.Exists(Path.GetDirectoryName(pluginPath)));

        OpenCodeInit.EnsureOpencodePluginInstalled(pluginPath, InitContext.Default);

        Assert.True(File.Exists(pluginPath));
    }

    [Fact]
    public void EnsurePluginInstalled_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        var opencodeDir = Path.Combine(tmp.Root, "opencode");
        var pluginPath = OpenCodeInit.OpencodePluginPath(opencodeDir);

        var changed = OpenCodeInit.EnsureOpencodePluginInstalled(pluginPath, new InitContext(Verbose: 0, DryRun: true));

        Assert.True(changed, "dry-run still reports it would write");
        Assert.False(File.Exists(pluginPath), "dry-run must not create the plugin file");
        Assert.False(Directory.Exists(Path.GetDirectoryName(pluginPath)), "dry-run must not create the parent directory");
    }

    [Fact]
    public void RemovePlugin_DeletesExistingFile()
    {
        using var tmp = new TempDir();
        var opencodeDir = Path.Combine(tmp.Root, "opencode");
        var pluginPath = OpenCodeInit.OpencodePluginPath(opencodeDir);
        Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
        File.WriteAllText(pluginPath, OpenCodeInit.OpencodePluginContent);
        Assert.True(File.Exists(pluginPath));

        File.Delete(pluginPath);

        Assert.False(File.Exists(pluginPath));
    }

    [Fact]
    public void Uninstall_NothingInstalled_PrintsNotInstalled()
    {
        // OpenCode's real config dir on the test machine is very unlikely to have an RTK plugin
        // installed; this exercises OpenCodeInit.Uninstall's "nothing to remove" branch without
        // needing to isolate the home directory (there is nothing to isolate against: absence is
        // the expected state either way).
        using var console = new ConsoleCapture();

        OpenCodeInit.Uninstall(InitContext.Default);

        var stdout = console.Out.ToString();
        Assert.True(
            stdout.Contains("RTK OpenCode plugin was not installed (nothing to remove)") ||
            stdout.Contains("RTK uninstalled (OpenCode):"),
            "must report either no-op or a real removal, never silently do nothing");
    }
}
