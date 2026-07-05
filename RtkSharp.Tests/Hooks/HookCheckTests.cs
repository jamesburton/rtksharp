using System;
using System.IO;
using RtkSharp;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Port of Rust <c>src/hooks/hook_check.rs</c>'s <c>mod tests</c> (<c>hook_check.rs:150-319</c>),
/// plus additional coverage for <see cref="HookCheck.Status"/>'s binary-hook/legacy-script branches
/// and <see cref="HookCheck.MaybeWarn"/>'s byte-exact, rate-limited stderr output — neither of which
/// the Rust suite exercises with fixtures (its own <c>test_status_returns_valid_variant</c> only
/// probes the real host's <c>~/.claude</c>, which is unsuitable for a hermetic CI battery).
/// </summary>
public sealed class HookCheckTests
{
    private const string LegacyHookRelativePath = "hooks/rtk-rewrite.sh";
    private const string SettingsRelativePath = "settings.json";

    private static readonly string RegisteredSettingsJson =
        """
        {
          "hooks": {
            "PreToolUse": [
              {
                "matcher": "*",
                "hooks": [
                  { "type": "command", "command": "rtk hook claude" }
                ]
              }
            ]
          }
        }
        """;

    // -----------------------------------------------------------------------
    // ParseHookVersion — 1:1 port of hook_check.rs's version-parsing tests.
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseHookVersion_Present()
    {
        const string content = "#!/usr/bin/env bash\n# rtk-hook-version: 2\n# some comment\n";
        Assert.Equal(2, HookCheck.ParseHookVersion(content));
    }

    [Fact]
    public void ParseHookVersion_Missing()
    {
        const string content = "#!/usr/bin/env bash\n# old hook without version\n";
        Assert.Equal(0, HookCheck.ParseHookVersion(content));
    }

    [Fact]
    public void ParseHookVersion_Future()
    {
        const string content = "#!/usr/bin/env bash\n# rtk-hook-version: 5\n";
        Assert.Equal(5, HookCheck.ParseHookVersion(content));
    }

    [Fact]
    public void ParseHookVersion_NoTag()
    {
        Assert.Equal(0, HookCheck.ParseHookVersion("no version here"));
        Assert.Equal(0, HookCheck.ParseHookVersion(""));
    }

    [Fact]
    public void ParseHookVersion_TagBeyondFirstFiveLines_IsIgnored()
    {
        // Mirrors the Rust `.lines().take(5)` guard: a tag on line 6 must not count.
        const string content = "1\n2\n3\n4\n5\n# rtk-hook-version: 9\n";
        Assert.Equal(0, HookCheck.ParseHookVersion(content));
    }

    [Fact]
    public void HookStatus_EnumValues_AreDistinct()
    {
        Assert.NotEqual(HookStatus.Ok, HookStatus.Missing);
        Assert.NotEqual(HookStatus.Outdated, HookStatus.Missing);
        Assert.Equal(HookStatus.Ok, HookStatus.Ok);
    }

    // -----------------------------------------------------------------------
    // Status() — synthetic hook-file/settings.json fixtures for each of the three states.
    // -----------------------------------------------------------------------

    [Fact]
    public void Status_NoClaudeDir_ReturnsOk()
    {
        using var tmp = new TempDir();
        using var env = new EnvVarScope("CLAUDE_CONFIG_DIR", Path.Combine(tmp.Root, "does-not-exist"));

        Assert.Equal(HookStatus.Ok, HookCheck.Status());
    }

