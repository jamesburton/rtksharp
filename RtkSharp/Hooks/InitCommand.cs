using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using RtkSharp.Core;

namespace RtkSharp.Hooks;

/// <summary>
/// Implements the <c>rtk init</c> CLI verb: registers the RTK hook and writes the agent-instruction
/// artifacts for AI coding agents. Faithful (partial) port of Rust <c>src/hooks/init.rs</c>'s
/// <c>run</c> entry point (init.rs:251) and CLI dispatch (<c>main.rs</c>:1876).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> This port implements the full <b>Claude Code</b> path, both project- and
/// global-scope: project-scope default mode (legacy full-block injection into <c>./CLAUDE.md</c> +
/// <c>.rtk/filters.toml</c> template), project-scope <c>--claude-md</c>, project-scope
/// <c>--hook-only</c> (a no-op warning, since it only makes sense with <c>--global</c>), and the
/// global-scope default/<c>--claude-md</c>/<c>--hook-only</c> modes (RTK.md, <c>@RTK.md</c>,
/// <c>settings.json</c> deep-merge patching via <see cref="SettingsPatcher"/>, and the user-global
/// filters template), plus global-scope <c>--uninstall</c> and <c>--show</c> for Claude Code. Every
/// other mode — Codex, Gemini, Copilot, OpenCode, Cursor, Windsurf, Cline, Kilocode, Antigravity,
/// Pi, and Hermes — is parsed (so the CLI surface matches Rust's <c>clap</c> definition) but
/// rejected with a clear "not yet implemented" diagnostic rather than silently doing nothing or
/// guessing at behavior. Those modes land in follow-up tasks; see
/// <c>docs/superpowers/plans/2026-07-03-phase9b-init-hooks.md</c>.
/// </para>
/// <para>
/// <b>Fail-loud, not never-block.</b> <c>init</c> is a user command, not a runtime hook — the
/// RTK-wide "never block the user" fallback pattern does not apply here. Any exception (including
/// <see cref="InitAbortException"/> thrown by the <see cref="InitArtifacts"/> primitives) is caught
/// once, at the top of <see cref="Run"/>, and reported as <c>rtk: {message}</c> on stderr with exit
/// code 1 — mirroring Rust <c>main.rs</c>'s <c>eprintln!("rtk: {:#}", e); 1</c> handling of a
/// propagated <c>anyhow::Error</c>.
/// </para>
/// </remarks>
public static class InitCommand
{
    /// <summary>
    /// Runs <c>rtk init</c> with the given arguments (the remainder after the <c>init</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>init</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        try
        {
            return RunCore(args);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    private static int RunCore(string[] args)
    {
        var flags = ParseArgs(args);
        // Rust's `-v`/`-vv`/`-vvv`/`--verbose` is a top-level `Cli` flag "only recognized before
        // the subcommand" (main.rs:65-67: `Cli { verbose: u8, .. }`, threaded as `cli.verbose` into
        // `InitContext`, main.rs:1892) — NOT an `init`-subcommand argument. `rtk init --dry-run -v`
        // is a clap parse error on the oracle (exit 2, "unexpected argument '-v' found"); only
        // `rtk -v init --dry-run` enables verbose diagnostics. `ParseArgs` below therefore does not
        // recognize `-v`/`--verbose` as an init-level flag (an unrecognized-argument abort with this
        // port's own diagnostic is the closest achievable match — see Task 4 parity report for the
        // exit-code caveat), and verbosity is read from the ambient top-level flag instead, exactly
        // like `RuntimeOptions.UltraCompact` is threaded into other verbs whose registry delegate
        // cannot carry it as a parameter.
        var ctx = new InitContext(RuntimeOptions.Verbosity, flags.DryRun);

        if (flags.Show)
        {
            // Rust's show_config(codex) (init.rs:3292) only branches on --codex; every other flag
            // (including --global, which --show implies for Claude Code) is ignored. Codex's own
            // show_codex_config isn't ported (Task 5 territory).
            if (flags.Codex)
            {
                throw Deferred("--show --codex");
            }

            ShowClaudeConfig();
            return 0;
        }

        if (flags.Uninstall)
        {
            if (flags.Copilot)
            {
                throw Deferred("--uninstall --copilot");
            }

            // Rust's uninstall() (init.rs:620) checks codex/cursor/pi BEFORE the generic
            // !global bail, in that order. Codex and Pi dispatch unconditionally into their
            // own uninstall bodies (uninstall_codex/uninstall_pi) regardless of --global —
            // those bodies aren't ported yet (Task 2/3/5 territory), so we fail loud with an
            // honest "not yet implemented" message rather than fabricating their behavior.
            // Cursor, however, bails right here in Rust (init.rs:637-640) when !global with
            // its own distinct message, so that exact text is reproduced below.
            if (flags.Codex)
            {
                throw Deferred("--uninstall --codex");
            }

            if (flags.Agent == "cursor")
            {
                if (!flags.Global)
                {
                    throw new InitAbortException("Cursor uninstall only works with --global flag");
                }

                throw Deferred("--uninstall --agent cursor --global");
            }

            if (flags.Agent == "pi")
            {
                throw Deferred("--uninstall --agent pi");
            }

            if (!flags.Global)
            {
                throw new InitAbortException(
                    "Uninstall only works with --global flag. For local projects, manually remove RTK from CLAUDE.md");
            }

            // Rust checks --gemini AFTER the !global bail (init.rs:676-700) — i.e. `--uninstall
            // --gemini` without --global hits the generic bail above, not this deferral.
            if (flags.Gemini)
            {
                throw Deferred("--uninstall --gemini --global");
            }

            RunGlobalUninstall(ctx);

            if (ctx.DryRun)
            {
                InitArtifacts.PrintDryRunFooter();
            }

            return 0;
        }

        if (flags.Gemini)
        {
            throw Deferred("--gemini");
        }

        if (flags.Copilot)
        {
            throw Deferred("--copilot");
        }

        if (flags.Agent is "pi" or "kilocode" or "antigravity" or "hermes" or "cursor" or "windsurf" or "cline")
        {
            throw Deferred($"--agent {flags.Agent}");
        }

        if (flags.Agent is not null and not "claude")
        {
            throw new InitAbortException($"unknown --agent value: {flags.Agent}");
        }

        if (flags.Codex)
        {
            // Rust's own combination-validation bails (--codex cannot be combined with
            // --opencode/--claude-md/--hook-only/--auto-patch/--no-patch) are not replicated here
            // since full Codex support (run_codex_mode) is not implemented yet — see Task 5 of the
            // phase-9b plan, which flips this branch to a real dispatch.
            throw Deferred("--codex");
        }

        if (flags.Opencode && !flags.Global)
        {
            throw new InitAbortException("OpenCode plugin is global-only. Use: rtk init -g --opencode");
        }

        if (flags.Opencode)
        {
            throw Deferred("--opencode");
        }

        if (flags.ClaudeMd && flags.HookOnly)
        {
            throw new InitAbortException("--claude-md and --hook-only cannot be combined");
        }

        if (flags.AutoPatch && flags.NoPatch)
        {
            throw new InitAbortException("--auto-patch and --no-patch cannot be combined");
        }

        if (flags.Global)
        {
            // Rust's run() mode-selection match (init.rs:303) reduces, once --opencode is deferred
            // above (so install_opencode is always false by the time we reach here), to a plain
            // switch on claude_md vs hook_only vs default — global scope for all three.
            var patchMode = flags.AutoPatch ? PatchMode.Auto : flags.NoPatch ? PatchMode.Skip : PatchMode.Ask;

            if (flags.ClaudeMd)
            {
                RunGlobalClaudeMdMode(ctx);
            }
            else if (flags.HookOnly)
            {
                RunGlobalHookOnlyMode(patchMode, ctx);
            }
            else
            {
                RunGlobalDefaultMode(patchMode, ctx);
            }

            if (ctx.DryRun)
            {
                InitArtifacts.PrintDryRunFooter();
            }
            else
            {
                Console.Out.Write("\n");
            }

            return 0;
        }

        // Project-scope Claude Code dispatch.
        if (flags.ClaudeMd)
        {
            RunProjectClaudeMdMode(ctx);
        }
        else if (flags.HookOnly)
        {
            Console.Error.Write("[warn] Warning: --hook-only only makes sense with --global\n");
            Console.Error.Write("    For local projects, use default mode or --claude-md\n");
        }
        else
        {
            RunProjectClaudeMdMode(ctx);
            InitArtifacts.GenerateProjectFiltersTemplate(ctx);
        }

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
        else
        {
            Console.Out.Write("\n");
        }

        return 0;
    }

    /// <summary>
    /// Legacy project-scope mode: idempotently injects the full RTK instructions block into
    /// <c>./CLAUDE.md</c>. Port of the <c>!global</c> branch of Rust <c>run_claude_md_mode</c>
    /// (init.rs:1489). The <c>global</c> branch is <see cref="RunGlobalClaudeMdMode"/>.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void RunProjectClaudeMdMode(InitContext ctx)
    {
        const string path = "CLAUDE.md";

        if (ctx.Verbose > 0)
        {
            Console.Error.Write($"Writing rtk instructions to: {path}\n");
        }

        var action = InitArtifacts.WriteRtkBlock(path, InitArtifacts.RtkInstructions, "rtk instructions", "rtk init --claude-md", ctx);

        if (action == RtkBlockUpsert.Unchanged)
        {
            return;
        }

        if (!ctx.DryRun)
        {
            Console.Out.Write("   Claude Code will use rtk in this project\n");
        }
    }

    /// <summary>
    /// Global-scope default mode: hook registration + slim RTK.md + <c>@RTK.md</c> reference +
    /// settings.json patch + user-global filters template. Port of the <c>global</c> branch of Rust
    /// <c>run_default_mode</c> (init.rs:1120), with <c>install_opencode</c> always <see langword="false"/>
    /// (OpenCode co-install is deferred — see <see cref="Deferred"/> calls above this method's caller).
    /// </summary>
    /// <param name="patchMode">The settings.json patch-consent mode.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void RunGlobalDefaultMode(PatchMode patchMode, InitContext ctx)
    {
        var claudeDir = SettingsPatcher.ResolveClaudeDir();
        var rtkMdPath = Path.Combine(claudeDir, "RTK.md");
        var claudeMdPath = Path.Combine(claudeDir, "CLAUDE.md");

        // 1. Migrate old hook script if present (mostly-dead path, ported for parity).
        MigrateOldHookScript(ctx);

        // 2. Write RTK.md.
        InitArtifacts.WriteIfChanged(rtkMdPath, InitArtifacts.RtkSlim, "RTK.md", ctx);

        // 3. Patch CLAUDE.md (add @RTK.md, migrate old inline block if present).
        var migrated = InitArtifacts.PatchClaudeMd(claudeMdPath, ctx);

        // 4. Print success message (skipped in dry-run).
        if (!ctx.DryRun)
        {
            Console.Out.Write("\nRTK hook registered (global).\n\n");
            Console.Out.Write($"  Command:   {SettingsPatcher.ClaudeHookCommand}\n");
            Console.Out.Write($"  RTK.md:    {rtkMdPath} (10 lines)\n");
            Console.Out.Write("  CLAUDE.md: @RTK.md reference added\n");

            if (migrated)
            {
                Console.Out.Write("\n  [ok] Migrated: removed 137-line RTK block from CLAUDE.md\n");
                Console.Out.Write("              replaced with @RTK.md (10 lines)\n");
            }
        }

        // 5. Patch settings.json with the binary hook command.
        var patchResult = SettingsPatcher.PatchSettingsJsonCommand(SettingsPatcher.ClaudeHookCommand, patchMode, includeOpencode: false, ctx);

        if (!ctx.DryRun)
        {
            ReportPatchResult(patchResult);
        }

        // 6. Generate the user-global filters template.
        InitArtifacts.GenerateGlobalFiltersTemplate(ctx);

        if (!ctx.DryRun)
        {
            Console.Out.Write("\n");
        }
    }

    /// <summary>
    /// Global-scope hook-only mode: the hook and settings.json patch, with no RTK.md. Port of the
    /// <c>global</c> branch of Rust <c>run_hook_only_mode</c> (init.rs:1419); the <c>!global</c>
    /// warning branch is handled directly in <see cref="RunCore"/>.
    /// </summary>
    /// <param name="patchMode">The settings.json patch-consent mode.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void RunGlobalHookOnlyMode(PatchMode patchMode, InitContext ctx)
    {
        MigrateOldHookScript(ctx);

        if (!ctx.DryRun)
        {
            Console.Out.Write("\nRTK hook registered (hook-only mode).\n\n");
            Console.Out.Write($"  Command: {SettingsPatcher.ClaudeHookCommand}\n");
            Console.Out.Write("  Note: No RTK.md created. Claude won't know about meta commands (gain, discover, proxy).\n");
        }

        var patchResult = SettingsPatcher.PatchSettingsJsonCommand(SettingsPatcher.ClaudeHookCommand, patchMode, includeOpencode: false, ctx);

        if (!ctx.DryRun)
        {
            ReportPatchResult(patchResult);
            Console.Out.Write("\n");
        }
    }

    /// <summary>
    /// Prints the post-patch status line for <see cref="PatchResult.AlreadyPresent"/> (the
    /// <see cref="PatchResult.Patched"/>/<see cref="PatchResult.Declined"/>/<see cref="PatchResult.Skipped"/>
    /// cases already printed their own messages inside <see cref="SettingsPatcher.PatchSettingsJsonCommand"/>,
    /// and <see cref="PatchResult.WouldPatch"/> cannot occur outside dry-run). Port of the
    /// <c>match patch_result</c> block shared by <c>run_default_mode</c> (init.rs:1176-1196) and
    /// <c>run_hook_only_mode</c> (init.rs:1459-1479).
    /// </summary>
    /// <param name="patchResult">The result returned by <see cref="SettingsPatcher.PatchSettingsJsonCommand"/>.</param>
    private static void ReportPatchResult(PatchResult patchResult)
    {
        if (patchResult == PatchResult.AlreadyPresent)
        {
            Console.Out.Write("\n  settings.json: hook already present\n");
            Console.Out.Write("  Restart Claude Code. Test with: git status\n");
        }
    }

    /// <summary>
    /// Global-scope legacy mode: full 137-line block injection into <c>{ClaudeConfigDir}/CLAUDE.md</c>.
    /// Port of the <c>global</c> branch of Rust <c>run_claude_md_mode</c> (init.rs:1489), with
    /// <c>install_opencode</c> always <see langword="false"/>.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void RunGlobalClaudeMdMode(InitContext ctx)
    {
        var claudeDir = SettingsPatcher.ResolveClaudeDir();
        var path = Path.Combine(claudeDir, "CLAUDE.md");

        if (!ctx.DryRun)
        {
            Directory.CreateDirectory(claudeDir);
        }

        if (ctx.Verbose > 0)
        {
            Console.Error.Write($"Writing rtk instructions to: {path}\n");
        }

        var action = InitArtifacts.WriteRtkBlock(path, InitArtifacts.RtkInstructions, "rtk instructions", "rtk init -g --claude-md", ctx);

        if (action == RtkBlockUpsert.Unchanged)
        {
            return;
        }

        if (!ctx.DryRun)
        {
            Console.Out.Write("   Claude Code will now use rtk in all sessions\n");
        }
    }

    /// <summary>
    /// Migrates the legacy shell-script hook installation: deletes
    /// <c>{home}/.claude/hooks/rtk-rewrite.sh</c>, its <c>.rtk-hook.sha256</c> integrity sidecar, and
    /// the equivalent legacy Cursor hook, cleaning up the stale settings.json entry the deleted
    /// script left behind. Port of Rust <c>migrate_old_hook_script</c> (init.rs:1212).
    /// </summary>
    /// <remarks>
    /// A mostly-dead path in current usage (no supported RTK version has written a shell-script hook
    /// in a long time) — ported for parity per the phase-9b plan. Faithfully mirrors Rust's use of
    /// the <b>real</b> home directory (<c>dirs::home_dir()</c>) here, not <see cref="SettingsPatcher.ResolveClaudeDir"/>'s
    /// <c>CLAUDE_CONFIG_DIR</c>-aware resolution — confirmed against the Rust oracle, this function
    /// does not honor that override, so test isolation of this specific path is not possible without
    /// also virtualizing the process home directory.
    /// </remarks>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void MigrateOldHookScript(InitContext ctx)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return;
        }

