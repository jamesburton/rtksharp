using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtkSharp.Hooks;

/// <summary>
/// Gemini CLI hook-script artifact writer for <c>rtk init --gemini</c>. Faithful port of the Gemini
/// subset of Rust <c>src/hooks/init.rs</c>: <c>resolve_gemini_dir</c> (init.rs:3606),
/// <c>run_gemini</c> (init.rs:3611), <c>patch_gemini_settings</c> (init.rs:3679), and
/// <c>uninstall_gemini</c> (init.rs:3796).
/// </summary>
/// <remarks>
/// <para>
/// <b>Gemini is the one agent using a legacy-style script-file hook with an integrity sidecar</b> —
/// unlike Claude Code's native binary-command hook (<c>rtk hook claude</c> registered directly in
/// <c>settings.json</c>), Gemini gets a standalone shell script (<c>~/.gemini/hooks/rtk-hook-gemini.sh</c>)
/// that <c>settings.json</c>'s <c>BeforeTool</c> hook array points at by path, plus a SHA-256 hash
/// sidecar (<see cref="Integrity.StoreHash"/>) so <c>rtk verify</c>-style tamper detection has
/// something to check against.
/// </para>
/// <para>
/// <b>Gemini is global-only.</b> Rust's <c>run_gemini</c> bails immediately if <c>!global</c>; there
/// is no project-scope Gemini mode (unlike Codex, which supports both scopes).
/// </para>
/// <para>
/// <b>settings.json shape differs from Claude Code's.</b> Gemini's hook lives at
/// <c>hooks.BeforeTool</c> (an array of <c>{ matcher: "run_shell_command", hooks: [{ type: "command",
/// command: "&lt;hook path&gt;" }] }</c> entries) rather than Claude's <c>hooks.PreToolUse</c> with a
/// <c>Bash</c> matcher. "Already present" detection also differs: Rust checks whether the *first*
/// hook's command *contains* the substring <c>"rtk"</c> (not an exact match against a canonical
/// command string, since the command here is a full absolute script path that varies by machine).
/// </para>
/// <para>
/// <b>No <c>.bak</c> backup on patch, and a non-atomic write on removal.</b> Confirmed by reading the
/// Rust source directly: <c>patch_gemini_settings</c> writes via a temp-file-then-rename (atomic, no
/// backup copy — unlike Claude's <see cref="SettingsPatcher.PatchSettingsJsonCommand"/>, which does
/// back up), and <c>uninstall_gemini</c>'s settings.json rewrite uses a plain <c>fs::write</c> (no
/// temp file at all). Both quirks are reproduced here verbatim rather than "fixed", per this port's
/// byte-exact parity mandate.
/// </para>
/// <para>
/// <b>Lenient settings.json parsing on install, strict on uninstall.</b> <c>patch_gemini_settings</c>
/// reads <c>settings.json</c> with <c>serde_json::from_str(..).unwrap_or(json!({}))</c> — a parse
/// failure silently resets to an empty object rather than aborting — while <c>uninstall_gemini</c>
/// propagates a read error with <c>?</c> but silently no-ops (no removal, no error) if the content
/// merely fails to parse as JSON (<c>if let Ok(mut settings) = serde_json::from_str(..)</c>). Both
/// behaviors are reproduced here.
/// </para>
/// <para>
/// <b>The uninstall path does not remove the integrity hash sidecar.</b> Confirmed by reading
/// <c>uninstall_gemini</c> (init.rs:3796) end to end: unlike Claude's uninstall (init.rs:717-726,
/// which explicitly calls <c>integrity::remove_hash</c>), Gemini's uninstall removes the hook script
/// and <c>GEMINI.md</c> and cleans <c>settings.json</c>, but never touches
/// <c>~/.gemini/hooks/.rtk-hook.sha256</c>. This looks like an oracle gap rather than an intentional
/// design choice, but is reproduced verbatim here — see this class's remarks and the calling
/// convention established by <c>CodexInit</c> for precedent on preserving oracle quirks over "fixing"
/// them. Flagged for centralized reconciliation.
/// </para>
/// </remarks>
public static class GeminiInit
{
    /// <summary>The Gemini config directory name under the user's home directory (Rust <c>GEMINI_DIR</c>, constants.rs:23).</summary>
    private const string GeminiDirName = ".gemini";

    /// <summary>The hooks subdirectory under the Gemini config dir (Rust <c>HOOKS_SUBDIR</c>, constants.rs:4).</summary>
    private const string HooksSubdir = "hooks";

    /// <summary>The Gemini hook script filename (Rust <c>GEMINI_HOOK_FILE</c>, constants.rs:2).</summary>
    private const string GeminiHookFileName = "rtk-hook-gemini.sh";