    [Fact]
    public void Status_ClaudeDirWithNothingInstalled_ReturnsMissing()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);

        Assert.Equal(HookStatus.Missing, HookCheck.Status());
    }

    [Fact]
    public void Status_LegacyHookCurrentVersion_ReturnsOk()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        WriteLegacyHook(guard.ClaudeDir, version: 3);

        Assert.Equal(HookStatus.Ok, HookCheck.Status());
    }

    [Fact]
    public void Status_LegacyHookOldVersion_ReturnsOutdated()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        WriteLegacyHook(guard.ClaudeDir, version: 2);

        Assert.Equal(HookStatus.Outdated, HookCheck.Status());
    }

    [Fact]
    public void Status_LegacyHookNoVersionTag_ReturnsOutdated()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        var hookPath = Path.Combine(guard.ClaudeDir, LegacyHookRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(hookPath, "#!/usr/bin/env bash\necho legacy\n");

        Assert.Equal(HookStatus.Outdated, HookCheck.Status());
    }

    [Fact]
    public void Status_BinaryHookRegistered_ReturnsOk()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        File.WriteAllText(Path.Combine(guard.ClaudeDir, SettingsRelativePath), RegisteredSettingsJson);

        Assert.Equal(HookStatus.Ok, HookCheck.Status());
    }

    [Fact]
    public void Status_BinaryHookRegisteredButLegacyScriptStillPresent_ReturnsOutdated()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        File.WriteAllText(Path.Combine(guard.ClaudeDir, SettingsRelativePath), RegisteredSettingsJson);
        WriteLegacyHook(guard.ClaudeDir, version: 3); // Migration incomplete: old script left behind.

        Assert.Equal(HookStatus.Outdated, HookCheck.Status());
    }

    [Fact]
    public void Status_SettingsJsonPresentButHookNotRegistered_FallsBackToLegacyCheck()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        File.WriteAllText(Path.Combine(guard.ClaudeDir, SettingsRelativePath), "{ \"other\": true }");

        Assert.Equal(HookStatus.Missing, HookCheck.Status());
    }

    [Fact]
    public void Status_MalformedSettingsJson_FallsBackToLegacyCheck()
    {
        using var tmp = new TempDir();
        using var guard = new GlobalScopeGuard(tmp);
        File.WriteAllText(Path.Combine(guard.ClaudeDir, SettingsRelativePath), "{ not valid json");
        WriteLegacyHook(guard.ClaudeDir, version: 3);

        Assert.Equal(HookStatus.Ok, HookCheck.Status());
    }

    // -----------------------------------------------------------------------
    // MaybeWarn() — byte-exact stderr text (Rust `eprintln!("{}", warning)`, one trailing newline,
    // no separate blank line — that blank-line-after-yellow-text form belongs to `gain.rs`'s own
    // summary view, a different Rust module Phase 5 Task 5 will port; hook_check.rs's own
    // `maybe_warn()` string differs from `gain.rs`'s: "[rtk] /!\" prefix, not "[warn]") and
    // once-per-day rate limiting.
    // -----------------------------------------------------------------------

    [Fact]
    public void MaybeWarn_Missing_PrintsExactWarningSingleLine()
    {
        using var tmp = new TempDir();
        using var globalScope = new GlobalScopeGuard(tmp);
        using var dataDir = new DataDirGuard(tmp);
        using var console = new ConsoleCapture();

        HookCheck.MaybeWarn();

        Assert.Equal(
            "[rtk] /!\\ No hook installed — run `rtk init -g` for automatic token savings\n",
            console.Error.ToString());
        Assert.Equal(string.Empty, console.Out.ToString());
    }

    [Fact]
    public void MaybeWarn_Outdated_PrintsExactWarningSingleLine()
    {
        using var tmp = new TempDir();
        using var globalScope = new GlobalScopeGuard(tmp);
        using var dataDir = new DataDirGuard(tmp);
        using var console = new ConsoleCapture();
        WriteLegacyHook(globalScope.ClaudeDir, version: 1);

        HookCheck.MaybeWarn();

        Assert.Equal(
            "[rtk] /!\\ Hook outdated — run `rtk init -g` to update\n",
            console.Error.ToString());
    }

    [Fact]
    public void MaybeWarn_Ok_PrintsNothing()
    {
        using var tmp = new TempDir();
        using var globalScope = new GlobalScopeGuard(tmp);
        using var dataDir = new DataDirGuard(tmp);
        using var console = new ConsoleCapture();
        File.WriteAllText(Path.Combine(globalScope.ClaudeDir, SettingsRelativePath), RegisteredSettingsJson);

        HookCheck.MaybeWarn();

        Assert.Equal(string.Empty, console.Error.ToString());
    }

    [Fact]
    public void MaybeWarn_SecondCallWithinRateLimitWindow_PrintsNothing()
    {
        using var tmp = new TempDir();
        using var globalScope = new GlobalScopeGuard(tmp);
        using var dataDir = new DataDirGuard(tmp);

        using (var first = new ConsoleCapture())
        {
            HookCheck.MaybeWarn();
            Assert.NotEqual(string.Empty, first.Error.ToString());
        }

        using var second = new ConsoleCapture();
        HookCheck.MaybeWarn();
        Assert.Equal(string.Empty, second.Error.ToString());
    }

    [Fact]
    public void MaybeWarn_RateLimitWindowElapsed_WarnsAgain()
    {
        using var tmp = new TempDir();
        using var globalScope = new GlobalScopeGuard(tmp);
        using var dataDir = new DataDirGuard(tmp);

        using (var first = new ConsoleCapture())
        {
            HookCheck.MaybeWarn();
        }

        // Backdate the marker file past the 24h window.
        var marker = HookCheck.WarnMarkerPath();
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddHours(-25));

        using var second = new ConsoleCapture();
        HookCheck.MaybeWarn();
        Assert.NotEqual(string.Empty, second.Error.ToString());
    }

    [Fact]
    public void MaybeWarn_NeverThrows_EvenWhenClaudeConfigDirUnresolvable()
    {
        using var tmp = new TempDir();

        // MaybeWarn's own try/catch must swallow any failure regardless of hook state.
        using var globalScope = new GlobalScopeGuard(tmp);
        using var dataDir = new DataDirGuard(tmp);

        var exception = Record.Exception(() => HookCheck.MaybeWarn());
        Assert.Null(exception);
    }

    // -----------------------------------------------------------------------
    // Dispatch wiring — `gain` must skip the startup warning entirely (Rust main.rs:1483-1485).
    // -----------------------------------------------------------------------

    // xUnit1031 (avoid blocking task operations) is intentionally suppressed for both dispatch
    // tests below: they block synchronously via GetAwaiter().GetResult() rather than `await` purely
    // to keep the test method itself simple (no `async Task` signature needed for a single
    // dispatch call) — not for correctness. InitTestSupport's env-var guards are safe to hold across
    // a real `await` regardless (EnterEnvLock/ExitEnvLock use an AsyncLocal-tracked reentrant
    // semaphore, not a thread-affine Monitor, specifically so a guard can be entered on one
    // thread-pool thread and exited on another after an await's continuation hops threads). There is
    // no risk of the usual synchronous-blocking-causes-deadlock scenario here since
    // RtkProgram.RunAsync never captures a SynchronizationContext (xUnit tests run without one).
