using System;
using System.Collections.Generic;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Tests for the Hermes CLI plugin-install path (<c>rtk init --agent hermes</c>): plugin file
/// creation, the <c>config.yaml</c> YAML-surgery patch/unpatch (inline flow-sequence and
/// block-sequence <c>enabled</c> lists, including PyYAML's indentation-less-sequence style, missing
/// <c>enabled</c> key inference, and various missing-trailing-newline edge cases), uninstall
/// round-trip, and <c>resolve_hermes_home_from_env</c>. Oracle-derived expectations were captured
/// 1:1 from <c>src/hooks/init.rs</c>'s Hermes unit tests (init.rs:4504-5085).
/// </summary>
public sealed class HermesInitTests
{
    // ─── run_hermes_mode_at / uninstall_hermes_at (end-to-end) ────────────────────────

    [Fact]
    public void RunHermesAt_CreatesPluginFiles()
    {
        using var temp = new TempDir();

        HermesInit.RunHermesAt(temp.Root, InitContext.Default);

        var pluginDir = Path.Combine(temp.Root, "plugins", "rtk-rewrite");
        var initPath = Path.Combine(pluginDir, "__init__.py");
        var manifestPath = Path.Combine(pluginDir, "plugin.yaml");
        var configPath = Path.Combine(temp.Root, "config.yaml");

        Assert.True(File.Exists(initPath));
        Assert.True(File.Exists(manifestPath));
        Assert.Equal(HermesInit.HermesPluginInit, File.ReadAllText(initPath));
        Assert.Equal(HermesInit.HermesPluginManifest, File.ReadAllText(manifestPath));

        var config = File.ReadAllText(configPath);
        Assert.Contains("plugins:\n", config);
        Assert.Contains("  enabled:\n", config);
        Assert.Equal(1, CountOccurrences(config, "rtk-rewrite"));
    }