    /// <summary>The Gemini awareness doc filename (Rust <c>GEMINI_MD</c>, init.rs:69).</summary>
    private const string GeminiMdFileName = "GEMINI.md";

    /// <summary>The Gemini <c>settings.json</c> filename (Rust <c>SETTINGS_JSON</c>, constants.rs:5).</summary>
    private const string SettingsJsonName = "settings.json";

    /// <summary>The Gemini <c>BeforeTool</c> hooks array key (Rust <c>BEFORE_TOOL_KEY</c>, constants.rs:9).</summary>
    private const string BeforeToolKey = "BeforeTool";

    /// <summary>
    /// The Gemini hook wrapper script content — delegates to <c>rtk hook gemini</c>. Byte-for-byte
    /// copy of Rust <c>GEMINI_HOOK_SCRIPT</c> (init.rs:3602).
    /// </summary>
    private const string GeminiHookScript = "#!/bin/bash\nexec rtk hook gemini\n";

    /// <summary>The executable file mode set on the installed hook script: <c>rwxr-xr-x</c> (0o755).</summary>
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Test/parity-only override for the resolved Gemini config directory, consulted by
    /// <see cref="ResolveGeminiDir"/> before falling back to the real home directory. Mirrors the
    /// precedent set by <see cref="InitArtifacts.ConfigDirOverrideEnvVar"/>: Rust's
    /// <c>resolve_gemini_dir</c> has no environment-variable override at all (unlike Claude's
    /// <c>CLAUDE_CONFIG_DIR</c> or Codex's <c>CODEX_HOME</c>), so this escape hatch exists purely so
    /// tests can redirect <c>~/.gemini</c> deterministically without touching the real user profile —
    /// it is <b>never a user-facing flag</b> and must not be documented as one.
    /// </summary>
    internal const string GeminiDirOverrideEnvVar = "RTK_GEMINI_DIR_OVERRIDE";

    /// <summary>
    /// Resolves <c>~/.gemini</c>. Port of Rust <c>resolve_gemini_dir</c> (init.rs:3606), which
    /// delegates to the shared <c>resolve_home_subdir</c> helper (init.rs:2714) — there is no
    /// environment-variable override for Gemini's config directory (unlike Claude's
    /// <c>CLAUDE_CONFIG_DIR</c> or Codex's <c>CODEX_HOME</c>) in the Rust oracle; see
    /// <see cref="GeminiDirOverrideEnvVar"/> for the test-only escape hatch this port adds.
    /// </summary>
    /// <returns>The resolved Gemini config directory path.</returns>
    /// <exception cref="InitAbortException">The home directory could not be determined.</exception>
    internal static string ResolveGeminiDir()
    {
        var overridden = Environment.GetEnvironmentVariable(GeminiDirOverrideEnvVar);
        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            throw new InitAbortException(OperatingSystem.IsWindows()
                ? "Cannot determine home directory. Is %USERPROFILE% set?"
                : "Cannot determine home directory. Is $HOME set?");
        }

