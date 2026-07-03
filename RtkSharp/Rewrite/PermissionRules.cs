using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RtkSharp.Rewrite;

/// <summary>
/// The deny / ask / allow Bash permission patterns loaded from Claude Code settings files.
/// </summary>
/// <param name="Deny">Bash deny patterns (highest precedence).</param>
/// <param name="Ask">Bash ask patterns.</param>
/// <param name="Allow">Bash allow patterns.</param>
public sealed record PermissionRuleSet(
    IReadOnlyList<string> Deny,
    IReadOnlyList<string> Ask,
    IReadOnlyList<string> Allow)
{
    /// <summary>An empty rule set — the state Claude Code is in when no permission rules exist.</summary>
    public static readonly PermissionRuleSet Empty = new([], [], []);
}

/// <summary>
/// Loads Claude Code's Bash permission rules from <c>settings.json</c> / <c>settings.local.json</c>,
/// feeding <see cref="Permissions.CheckCommand"/>. Faithful port of
/// <c>load_permission_rules</c> (and its helpers) from rtk's <c>src/hooks/permissions.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Files are read (and their <c>permissions.deny/ask/allow</c> arrays merged — no file overrides
/// another; all matching rules are unioned) in this order:
/// </para>
/// <list type="number">
/// <item><description><c>{projectRoot}/.claude/settings.json</c></description></item>
/// <item><description><c>{projectRoot}/.claude/settings.local.json</c></description></item>
/// <item><description><c>{home}/.claude/settings.json</c></description></item>
/// <item><description><c>{home}/.claude/settings.local.json</c></description></item>
/// </list>
/// <para>
/// Only <c>Bash(...)</c>-scoped rules are kept; the wrapper is stripped via
/// <see cref="ExtractBashPattern"/>. Missing files are skipped silently; malformed JSON is
/// skipped with a warning to stderr (matching Rust). The result is cached per-process in a lazy
/// static: a single CLI/hook invocation reads the settings files at most once, which is
/// behaviourally identical to Rust (where each process invokes <c>check_command</c> once).
/// </para>
/// </remarks>
public static class PermissionRules
{
    private const string ClaudeDir = ".claude";
    private const string SettingsJson = "settings.json";
    private const string SettingsLocalJson = "settings.local.json";
    private const string BashPrefix = "Bash(";

    private static readonly Lazy<PermissionRuleSet> CachedDefault = new(() => Load(baseOverride: null));

    /// <summary>
    /// The permission rules for the real user profile, loaded once and cached for the process.
    /// </summary>
    public static PermissionRuleSet Default => CachedDefault.Value;

    /// <summary>
    /// Loads permission rules, optionally overriding the settings root for deterministic testing.
    /// </summary>
    /// <param name="baseOverride">
    /// When non-<see langword="null"/>, the sole settings root: only
    /// <c>{baseOverride}/.claude/settings.json</c> and <c>settings.local.json</c> are read (the
    /// project-root walk and real home directory are ignored). This keeps unit tests fully
    /// deterministic and independent of the host's real <c>~/.claude/settings.json</c>. When
    /// <see langword="null"/> the full Rust loading path runs (project root + real user profile).
    /// </param>
    /// <returns>The merged deny / ask / allow Bash rule set.</returns>
    internal static PermissionRuleSet Load(string? baseOverride)
    {
        var deny = new List<string>();
        var ask = new List<string>();
        var allow = new List<string>();

        foreach (var path in GetSettingsPaths(baseOverride))
        {
            if (!TryReadJson(path, out var doc))
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("permissions", out var permissions) ||
                    permissions.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                AppendBashRules(permissions, "deny", deny);
                AppendBashRules(permissions, "ask", ask);
                AppendBashRules(permissions, "allow", allow);
            }
        }

