using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtkSharp.Hooks;

/// <summary>
/// <c>rtk init -g --agent cursor</c> / <c>rtk init -g --agent cursor --uninstall</c>: registers (or
/// removes) the RTK binary <c>preToolUse</c> hook in Cursor's <c>~/.cursor/hooks.json</c>. Faithful
/// port of the Cursor-specific helpers in Rust <c>src/hooks/init.rs</c>: <c>resolve_cursor_dir</c>
/// (init.rs:2981), <c>install_cursor_hooks</c> (init.rs:2986), <c>patch_cursor_hooks_json</c>
/// (init.rs:3040), <c>cursor_hook_already_present</c> (init.rs:3097), <c>insert_cursor_hook_entry</c>
/// (init.rs:3116), <c>remove_legacy_cursor_hooks_json_entries</c> (init.rs:3148),
/// <c>remove_legacy_cursor_hook_entries_from_json</c> (init.rs:3188), <c>remove_cursor_hooks</c>
/// (init.rs:3210), and <c>remove_cursor_hook_from_json</c> (init.rs:3270).
/// </summary>
/// <remarks>
/// <para>
/// <b>Mechanism parity with <see cref="SettingsPatcher"/>.</b> Cursor's <c>hooks.json</c> patching is
/// the same deep-merge/backup/idempotent shape as Claude's <c>settings.json</c> patching, applied to a
/// differently-shaped document: the top level carries a <c>version</c> field, and the hook array lives
/// at <c>hooks.preToolUse</c> (camelCase, unlike Claude's <c>hooks.PreToolUse</c>) with each entry
/// itself being <c>{ "command": ..., "matcher": "Shell" }</c> — flatter than Claude's nested
/// <c>{ matcher, hooks: [{ type, command }] }</c> shape. <see cref="JsonNode"/>/<see cref="JsonObject"/>
/// is used for the same reason <see cref="SettingsPatcher"/> uses it: preserve unrecognized keys and
/// insertion order exactly, mirroring serde_json's <c>Value</c>.
/// </para>
/// <para>
/// <b>No env-var override.</b> Unlike Claude's <c>CLAUDE_CONFIG_DIR</c>, Rust's
/// <c>resolve_cursor_dir</c> (init.rs:2981) resolves strictly via <c>dirs::home_dir()</c> — there is no
/// <c>CURSOR_CONFIG_DIR</c> escape hatch. <see cref="ResolveCursorDir"/> mirrors that: it reads
/// <see cref="Environment.SpecialFolder.UserProfile"/> unconditionally. The JSON-mutation primitives
/// below (<see cref="HookAlreadyPresent"/>, <see cref="InsertHookEntry"/>,
/// <see cref="RemoveLegacyEntriesFromJson"/>, <see cref="RemoveHookFromJson"/>) and the
/// path-parameterized <see cref="PatchHooksJsonAt"/>/<see cref="RemoveLegacyHooksJsonEntriesAt"/> are
/// exposed separately (mirroring how the Rust functions themselves take an explicit <c>path: &amp;Path</c>)
/// so tests can exercise the full read/patch/backup/write cycle against a temp directory without
/// touching the real home directory — the same testability seam Rust's own unit tests rely on
/// (init.rs:5584-5864 test the JSON-level helpers directly; nothing in the Rust suite drives
/// <c>resolve_cursor_dir</c> itself, since it has no override).
/// </para>
/// </remarks>
public static class CursorInit
{
    /// <summary>The binary hook command RTK registers for Cursor (Rust <c>CURSOR_HOOK_COMMAND</c>, constants.rs:14).</summary>
    public const string CursorHookCommand = "rtk hook cursor";

    private const string CursorDirName = ".cursor";
    private const string HooksJsonName = "hooks.json";
    private const string HooksSubdirName = "hooks";

    /// <summary>The legacy shell hook filename matched by substring for backward compatibility (Rust <c>REWRITE_HOOK_FILE</c>).</summary>
    private const string RewriteHookFile = "rtk-rewrite.sh";