#pragma warning disable xUnit1031

    [Fact]
    public void RunAsync_GainCommand_SkipsHookWarningEvenWhenHookMissing()
    {
        using var tmp = new TempDir();
        using var globalScope = new GlobalScopeGuard(tmp); // Empty .claude dir => HookStatus.Missing.
        using var dataDir = new DataDirGuard(tmp);
        using var console = new ConsoleCapture();

        RtkProgram.RunAsync(new[] { "gain" }).GetAwaiter().GetResult();

        Assert.DoesNotContain("[rtk] /!\\", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunAsync_NonGainCommand_StillEmitsHookWarningWhenMissing()
    {
        using var tmp = new TempDir();
        using var globalScope = new GlobalScopeGuard(tmp); // Empty .claude dir => HookStatus.Missing.
        using var dataDir = new DataDirGuard(tmp);
        using var console = new ConsoleCapture();

        var args = OperatingSystem.IsWindows()
            ? new[] { "cmd", "/c", "exit", "0" }
            : new[] { "sh", "-c", "exit 0" };
        RtkProgram.RunAsync(args).GetAwaiter().GetResult();

        Assert.Contains("[rtk] /!\\ No hook installed", console.Error.ToString(), StringComparison.Ordinal);
    }

#pragma warning restore xUnit1031

    private static void WriteLegacyHook(string claudeDir, int version)
    {
        var hookPath = Path.Combine(claudeDir, LegacyHookRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(hookPath, $"#!/usr/bin/env bash\n# rtk-hook-version: {version}\necho hook\n");
    }
}