        return new PermissionRuleSet(deny, ask, allow);
    }

    /// <summary>
    /// Returns the ordered list of Claude Code settings file paths to read.
    /// </summary>
    /// <param name="baseOverride">Optional settings root override (see <see cref="Load"/>).</param>
    /// <returns>The settings file paths, in read order.</returns>
    internal static IReadOnlyList<string> GetSettingsPaths(string? baseOverride)
    {
        var paths = new List<string>();

        if (baseOverride is not null)
        {
            paths.Add(Path.Combine(baseOverride, ClaudeDir, SettingsJson));
            paths.Add(Path.Combine(baseOverride, ClaudeDir, SettingsLocalJson));
            return paths;
        }

        if (FindProjectRoot() is { } root)
        {
            paths.Add(Path.Combine(root, ClaudeDir, SettingsJson));
            paths.Add(Path.Combine(root, ClaudeDir, SettingsLocalJson));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            paths.Add(Path.Combine(home, ClaudeDir, SettingsJson));
            paths.Add(Path.Combine(home, ClaudeDir, SettingsLocalJson));
        }

        return paths;
    }

    /// <summary>
    /// Extracts the pattern inside <c>Bash(pattern)</c>, or returns the input unchanged if it does
    /// not match that shape. Port of <c>extract_bash_pattern</c> (permissions.rs:304).
    /// </summary>
    /// <param name="rule">The raw rule string (e.g. <c>Bash(git:*)</c>).</param>
    /// <returns>The inner pattern (e.g. <c>git:*</c>), or <paramref name="rule"/> unchanged.</returns>
    internal static string ExtractBashPattern(string rule)
    {
        if (rule.StartsWith(BashPrefix, StringComparison.Ordinal) && rule.EndsWith(')'))
        {
            return rule[BashPrefix.Length..^1];
        }

        return rule;
    }

    /// <summary>
    /// Appends the <c>Bash(...)</c>-scoped patterns from a permissions array to <paramref name="target"/>.
    /// Non-Bash rules (e.g. <c>Read(...)</c>) are ignored.
    /// </summary>
    /// <param name="permissions">The <c>permissions</c> JSON object.</param>
    /// <param name="key">The array key (<c>deny</c>, <c>ask</c>, or <c>allow</c>).</param>
    /// <param name="target">The list to append extracted patterns to.</param>
    private static void AppendBashRules(JsonElement permissions, string key, List<string> target)
    {
        if (!permissions.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var rule in arr.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var s = rule.GetString()!;
            if (s.StartsWith(BashPrefix, StringComparison.Ordinal))
            {
                target.Add(ExtractBashPattern(s));
            }
        }
    }

    /// <summary>
    /// Reads and parses a settings JSON file. Missing/unreadable files return
    /// <see langword="false"/> silently; malformed JSON returns <see langword="false"/> with a
    /// stderr warning (matching Rust's <c>load_permission_rules</c>).
    /// </summary>
    /// <param name="path">The settings file path.</param>
    /// <param name="doc">The parsed document on success; otherwise <see langword="null"/>.</param>
    /// <returns>True if the file was read and parsed as JSON.</returns>
    private static bool TryReadJson(string path, out JsonDocument doc)
    {
        doc = null!;

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(content);
            return true;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"[rtk] warning: failed to parse permissions from {path}");
            return false;
        }
    }

    /// <summary>
    /// Locates the project root by walking up from the current directory looking for a
    /// <c>.claude/</c> directory, falling back to <c>git rev-parse --show-toplevel</c>.
    /// Port of <c>find_project_root</c> (permissions.rs:277).
    /// </summary>
    /// <returns>The project root path, or <see langword="null"/> if none is found.</returns>
    private static string? FindProjectRoot()
    {
        string cwd;
        try
        {
            cwd = Directory.GetCurrentDirectory();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ClaudeDir)))
            {
                return dir.FullName;
            }
        }

        return GitToplevel();
    }

    /// <summary>
    /// Runs <c>git rev-parse --show-toplevel</c> and returns the trimmed path, or
    /// <see langword="null"/> if git is unavailable or the command fails.
    /// </summary>
    /// <returns>The git top-level directory, or <see langword="null"/>.</returns>
    private static string? GitToplevel()
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--show-toplevel");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode == 0)
            {
                var trimmed = stdout.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // git not on PATH / spawn failure — no project root via this fallback.
        }

        return null;
    }
}