        var oldHook = Path.Combine(home, ".claude", "hooks", "rtk-rewrite.sh");
        if (File.Exists(oldHook))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would migrate legacy hook script: {oldHook}\n");
            }
            else
            {
                try
                {
                    File.Delete(oldHook);
                    if (ctx.Verbose > 0)
                    {
                        Console.Error.Write($"  [ok] Removed old hook script: {oldHook}\n");
                    }

                    RemoveLegacySettingsEntries(ctx);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (ctx.Verbose > 0)
                    {
                        Console.Error.Write($"  [warn] Failed to remove old hook script: {ex.Message}\n");
                    }
                }
            }
        }

        var hashFile = Path.Combine(home, ".claude", "hooks", ".rtk-hook.sha256");
        if (File.Exists(hashFile))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove legacy hash file: {hashFile}\n");
            }
            else
            {
                TryDeleteBestEffort(hashFile);
            }
        }

        var cursorHook = Path.Combine(home, ".cursor", "hooks", "rtk-rewrite.sh");
        if (File.Exists(cursorHook))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove legacy Cursor hook: {cursorHook}\n");
            }
            else
            {
                TryDeleteBestEffort(cursorHook);
            }
        }
    }

    /// <summary>
    /// Removes only legacy <c>rtk-rewrite.sh</c> entries from settings.json, preserving any existing
    /// new-format <c>rtk hook claude</c> entries. Port of Rust <c>remove_legacy_settings_entries</c>
    /// (init.rs:1274). Rust swallows every error here (logging a <c>[warn]</c> only when verbose) —
    /// unlike the rest of this file's fail-loud contract — since this cleanup is best-effort
    /// migration plumbing for a mostly-dead legacy path, not a user-requested action in its own right.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void RemoveLegacySettingsEntries(InitContext ctx)
    {
        try
        {
            var claudeDir = SettingsPatcher.ResolveClaudeDir();
            var settingsPath = Path.Combine(claudeDir, "settings.json");

            if (!File.Exists(settingsPath))
            {
                return;
            }

            var content = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var root = SettingsPatcher.ParseSettingsObject(content, settingsPath);
            if (!SettingsPatcher.RemoveLegacyHookEntriesFromJson(root))
            {
                return;
            }

            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove legacy rtk-rewrite.sh entry from {settingsPath}\n");
                return;
            }

            var backupPath = settingsPath + ".bak";
            File.Copy(settingsPath, backupPath, overwrite: true);

            var serialized = SettingsPatcher.SerializePretty(root);
            InitArtifacts.AtomicWrite(settingsPath, serialized);

            if (ctx.Verbose > 0)
            {
                Console.Error.Write("  [ok] Removed legacy rtk-rewrite.sh entry from settings.json\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InitAbortException or JsonException)
        {
            if (ctx.Verbose > 0)
            {
                Console.Error.Write($"  [warn] Failed to clean legacy settings.json entry: {ex.Message}\n");
            }
        }
    }

    /// <summary>Best-effort file deletion, swallowing IO errors (matches Rust's <c>let _ = fs::remove_file(...)</c>).</summary>
    /// <param name="path">The file to delete.</param>
    private static void TryDeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: Rust discards this error too (`let _ = ...`).
        }
    }

    /// <summary>
    /// Full uninstall for the global Claude Code scope: removes the legacy hook script + integrity
    /// sidecar, <c>RTK.md</c>, the <c>@RTK.md</c> reference / <c>rtk-instructions</c> block from
    /// <c>CLAUDE.md</c>, and the settings.json hook entry. Port of the Claude subset of Rust
    /// <c>uninstall</c> (init.rs:620), specifically the body from init.rs:673 onward (the
    /// codex/cursor/pi/gemini special-casing above it is handled by <see cref="RunCore"/>).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void RunGlobalUninstall(InitContext ctx)
    {
        var claudeDir = SettingsPatcher.ResolveClaudeDir();
        var removed = new List<string>();

        // 1. Remove legacy hook script (if present from an old installation).
        var hookPath = Path.Combine(claudeDir, "hooks", "rtk-rewrite.sh");
        if (File.Exists(hookPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove hook script: {hookPath}\n");
            }
            else
            {
                File.Delete(hookPath);
            }

            removed.Add($"Hook script: {hookPath}");
        }

        // 1b. Remove the integrity hash sidecar (path-only: full hash verification is Task 3 scope).
        var hashSidecar = Path.Combine(claudeDir, "hooks", ".rtk-hook.sha256");
        if (File.Exists(hashSidecar))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write("[dry-run] would remove integrity hash sidecar\n");
            }
            else
            {
                File.Delete(hashSidecar);
            }

            removed.Add("Integrity hash: removed");
        }

        // 2. Remove RTK.md.
        var rtkMdPath = Path.Combine(claudeDir, "RTK.md");
        if (File.Exists(rtkMdPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove RTK.md: {rtkMdPath}\n");
            }
            else
            {
                File.Delete(rtkMdPath);
            }

            removed.Add($"RTK.md: {rtkMdPath}");
        }

        // 3. Remove @RTK.md reference / rtk-instructions block from CLAUDE.md.
        var claudeMdPath = Path.Combine(claudeDir, "CLAUDE.md");
        if (File.Exists(claudeMdPath))
        {
            CleanClaudeMd(claudeMdPath, removed, ctx);
        }

        // 4. Remove the hook entry from settings.json.
        if (SettingsPatcher.RemoveHookFromSettings(ctx))
        {
            removed.Add("settings.json: removed RTK hook entry");
        }

        // 5/6. OpenCode plugin removal / Cursor hook removal: no-ops in this port (neither install
        // path exists yet), so there is nothing they could ever find to remove — matching Rust's
        // remove_opencode_plugin/remove_cursor_hooks behavior on a machine where neither was installed.

        // Report results.
        if (removed.Count == 0)
        {
            Console.Out.Write("RTK was not installed (nothing to remove)\n");
            Console.Out.Write($"  Checked: {hookPath}\n");
            Console.Out.Write($"  Checked: {rtkMdPath}\n");
            Console.Out.Write($"  Checked: {claudeMdPath}\n");
            Console.Out.Write($"  Checked: {Path.Combine(claudeDir, "settings.json")}\n");
            return;
        }

        var header = ctx.DryRun ? "[dry-run] would uninstall RTK:" : "RTK uninstalled:";
        Console.Out.Write($"{header}\n");
        foreach (var item in removed)
        {
            Console.Out.Write($"  - {item}\n");
        }

        if (!ctx.DryRun)
        {
            Console.Out.Write("\nRestart Claude Code, OpenCode, and Cursor (if used) to apply changes.\n");
        }
    }

    /// <summary>
    /// Removes the <c>@RTK.md</c> reference line and/or the old inline <c>rtk-instructions</c> block
    /// from <c>CLAUDE.md</c>, deleting the file if nothing but whitespace remains. Port of step 3 of
    /// Rust <c>uninstall</c> (init.rs:740-803).
    /// </summary>
    /// <param name="claudeMdPath">The <c>CLAUDE.md</c> path.</param>
    /// <param name="removed">The running list of removed-artifact descriptions to append to.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void CleanClaudeMd(string claudeMdPath, List<string> removed, InitContext ctx)
    {
        var working = File.ReadAllText(claudeMdPath);
        var changed = false;

        if (working.Contains(InitArtifacts.RtkMdRef, StringComparison.Ordinal))
        {
            var filteredLines = InitArtifacts.SplitRustLines(working)
                .Where(line => !line.Trim().StartsWith(InitArtifacts.RtkMdRef, StringComparison.Ordinal));
            working = InitArtifacts.CleanDoubleBlanks(string.Join("\n", filteredLines));
            changed = true;
            removed.Add("CLAUDE.md: removed @RTK.md reference");
        }

        if (working.Contains(InitArtifacts.RtkBlockStart, StringComparison.Ordinal))
        {
            var (cleaned, didRemove) = InitArtifacts.RemoveRtkBlock(working);
            if (didRemove)
            {
                working = cleaned;
                changed = true;
                removed.Add("CLAUDE.md: removed rtk-instructions block");
            }
        }

        if (!changed)
        {
            return;
        }

        var trimmed = working.Trim();
        if (trimmed.Length == 0)
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove CLAUDE.md (empty after cleanup): {claudeMdPath}\n");
            }
            else
            {
                File.Delete(claudeMdPath);
            }

            removed.RemoveAll(r => r.StartsWith("CLAUDE.md:", StringComparison.Ordinal));
            removed.Add("CLAUDE.md: removed (was empty after cleanup)");
            return;
        }

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would update CLAUDE.md: {claudeMdPath}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{working}\n");
            }

            return;
        }

        File.WriteAllText(claudeMdPath, working);
    }

    /// <summary>
    /// Prints current global Claude Code configuration status. Port of the Claude subset of Rust
    /// <c>show_claude_config</c> (init.rs:3300): hook / RTK.md / CLAUDE.md (global + local) /
    /// settings.json status lines, the unconditional OpenCode and Cursor "not found" status lines
    /// (reproduced byte-exact even though those agents' install paths are deferred to a later
    /// task — they always resolve to their "not installed" branch since this port never writes
    /// those artifacts), and the closing Usage block.
    /// </summary>
    /// <remarks>
    /// The Rust function's hook-integrity status line (only shown when a <b>legacy shell-script</b>
    /// hook file exists and the binary command isn't registered) is intentionally not reproduced:
    /// this port never writes a legacy shell-script hook, so that condition is structurally
    /// unreachable here, and the real integrity-verification logic (hashing, tamper detection) is
    /// Task 3 scope. Likewise, the Rust function's Unix-only detailed hook-script diagnostics
    /// (executable bit, guard-comment sniffing, version parsing) are not reproduced for the same
    /// reason — this port has nothing to diagnose there.
    /// </remarks>
    private static void ShowClaudeConfig()
    {
        var claudeDir = SettingsPatcher.ResolveClaudeDir();
        var hookPath = Path.Combine(claudeDir, "hooks", "rtk-rewrite.sh");
        var rtkMdPath = Path.Combine(claudeDir, "RTK.md");
        var globalClaudeMdPath = Path.Combine(claudeDir, "CLAUDE.md");
        const string localClaudeMdPath = "CLAUDE.md";
        var settingsPath = Path.Combine(claudeDir, "settings.json");

        Console.Out.Write("rtk Configuration:\n\n");

        var binaryHookRegistered = TryReadSettingsObject(settingsPath, out var settingsRoot) &&
            SettingsPatcher.HookAlreadyPresent(settingsRoot!, SettingsPatcher.ClaudeHookCommand);

        if (binaryHookRegistered)
        {
            Console.Out.Write($"[ok] Hook: {SettingsPatcher.ClaudeHookCommand} (native binary command)\n");
        }
        else if (File.Exists(hookPath))
        {
            Console.Out.Write($"[warn] Hook: {hookPath} (legacy script — run `rtk init -g` to upgrade)\n");
        }
        else
        {
            Console.Out.Write("[--] Hook: not found\n");
        }

        if (File.Exists(rtkMdPath))
        {
            Console.Out.Write($"[ok] RTK.md: {rtkMdPath} (slim mode)\n");
        }
        else
        {
            Console.Out.Write("[--] RTK.md: not found\n");
        }

        if (File.Exists(globalClaudeMdPath))
        {
            var content = File.ReadAllText(globalClaudeMdPath);
            if (content.Contains(InitArtifacts.RtkMdRef, StringComparison.Ordinal))
            {
                Console.Out.Write("[ok] Global (~/.claude/CLAUDE.md): @RTK.md reference\n");
            }
            else if (content.Contains(InitArtifacts.RtkBlockStart, StringComparison.Ordinal))
            {
                Console.Out.Write("[warn] Global (~/.claude/CLAUDE.md): old RTK block (run: rtk init -g to migrate)\n");
            }
            else
            {
                Console.Out.Write("[--] Global (~/.claude/CLAUDE.md): exists but rtk not configured\n");
            }
        }
        else
        {
            Console.Out.Write("[--] Global (~/.claude/CLAUDE.md): not found\n");
        }

        if (File.Exists(localClaudeMdPath))
        {
            var content = File.ReadAllText(localClaudeMdPath);
            Console.Out.Write(content.Contains("rtk", StringComparison.Ordinal)
                ? "[ok] Local (./CLAUDE.md): rtk enabled\n"
                : "[--] Local (./CLAUDE.md): exists but rtk not configured\n");
        }
        else
        {
            Console.Out.Write("[--] Local (./CLAUDE.md): not found\n");
        }

        if (File.Exists(settingsPath))
        {
            var content = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                Console.Out.Write("[--] settings.json: empty\n");
            }
            else if (binaryHookRegistered)
            {
                Console.Out.Write("[ok] settings.json: RTK hook configured\n");
            }
            else if (settingsRoot is not null)
            {
                Console.Out.Write("[warn] settings.json: exists but RTK hook not configured\n");
                Console.Out.Write("    Run: rtk init -g --auto-patch\n");
            }
            else
            {
                Console.Out.Write("[warn] settings.json: exists but invalid JSON\n");
            }
        }
        else
        {
            Console.Out.Write("[--] settings.json: not found\n");
        }

        var opencodePlugin = Path.Combine(ResolveHomeSubdir(".config"), "opencode", "plugins", "rtk.ts");
        Console.Out.Write(File.Exists(opencodePlugin)
            ? $"[ok] OpenCode: plugin installed ({opencodePlugin})\n"
            : "[--] OpenCode: plugin not found\n");

        var cursorDir = ResolveHomeSubdir(".cursor");
        var cursorHook = Path.Combine(cursorDir, "hooks", "rtk-rewrite.sh");
        Console.Out.Write(File.Exists(cursorHook)
            ? $"[warn] Cursor hook: {cursorHook} (legacy script — run `rtk init -g --agent cursor` to upgrade)\n"
            : "[--] Cursor hook: not found\n");

        Console.Out.Write("\nUsage:\n");
        Console.Out.Write("  rtk init              # Full injection into local CLAUDE.md\n");
        Console.Out.Write("  rtk init -g           # Hook + RTK.md + @RTK.md + settings.json (recommended)\n");
        Console.Out.Write("  rtk init -g --auto-patch    # Same as above but no prompt\n");
        Console.Out.Write("  rtk init -g --no-patch      # Skip settings.json (manual setup)\n");
        Console.Out.Write("  rtk init -g --uninstall     # Remove all RTK artifacts\n");
        Console.Out.Write("  rtk init -g --claude-md     # Legacy: full injection into ~/.claude/CLAUDE.md\n");
        Console.Out.Write("  rtk init -g --hook-only     # Hook only, no RTK.md\n");
        Console.Out.Write("  rtk init --codex            # Configure local AGENTS.md + RTK.md\n");
        Console.Out.Write("  rtk init -g --codex         # Configure $CODEX_HOME/AGENTS.md + $CODEX_HOME/RTK.md (or ~/.codex/)\n");
        Console.Out.Write("  rtk init -g --opencode      # OpenCode plugin only\n");
        Console.Out.Write("  rtk init -g --agent cursor  # Install Cursor Agent hooks\n");
    }

    /// <summary>Resolves <c>{home}/{subdir}</c>, matching Rust's <c>resolve_home_subdir</c> (init.rs:2714).</summary>
    /// <param name="subdir">The subdirectory name (e.g. <c>".cursor"</c>).</param>
    /// <returns>The resolved path.</returns>
    private static string ResolveHomeSubdir(string subdir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, subdir);
    }

    /// <summary>
    /// Attempts to read and parse <paramref name="settingsPath"/> as a JSON object, matching Rust's
    /// silent-fallback-to-<see langword="false"/> pattern in <c>show_claude_config</c>'s hook/settings
    /// checks (missing file, empty content, or malformed JSON all just mean "not configured" here,
    /// not a fatal error — <c>--show</c> is read-only diagnostics).
    /// </summary>
    /// <param name="settingsPath">The settings.json path.</param>
    /// <param name="root">The parsed object on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the file existed and parsed as a JSON object.</returns>
    private static bool TryReadSettingsObject(string settingsPath, out JsonObject? root)
    {
        root = null;
        if (!File.Exists(settingsPath))
        {
            return false;
        }

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

        try
        {
            root = SettingsPatcher.ParseSettingsObject(content, settingsPath);
            return true;
        }
        catch (InitAbortException)
        {
            return false;
        }
    }

    /// <summary>Builds the "not yet implemented" abort for a mode deferred to a follow-up task.</summary>
    /// <param name="feature">The flag/mode name, e.g. <c>"--gemini"</c>.</param>
    /// <returns>An <see cref="InitAbortException"/> carrying the deferred-mode diagnostic.</returns>
    private static InitAbortException Deferred(string feature) =>
        new($"{feature} is not yet implemented in this port (tracked for a follow-up task)");

    /// <summary>The parsed <c>rtk init</c> flags, mirroring Rust's <c>Commands::Init</c> struct variant.</summary>
    private sealed class InitFlags
    {
        public bool Global;
        public bool Opencode;
        public bool Gemini;
        public string? Agent;
        public bool Show;
        public bool ClaudeMd;
        public bool HookOnly;
        public bool AutoPatch;
        public bool NoPatch;
        public bool Uninstall;
        public bool Codex;
        public bool Copilot;
        public bool DryRun;
    }

    /// <summary>Parses the <c>init</c> verb's argument list into <see cref="InitFlags"/>.</summary>
    /// <param name="args">The arguments following <c>init</c>.</param>
    /// <returns>The parsed flags.</returns>
    private static InitFlags ParseArgs(string[] args)
    {
        var flags = new InitFlags();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-g":
                case "--global":
                    flags.Global = true;
                    break;
                case "--opencode":
                    flags.Opencode = true;
                    break;
                case "--gemini":
                    flags.Gemini = true;
                    break;
                case "--agent":
                    if (++i >= args.Length)
                    {
                        throw new InitAbortException("--agent requires a value");
                    }

                    flags.Agent = args[i];
                    break;
                case "--show":
                    flags.Show = true;
                    break;
                case "--claude-md":
                    flags.ClaudeMd = true;
                    break;
                case "--hook-only":
                    flags.HookOnly = true;
                    break;
                case "--auto-patch":
                    flags.AutoPatch = true;
                    break;
                case "--no-patch":
                    flags.NoPatch = true;
                    break;
                case "--uninstall":
                    flags.Uninstall = true;
                    break;
                case "--codex":
                    flags.Codex = true;
                    break;
                case "--copilot":
                    flags.Copilot = true;
                    break;
                case "--dry-run":
                    flags.DryRun = true;
                    break;
                default:
                    throw new InitAbortException($"unrecognized init argument: {args[i]}");
            }
        }

        return flags;
    }
}
