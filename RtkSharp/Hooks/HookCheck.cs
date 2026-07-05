using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using RtkSharp.Core.Tracking;

namespace RtkSharp.Hooks;

/// <summary>
/// Hook status for diagnostics and <c>rtk gain</c>. Faithful port of Rust <c>HookStatus</c>
/// (<c>hook_check.rs:15-22</c>).
/// </summary>
public enum HookStatus
{
    /// <summary>Hook is installed and up to date.</summary>
    Ok,

    /// <summary>Hook exists but is outdated or unreadable.</summary>
    Outdated,

    /// <summary>No hook file found (but Claude Code is installed).</summary>
    Missing,
}

/// <summary>
/// Detects whether RTK hooks are installed and warns if they are outdated. Faithful port of Rust
/// <c>src/hooks/hook_check.rs</c>: <c>status()</c> (the pure detection logic used by both the
/// startup warning and, in a later phase, <c>rtk gain</c>'s summary view) and <c>maybe_warn()</c>
/// (the rate-limited startup warning called once per process, before command dispatch).
/// </summary>
/// <remarks>
/// <para>
/// <b>Relationship to <see cref="Integrity"/>.</b> Despite both modules being "hook checking",
/// they detect different things and share no code: <see cref="Integrity"/> asks "has the
/// installed hook file been tampered with since <c>rtk init</c> stored its hash?" (SHA-256
/// comparison), while <see cref="HookCheck"/> asks "is a hook installed at all, and if so, is it
/// new enough?" (settings.json registration check, or a legacy script's embedded version tag).
/// Rust's own module boundary keeps them entirely separate (<c>hook_check.rs</c> never touches
/// <c>integrity.rs</c>'s hash sidecar), and this port mirrors that.
/// </para>
/// <para>
/// <b>Not to be confused with <c>rtk gain</c>'s own hook-status line.</b> <c>gain.rs</c>
/// separately calls <see cref="Status"/> directly (not <see cref="MaybeWarn"/>) and prints its
/// own differently-formatted, <c>"[warn] ..."</c>-prefixed, yellow-styled warning with a
/// trailing blank line as part of its summary view (<c>gain.rs:125-137</c>) — that is Phase 5
/// Task 5's concern, not this module's. This module ports only <c>hook_check.rs</c> itself:
/// its <c>maybe_warn()</c> startup check uses the exact verbatim text below, printed via a
/// single <c>eprintln!</c> (one trailing newline, no blank line).
/// </para>
/// </remarks>
public static class HookCheck
{
    /// <summary>The hooks subdirectory under the Claude config dir (Rust <c>HOOKS_SUBDIR</c>, constants.rs:15).</summary>
    private const string HooksSubdir = "hooks";

    /// <summary>The legacy shell hook filename (Rust <c>REWRITE_HOOK_FILE</c>, constants.rs:12).</summary>
    private const string RewriteHookFile = "rtk-rewrite.sh";

    /// <summary>The Claude Code settings file name (Rust <c>SETTINGS_JSON</c>, constants.rs:5).</summary>
    private const string SettingsJsonName = "settings.json";

    /// <summary>The settings.json array key holding pre-tool-use hook entries (Rust <c>PRE_TOOL_USE_KEY</c>, constants.rs:8).</summary>
    private const string PreToolUseKey = "PreToolUse";

    /// <summary>The settings.json object key that contains all hook categories.</summary>
    private const string HooksKey = "hooks";

    /// <summary>The minimum embedded version tag a legacy script hook must carry to be considered up to date (Rust <c>CURRENT_HOOK_VERSION</c>, hook_check.rs:10).</summary>
    private const byte CurrentHookVersion = 3;

    /// <summary>How long a printed warning suppresses further warnings, in seconds (Rust <c>WARN_INTERVAL_SECS</c>, hook_check.rs:11).</summary>
    private const long WarnIntervalSeconds = 24 * 3600;

    /// <summary>The rate-limit marker file's name (Rust <c>warn_marker_path</c>, hook_check.rs:145-148).</summary>
    private const string WarnMarkerFileName = ".hook_warn_last";

