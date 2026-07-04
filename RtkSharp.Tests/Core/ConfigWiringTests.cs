using System.IO;
using System.Text;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Hooks;
using RtkSharp.Tests.Hooks;
using Xunit;

namespace RtkSharp.Tests.Core;

/// <summary>
/// Tests for Phase 4 Task 5's config wiring into the runtime hot paths: the rewrite engine's
/// <c>hooks.exclude_commands</c> and the grep per-file cap (<c>limits.grep_max_per_file</c>). Each
/// path must (a) honor a valid <c>config.toml</c> and (b) fall back to today's default behavior when
/// the config is corrupt — never crashing the pipeline (the fallback-pattern Global Constraint). All
/// tests redirect the config dir via <see cref="GlobalScopeGuard"/> so the real user config is never
/// read or written.
/// </summary>
public sealed class ConfigWiringTests
{
    private static string BuildRawNulOutput(string file, int matchCount)
    {
        var sb = new StringBuilder();
        for (var n = 1; n <= matchCount; n++)
        {
            sb.Append(file).Append('\0').Append(n).Append(':').Append("line").Append(n).Append('\n');
        }

        return sb.ToString();
    }

    // ── Rewrite engine: hooks.exclude_commands wiring ────────────────────────────────────────

    [Fact]
    public void RewriteHook_ExcludeCommandsFromConfig_SuppressesRewrite()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);

        var config = new Config();
        config.Hooks.ExcludeCommands.Add("git status");
        config.Save();

        using var console = new ConsoleCapture();
        // `rtk hook check git status` runs the rewrite engine with the config's exclude list.
        var exit = HookCommand.Run(["check", "git", "status"]);

        Assert.Equal(1, exit); // excluded → no rewrite
        Assert.Contains("No rewrite for: git status", console.Error.ToString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteHook_NoExclusion_RewritesNormally()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);
        // No config file written → all-defaults (empty exclude list).

        using var console = new ConsoleCapture();
        var exit = HookCommand.Run(["check", "git", "status"]);

        Assert.Equal(0, exit);
        Assert.Contains("rtk git status", console.Out.ToString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteHook_CorruptConfig_FallsBackToEmptyListsAndStillRewrites()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);

        var path = Config.GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is definitely {{ not valid toml =");

        using var console = new ConsoleCapture();
        var exit = HookCommand.Run(["check", "git", "status"]);

        // Fallback pattern: a corrupt config must not crash the rewrite — it degrades to empty lists,
        // so the command still rewrites exactly as with no config at all.
        Assert.Equal(0, exit);
        Assert.Contains("rtk git status", console.Out.ToString(), System.StringComparison.Ordinal);
    }

    // ── Grep: limits.grep_max_per_file wiring ────────────────────────────────────────────────

    [Fact]
    public void GrepCap_FromConfig_LimitsPerFileLines()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);

        var config = new Config();
        config.Limits.GrepMaxPerFile = 3;
        config.Save();

        var raw = BuildRawNulOutput("app.txt", 30);
        var output = GrepCommand.BuildGroupedOutput(raw, "line", maxLen: 80, maxResults: 200, contextOnly: false);

        // 3 of 30 shown → 27 suppressed.
        Assert.Contains("[+27 more]", output, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GrepCap_CorruptConfig_FallsBackToDefault25()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);

        var path = Config.GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not = valid = toml = at = all {{{");

        var raw = BuildRawNulOutput("app.txt", 30);
        var output = GrepCommand.BuildGroupedOutput(raw, "line", maxLen: 80, maxResults: 200, contextOnly: false);

        // Fallback to the default cap of 25 → 5 suppressed.
        Assert.Contains("[+5 more]", output, System.StringComparison.Ordinal);
    }
}