        return Path.Combine(home, GeminiDirName);
    }

    /// <summary>
    /// Entry point for <c>rtk init --gemini</c>. Installs the hook script (with integrity baseline),
    /// optionally <c>GEMINI.md</c>, and patches <c>settings.json</c>. Port of Rust <c>run_gemini</c>
    /// (init.rs:3611).
    /// </summary>
    /// <param name="global">Must be <see langword="true"/> — Gemini support is global-only.</param>
    /// <param name="hookOnly">When <see langword="true"/>, skips writing <c>GEMINI.md</c>.</param>
    /// <param name="patchMode">The <c>settings.json</c> patch-consent mode.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <exception cref="InitAbortException"><paramref name="global"/> is <see langword="false"/>, or a filesystem/JSON operation failed.</exception>
    public static void Run(bool global, bool hookOnly, PatchMode patchMode, InitContext ctx)
    {
        if (!global)
        {
            throw new InitAbortException("Gemini support is global-only. Use: rtk init -g --gemini");
        }

        var geminiDir = ResolveGeminiDir();
        if (!ctx.DryRun)
        {
            CreateDirectory(geminiDir, $"Failed to create Gemini config dir: {geminiDir}");
        }

        // 1. Install hook script.
        var hookDir = Path.Combine(geminiDir, HooksSubdir);
        if (!ctx.DryRun)
        {
            CreateDirectory(hookDir, $"Failed to create hook dir: {hookDir}");
        }

        var hookPath = Path.Combine(hookDir, GeminiHookFileName);
        InitArtifacts.WriteIfChanged(hookPath, GeminiHookScript, "Gemini hook", ctx);

        if (!OperatingSystem.IsWindows() && !ctx.DryRun)
        {
            try
            {
                File.SetUnixFileMode(hookPath, ExecutableMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InitAbortException($"Failed to set hook permissions: {hookPath}: {ex.Message}");
            }
        }

        // Store integrity baseline for tamper detection (skip in dry-run).
        if (!ctx.DryRun)
        {
            Integrity.StoreHash(hookPath);
        }

        // 2. Install GEMINI.md (RTK awareness for Gemini) — reuses the same slim RTK awareness
        // content as Claude Code's RTK.md/CLAUDE.md (Rust: RTK_SLIM, init.rs:31).
        var geminiMdPath = Path.Combine(geminiDir, GeminiMdFileName);
        if (!hookOnly)
        {
            InitArtifacts.WriteIfChanged(geminiMdPath, InitArtifacts.RtkSlim, GeminiMdFileName, ctx);
        }

        // 3. Patch ~/.gemini/settings.json.
        PatchGeminiSettings(geminiDir, hookPath, patchMode, ctx);

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
            return;
        }

        Console.Out.Write("\nGemini CLI hook installed (global).\n\n");
        Console.Out.Write($"  Hook: {hookPath}\n");
        if (!hookOnly)
        {
            Console.Out.Write($"  GEMINI.md: {geminiMdPath}\n");
        }

        Console.Out.Write("  Restart Gemini CLI. Test with: git status\n\n");
    }

    /// <summary>
    /// Patches <c>~/.gemini/settings.json</c> with the RTK <c>BeforeTool</c> hook entry. Port of Rust
    /// <c>patch_gemini_settings</c> (init.rs:3679).
    /// </summary>
    /// <param name="geminiDir">The <c>~/.gemini</c> directory.</param>
    /// <param name="hookPath">The installed hook script's absolute path (used verbatim as the registered command).</param>
    /// <param name="patchMode">The patch-consent mode.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <exception cref="InitAbortException">
    /// The existing <c>hooks</c>/<c>hooks.BeforeTool</c> value is present but not the expected shape,
    /// or the root value is not a JSON object at the point of mutation.
    /// </exception>
    private static void PatchGeminiSettings(string geminiDir, string hookPath, PatchMode patchMode, InitContext ctx)
    {
        var settingsPath = Path.Combine(geminiDir, SettingsJsonName);
        var hookCmd = hookPath;

        // Read or create settings.json. Unlike SettingsPatcher.ParseSettingsObject (fail-loud), Rust's
        // patch_gemini_settings falls back to an empty object on a parse failure rather than aborting
        // (serde_json::from_str(..).unwrap_or(json!({}))) — reproduced verbatim via TryParseLenient.
        JsonNode? settings;
        if (File.Exists(settingsPath))
        {
            string content;
            try
            {
                content = File.ReadAllText(settingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InitAbortException($"Failed to read {settingsPath}: {ex.Message}");
            }

            settings = TryParseLenient(content);
        }
        else
        {
            settings = new JsonObject();
        }

        // Already-present check: does any hooks.BeforeTool[*].hooks[0].command contain "rtk"?
        if (settings is JsonObject existingRoot &&
            existingRoot["hooks"] is JsonObject existingHooks &&
            existingHooks[BeforeToolKey] is JsonArray existingBeforeTool)
        {
            foreach (var entry in existingBeforeTool)
            {
                if (FirstHookCommandContains(entry, "rtk"))
                {
                    if (ctx.Verbose > 0)
                    {
                        Console.Error.Write("Gemini settings.json already has RTK hook\n");
                    }

                    return;
                }
            }
        }

        // Ask user before patching.
        if (patchMode == PatchMode.Skip)
        {
            Console.Out.Write($"\nManual setup needed: add RTK hook to {settingsPath}\nSee: https://github.com/rtk-ai/rtk#gemini-cli\n");
            return;
        }

        if (patchMode == PatchMode.Ask)
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would prompt before patching {settingsPath}\n");
            }
            else
            {
                Console.Out.Write($"Patch {settingsPath} with RTK hook? [y/N] ");
                Console.Out.Flush();
                var answer = Console.In.ReadLine() ?? string.Empty;

                // Rust: answer.trim().eq_ignore_ascii_case("y") — exactly "y" (not "yes"), unlike
                // SettingsPatcher.PromptUserConsent's Claude-flavored "y"/"yes" acceptance.
                if (!answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Out.Write("Skipped. Add hook manually later.\n");
                    return;
                }
            }
        }

        // Build hook entry matching Gemini CLI format.
        var hookEntry = new JsonObject
        {
            ["matcher"] = "run_shell_command",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = hookCmd,
            }),
        };

        // Insert into settings.
        if (settings is not JsonObject root)
        {
            throw new InitAbortException("settings.json is not an object");
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            if (root["hooks"] is not null)
            {
                throw new InitAbortException("hooks is not an object");
            }

            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        if (hooks[BeforeToolKey] is not JsonArray beforeTool)
        {
            if (hooks[BeforeToolKey] is not null)
            {
                throw new InitAbortException("BeforeTool is not an array");
            }

            beforeTool = new JsonArray();
            hooks[BeforeToolKey] = beforeTool;
        }

        // See SettingsPatcher.InsertHookEntry for why the IList<JsonNode?> cast is used over
        // JsonArray's Add<T>(T) extension (AOT/trimming-unfriendly for non-primitive T).
        ((IList<JsonNode?>)beforeTool).Add(hookEntry);

        var serialized = SettingsPatcher.SerializePretty(root);

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would patch Gemini settings.json: {settingsPath}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{serialized}\n");
            }

            return;
        }

        // Write atomically (temp file + rename) — no .bak backup, unlike Claude's settings.json patch.
        InitArtifacts.AtomicWrite(settingsPath, serialized);

        if (ctx.Verbose > 0)
        {
            Console.Error.Write($"Patched {settingsPath}\n");
        }
    }

    /// <summary>
    /// Removes Gemini artifacts during uninstall: the hook script, <c>GEMINI.md</c>, and the RTK
    /// <c>BeforeTool</c> entry (or entries) from <c>settings.json</c>. Testable core, returning the
    /// list of human-readable descriptions of what was removed (or would be, in dry-run), without any
    /// header/footer printing — mirrors Rust <c>uninstall_gemini</c> (init.rs:3796), whose caller
    /// (the shared <c>uninstall()</c> dispatcher, init.rs:620) does the header/footer printing. See
    /// <see cref="Uninstall"/> for that wrapper.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns>The list of removed (or would-be-removed) artifact descriptions.</returns>
    internal static List<string> UninstallArtifacts(InitContext ctx)
    {
        var removed = new List<string>();

        string geminiDir;
        try
        {
            geminiDir = ResolveGeminiDir();
        }
        catch (InitAbortException)
        {
            // Rust: `Err(_) => return Ok(removed)` — home directory undeterminable, nothing to do.
            return removed;
        }

        // Remove hook script.
        var hookPath = Path.Combine(geminiDir, HooksSubdir, GeminiHookFileName);
        if (File.Exists(hookPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove Gemini hook: {hookPath}\n");
            }
            else
            {
                try
                {
                    File.Delete(hookPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InitAbortException($"Failed to remove {hookPath}: {ex.Message}");
                }
            }

            removed.Add($"Gemini hook: {hookPath}");
        }

        // Remove GEMINI.md.
        var geminiMdPath = Path.Combine(geminiDir, GeminiMdFileName);
        if (File.Exists(geminiMdPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove GEMINI.md: {geminiMdPath}\n");
            }
            else
            {
                try
                {
                    File.Delete(geminiMdPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InitAbortException($"Failed to remove {geminiMdPath}: {ex.Message}");
                }
            }

            removed.Add($"GEMINI.md: {geminiMdPath}");
        }

        // Remove hook from settings.json. Rust propagates a read error with `?` here (unlike the
        // lenient read in patch_gemini_settings) but silently no-ops on a JSON parse failure
        // (`if let Ok(mut settings) = serde_json::from_str(..)`) — both reproduced verbatim.
        var settingsPath = Path.Combine(geminiDir, SettingsJsonName);
        if (File.Exists(settingsPath))
        {
            string content;
            try
            {
                content = File.ReadAllText(settingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InitAbortException($"Failed to read {settingsPath}: {ex.Message}");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(content);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is JsonObject settings &&
                settings["hooks"] is JsonObject hooks &&
                hooks[BeforeToolKey] is JsonArray beforeTool)
            {
                var before = beforeTool.Count;
                for (var i = beforeTool.Count - 1; i >= 0; i--)
                {
                    if (FirstHookCommandContains(beforeTool[i], "rtk"))
                    {
                        beforeTool.RemoveAt(i);
                    }
                }

                if (beforeTool.Count < before)
                {
                    if (ctx.DryRun)
                    {
                        Console.Out.Write($"[dry-run] would remove RTK hook from Gemini settings.json: {settingsPath}\n");
                    }
                    else
                    {
                        // Rust: `fs::write` here, not the temp-file-then-rename used by
                        // patch_gemini_settings/InitArtifacts.AtomicWrite — reproduced verbatim
                        // (non-atomic write on the uninstall path).
                        var newContent = SettingsPatcher.SerializePretty(settings);
                        try
                        {
                            File.WriteAllText(settingsPath, newContent);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            throw new InitAbortException($"Failed to write {settingsPath}: {ex.Message}");
                        }
                    }

                    removed.Add("Gemini settings.json: removed RTK hook entry");
                }
            }
        }

        if (ctx.Verbose > 0 && removed.Count > 0)
        {
            Console.Error.Write("Gemini artifacts removed\n");
        }

        return removed;
    }

    /// <summary>
    /// Full uninstall wrapper for Gemini artifacts, matching the Gemini branch of Rust's shared
    /// <c>uninstall()</c> dispatcher (init.rs:676-699): calls <see cref="UninstallArtifacts"/>, prints
    /// the header/item list (or the "nothing to remove" message), the restart hint, and the dry-run
    /// footer. Callers must have already enforced the <c>--global</c> requirement (Rust checks
    /// <c>--gemini</c> only after the shared <c>!global</c> bail — see the comment at the
    /// <c>InitCommand</c> call site).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void Uninstall(InitContext ctx)
    {
        var removed = UninstallArtifacts(ctx);

        if (removed.Count > 0)
        {
            Console.Out.Write(ctx.DryRun ? "[dry-run] would uninstall RTK (Gemini):\n" : "RTK uninstalled (Gemini):\n");
            foreach (var item in removed)
            {
                Console.Out.Write($"  - {item}\n");
            }

            if (!ctx.DryRun)
            {
                Console.Out.Write("\nRestart Gemini CLI to apply changes.\n");
            }
        }
        else
        {
            Console.Out.Write("RTK Gemini support was not installed (nothing to remove)\n");
        }

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
    }

    /// <summary>
    /// Returns whether <paramref name="entry"/>'s first hook's <c>command</c> string contains
    /// <paramref name="needle"/>. Mirrors Rust's pointer-based check
    /// <c>h.pointer("/hooks/0/command").and_then(|v| v.as_str()).is_some_and(|c| c.contains(needle))</c>
    /// used by both <c>patch_gemini_settings</c>'s already-present check and
    /// <c>uninstall_gemini</c>'s retain predicate.
    /// </summary>
    /// <param name="entry">A <c>hooks.BeforeTool</c> array entry.</param>
    /// <param name="needle">The substring to search for.</param>
    /// <returns><see langword="true"/> if the entry's first hook command contains <paramref name="needle"/>.</returns>
    private static bool FirstHookCommandContains(JsonNode? entry, string needle)
    {
        if (entry is not JsonObject entryObj || entryObj["hooks"] is not JsonArray hooksArr || hooksArr.Count == 0)
        {
            return false;
        }

        return hooksArr[0] is JsonObject firstHook &&
            firstHook["command"] is JsonValue cmdVal &&
            cmdVal.GetValueKind() == JsonValueKind.String &&
            cmdVal.GetValue<string>().Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses <paramref name="content"/> as JSON, falling back to an empty object on a parse failure.
    /// Port of Rust's <c>serde_json::from_str(&amp;content).unwrap_or(serde_json::json!({}))</c>
    /// (init.rs:3693) used by <c>patch_gemini_settings</c>'s lenient read.
    /// </summary>
    /// <remarks>
    /// One documented divergence: parsing the literal JSON text <c>null</c> yields
    /// <see langword="null"/> from <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
    /// (not an exception), which this method folds into an empty object — whereas Rust's
    /// <c>serde_json::Value::Null</c> would survive as a non-object value and later cause the
    /// mutation step to bail with "settings.json is not an object". This edge case (a literal
    /// <c>null</c> settings.json) is judged unlikely enough in practice not to warrant replicating
    /// exactly; flagged here for centralized reconciliation if parity testing surfaces it.
    /// </remarks>
    /// <param name="content">The raw JSON text.</param>
    /// <returns>The parsed JSON value, or an empty <see cref="JsonObject"/> if parsing failed.</returns>
    private static JsonNode TryParseLenient(string content)
    {
        try
        {
            return JsonNode.Parse(content) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>Creates a directory, wrapping any failure in an <see cref="InitAbortException"/> with <paramref name="errorContext"/>.</summary>
    /// <param name="path">The directory to create (and any missing parents).</param>
    /// <param name="errorContext">The context message to prefix on failure.</param>
    private static void CreateDirectory(string path, string errorContext)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InitAbortException($"{errorContext}: {ex.Message}");
        }
    }
}