    /// <summary>
    /// Returns the current hook status without printing anything. Returns <see cref="HookStatus.Ok"/>
    /// if no Claude Code installation is detected (not applicable). Faithful port of Rust
    /// <c>status()</c> (<c>hook_check.rs:26-59</c>).
    /// </summary>
    /// <returns>The detected <see cref="HookStatus"/>.</returns>
    public static HookStatus Status()
    {
        // Don't warn users who don't have Claude Code installed.
        string claudeDir;
        try
        {
            claudeDir = SettingsPatcher.ResolveClaudeDir();
        }
        catch (InitAbortException)
        {
            return HookStatus.Ok;
        }

        if (!Directory.Exists(claudeDir))
        {
            return HookStatus.Ok;
        }

        // Check for the new binary command in settings.json first.
        if (BinaryHookRegistered(claudeDir))
        {
            // If the old script file still exists alongside the new command, report Outdated
            // (migration not complete — user should run `rtk init -g` to clean up).
            var oldHook = Path.Combine(claudeDir, HooksSubdir, RewriteHookFile);
            return File.Exists(oldHook) ? HookStatus.Outdated : HookStatus.Ok;
        }

        // Fall back to the legacy script file check.
        var hookPath = HookInstalledPath(claudeDir);
        if (hookPath is null)
        {
            return HookStatus.Missing;
        }

        string content;
        try
        {
            content = File.ReadAllText(hookPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HookStatus.Outdated; // Exists but unreadable — treat as needs-update.
        }

        return ParseHookVersion(content) >= CurrentHookVersion ? HookStatus.Ok : HookStatus.Outdated;
    }

    /// <summary>
    /// Checks if the native binary command is registered in <c>settings.json</c>. Faithful port of
    /// Rust <c>binary_hook_registered</c> (<c>hook_check.rs:62-86</c>).
    /// </summary>
    /// <param name="claudeDir">The resolved Claude config directory.</param>
    /// <returns><see langword="true"/> if <see cref="SettingsPatcher.ClaudeHookCommand"/> is registered under <c>hooks.PreToolUse[*].hooks[*].command</c>.</returns>
    private static bool BinaryHookRegistered(string claudeDir)
    {
        var settingsPath = Path.Combine(claudeDir, SettingsJsonName);

        string content;
        try
        {
            content = File.ReadAllText(settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root?[HooksKey] is not JsonObject hooksObject || hooksObject[PreToolUseKey] is not JsonArray preToolUse)
        {
            return false;
        }

        foreach (var entry in preToolUse)
        {
            if (entry?["hooks"] is not JsonArray innerHooks)
            {
                continue;
            }

            foreach (var hook in innerHooks)
            {
                if (hook?["command"] is JsonValue commandValue
                    && commandValue.TryGetValue(out string? command)
                    && command == SettingsPatcher.ClaudeHookCommand)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the installed hook is missing or outdated, and warns to stderr — rate-limited to
    /// once per day. Never blocks startup: any error is swallowed silently. Faithful port of Rust
    /// <c>maybe_warn()</c> (<c>hook_check.rs:89-92</c>).
    /// </summary>
    public static void MaybeWarn()
    {
        try
        {
            CheckAndWarn();
        }
        catch
        {
            // Don't block startup — fail silently on any error, matching Rust's `let _ = ...`.
        }
    }

    /// <summary>
    /// Single source of truth: delegates to <see cref="Status"/> then rate-limits the warning.
    /// Faithful port of Rust <c>check_and_warn</c> (<c>hook_check.rs:95-121</c>).
    /// </summary>
    private static void CheckAndWarn()
    {
        string warning;
        switch (Status())
        {
            case HookStatus.Ok:
                return;
            case HookStatus.Missing:
                warning = "[rtk] /!\\ No hook installed — run `rtk init -g` for automatic token savings";
                break;
            case HookStatus.Outdated:
                warning = "[rtk] /!\\ Hook outdated — run `rtk init -g` to update";
                break;
            default:
                return;
        }

        // Rate limit: warn once per day.
        var marker = WarnMarkerPath();
        if (File.Exists(marker))
        {
            var modified = File.GetLastWriteTimeUtc(marker);
            if ((DateTime.UtcNow - modified).TotalSeconds < WarnIntervalSeconds)
            {
                return;
            }
        }

        // Byte-exact: single line, matching Rust's `eprintln!("{}", warning)` (one trailing newline).
        Console.Error.Write(warning + "\n");

        // Touch marker after the warning is printed.
        var markerDir = Path.GetDirectoryName(marker);
        if (!string.IsNullOrEmpty(markerDir))
        {
            Directory.CreateDirectory(markerDir);
        }

        File.WriteAllBytes(marker, []);
    }

    /// <summary>
    /// Parses the embedded <c>rtk-hook-version</c> tag from a legacy shell hook's first five lines.
    /// Faithful port of Rust <c>parse_hook_version</c> (<c>hook_check.rs:123-133</c>).
    /// </summary>
    /// <param name="content">The hook script's full text content.</param>
    /// <returns>The parsed version number, or <c>0</c> if no valid tag is found.</returns>
    public static byte ParseHookVersion(string content)
    {
        const string prefix = "# rtk-hook-version:";

        // Version tag must be in the first 5 lines (shebang + header convention).
        var lines = InitArtifacts.SplitRustLines(content);
        var limit = Math.Min(lines.Length, 5);
        for (var i = 0; i < limit; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[prefix.Length..].Trim();
            if (byte.TryParse(rest, out var version))
            {
                return version;
            }
        }

        return 0; // No version tag = version 0 (outdated).
    }

    /// <summary>
    /// Resolves the legacy hook script's installed path, if present. Faithful port of Rust
    /// <c>hook_installed_path</c> (<c>hook_check.rs:135-143</c>).
    /// </summary>
    /// <param name="claudeDir">The resolved Claude config directory.</param>
    /// <returns>The hook path, or <see langword="null"/> if it does not exist.</returns>
    private static string? HookInstalledPath(string claudeDir)
    {
        var path = Path.Combine(claudeDir, HooksSubdir, RewriteHookFile);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Resolves the rate-limit marker file's path: <c>{data_dir}/rtk/.hook_warn_last</c>. Faithful
    /// port of Rust <c>warn_marker_path</c> (<c>hook_check.rs:145-148</c>), except this reuses
    /// <see cref="TrustCommand.ResolveDataDir"/> (rather than a bare platform-data-dir lookup) so it
    /// honors the same <see cref="TrustCommand.DataDirOverrideEnvVar"/> (<c>RTK_DATA_DIR_OVERRIDE</c>)
    /// test/parity escape hatch already established for <see cref="Tracker"/>'s default DB path —
    /// otherwise unit tests exercising the rate-limit window would touch the real
    /// <c>%LOCALAPPDATA%\rtk</c>/<c>~/.local/share/rtk</c> directory on the host machine.
    /// </summary>
    /// <returns>The resolved marker file path.</returns>
    internal static string WarnMarkerPath() =>
        Path.Combine(TrustCommand.ResolveDataDir(), TrackingConstants.RtkDataDir, WarnMarkerFileName);
}
