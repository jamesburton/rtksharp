using System;
using System.IO;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Tests.Hooks;
using Xunit;

namespace RtkSharp.Tests.Core;

/// <summary>
/// Tests for <see cref="Config"/>'s load/save/pretty-print logic and the <c>rtk config</c> verb
/// (<see cref="ConfigCommand"/>). Every test redirects the resolved config directory via
/// <see cref="GlobalScopeGuard"/> (the same <c>RTK_CONFIG_DIR_OVERRIDE</c> escape hatch Phase 9b's
/// global filters template uses) so none of these ever touch the real user profile.
/// </summary>
public sealed class ConfigTests
{
    [Fact]
    public void DefaultConfig_RoundTrips_ThroughPrettyToml()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);

        var original = new Config();
        original.Save();

        var reloaded = Config.Load();

        Assert.Equivalent(original, reloaded, strict: true);
    }

    [Fact]
    public void Load_NoFile_ReturnsAllDefaults()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);

        var config = Config.Load();

        Assert.True(config.Tracking.Enabled);
        Assert.Equal(90u, config.Tracking.HistoryDays);
        Assert.Null(config.Tracking.DatabasePath);
        Assert.True(config.Display.Colors);
        Assert.True(config.Display.Emoji);
        Assert.Equal(120, config.Display.MaxWidth);
        Assert.Equal([".git", "node_modules", "target", "__pycache__", ".venv", "vendor"], config.Filters.IgnoreDirs);
        Assert.Equal(["*.lock", "*.min.js", "*.min.css"], config.Filters.IgnoreFiles);
        Assert.False(config.Telemetry.Enabled);
        Assert.Null(config.Telemetry.ConsentGiven);
        Assert.Null(config.Telemetry.ConsentDate);
        Assert.Empty(config.Hooks.ExcludeCommands);
        Assert.Empty(config.Hooks.TransparentPrefixes);
        Assert.Equal(200, config.Limits.GrepMaxResults);
        Assert.Equal(25, config.Limits.GrepMaxPerFile);
        Assert.Equal(15, config.Limits.StatusMaxFiles);
        Assert.Equal(10, config.Limits.StatusMaxUntracked);
        Assert.Equal(2000, config.Limits.PassthroughMaxChars);
    }

    [Fact]
    public void ShowConfig_NoFile_PrintsDefaultsWithNotCreatedNotice()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var expectedPath = Path.Combine(guard.ConfigDir, "rtk", "config.toml");

        var exit = ConfigCommand.Run([]);

        Assert.Equal(0, exit);
        var output = console.Out.ToString();
        Assert.StartsWith($"Config: {expectedPath}\n\n(default config, file not created)\n\n[tracking]\n", output, StringComparison.Ordinal);
        Assert.Contains("[limits]\ngrep_max_results = 200\n", output, StringComparison.Ordinal);
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void ShowConfig_ExistingFile_PrintsThatFilesContent()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var config = new Config();
        config.Hooks.ExcludeCommands.Add("curl");
        config.Save();

        var exit = ConfigCommand.Run([]);

        Assert.Equal(0, exit);
        var output = console.Out.ToString();
        Assert.DoesNotContain("not created", output, StringComparison.Ordinal);
        Assert.Contains("exclude_commands = [\n    \"curl\",\n]\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WritesDefaultFile_AndPrintsCreatedMessage()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        using var console = new ConsoleCapture();

        var expectedPath = Path.Combine(guard.ConfigDir, "rtk", "config.toml");

        var exit = ConfigCommand.Run(["--create"]);

        Assert.Equal(0, exit);
        Assert.Equal($"Created: {expectedPath}\n", console.Out.ToString());
        Assert.True(File.Exists(expectedPath));

        var content = File.ReadAllText(expectedPath);
        Assert.StartsWith("[tracking]\nenabled = true\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_IsIdempotent_SecondRunProducesIdenticalFile()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);

        var expectedPath = Path.Combine(guard.ConfigDir, "rtk", "config.toml");

        ConfigCommand.Run(["--create"]);
        var firstContent = File.ReadAllText(expectedPath);

        ConfigCommand.Run(["--create"]);
        var secondContent = File.ReadAllText(expectedPath);

        Assert.Equal(firstContent, secondContent);
    }

    [Fact]
    public void Load_ConfigWithOnlyHooksSection_FillsRemainingSectionsWithDefaults()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);

        var path = Path.Combine(guard.ConfigDir, "rtk", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[hooks]\nexclude_commands = [\"curl\", \"gh\"]\n");

        var config = Config.Load();

        Assert.Equal(["curl", "gh"], config.Hooks.ExcludeCommands);
        Assert.Empty(config.Hooks.TransparentPrefixes);

        // Every other section falls back to its declared defaults.
        Assert.True(config.Tracking.Enabled);
        Assert.Equal(90u, config.Tracking.HistoryDays);
        Assert.True(config.Display.Colors);
        Assert.Equal(120, config.Display.MaxWidth);
        Assert.Equal([".git", "node_modules", "target", "__pycache__", ".venv", "vendor"], config.Filters.IgnoreDirs);
        Assert.False(config.Telemetry.Enabled);
        Assert.Equal(200, config.Limits.GrepMaxResults);
        Assert.Equal(2000, config.Limits.PassthroughMaxChars);
    }

    /// <summary>
    /// Proves <see cref="Config.Load"/> never consults a project-local <c>.rtk/config.toml</c> — an
    /// easy assumption to get backwards by analogy with the (genuinely two-tier) filters system. A
    /// project-local file with non-default content must be completely ignored: the loaded config
    /// must equal the compiled-in defaults, not the project-local file's content.
    /// </summary>
    [Fact]
    public void Load_IgnoresProjectLocalRtkConfigToml()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        using var cwd = new CwdGuard(tmp.Root);

        var projectLocalDir = Path.Combine(tmp.Root, ".rtk");
        Directory.CreateDirectory(projectLocalDir);
        File.WriteAllText(
            Path.Combine(projectLocalDir, "config.toml"),
            "[hooks]\nexclude_commands = [\"this-should-never-be-read\"]\n\n[limits]\ngrep_max_results = 1\n");

        // No global config.toml exists (GlobalScopeGuard's ConfigDir is empty), so Load() must
        // return all-defaults regardless of the project-local file sitting in the CWD.
        var config = Config.Load();

        Assert.Empty(config.Hooks.ExcludeCommands);
        Assert.Equal(200, config.Limits.GrepMaxResults);
    }
}
