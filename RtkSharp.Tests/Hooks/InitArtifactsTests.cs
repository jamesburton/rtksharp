using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Tests for the <see cref="InitArtifacts"/> write primitives and embedded templates: the
/// idempotent-write core ported from Rust <c>src/hooks/init.rs</c>. Oracle-derived expectations were
/// captured against <c>target/release/rtk.exe init</c> under a redirected <c>CLAUDE_CONFIG_DIR</c>
/// and throwaway working directory.
/// </summary>
public sealed class InitArtifactsTests
{
    // ── Embedded templates ──────────────────────────────────────────────────

    [Fact]
    public void RtkInstructions_ContainsBlockMarkersAndKeyCommands()
    {
        Assert.Contains("<!-- rtk-instructions", InitArtifacts.RtkInstructions);
        Assert.Contains("<!-- /rtk-instructions -->", InitArtifacts.RtkInstructions);
        Assert.Contains("rtk cargo test", InitArtifacts.RtkInstructions);
        Assert.True(InitArtifacts.RtkInstructions.Length > 4000);
    }

    [Fact]
    public void RtkInstructions_HasExactlyOneTrailingNewline()
    {
        Assert.EndsWith("<!-- /rtk-instructions -->\n", InitArtifacts.RtkInstructions);
        Assert.False(InitArtifacts.RtkInstructions.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void FiltersTemplate_MatchesOracleShape()
    {
        Assert.StartsWith("# Project-local RTK filters", InitArtifacts.FiltersTemplate);
        Assert.EndsWith("# on_empty = \"my-tool: ok\"\n", InitArtifacts.FiltersTemplate);
        Assert.Contains("schema_version = 1", InitArtifacts.FiltersTemplate);
    }

    [Fact]
    public void FiltersGlobalTemplate_MatchesOracleShape()
    {
        Assert.StartsWith("# User-global RTK filters", InitArtifacts.FiltersGlobalTemplate);
        Assert.EndsWith("# max_lines = 40\n", InitArtifacts.FiltersGlobalTemplate);
    }

    [Fact]
    public void RtkSlim_LoadsFromEmbeddedResource()
    {
        Assert.Contains("RTK - Rust Token Killer", InitArtifacts.RtkSlim);
        Assert.Contains("rtk gain", InitArtifacts.RtkSlim);
    }

    // ── ResolveGlobalConfigDir (test-only override) ─────────────────────────

    [Fact]
    public void ResolveGlobalConfigDir_HonorsOverrideEnvVar()
    {
        var previous = Environment.GetEnvironmentVariable(InitArtifacts.ConfigDirOverrideEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(InitArtifacts.ConfigDirOverrideEnvVar, "/tmp/rtk-config-override");
            Assert.Equal("/tmp/rtk-config-override", InitArtifacts.ResolveGlobalConfigDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable(InitArtifacts.ConfigDirOverrideEnvVar, previous);
        }
    }

    [Fact]
    public void ResolveGlobalConfigDir_FallsBackWhenOverrideUnset()
    {
        var previous = Environment.GetEnvironmentVariable(InitArtifacts.ConfigDirOverrideEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(InitArtifacts.ConfigDirOverrideEnvVar, null);
            var resolved = InitArtifacts.ResolveGlobalConfigDir();
            Assert.False(string.IsNullOrEmpty(resolved));
        }
        finally
        {
            Environment.SetEnvironmentVariable(InitArtifacts.ConfigDirOverrideEnvVar, previous);
        }
    }

    // ── UpsertRtkBlock ───────────────────────────────────────────────────────

    [Fact]
    public void UpsertRtkBlock_AppendsWhenMissing()
    {
        var (content, action) = InitArtifacts.UpsertRtkBlock("# Team instructions", InitArtifacts.RtkInstructions);

        Assert.Equal(RtkBlockUpsert.Added, action);
        Assert.Contains("# Team instructions", content);
        Assert.Contains("<!-- rtk-instructions", content);
    }

    [Fact]
    public void UpsertRtkBlock_AddedIntoEmptyFile_UsesRawBlockUntrimmed()
    {
        var (content, action) = InitArtifacts.UpsertRtkBlock(string.Empty, InitArtifacts.RtkInstructions);

        Assert.Equal(RtkBlockUpsert.Added, action);
        Assert.Equal(InitArtifacts.RtkInstructions, content);
    }

    [Fact]
    public void UpsertRtkBlock_UpdatesStaleBlock()
    {
        var input = "# Team instructions\n\n<!-- rtk-instructions v1 -->\nOLD RTK CONTENT\n<!-- /rtk-instructions -->\n\nMore notes\n";

        var (content, action) = InitArtifacts.UpsertRtkBlock(input, InitArtifacts.RtkInstructions);

        Assert.Equal(RtkBlockUpsert.Updated, action);
        Assert.DoesNotContain("OLD RTK CONTENT", content);
        Assert.Contains("rtk cargo test", content);
        Assert.Contains("# Team instructions", content);
        Assert.Contains("More notes", content);
    }

    [Fact]
    public void UpsertRtkBlock_NoopWhenAlreadyCurrent()
    {
        var input = $"# Team instructions\n\n{InitArtifacts.RtkInstructions}\n\nMore notes\n";

        var (content, action) = InitArtifacts.UpsertRtkBlock(input, InitArtifacts.RtkInstructions);

        Assert.Equal(RtkBlockUpsert.Unchanged, action);
        Assert.Equal(input, content);
    }

    [Fact]
    public void UpsertRtkBlock_DetectsMalformedBlock()
    {
        var input = "<!-- rtk-instructions v2 -->\npartial";

        var (content, action) = InitArtifacts.UpsertRtkBlock(input, InitArtifacts.RtkInstructions);

        Assert.Equal(RtkBlockUpsert.Malformed, action);
        Assert.Equal(input, content);
    }

    // ── WriteRtkBlock (idempotency + malformed refuse-and-bail) ─────────────

    [Fact]
    public void WriteRtkBlock_FirstRunAdds_SecondRunUnchanged()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "CLAUDE.md");
        var ctx = InitContext.Default;

        var first = InitArtifacts.WriteRtkBlock(path, InitArtifacts.RtkInstructions, "rtk instructions", "rtk init --claude-md", ctx);
        var contentAfterFirst = File.ReadAllText(path);

        var second = InitArtifacts.WriteRtkBlock(path, InitArtifacts.RtkInstructions, "rtk instructions", "rtk init --claude-md", ctx);
        var contentAfterSecond = File.ReadAllText(path);

        Assert.Equal(RtkBlockUpsert.Added, first);
        Assert.Equal(RtkBlockUpsert.Unchanged, second);
        Assert.Equal(contentAfterFirst, contentAfterSecond);
        Assert.Equal(1, CountOccurrences(contentAfterSecond, "<!-- rtk-instructions"));
    }

    [Fact]
    public void WriteRtkBlock_MalformedBlock_ThrowsAndLeavesFileUntouched()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "CLAUDE.md");
        var malformed = "# Existing notes\n\n<!-- rtk-instructions\nincomplete RTK block\n";
        File.WriteAllText(path, malformed);

