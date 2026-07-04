using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtkSharp.Hooks;

/// <summary>
/// Result of the <c>settings.json</c> patching operation. Mirrors Rust <c>PatchResult</c>
/// (init.rs:84).
/// </summary>
public enum PatchResult
{
    /// <summary>The hook was added successfully.</summary>
    Patched,

    /// <summary>The hook was already present in <c>settings.json</c>.</summary>
    AlreadyPresent,

    /// <summary>The user declined the <c>[y/N]</c> prompt.</summary>
    Declined,

    /// <summary><c>--no-patch</c> was used; manual instructions were printed instead.</summary>
    Skipped,

    /// <summary>Dry-run: the hook would have been added.</summary>
    WouldPatch,
}

/// <summary>
/// <c>settings.json</c> deep-merge and patch-decision logic for global-scope <c>rtk init</c>.
/// Faithful port of the settings.json helpers in Rust <c>src/hooks/init.rs</c>:
/// <c>resolve_claude_dir</c> (init.rs:2724), <c>hook_already_present</c> (init.rs:1099),
/// <c>insert_hook_entry</c> (init.rs:1066), <c>remove_hook_from_json</c> (init.rs:529),
/// <c>remove_legacy_hook_entries_from_json</c> (init.rs:1322), <c>patch_settings_json_command</c>
/// (init.rs:933), <c>remove_hook_from_settings</c> (init.rs:563), <c>print_manual_instructions</c>
/// (init.rs:509), and <c>prompt_user_consent</c> (init.rs:427).
/// </summary>
/// <remarks>
/// Uses <see cref="JsonNode"/>/<see cref="JsonObject"/> (not <see cref="JsonSerializer"/> POCO
/// round-tripping) so unrecognized keys and array/object insertion order are preserved exactly —
/// mirroring serde_json's <c>Value</c>, which RTK relies on to merge the RTK hook entry into an
/// existing <c>settings.json</c> without disturbing any other content the user (or another tool)
/// put there.
/// </remarks>
public static class SettingsPatcher
{
    /// <summary>The binary hook command RTK registers for Claude Code (Rust <c>CLAUDE_HOOK_COMMAND</c>, constants.rs:12).</summary>
    public const string ClaudeHookCommand = "rtk hook claude";

    /// <summary>The environment variable that overrides the resolved Claude config directory, honored first.</summary>
    private const string ClaudeConfigDirEnvVar = "CLAUDE_CONFIG_DIR";

    private const string ClaudeDirName = ".claude";
    private const string SettingsJsonName = "settings.json";
    private const string PreToolUseKey = "PreToolUse";