    [Fact]
    public void RunHermesAt_PreservesConfigAndIsIdempotent()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Root, "config.yaml");
        File.WriteAllText(configPath, "theme: dark\nplugins:\n  enabled:\n    - existing-plugin\n  search_path: ./plugins\nother: true\n");

        HermesInit.RunHermesAt(temp.Root, InitContext.Default);
        var first = File.ReadAllText(configPath);
        HermesInit.RunHermesAt(temp.Root, InitContext.Default);
        var second = File.ReadAllText(configPath);

        Assert.Equal(first, second);
        Assert.Contains("theme: dark\n", first);
        Assert.Contains("    - existing-plugin\n", first);
        Assert.Contains("  search_path: ./plugins\n", first);
        Assert.Contains("other: true\n", first);
        Assert.Equal(1, CountOccurrences(first, "rtk-rewrite"));
    }

    [Fact]
    public void RunHermesAt_PreservesPyYamlSameIndentConfigAndIsIdempotent()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Root, "config.yaml");
        File.WriteAllText(
            configPath,
            "theme: dark\nplugins:\n disabled:\n - google_meet\n - spotify\n enabled:\n - disk-cleanup\n search_path: ./plugins\nother: true\n");

        HermesInit.RunHermesAt(temp.Root, InitContext.Default);
        var first = File.ReadAllText(configPath);
        HermesInit.RunHermesAt(temp.Root, InitContext.Default);
        var second = File.ReadAllText(configPath);

        const string expected = "theme: dark\nplugins:\n disabled:\n - google_meet\n - spotify\n enabled:\n - disk-cleanup\n - rtk-rewrite\n search_path: ./plugins\nother: true\n";
        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Equal(1, CountOccurrences(first, "rtk-rewrite"));
    }

    [Fact]
    public void RunHermesAt_PatchesAndUninstallsPyYamlSameIndentMissingEnabledIdempotently()
    {
        using var temp = new TempDir();
        var hermesHome = temp.Root;
        var pluginDir = Path.Combine(hermesHome, "plugins", "rtk-rewrite");
        var otherPluginDir = Path.Combine(hermesHome, "plugins", "keep-me");
        var otherPluginFile = Path.Combine(otherPluginDir, "plugin.yaml");
        var configPath = Path.Combine(hermesHome, "config.yaml");

        Directory.CreateDirectory(otherPluginDir);
        File.WriteAllText(otherPluginFile, "keep");
        File.WriteAllText(
            configPath,
            "theme: dark\nplugins:\n disabled:\n - google_meet\n - spotify\n search_path: ./plugins\nother: true\n");

        HermesInit.RunHermesAt(hermesHome, InitContext.Default);
        var first = File.ReadAllText(configPath);
        HermesInit.RunHermesAt(hermesHome, InitContext.Default);
        var second = File.ReadAllText(configPath);

        const string installed = "theme: dark\nplugins:\n disabled:\n - google_meet\n - spotify\n search_path: ./plugins\n enabled:\n - rtk-rewrite\nother: true\n";
        Assert.Equal(installed, first);
        Assert.Equal(installed, second);
        Assert.Equal(1, CountOccurrences(first, "rtk-rewrite"));
        Assert.True(Directory.Exists(pluginDir));
        Assert.Equal("keep", File.ReadAllText(otherPluginFile));

        var removedFirst = HermesInit.UninstallHermesAt(hermesHome, InitContext.Default);
        var removedSecond = HermesInit.UninstallHermesAt(hermesHome, InitContext.Default);

        Assert.Equal(2, removedFirst.Count);
        Assert.Empty(removedSecond);
        Assert.False(Directory.Exists(pluginDir));
        Assert.True(Directory.Exists(otherPluginDir));
        Assert.Equal("keep", File.ReadAllText(otherPluginFile));

        var uninstalled = File.ReadAllText(configPath);
        Assert.Equal(
            "theme: dark\nplugins:\n disabled:\n - google_meet\n - spotify\n search_path: ./plugins\n enabled: []\nother: true\n",
            uninstalled);
        Assert.DoesNotContain("\n - \n", uninstalled);
        Assert.DoesNotContain("\n -\n", uninstalled);
        Assert.Equal(0, CountOccurrences(uninstalled, "rtk-rewrite"));
    }

    [Fact]
    public void UninstallHermesAt_RemovesPluginDirAndCleansConfig()
    {
        using var temp = new TempDir();
        var hermesHome = temp.Root;
        var pluginDir = Path.Combine(hermesHome, "plugins", "rtk-rewrite");
        var nestedPluginFile = Path.Combine(pluginDir, "nested", "marker.txt");
        var otherPluginDir = Path.Combine(hermesHome, "plugins", "keep-me");
        var otherPluginFile = Path.Combine(otherPluginDir, "plugin.yaml");
        var configPath = Path.Combine(hermesHome, "config.yaml");

        Directory.CreateDirectory(Path.GetDirectoryName(nestedPluginFile)!);
        File.WriteAllText(nestedPluginFile, "rtk");
        Directory.CreateDirectory(otherPluginDir);
        File.WriteAllText(otherPluginFile, "keep");
        File.WriteAllText(
            configPath,
            "theme: dark\nplugins:\n  enabled:\n    - existing-plugin\n    - rtk-rewrite\n  search_path: ./plugins\nother: true\n");

        var removedFirst = HermesInit.UninstallHermesAt(hermesHome, InitContext.Default);
        var removedSecond = HermesInit.UninstallHermesAt(hermesHome, InitContext.Default);

        Assert.Equal(2, removedFirst.Count);
        Assert.Empty(removedSecond);
        Assert.False(Directory.Exists(pluginDir));
        Assert.True(Directory.Exists(otherPluginDir));
        Assert.Equal("keep", File.ReadAllText(otherPluginFile));

        var config = File.ReadAllText(configPath);
        Assert.Contains("theme: dark\n", config);
        Assert.Contains("    - existing-plugin\n", config);
        Assert.Contains("  search_path: ./plugins\n", config);
        Assert.Contains("other: true\n", config);
        Assert.Equal(0, CountOccurrences(config, "rtk-rewrite"));
    }

    [Fact]
    public void UninstallHermesAt_CleansPyYamlSameIndentConfigIdempotently()
    {
        using var temp = new TempDir();
        var hermesHome = temp.Root;
        var pluginDir = Path.Combine(hermesHome, "plugins", "rtk-rewrite");
        var nestedPluginFile = Path.Combine(pluginDir, "nested", "marker.txt");
        var otherPluginDir = Path.Combine(hermesHome, "plugins", "keep-me");
        var otherPluginFile = Path.Combine(otherPluginDir, "plugin.yaml");
        var configPath = Path.Combine(hermesHome, "config.yaml");

        Directory.CreateDirectory(Path.GetDirectoryName(nestedPluginFile)!);
        File.WriteAllText(nestedPluginFile, "rtk");
        Directory.CreateDirectory(otherPluginDir);
        File.WriteAllText(otherPluginFile, "keep");
        File.WriteAllText(
            configPath,
            "theme: dark\nplugins:\n disabled:\n - google_meet\n - spotify\n enabled:\n - disk-cleanup\n - rtk-rewrite\n search_path: ./plugins\nother: true\n");

        var removedFirst = HermesInit.UninstallHermesAt(hermesHome, InitContext.Default);
        var removedSecond = HermesInit.UninstallHermesAt(hermesHome, InitContext.Default);

        Assert.Equal(2, removedFirst.Count);
        Assert.Empty(removedSecond);
        Assert.False(Directory.Exists(pluginDir));
        Assert.True(Directory.Exists(otherPluginDir));
        Assert.Equal("keep", File.ReadAllText(otherPluginFile));

        var config = File.ReadAllText(configPath);
        Assert.Equal(
            "theme: dark\nplugins:\n disabled:\n - google_meet\n - spotify\n enabled:\n - disk-cleanup\n search_path: ./plugins\nother: true\n",
            config);
        Assert.DoesNotContain("\n - \n", config);
        Assert.DoesNotContain("\n -\n", config);
        Assert.Equal(0, CountOccurrences(config, "rtk-rewrite"));
    }

    [Fact]
    public void UninstallHermesAt_MissingFilesIsIdempotent()
    {
        using var temp = new TempDir();

        var removedFirst = HermesInit.UninstallHermesAt(temp.Root, InitContext.Default);
        var removedSecond = HermesInit.UninstallHermesAt(temp.Root, InitContext.Default);

        Assert.Empty(removedFirst);
        Assert.Empty(removedSecond);
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "plugins")));
        Assert.False(File.Exists(Path.Combine(temp.Root, "config.yaml")));
    }

    // ─── patch_hermes_config / unpatch_hermes_config (pure YAML-surgery) ──────────────

    [Fact]
    public void PatchHermesConfig_AddsMissingEnabledList()
    {
        const string existing = "theme: dark\nplugins:\n  search_path: ./plugins\nother: true\n";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Contains("theme: dark\n", patched);
        Assert.Contains("plugins:\n", patched);
        Assert.Contains("  search_path: ./plugins\n", patched);
        Assert.Contains("  enabled:\n    - rtk-rewrite\n", patched);
        Assert.Contains("other: true\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_RemovesDuplicateRtkRewrite()
    {
        const string existing = "plugins:\n  enabled:\n    - rtk-rewrite\n    - other\n    - rtk-rewrite\n";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Contains("    - other\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_PyYamlIndentationlessEnabledList()
    {
        const string existing = "plugins:\n disabled:\n - google_meet\n - spotify\n enabled:\n - disk-cleanup\n";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal("plugins:\n disabled:\n - google_meet\n - spotify\n enabled:\n - disk-cleanup\n - rtk-rewrite\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_PyYamlDefaultCompactEnabledList()
    {
        const string existing = "plugins:\n  enabled:\n  - foo\n";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled:\n  - foo\n  - rtk-rewrite\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_PyYamlIndentationlessMissingEnabledList()
    {
        const string existing = "plugins:\n disabled:\n - google_meet\n - spotify\n search_path: ./plugins\n";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal("plugins:\n disabled:\n - google_meet\n - spotify\n search_path: ./plugins\n enabled:\n - rtk-rewrite\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_PyYamlIndentationlessEnabledIsIdempotent()
    {
        const string existing = "plugins:\n enabled:\n - disk-cleanup\n disabled:\n - spotify\n";
        var patchedOnce = HermesInit.PatchHermesConfig(existing);
        var patchedTwice = HermesInit.PatchHermesConfig(patchedOnce);

        Assert.Equal("plugins:\n enabled:\n - disk-cleanup\n - rtk-rewrite\n disabled:\n - spotify\n", patchedOnce);
        Assert.Equal(patchedOnce, patchedTwice);
        Assert.Equal(1, CountOccurrences(patchedOnce, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_PyYamlIndentationlessFinalLineWithoutNewline()
    {
        const string existing = "plugins:\n enabled:\n - disk-cleanup";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal("plugins:\n enabled:\n - disk-cleanup\n - rtk-rewrite\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_BlockEnabledFinalLineWithoutNewline()
    {
        const string existing = "plugins:\n  enabled:\n    - existing-plugin";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled:\n    - existing-plugin\n    - rtk-rewrite\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_MissingEnabledAfterFinalChildWithoutNewline()
    {
        const string existing = "plugins:\n  search_path: ./plugins";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal("plugins:\n  search_path: ./plugins\n  enabled:\n    - rtk-rewrite\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_EmptyEnabledFinalLineWithoutNewline()
    {
        const string existing = "plugins:\n  enabled:";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled:\n    - rtk-rewrite\n", patched);
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_InlineEnabledIsIdempotent()
    {
        const string existing = "theme: dark\nplugins:\n  enabled: [existing-plugin, rtk-rewrite] # keep\n  search_path: ./plugins\nother: true\n";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal(existing, patched);
        Assert.Equal(patched, HermesInit.PatchHermesConfig(patched));
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void PatchHermesConfig_InlineEnabledWithoutFinalNewlineIsIdempotent()
    {
        const string existing = "plugins:\n  enabled: [existing-plugin, rtk-rewrite]";
        var patched = HermesInit.PatchHermesConfig(existing);

        Assert.Equal(existing, patched);
        Assert.Equal(patched, HermesInit.PatchHermesConfig(patched));
        Assert.Equal(1, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_InlineEnabledWithoutRtkPreservesMissingFinalNewline()
    {
        const string existing = "plugins:\n  enabled: [existing-plugin]";
        Assert.Equal(existing, HermesInit.UnpatchHermesConfig(existing));
    }

    [Fact]
    public void UnpatchHermesConfig_InlineEnabledPreservesUnrelatedEntries()
    {
        const string existing = "theme: dark\nplugins:\n  enabled: [alpha, rtk-rewrite, beta] # keep comment\n  search_path: ./plugins\nother: true\n";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("theme: dark\nplugins:\n  enabled: [alpha, beta] # keep comment\n  search_path: ./plugins\nother: true\n", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_InlineEnabledFinalLineWithoutNewline()
    {
        const string existing = "plugins:\n  enabled: [existing-plugin, rtk-rewrite]";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled: [existing-plugin]", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_RemovesDuplicateInlineRtkRewrite()
    {
        const string existing = "plugins:\n  enabled: [alpha, rtk-rewrite, beta, rtk-rewrite]\n";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled: [alpha, beta]\n", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_RemovesDuplicateBlockRtkRewrite()
    {
        const string existing = "plugins:\n  enabled:\n    - rtk-rewrite\n    - other\n    - rtk-rewrite\n";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled:\n    - other\n", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_PyYamlIndentationlessEnabledList()
    {
        const string existing = "plugins:\n disabled:\n - google_meet\n - spotify\n enabled:\n - disk-cleanup\n - rtk-rewrite\n search_path: ./plugins\n";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("plugins:\n disabled:\n - google_meet\n - spotify\n enabled:\n - disk-cleanup\n search_path: ./plugins\n", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_PyYamlIndentationlessOnlyRtkCollapsesToEmpty()
    {
        const string existing = "plugins:\n enabled:\n - rtk-rewrite\n search_path: ./plugins\n";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("plugins:\n enabled: []\n search_path: ./plugins\n", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_BlockEnabledFinalLineWithoutNewline()
    {
        const string existing = "plugins:\n  enabled:\n    - existing-plugin\n    - rtk-rewrite";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled:\n    - existing-plugin\n", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_BlockEnabledWithoutRtkPreservesMissingFinalNewline()
    {
        const string existing = "plugins:\n  enabled:\n    - existing-plugin";
        Assert.Equal(existing, HermesInit.UnpatchHermesConfig(existing));
    }

    [Fact]
    public void UnpatchHermesConfig_PreservesQuotedExactValues()
    {
        const string existing = "plugins:\n  enabled:\n    - 'alpha'\n    - \"rtk-rewrite\"\n    - 'beta'\n  search_path: ./plugins\n";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled:\n    - 'alpha'\n    - 'beta'\n  search_path: ./plugins\n", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    [Fact]
    public void UnpatchHermesConfig_LeavesMissingEnabledListUnchanged()
    {
        const string existing = "theme: dark\nplugins:\n  search_path: ./plugins\nother: true\n";
        Assert.Equal(existing, HermesInit.UnpatchHermesConfig(existing));
    }

    [Fact]
    public void UnpatchHermesConfig_CollapsesEmptyEnabledList()
    {
        const string existing = "plugins:\n  enabled:\n    - rtk-rewrite\n";
        var patched = HermesInit.UnpatchHermesConfig(existing);

        Assert.Equal("plugins:\n  enabled: []\n", patched);
        Assert.Equal(0, CountOccurrences(patched, "rtk-rewrite"));
    }

    // ─── resolve_hermes_home_from_env ──────────────────────────────────────────────────

    [Fact]
    public void ResolveHermesHomeFrom_PrefersHermesHome()
    {
        var resolved = HermesInit.ResolveHermesHomeFrom("/tmp/home", "~/custom hermes home");
        Assert.Equal("~/custom hermes home", resolved);
    }

    [Fact]
    public void ResolveHermesHomeFrom_EmptyEnvFallsBackToHome()
    {
        var emptyFallsBack = HermesInit.ResolveHermesHomeFrom("/tmp/home", string.Empty);
        var missingFallsBack = HermesInit.ResolveHermesHomeFrom("/tmp/home", null);

        Assert.Equal(Path.Combine("/tmp/home", ".hermes"), emptyFallsBack);
        Assert.Equal(Path.Combine("/tmp/home", ".hermes"), missingFallsBack);
    }

    [Fact]
    public void ResolveHermesHomeFrom_ThrowsWithoutHomeOrEnv()
    {
        var ex = Assert.Throws<InitAbortException>(() => HermesInit.ResolveHermesHomeFrom(null, null));
        Assert.Contains("Cannot determine Hermes home directory", ex.Message);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