        var ex = Assert.Throws<InitAbortException>(() =>
            InitArtifacts.WriteRtkBlock(path, InitArtifacts.RtkInstructions, "rtk instructions", "rtk init --claude-md", InitContext.Default));

        Assert.Contains("Refusing to modify malformed rtk instructions", ex.Message);
        Assert.Equal(malformed, File.ReadAllText(path));
    }

    [Fact]
    public void WriteRtkBlock_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "CLAUDE.md");
        var ctx = new InitContext(Verbose: 0, DryRun: true);

        InitArtifacts.WriteRtkBlock(path, InitArtifacts.RtkInstructions, "rtk instructions", "rtk init --claude-md", ctx);

        Assert.False(File.Exists(path));
    }

    // ── RemoveRtkBlock (migration helper — soft-fails on malformed) ─────────

    [Fact]
    public void RemoveRtkBlock_RemovesOldBlockPreservingSurroundingContent()
    {
        var input = $"# My Config\n\n{InitArtifacts.RtkInstructions}\n\nMore content";

        var (result, didRemove) = InitArtifacts.RemoveRtkBlock(input);

        Assert.True(didRemove);
        Assert.DoesNotContain("rtk cargo test", result);
        Assert.Contains("# My Config", result);
        Assert.Contains("More content", result);
    }

    [Fact]
    public void RemoveRtkBlock_MalformedBlock_WarnsButDoesNotThrow()
    {
        var input = "<!-- rtk-instructions v2 -->\nOLD STUFF\nNo end marker";

        var (result, didRemove) = InitArtifacts.RemoveRtkBlock(input);

        Assert.False(didRemove);
        Assert.Equal(input, result);
    }

    // ── PatchClaudeMd (@RTK.md migration) ───────────────────────────────────

    [Fact]
    public void PatchClaudeMd_AddsAtRtkMdReferenceOnce()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "CLAUDE.md");
        File.WriteAllText(path, "# Team rules\n");

        var firstMigrated = InitArtifacts.PatchClaudeMd(path, InitContext.Default);
        var secondMigrated = InitArtifacts.PatchClaudeMd(path, InitContext.Default);

        Assert.False(firstMigrated);
        Assert.False(secondMigrated);
        var content = File.ReadAllText(path);
        Assert.Equal(1, CountOccurrences(content, "@RTK.md"));
    }

    [Fact]
    public void PatchClaudeMd_MigratesOldBlockToAtRtkMdReference()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "CLAUDE.md");
        File.WriteAllText(path, InitArtifacts.RtkInstructions);

        var migrated = InitArtifacts.PatchClaudeMd(path, InitContext.Default);

        Assert.True(migrated);
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("<!-- rtk-instructions", content);
        Assert.Contains("@RTK.md", content);
    }

    [Fact]
    public void PatchClaudeMd_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "CLAUDE.md");
        File.WriteAllText(path, "# Team rules\n");
        var ctx = new InitContext(Verbose: 0, DryRun: true);

        InitArtifacts.PatchClaudeMd(path, ctx);

        Assert.Equal("# Team rules\n", File.ReadAllText(path));
    }

    // ── CleanDoubleBlanks ────────────────────────────────────────────────────

    [Theory]
    [InlineData("a\n\n\n\nb", "a\n\n\nb")]
    [InlineData("a\n\nb", "a\n\nb")]
    [InlineData("a\nb", "a\nb")]
    public void CleanDoubleBlanks_CollapsesRunsToAtMostTwo(string input, string expected)
    {
        Assert.Equal(expected, InitArtifacts.CleanDoubleBlanks(input));
    }

    // ── GenerateProjectFiltersTemplate ───────────────────────────────────────

    [Fact]
    public void GenerateProjectFiltersTemplate_CreatesTemplate_ThenSkipsIfPresent()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        InitArtifacts.GenerateProjectFiltersTemplate(InitContext.Default);

        var path = Path.Combine(tmp.Root, ".rtk", "filters.toml");
        Assert.True(File.Exists(path));
        Assert.Equal(InitArtifacts.FiltersTemplate, File.ReadAllText(path));

        // Second run must not overwrite / must not throw.
        File.WriteAllText(path, "# user edited\n");
        InitArtifacts.GenerateProjectFiltersTemplate(InitContext.Default);
        Assert.Equal("# user edited\n", File.ReadAllText(path));
    }

    [Fact]
    public void GenerateProjectFiltersTemplate_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        InitArtifacts.GenerateProjectFiltersTemplate(new InitContext(Verbose: 0, DryRun: true));

        Assert.False(Directory.Exists(Path.Combine(tmp.Root, ".rtk")));
    }

    // ── AtomicWrite / WriteIfChanged ─────────────────────────────────────────

    [Fact]
    public void WriteIfChanged_CreatesThenUpdatesThenNoopsWhenUnchanged()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "RTK.md");
        var ctx = InitContext.Default;

        var created = InitArtifacts.WriteIfChanged(path, "v1", "RTK.md", ctx);
        var updated = InitArtifacts.WriteIfChanged(path, "v2", "RTK.md", ctx);
        var noop = InitArtifacts.WriteIfChanged(path, "v2", "RTK.md", ctx);

        Assert.True(created);
        Assert.True(updated);
        Assert.False(noop);
        Assert.Equal("v2", File.ReadAllText(path));
    }

    [Fact]
    public void WriteIfChanged_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Root, "RTK.md");

        InitArtifacts.WriteIfChanged(path, "v1", "RTK.md", new InitContext(Verbose: 0, DryRun: true));

        Assert.False(File.Exists(path));
    }

    // ── Test helpers ─────────────────────────────────────────────────────────

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