    /// <summary>The legacy shell hook filename matched by substring for backward compatibility (Rust <c>REWRITE_HOOK_FILE</c>).</summary>
    private const string RewriteHookFile = "rtk-rewrite.sh";

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        IndentSize = 2,
    };

    /// <summary>
    /// Resolves the Claude Code config directory: <c>CLAUDE_CONFIG_DIR</c> environment variable
    /// first (the isolation hook honored on every OS, including Windows, where
    /// <c>dirs::home_dir()</c> has no equivalent override), then <c>{home}/.claude</c>. Port of
    /// Rust <c>resolve_claude_dir</c>/<c>resolve_claude_dir_from</c> (init.rs:2724-2741).
    /// </summary>
    /// <returns>The resolved Claude config directory path.</returns>
    /// <exception cref="InitAbortException">Neither <c>CLAUDE_CONFIG_DIR</c> nor the home directory could be resolved.</exception>
    public static string ResolveClaudeDir()
    {
        var overridden = Environment.GetEnvironmentVariable(ClaudeConfigDirEnvVar);
        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            throw new InitAbortException("Cannot determine Claude config directory. Set $CLAUDE_CONFIG_DIR or $HOME.");
        }

        return Path.Combine(home, ClaudeDirName);
    }

    /// <summary>
    /// Checks whether the RTK hook is already registered in <paramref name="root"/>'s
    /// <c>hooks.PreToolUse</c> array — matching either <paramref name="hookCommand"/> verbatim, the
    /// canonical <see cref="ClaudeHookCommand"/>, or (for backward compatibility) any command
    /// substring-containing the legacy <c>rtk-rewrite.sh</c> script path. Port of Rust
    /// <c>hook_already_present</c> (init.rs:1099).
    /// </summary>
    /// <param name="root">The parsed <c>settings.json</c> root object.</param>
    /// <param name="hookCommand">The hook command to check for.</param>
    /// <returns><see langword="true"/> if the hook is already present.</returns>
    public static bool HookAlreadyPresent(JsonObject root, string hookCommand)
    {
        if (root[PreToolUseParentKey] is not JsonObject hooks || hooks[PreToolUseKey] is not JsonArray preToolUse)
        {
            return false;
        }

        foreach (var entry in preToolUse)
        {
            if (EntryHasCommand(entry, cmd => cmd == hookCommand || cmd == ClaudeHookCommand || cmd.Contains(RewriteHookFile, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private const string PreToolUseParentKey = "hooks";

    /// <summary>
    /// Deep-merges the RTK hook entry into <paramref name="root"/>'s <c>hooks.PreToolUse</c> array,
    /// creating the <c>hooks</c> object and <c>PreToolUse</c> array if missing and preserving any
    /// existing entries. Port of Rust <c>insert_hook_entry</c> (init.rs:1066).
    /// </summary>
    /// <param name="root">The <c>settings.json</c> root object to mutate.</param>
    /// <param name="hookCommand">The hook command to register.</param>
    /// <exception cref="InitAbortException">
    /// The existing <c>hooks</c> or <c>hooks.PreToolUse</c> value is present but not the expected
    /// shape (object / array respectively).
    /// </exception>
    public static void InsertHookEntry(JsonObject root, string hookCommand)
    {
        if (root[PreToolUseParentKey] is not JsonObject hooks)
        {
            if (root[PreToolUseParentKey] is not null)
            {
                throw new InitAbortException("hooks value is not an object");
            }

            hooks = new JsonObject();
            root[PreToolUseParentKey] = hooks;
        }

        if (hooks[PreToolUseKey] is not JsonArray preToolUse)
        {
            if (hooks[PreToolUseKey] is not null)
            {
                throw new InitAbortException("PreToolUse value is not an array");
            }

            preToolUse = new JsonArray();
            hooks[PreToolUseKey] = preToolUse;
        }

        // Cast to the IList<JsonNode?> interface explicitly: JsonArray's public Add<T>(T) extension
        // (System.Text.Json.Nodes.JsonNodeExtensions) is AOT/trimming-unfriendly for non-primitive T
        // (IL2026/IL3050) since it has to consider wrapping T in a JsonValue; the plain
        // IList<JsonNode?>.Add(JsonNode?) call below adds the already-constructed node directly.
        ((IList<JsonNode?>)preToolUse).Add(new JsonObject
        {
            ["matcher"] = "Bash",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = hookCommand,
            }),
        });
    }

    /// <summary>
    /// Removes any <c>hooks.PreToolUse</c> entry containing a hook whose command is the canonical
    /// <see cref="ClaudeHookCommand"/> or contains the legacy <c>rtk-rewrite.sh</c> path. Port of
    /// Rust <c>remove_hook_from_json</c> (init.rs:529).
    /// </summary>
    /// <param name="root">The <c>settings.json</c> root object to mutate.</param>
    /// <returns><see langword="true"/> if any entry was removed.</returns>
    public static bool RemoveHookFromJson(JsonObject root)
    {
        if (root[PreToolUseParentKey] is not JsonObject hooks || hooks[PreToolUseKey] is not JsonArray preToolUse)
        {
            return false;
        }

        return RemoveMatchingEntries(preToolUse, entry => EntryHasCommand(entry, cmd => cmd.Contains(RewriteHookFile, StringComparison.Ordinal) || cmd == ClaudeHookCommand));
    }

    /// <summary>
    /// Removes only <c>hooks.PreToolUse</c> entries entirely dominated by the legacy
    /// <c>rtk-rewrite.sh</c> script (every hook in the entry references it) — preserving any entry
    /// that also contains a new-format <see cref="ClaudeHookCommand"/> hook. Port of Rust
    /// <c>remove_legacy_hook_entries_from_json</c> (init.rs:1322).
    /// </summary>
    /// <param name="root">The <c>settings.json</c> root object to mutate.</param>
    /// <returns><see langword="true"/> if any entry was removed.</returns>
    public static bool RemoveLegacyHookEntriesFromJson(JsonObject root)
    {
        if (root[PreToolUseParentKey] is not JsonObject hooks || hooks[PreToolUseKey] is not JsonArray preToolUse)
        {
            return false;
        }

        return RemoveMatchingEntries(preToolUse, IsDominatedByLegacyScript);
    }

    private static bool IsDominatedByLegacyScript(JsonNode? entry)
    {
        if (entry is not JsonObject entryObj || entryObj["hooks"] is not JsonArray hooksArr)
        {
            return false;
        }

        // Rust: hooks.iter().all(|hook| command contains REWRITE_HOOK_FILE) — vacuously true for an
        // empty array, matching Iterator::all's short-circuit semantics.
        foreach (var hookNode in hooksArr)
        {
            var isLegacy = hookNode is JsonObject hookObj &&
                hookObj["command"] is JsonValue cmdVal &&
                cmdVal.GetValueKind() == JsonValueKind.String &&
                cmdVal.GetValue<string>().Contains(RewriteHookFile, StringComparison.Ordinal);
            if (!isLegacy)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RemoveMatchingEntries(JsonArray preToolUse, Func<JsonNode?, bool> shouldRemove)
    {
        var originalCount = preToolUse.Count;
        for (var i = preToolUse.Count - 1; i >= 0; i--)
        {
            if (shouldRemove(preToolUse[i]))
            {
                preToolUse.RemoveAt(i);
            }
        }

        return preToolUse.Count < originalCount;
    }

    private static bool EntryHasCommand(JsonNode? entry, Func<string, bool> predicate)
    {
        if (entry is not JsonObject entryObj || entryObj["hooks"] is not JsonArray hooksArr)
        {
            return false;
        }

        foreach (var hookNode in hooksArr)
        {
            if (hookNode is JsonObject hookObj &&
                hookObj["command"] is JsonValue cmdVal &&
                cmdVal.GetValueKind() == JsonValueKind.String &&
                predicate(cmdVal.GetValue<string>()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Serializes <paramref name="node"/> matching serde_json's <c>to_string_pretty</c> shape
    /// exactly: 2-space indent, <c>\n</c> line endings (never <c>\r\n</c>), no trailing newline.
    /// Verified byte-for-byte against the Rust oracle's actual <c>settings.json</c> output.
    /// </summary>
    /// <param name="node">The JSON node to serialize.</param>
    /// <returns>The pretty-printed JSON text.</returns>
    public static string SerializePretty(JsonNode node) => node.ToJsonString(PrettyOptions);

    /// <summary>
    /// Parses <paramref name="content"/> as a JSON object. Port of the JSON-parsing half of Rust's
    /// <c>patch_settings_json_command</c>/<c>remove_hook_from_settings</c> (which parse into
    /// <c>serde_json::Value</c> — a top-level non-object is a realistic-but-unsupported shape for
    /// <c>settings.json</c>, so this port fails loud rather than replicating Rust's
    /// wholesale-reset-to-<c>{}</c> quirk for that edge case).
    /// </summary>
    /// <param name="content">The raw JSON text.</param>
    /// <param name="path">The source file path, used only in the error message.</param>
    /// <returns>The parsed JSON object.</returns>
    /// <exception cref="InitAbortException">The content is not valid JSON, or its top-level value is not an object.</exception>
    public static JsonObject ParseSettingsObject(string content, string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InitAbortException($"Failed to parse {path} as JSON: {ex.Message}");
        }

        if (node is not JsonObject obj)
        {
            throw new InitAbortException($"Failed to parse {path} as JSON: top-level value is not an object");
        }

        return obj;
    }

    /// <summary>
    /// Prints the manual settings.json patch instructions to stdout, used when
    /// <see cref="PatchMode.Skip"/> is selected or the user declines the <see cref="PatchMode.Ask"/>
    /// prompt. Port of Rust <c>print_manual_instructions</c> (init.rs:509).
    /// </summary>
    /// <param name="hookCommand">The hook command to display in the JSON snippet.</param>
    /// <param name="includeOpencode">Whether to mention restarting OpenCode alongside Claude Code.</param>
    public static void PrintManualInstructions(string hookCommand, bool includeOpencode)
    {
        string settingsPath;
        try
        {
            settingsPath = Path.Combine(ResolveClaudeDir(), SettingsJsonName);
        }
        catch (InitAbortException)
        {
            settingsPath = $"~/{ClaudeDirName}/{SettingsJsonName}";
        }

        Console.Out.Write($"\n  MANUAL STEP: Add this to {settingsPath}:\n");
        Console.Out.Write("  {\n");
        Console.Out.Write("    \"hooks\": { \"PreToolUse\": [{\n");
        Console.Out.Write("      \"matcher\": \"Bash\",\n");
        Console.Out.Write("      \"hooks\": [{ \"type\": \"command\",\n");
        Console.Out.Write($"        \"command\": \"{hookCommand}\"\n");
        Console.Out.Write("      }]\n");
        Console.Out.Write("    }]}\n");
        Console.Out.Write("  }\n");
        Console.Out.Write(includeOpencode
            ? "\n  Then restart Claude Code and OpenCode. Test with: git status\n\n"
            : "\n  Then restart Claude Code. Test with: git status\n\n");
    }

    /// <summary>
    /// Prompts the user (on stderr) whether to patch an existing <c>settings.json</c>, defaulting to
    /// No when stdin is not a terminal (piped/redirected). Port of Rust <c>prompt_user_consent</c>
    /// (init.rs:427).
    /// </summary>
    /// <param name="settingsPath">The settings.json path, shown in the prompt.</param>
    /// <returns><see langword="true"/> only if the user explicitly typed <c>y</c>/<c>yes</c> at an interactive terminal.</returns>
    public static bool PromptUserConsent(string settingsPath)
    {
        Console.Error.Write($"\nPatch existing {settingsPath}? [y/N] \n");

        if (Console.IsInputRedirected)
        {
            Console.Error.Write("(non-interactive mode, defaulting to N)\n");
            return false;
        }

        var line = Console.In.ReadLine() ?? string.Empty;
        var response = line.Trim().ToLowerInvariant();
        return response == "y" || response == "yes";
    }

    /// <summary>
    /// Orchestrates patching <c>settings.json</c> with the RTK hook: reads or creates the file,
    /// checks idempotency, applies the <see cref="PatchMode"/> decision (prompting, skipping, or
    /// proceeding), deep-merges the hook entry, backs up the original (<c>.json.bak</c>), and writes
    /// atomically. Port of Rust <c>patch_settings_json_command</c> (init.rs:933).
    /// </summary>
    /// <param name="hookCommand">The hook command to register.</param>
    /// <param name="mode">The patch-consent mode.</param>
    /// <param name="includeOpencode">Whether success/manual messages should mention OpenCode too.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns>The <see cref="PatchResult"/> describing what happened.</returns>
    public static PatchResult PatchSettingsJsonCommand(string hookCommand, PatchMode mode, bool includeOpencode, InitContext ctx)
    {
        var claudeDir = ResolveClaudeDir();
        var settingsPath = Path.Combine(claudeDir, SettingsJsonName);

        JsonObject root;
        if (File.Exists(settingsPath))
        {
            var content = File.ReadAllText(settingsPath);
            root = string.IsNullOrWhiteSpace(content) ? new JsonObject() : ParseSettingsObject(content, settingsPath);
        }
        else
        {
            root = new JsonObject();
        }

        if (HookAlreadyPresent(root, hookCommand))
        {
            if (ctx.Verbose > 0)
            {
                Console.Error.Write("settings.json: hook already present\n");
            }

            return PatchResult.AlreadyPresent;
        }

        switch (mode)
        {
            case PatchMode.Skip:
                PrintManualInstructions(hookCommand, includeOpencode);
                return PatchResult.Skipped;

            case PatchMode.Ask:
                if (ctx.DryRun)
                {
                    Console.Out.Write($"[dry-run] would prompt before patching {settingsPath}\n");
                }
                else if (!PromptUserConsent(settingsPath))
                {
                    PrintManualInstructions(hookCommand, includeOpencode);
                    return PatchResult.Declined;
                }

                break;

            case PatchMode.Auto:
                break;
        }

        InsertHookEntry(root, hookCommand);
        var serialized = SerializePretty(root);

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would patch settings.json: {settingsPath}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{serialized}\n");
            }

            return PatchResult.WouldPatch;
        }

        var backupPath = settingsPath + ".bak";
        if (File.Exists(settingsPath))
        {
            File.Copy(settingsPath, backupPath, overwrite: true);
            if (ctx.Verbose > 0)
            {
                Console.Error.Write($"Backup: {backupPath}\n");
            }
        }

        InitArtifacts.AtomicWrite(settingsPath, serialized);

        Console.Out.Write("\n  settings.json: hook added\n");
        if (File.Exists(backupPath))
        {
            Console.Out.Write($"  Backup: {backupPath}\n");
        }

        Console.Out.Write(includeOpencode
            ? "  Restart Claude Code and OpenCode. Test with: git status\n"
            : "  Restart Claude Code. Test with: git status\n");

        return PatchResult.Patched;
    }

    /// <summary>
    /// Removes the RTK hook entry from <c>settings.json</c> (if present), backing up the original
    /// first. Port of Rust <c>remove_hook_from_settings</c> (init.rs:563).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if the hook entry was found (and, outside dry-run, removed).</returns>
    public static bool RemoveHookFromSettings(InitContext ctx)
    {
        var claudeDir = ResolveClaudeDir();
        var settingsPath = Path.Combine(claudeDir, SettingsJsonName);

        if (!File.Exists(settingsPath))
        {
            if (ctx.Verbose > 0)
            {
                Console.Error.Write("settings.json not found, nothing to remove\n");
            }

            return false;
        }

        var content = File.ReadAllText(settingsPath);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var root = ParseSettingsObject(content, settingsPath);
        var removed = RemoveHookFromJson(root);

        if (!removed)
        {
            return false;
        }

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would remove RTK hook entry from {settingsPath}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{SerializePretty(root)}\n");
            }

            return true;
        }

        var backupPath = settingsPath + ".bak";
        File.Copy(settingsPath, backupPath, overwrite: true);

        var serialized = SerializePretty(root);
        InitArtifacts.AtomicWrite(settingsPath, serialized);

        if (ctx.Verbose > 0)
        {
            Console.Error.Write("Removed RTK hook from settings.json\n");
        }

        return true;
    }
}
