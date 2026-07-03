using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtkSharp.Rewrite;

/// <summary>
/// The agent host whose own permission settings should be consulted when a hook evaluates a
/// command. Mirrors Rust <c>permissions::Host</c> (permissions.rs:31).
/// </summary>
public enum PermissionHost
{
    /// <summary>Claude Code / VS Code Copilot / Copilot CLI — reads <c>~/.claude/settings*.json</c>.</summary>
    Claude,

    /// <summary>Cursor Agent — reads <c>~/.cursor/cli-config.json</c> (<c>Shell(...)</c>-scoped rules).</summary>
    Cursor,

    /// <summary>Gemini CLI — reads <c>~/.gemini/settings.json</c> (project override when folder-trusted).</summary>
    Gemini,
}

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
    private const string CursorDir = ".cursor";
    private const string CursorConfig = "cli-config.json";
    private const string GeminiDir = ".gemini";
    private const string SettingsJson = "settings.json";
    private const string SettingsLocalJson = "settings.local.json";
    private const string BashPrefix = "Bash(";

    private static readonly Lazy<PermissionRuleSet> CachedDefault = new(() => Load(baseOverride: null));
    private static readonly Lazy<PermissionRuleSet> CachedCursor = new(LoadCursor);
    private static readonly Lazy<PermissionRuleSet> CachedGemini = new(LoadGemini);

    /// <summary>
    /// The permission rules for the real user profile, loaded once and cached for the process.
    /// </summary>
    public static PermissionRuleSet Default => CachedDefault.Value;

    /// <summary>
    /// Returns the cached permission rule set for the given agent host. Faithful port of Rust
    /// <c>check_command_for</c>'s host dispatch (permissions.rs:37): Claude reads the
    /// <c>~/.claude/settings*.json</c> Bash rules; Cursor reads <c>~/.cursor/cli-config.json</c>
    /// <c>Shell(...)</c> rules; Gemini reads <c>~/.gemini/settings.json</c> shell-tool rules.
    /// </summary>
    /// <param name="host">The agent host whose settings should be consulted.</param>
    /// <returns>The merged deny / ask / allow rule set for the host.</returns>
    public static PermissionRuleSet ForHost(PermissionHost host) => host switch
    {
        PermissionHost.Cursor => CachedCursor.Value,
        PermissionHost.Gemini => CachedGemini.Value,
        _ => Default,
    };

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

    /// <summary>
    /// Loads Cursor's <c>Shell(...)</c>-scoped deny/allow rules from <c>~/.cursor/cli-config.json</c>.
    /// Global config only, mirroring Rust <c>load_cursor_rules</c> (permissions.rs:226): RTK never
    /// applies Cursor's project/folder-trust config, keeping its allow set a subset of the host's.
    /// </summary>
    /// <returns>The cursor deny / (empty ask) / allow rule set.</returns>
    private static PermissionRuleSet LoadCursor()
    {
        var deny = new List<string>();
        var allow = new List<string>();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) &&
            TryReadNode(Path.Combine(home, CursorDir, CursorConfig), out var root) &&
            root["permissions"] is JsonObject perms)
        {
            AppendWrappedRules(perms["deny"], ["Shell("], deny);
            AppendWrappedRules(perms["allow"], ["Shell("], allow);
        }

        return new PermissionRuleSet(deny, [], allow);
    }

    /// <summary>
    /// Loads Gemini's shell-tool ask/allow rules from <c>~/.gemini/settings.json</c> (with a
    /// folder-trusted project override). Faithful port of <c>load_gemini_rules</c> /
    /// <c>gemini_settings</c> (permissions.rs:243).
    /// </summary>
    /// <returns>The gemini (empty deny) / ask / allow rule set.</returns>
    private static PermissionRuleSet LoadGemini()
    {
        var ask = new List<string>();
        var allow = new List<string>();
        string[] shells = ["run_shell_command(", "ShellTool("];

        if (GeminiSettings() is { } settings && settings["tools"] is JsonObject tools)
        {
            AppendWrappedRules(tools["allowed"], shells, allow);
            AppendWrappedRules(tools["confirmationRequired"], shells, ask);
        }

        return new PermissionRuleSet([], ask, allow);
    }

    /// <summary>
    /// Resolves the Gemini settings object to consult: the folder-trusted project
    /// <c>.gemini/settings.json</c> when the workspace is trusted, otherwise the global one.
    /// Port of Rust <c>gemini_settings</c> (permissions.rs:243).
    /// </summary>
    /// <returns>The chosen settings object, or <see langword="null"/> when none is readable.</returns>
    private static JsonObject? GeminiSettings()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        JsonObject? global = null;
        if (!string.IsNullOrEmpty(home) &&
            TryReadNode(Path.Combine(home, GeminiDir, SettingsJson), out var g))
        {
            global = g;
        }

        var folderTrustEnabled = global?["security"]?["folderTrust"]?["enabled"] is JsonValue v &&
            v.GetValueKind() == JsonValueKind.True;
        var trusted = string.Equals(
                Environment.GetEnvironmentVariable("GEMINI_CLI_TRUST_WORKSPACE"),
                "true",
                StringComparison.Ordinal)
            || !folderTrustEnabled;

        if (trusted && FindProjectRoot() is { } root &&
            TryReadNode(Path.Combine(root, GeminiDir, SettingsJson), out var projectSettings))
        {
            return projectSettings;
        }

        return global;
    }

    /// <summary>
    /// Extracts wrapped shell rules (e.g. <c>Shell(git:*)</c>, <c>run_shell_command(npm test)</c>)
    /// from a JSON array. A bare wrapper name (e.g. <c>run_shell_command</c>) maps to <c>*</c>.
    /// Port of Rust <c>append_wrapped_rules</c> (permissions.rs:200).
    /// </summary>
    /// <param name="rulesValue">The JSON array node (or <see langword="null"/>).</param>
    /// <param name="prefixes">The wrapper prefixes to strip, e.g. <c>Shell(</c>.</param>
    /// <param name="target">The list to append extracted patterns to.</param>
    private static void AppendWrappedRules(JsonNode? rulesValue, string[] prefixes, List<string> target)
    {
        if (rulesValue is not JsonArray arr)
        {
            return;
        }

        foreach (var node in arr)
        {
            if (node is not JsonValue val || val.GetValueKind() != JsonValueKind.String)
            {
                continue;
            }

            var rule = val.GetValue<string>();
            foreach (var pre in prefixes)
            {
                var bare = pre[..^1];
                if (rule == bare)
                {
                    target.Add("*");
                    break;
                }

                if (rule.StartsWith(pre, StringComparison.Ordinal) && rule.EndsWith(')'))
                {
                    target.Add(rule[pre.Length..^1]);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Reads and parses a settings file into a <see cref="JsonObject"/>. Missing/unreadable files
    /// return <see langword="false"/> silently; malformed JSON returns <see langword="false"/> with
    /// a stderr warning (matching Rust <c>read_json</c>).
    /// </summary>
    /// <param name="path">The settings file path.</param>
    /// <param name="obj">The parsed object on success; otherwise <see langword="null"/>.</param>
    /// <returns>True if the file was read and parsed as a JSON object.</returns>
    private static bool TryReadNode(string path, out JsonObject obj)
    {
        obj = null!;

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
            if (JsonNode.Parse(content) is JsonObject parsed)
            {
                obj = parsed;
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"[rtk] warning: failed to parse permissions from {path}");
            return false;
        }
    }
}