    private const string HooksKey = "hooks";
    private const string PreToolUseKey = "preToolUse";
    private const string CommandKey = "command";

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        IndentSize = 2,
    };

    /// <summary>
    /// Resolves <c>{home}/.cursor</c>. Port of Rust <c>resolve_cursor_dir</c> (init.rs:2981), itself
    /// <c>resolve_home_subdir(CURSOR_DIR)</c> (init.rs:2714).
    /// </summary>
    /// <returns>The resolved Cursor config directory path.</returns>
    /// <exception cref="InitAbortException">The home directory could not be resolved.</exception>
    public static string ResolveCursorDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            // Rust's resolve_home_subdir picks the OS-appropriate diagnostic via cfg!(windows);
            // this port only ships for Windows today, so the %USERPROFILE% wording is unconditional.
            throw new InitAbortException("Cannot determine home directory. Is %USERPROFILE% set?");
        }

        return Path.Combine(home, CursorDirName);
    }

    /// <summary>
    /// Runs <c>rtk init -g --agent cursor</c>: migrates any legacy <c>rtk-rewrite.sh</c> hook script and
    /// its stale <c>hooks.json</c> entry, then patches (or creates) <c>hooks.json</c> with the RTK
    /// binary hook. Port of Rust <c>install_cursor_hooks</c> (init.rs:2986).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <exception cref="InitAbortException">
    /// The home directory could not be resolved, an existing <c>hooks.json</c> could not be read/parsed,
    /// or a write/backup step failed.
    /// </exception>
    public static void RunCursor(InitContext ctx)
    {
        var cursorDir = ResolveCursorDir();

        // Migrate old hook script if present.
        var oldHook = Path.Combine(cursorDir, HooksSubdirName, RewriteHookFile);
        if (File.Exists(oldHook))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove old Cursor hook script: {oldHook}\n");
            }
            else
            {
                // Rust: `let _ = fs::remove_file(&old_hook);` — best-effort, error swallowed.
                TryDeleteBestEffort(oldHook);
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"  [ok] Removed old Cursor hook script: {oldHook}\n");
                }
            }

            // Clean stale hooks.json entry pointing to the deleted script. Rust logs a warning and
            // continues on failure here (init.rs:3009-3013) rather than aborting the whole install.
            var hooksJsonPathForMigration = Path.Combine(cursorDir, HooksJsonName);
            try
            {
                RemoveLegacyHooksJsonEntriesAt(hooksJsonPathForMigration, ctx);
            }
            catch (InitAbortException ex)
            {
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"  [warn] Failed to clean legacy Cursor hooks.json entry: {ex.Message}\n");
                }
            }
        }

        // Create or patch hooks.json with the binary command.
        var hooksJsonPath = Path.Combine(cursorDir, HooksJsonName);
        var patched = PatchHooksJsonAt(hooksJsonPath, ctx);

        if (!ctx.DryRun)
        {
            Console.Out.Write("\nCursor hook registered (global).\n\n");
            Console.Out.Write($"  Command:    {CursorHookCommand}\n");
            Console.Out.Write($"  hooks.json: {hooksJsonPath}\n");

            Console.Out.Write(patched
                ? "  hooks.json: RTK preToolUse entry added\n"
                : "  hooks.json: RTK preToolUse entry already present\n");

            Console.Out.Write("  Cursor reloads hooks.json automatically. Test with: git status\n\n");
        }
    }

    /// <summary>
    /// Removes Cursor RTK artifacts: the legacy hook script (if present) and the RTK
    /// <c>hooks.json</c> entry (if present). Port of Rust <c>remove_cursor_hooks</c> (init.rs:3210),
    /// invoked from Rust <c>uninstall</c> (init.rs:637-661) when <c>--agent cursor --uninstall</c> is
    /// used (global-only — the caller is responsible for the <c>!global</c> bail, mirroring how
    /// <c>InitCommand.RunCore</c> already reproduces that exact guard text before deferring here).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <exception cref="InitAbortException">A file could not be read, backed up, or written.</exception>
    public static void UninstallCursor(InitContext ctx)
    {
        var removed = RemoveCursorHooks(ctx);

        if (removed.Count > 0)
        {
            var header = ctx.DryRun
                ? "[dry-run] would uninstall RTK (Cursor):"
                : "RTK uninstalled (Cursor):";
            Console.Out.Write($"{header}\n");
            foreach (var item in removed)
            {
                Console.Out.Write($"  - {item}\n");
            }

            if (!ctx.DryRun)
            {
                Console.Out.Write("\nRestart Cursor to apply changes.\n");
            }
        }
        else
        {
            Console.Out.Write("RTK Cursor support was not installed (nothing to remove)\n");
        }

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
    }

    private static System.Collections.Generic.List<string> RemoveCursorHooks(InitContext ctx)
    {
        var cursorDir = ResolveCursorDir();
        var removed = new System.Collections.Generic.List<string>();

        // 1. Remove hook script.
        var hookPath = Path.Combine(cursorDir, HooksSubdirName, RewriteHookFile);
        if (File.Exists(hookPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove Cursor hook: {hookPath}\n");
            }
            else
            {
                try
                {
                    File.Delete(hookPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InitAbortException($"Failed to remove Cursor hook: {hookPath}: {ex.Message}");
                }
            }

            removed.Add($"Cursor hook: {hookPath}");
        }

        // 2. Remove the RTK entry from hooks.json.
        var hooksJsonPath = Path.Combine(cursorDir, HooksJsonName);
        if (RemoveHookFromHooksJsonAt(hooksJsonPath, ctx))
        {
            removed.Add("Cursor hooks.json: removed RTK entry");
        }

        return removed;
    }

    /// <summary>
    /// Removes the RTK <c>preToolUse</c> entry from the Cursor <c>hooks.json</c> at
    /// <paramref name="path"/> (if present), backing up before an actual write. A no-op if the file is
    /// missing, empty, unparsable, or has no RTK entry. Extracted for testability from the inline
    /// step-2 logic of Rust <c>remove_cursor_hooks</c> (init.rs:3232-3262), which parses/mutates/backs
    /// up/writes in place rather than delegating to a named helper.
    /// </summary>
    /// <param name="path">The <c>hooks.json</c> path.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if an entry was found (and, outside dry-run, removed).</returns>
    /// <exception cref="InitAbortException">The file could not be read, backed up, or written.</exception>
    public static bool RemoveHookFromHooksJsonAt(string path, InitContext ctx)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var content = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            // Rust: `if let Ok(mut root) = serde_json::from_str(...)` — malformed JSON is silently
            // skipped rather than aborting the whole uninstall.
            return false;
        }

        if (root is null || !RemoveHookFromJson(root))
        {
            return false;
        }

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would remove RTK entry from Cursor hooks.json: {path}\n");
            return true;
        }

        var backupPath = path + ".bak";
        // Rust: `fs::copy(...).ok();` — best-effort backup, error swallowed.
        TryCopyBestEffort(path, backupPath);

        var serialized = SerializePretty(root);
        InitArtifacts.AtomicWrite(path, serialized);

        if (ctx.Verbose > 0)
        {
            Console.Error.Write("Removed RTK hook from Cursor hooks.json\n");
        }

        return true;
    }

    /// <summary>
    /// Checks whether the RTK <c>preToolUse</c> hook is already present in a parsed Cursor
    /// <c>hooks.json</c> — matching either the legacy <c>rtk-rewrite.sh</c> path (by substring) or the
    /// canonical <see cref="CursorHookCommand"/> (exact match). Port of Rust
    /// <c>cursor_hook_already_present</c> (init.rs:3097).
    /// </summary>
    /// <param name="root">The parsed <c>hooks.json</c> root object.</param>
    /// <returns><see langword="true"/> if the hook is already present.</returns>
    public static bool HookAlreadyPresent(JsonObject root)
    {
        if (root[HooksKey] is not JsonObject hooks || hooks[PreToolUseKey] is not JsonArray preToolUse)
        {
            return false;
        }

        foreach (var entry in preToolUse)
        {
            if (EntryCommandMatches(entry, cmd => cmd.Contains(RewriteHookFile, StringComparison.Ordinal) || cmd == CursorHookCommand))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inserts the RTK <c>preToolUse</c> entry into a parsed Cursor <c>hooks.json</c>, creating the
    /// <c>version</c>, <c>hooks</c>, and <c>hooks.preToolUse</c> keys as needed and preserving any
    /// existing entries (including sibling hook kinds like <c>afterFileEdit</c>). Port of Rust
    /// <c>insert_cursor_hook_entry</c> (init.rs:3116).
    /// </summary>
    /// <param name="root">The <c>hooks.json</c> root object to mutate.</param>
    /// <exception cref="InitAbortException">
    /// The existing <c>hooks</c> or <c>hooks.preToolUse</c> value is present but not the expected shape
    /// (object / array respectively).
    /// </exception>
    public static void InsertHookEntry(JsonObject root)
    {
        root.TryAdd("version", JsonValue.Create(1));

        if (root[HooksKey] is not JsonObject hooks)
        {
            if (root[HooksKey] is not null)
            {
                throw new InitAbortException("hooks value is not an object");
            }

            hooks = new JsonObject();
            root[HooksKey] = hooks;
        }

        if (hooks[PreToolUseKey] is not JsonArray preToolUse)
        {
            if (hooks[PreToolUseKey] is not null)
            {
                throw new InitAbortException("preToolUse value is not an array");
            }

            preToolUse = new JsonArray();
            hooks[PreToolUseKey] = preToolUse;
        }

        ((System.Collections.Generic.IList<JsonNode?>)preToolUse).Add(new JsonObject
        {
            [CommandKey] = CursorHookCommand,
            ["matcher"] = "Shell",
        });
    }

    /// <summary>
    /// Removes only legacy <c>rtk-rewrite.sh</c> entries from a parsed Cursor <c>hooks.json</c>,
    /// preserving any existing <see cref="CursorHookCommand"/> (new-format) entries. Port of Rust
    /// <c>remove_legacy_cursor_hook_entries_from_json</c> (init.rs:3188).
    /// </summary>
    /// <param name="root">The <c>hooks.json</c> root object to mutate.</param>
    /// <returns><see langword="true"/> if any entries were removed.</returns>
    public static bool RemoveLegacyEntriesFromJson(JsonObject root)
    {
        if (root[HooksKey] is not JsonObject hooks || hooks[PreToolUseKey] is not JsonArray preToolUse)
        {
            return false;
        }

        return RemoveMatchingEntries(preToolUse, entry => EntryCommandMatches(entry, cmd => cmd.Contains(RewriteHookFile, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Removes the RTK <c>preToolUse</c> entry from a parsed Cursor <c>hooks.json</c> — matching both
    /// the legacy script path (by substring) and the new binary command (exact match). Port of Rust
    /// <c>remove_cursor_hook_from_json</c> (init.rs:3270).
    /// </summary>
    /// <param name="root">The <c>hooks.json</c> root object to mutate.</param>
    /// <returns><see langword="true"/> if an entry was found and removed.</returns>
    public static bool RemoveHookFromJson(JsonObject root)
    {
        if (root[HooksKey] is not JsonObject hooks || hooks[PreToolUseKey] is not JsonArray preToolUse)
        {
            return false;
        }

        return RemoveMatchingEntries(preToolUse, entry => EntryCommandMatches(entry, cmd => cmd.Contains(RewriteHookFile, StringComparison.Ordinal) || cmd == CursorHookCommand));
    }

    /// <summary>
    /// Patches (or creates) the Cursor <c>hooks.json</c> at <paramref name="path"/> with the RTK
    /// <c>preToolUse</c> entry: reads-or-defaults to <c>{ "version": 1 }</c>, checks idempotency,
    /// backs up any existing file to <c>.json.bak</c>, and writes atomically. Port of Rust
    /// <c>patch_cursor_hooks_json</c> (init.rs:3040).
    /// </summary>
    /// <param name="path">The <c>hooks.json</c> path.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if the file was (or, under dry-run, would be) modified.</returns>
    /// <exception cref="InitAbortException">
    /// The existing file could not be read or is not valid JSON, or a write/backup step failed.
    /// </exception>
    public static bool PatchHooksJsonAt(string path, InitContext ctx)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            var content = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(content)
                ? new JsonObject { ["version"] = 1 }
                : ParseHooksJsonObject(content, path);
        }
        else
        {
            root = new JsonObject { ["version"] = 1 };
        }

        if (HookAlreadyPresent(root))
        {
            if (ctx.Verbose > 0)
            {
                Console.Error.Write("Cursor hooks.json: RTK hook already present\n");
            }

            return false;
        }

        InsertHookEntry(root);

        var serialized = SerializePretty(root);

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would patch Cursor hooks.json: {path}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{serialized}\n");
            }

            return true;
        }

        if (File.Exists(path))
        {
            var backupPath = path + ".bak";
            try
            {
                File.Copy(path, backupPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InitAbortException($"Failed to backup to {backupPath}: {ex.Message}");
            }

            if (ctx.Verbose > 0)
            {
                Console.Error.Write($"Backup: {backupPath}\n");
            }
        }

        InitArtifacts.AtomicWrite(path, serialized);

        return true;
    }

    /// <summary>
    /// Removes only legacy <c>rtk-rewrite.sh</c> entries from the Cursor <c>hooks.json</c> at
    /// <paramref name="path"/> (a no-op if the file is missing, empty, or has no such entries), backing
    /// up before an actual write. Port of Rust <c>remove_legacy_cursor_hooks_json_entries</c>
    /// (init.rs:3148).
    /// </summary>
    /// <param name="path">The <c>hooks.json</c> path.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <exception cref="InitAbortException">The file exists but could not be read/parsed, or a write failed.</exception>
    public static void RemoveLegacyHooksJsonEntriesAt(string path, InitContext ctx)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var content = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var root = ParseHooksJsonObject(content, path);

        if (!RemoveLegacyEntriesFromJson(root))
        {
            return;
        }

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would remove legacy rtk-rewrite.sh entry from Cursor hooks.json: {path}\n");
            return;
        }

        var serialized = SerializePretty(root);
        InitArtifacts.AtomicWrite(path, serialized);

        if (ctx.Verbose > 0)
        {
            Console.Error.Write("  [ok] Removed legacy rtk-rewrite.sh entry from Cursor hooks.json\n");
        }
    }

    private static JsonObject ParseHooksJsonObject(string content, string path)
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
            throw new InitAbortException($"Failed to parse {path}: top-level value is not an object");
        }

        return obj;
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

    private static bool EntryCommandMatches(JsonNode? entry, Func<string, bool> predicate) =>
        entry is JsonObject entryObj &&
        entryObj[CommandKey] is JsonValue cmdVal &&
        cmdVal.GetValueKind() == JsonValueKind.String &&
        predicate(cmdVal.GetValue<string>());

    private static string SerializePretty(JsonNode node) => node.ToJsonString(PrettyOptions);

    private static void TryDeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rust: `let _ = fs::remove_file(&old_hook);` — error intentionally discarded.
        }
    }

    private static void TryCopyBestEffort(string source, string destination)
    {
        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rust: `fs::copy(&hooks_json_path, &backup_path).ok();` — error intentionally discarded.
        }
    }
}
