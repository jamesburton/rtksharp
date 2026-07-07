using System;
using System.Collections.Generic;
using System.IO;

namespace RtkSharp.Hooks;

/// <summary>
/// GitHub Copilot instructions-file + hook-config writer for <c>rtk init --copilot</c> (project
/// scope) and <c>rtk init --copilot --global</c> (user scope). Faithful port of the Copilot subset
/// of Rust <c>src/hooks/init.rs</c>: <c>run_copilot</c>/<c>run_copilot_at</c> (init.rs:3929-3976),
/// <c>uninstall_copilot</c>/<c>uninstall_copilot_at</c> (init.rs:3978-4052), <c>copilot_user_dir</c>
/// (init.rs:4054), <c>run_copilot_global</c>/<c>run_copilot_global_at</c> (init.rs:4062-4104), and
/// <c>uninstall_copilot_global</c>/<c>uninstall_copilot_global_at</c> (init.rs:4106-4181).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope selection.</b> Unlike Codex (which takes a <c>global: bool</c> parameter threaded
/// through a single <c>run_codex_mode</c> entry point), Rust's Copilot path exposes four separate
/// top-level functions with no shared dispatcher, and <c>main.rs</c> itself branches on the shared
/// <c>-g</c>/<c>--global</c> flag to choose between them — for install: if <c>copilot</c> and
/// <c>global</c>, call <c>run_copilot_global(ctx)</c>, else call <c>run_copilot(ctx)</c>
/// (main.rs:1922-1927); for uninstall: if <c>uninstall &amp;&amp; copilot</c>, then if
/// <c>global</c> call <c>uninstall_copilot_global(ctx)</c> else <c>uninstall_copilot(ctx)</c>
/// (main.rs:1897-1902), checked *before* the generic multi-target <c>uninstall()</c> dispatcher (init.rs:620) that
/// handles Codex/Cursor/Pi — that generic function has no Copilot branch at all. So Copilot's
/// project-vs-global scope is simply "the same <c>-g</c> flag every other agent uses", not a
/// Copilot-specific flag; this file mirrors that by exposing separate
/// <see cref="RunCopilot"/>/<see cref="RunCopilotGlobal"/> and
/// <see cref="UninstallCopilot"/>/<see cref="UninstallCopilotGlobal"/> methods for the central
/// dispatcher (<c>InitCommand.cs</c>, wired up separately) to select between based on
/// <c>flags.Global</c>.
/// </para>
/// <para>
/// <b>Unlike Codex, Copilot DOES install a runtime hook config</b> (<c>rtk-rewrite.json</c>, written
/// via <see cref="InitArtifacts.WriteIfChanged"/> — a plain file, not <c>settings.json</c> patching).
/// The hook JSON dual-encodes both VS Code Copilot Chat's <c>PreToolUse</c> schema and Copilot CLI's
/// <c>preToolUse</c> schema in the same file (see the source comment above
/// <see cref="CopilotHookJson"/> in Rust, init.rs:3872) so one artifact serves both hosts. The
/// Markdown instructions file (<c>copilot-instructions.md</c>) uses the same
/// <c>&lt;!-- rtk-instructions --&gt;</c> marker-block upsert
/// (<see cref="InitArtifacts.WriteRtkBlock"/>/<see cref="InitArtifacts.RemoveRtkBlock"/>) as Claude
/// Code's legacy <c>CLAUDE.md</c> block mode — not the Codex-style <c>@RTK.md</c> reference line.
/// </para>
/// <para>
/// <b>No <c>--show</c> integration.</b> Rust's <c>show_claude_config</c> (init.rs:3300, the function
/// behind <c>rtk init --show</c>) never mentions Copilot — it only reports on the Claude Code hook
/// and <c>CLAUDE.md</c>/<c>RTK.md</c> state. There is no Copilot status line to port, and no
/// <c>ShowConfig</c> method is provided here.
/// </para>
/// </remarks>
public static class CopilotInit
{
    /// <summary>Project-scope base directory (Rust <c>GITHUB_DIR</c>, constants.rs:25).</summary>
    private const string GithubDirName = ".github";

    /// <summary>Hook-config subdirectory under either scope's base (Rust <c>HOOKS_SUBDIR</c>, constants.rs:4).</summary>
    private const string HooksSubdirName = "hooks";

    /// <summary>Hook config file name (Rust <c>COPILOT_HOOK_FILE</c>, constants.rs:26).</summary>
    private const string CopilotHookFileName = "rtk-rewrite.json";

    /// <summary>Instructions file name (Rust <c>COPILOT_INSTRUCTIONS_FILE</c>, constants.rs:27).</summary>
    private const string CopilotInstructionsFileName = "copilot-instructions.md";

    /// <summary>Fallback global-scope subdirectory of the user's home directory (Rust <c>COPILOT_USER_DIR</c>, constants.rs:28).</summary>
    private const string CopilotUserDirName = ".copilot";

    /// <summary>Environment variable overriding the global-scope base directory (Rust <c>COPILOT_HOME_ENV</c>, constants.rs:29).</summary>
    private const string CopilotHomeEnvVar = "COPILOT_HOME";

    /// <summary>
    /// Hook config written to <c>hooks/rtk-rewrite.json</c> under either scope. Byte-for-byte copy of
    /// Rust <c>COPILOT_HOOK_JSON</c> (init.rs:3873): dual-encodes VS Code Copilot Chat's
    /// <c>PreToolUse</c> schema and Copilot CLI's <c>preToolUse</c> schema in a single file.
    /// </summary>
    public static readonly string CopilotHookJson = NormalizeTemplate(RawCopilotHookJson);

    /// <summary>
    /// RTK-owned marker block upserted into <c>copilot-instructions.md</c>. Byte-for-byte copy of
    /// Rust <c>COPILOT_INSTRUCTIONS</c> (init.rs:3897).
    /// </summary>
    public static readonly string CopilotInstructions = NormalizeTemplate(RawCopilotInstructions);

    private const string RawCopilotHookJson = """
        {
          "version": 1,
          "hooks": {
            "PreToolUse": [
              {
                "type": "command",
                "command": "rtk hook copilot",
                "cwd": ".",
                "timeout": 5
              }
            ],
            "preToolUse": [
              {
                "type": "command",
                "bash": "rtk hook copilot",
                "powershell": "rtk hook copilot",
                "cwd": ".",
                "timeoutSec": 5
              }
            ]
          }
        }
        """;

    private const string RawCopilotInstructions = """
        <!-- rtk-instructions v2 -->
        # RTK — Token-Optimized CLI

        **rtk** is a CLI proxy that filters and compresses command outputs, saving 60-90% tokens.

        ## Rule

        Always prefix shell commands with `rtk`:

        ```bash
        # Instead of:              Use:
        git status                 rtk git status
        git log -10                rtk git log -10
        cargo test                 rtk cargo test
        docker ps                  rtk docker ps
        kubectl get pods           rtk kubectl pods
        ```

        ## Meta commands (use directly)

        ```bash
        rtk gain              # Token savings dashboard
        rtk gain --history    # Per-command savings history
        rtk discover          # Find missed rtk opportunities
        rtk proxy <cmd>       # Run raw (no filtering) but track usage
        ```
        <!-- /rtk-instructions -->
        """;

    /// <summary>
    /// Normalizes an embedded raw-string template to exactly one trailing <c>\n</c> with LF-only
    /// line endings, regardless of how the C# raw string literal captured trailing whitespace or
    /// what EOL style the source file happened to have on disk. Same normalization
    /// <see cref="InitArtifacts"/> applies to its own inline templates (private there, duplicated
    /// here in miniature rather than exposing it across files).
    /// </summary>
    /// <param name="raw">The raw template text as captured by the C# raw string literal.</param>
    /// <returns>The template with LF line endings and exactly one trailing newline.</returns>
    private static string NormalizeTemplate(string raw) =>
        raw.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    /// <summary>
    /// Resolves the global-scope Copilot base directory: <c>$COPILOT_HOME</c> if set, else
    /// <c>{home}/.copilot</c>. Port of Rust <c>copilot_user_dir</c> (init.rs:4054).
    /// </summary>
    /// <returns>The resolved Copilot user directory path.</returns>
    internal static string CopilotUserDir()
    {
        var custom = Environment.GetEnvironmentVariable(CopilotHomeEnvVar);
        if (!string.IsNullOrEmpty(custom))
        {
            return custom;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            throw new InitAbortException("could not determine home directory");
        }

        return Path.Combine(home, CopilotUserDirName);
    }

    /// <summary>
    /// Entry point for <c>rtk init --copilot</c> (project scope): installs into the current working
    /// directory's <c>.github/</c> subdirectory. Port of Rust <c>run_copilot</c> (init.rs:3929).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void RunCopilot(InitContext ctx) => RunProjectAt(".", ctx);

    /// <summary>
    /// Same as <see cref="RunCopilot"/> but operates relative to an explicit base path. Port of Rust
    /// <c>run_copilot_at</c> (init.rs:3937). Exposed <c>internal</c> so tests can avoid mutating the
    /// process-global CWD.
    /// </summary>
    /// <param name="baseDir">The base directory under which <c>.github/</c> is resolved.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void RunProjectAt(string baseDir, InitContext ctx)
    {
        var githubDir = Path.Combine(baseDir, GithubDirName);
        var hooksDir = Path.Combine(githubDir, HooksSubdirName);

        if (!ctx.DryRun)
        {
            Directory.CreateDirectory(hooksDir);
        }

        // 1. Upsert RTK marker block in copilot-instructions.md (preserves user content).
        //    Done BEFORE writing the hook config so a malformed file aborts the install without
        //    leaving a stale hook on disk.
        var instructionsPath = Path.Combine(githubDir, CopilotInstructionsFileName);
        InitArtifacts.WriteRtkBlock(instructionsPath, CopilotInstructions, "Copilot instructions", "rtk init --copilot", ctx);

        // 2. Write hook config (only reached if the upsert above succeeded).
        var hookPath = Path.Combine(hooksDir, CopilotHookFileName);
        InitArtifacts.WriteIfChanged(hookPath, CopilotHookJson, "Copilot hook config", ctx);

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
            return;
        }

        Console.Out.Write("\nGitHub Copilot integration installed (project-scoped).\n\n");
        Console.Out.Write($"  Hook config:    {hookPath}\n");
        Console.Out.Write($"  Instructions:   {instructionsPath}\n");
        Console.Out.Write("\n  Works with VS Code Copilot Chat (transparent rewrite)\n");
        Console.Out.Write("  and Copilot CLI (deny-with-suggestion).\n");
        Console.Out.Write("\n  Restart your IDE or Copilot CLI session to activate.\n\n");
    }

    /// <summary>
    /// Entry point for <c>rtk init --copilot --global</c> (user scope). Port of Rust
    /// <c>run_copilot_global</c> (init.rs:4062).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void RunCopilotGlobal(InitContext ctx) => RunGlobalAt(CopilotUserDir(), ctx);

    /// <summary>
    /// Same as <see cref="RunCopilotGlobal"/> but operates against an explicit Copilot user
    /// directory. Port of Rust <c>run_copilot_global_at</c> (init.rs:4067). Exposed <c>internal</c>
    /// so tests can redirect the global scope without mutating <c>$COPILOT_HOME</c> process-wide (though
    /// tests typically still use the env var, matching <c>CodexScopeGuard</c>'s approach, since
    /// <see cref="CopilotUserDir"/> only reads it at call time).
    /// </summary>
    /// <param name="copilotDir">The resolved Copilot user directory (unlike project scope, this is the base itself — no <c>.github</c> join).</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void RunGlobalAt(string copilotDir, InitContext ctx)
    {
        var hooksDir = Path.Combine(copilotDir, HooksSubdirName);

        if (!ctx.DryRun)
        {
            Directory.CreateDirectory(hooksDir);
        }

        var instructionsPath = Path.Combine(copilotDir, CopilotInstructionsFileName);
        InitArtifacts.WriteRtkBlock(instructionsPath, CopilotInstructions, "Copilot user-level instructions", "rtk init --global --copilot", ctx);

        var hookPath = Path.Combine(hooksDir, CopilotHookFileName);
        InitArtifacts.WriteIfChanged(hookPath, CopilotHookJson, "Copilot global hook config", ctx);

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
            return;
        }

        Console.Out.Write("\nGitHub Copilot global integration installed (user-scoped).\n\n");
        Console.Out.Write($"  Hook config:    {hookPath}\n");
        Console.Out.Write($"  Instructions:   {instructionsPath}\n");
        Console.Out.Write("\n  Applies to all Copilot CLI sessions on this machine.\n");
        Console.Out.Write("  Restart your Copilot CLI session to activate.\n\n");
    }

    /// <summary>
    /// Entry point for <c>rtk init --uninstall --copilot</c> (project-scoped, like install). Port of
    /// Rust <c>uninstall_copilot</c> (init.rs:3978).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void UninstallCopilot(InitContext ctx)
    {
        var removed = UninstallProjectAt(".", ctx);

        if (removed.Count == 0)
        {
            Console.Out.Write("RTK Copilot support was not installed (nothing to remove)\n");
        }
        else
        {
            var header = ctx.DryRun ? "[dry-run] would uninstall RTK (GitHub Copilot):" : "RTK uninstalled (GitHub Copilot):";
            Console.Out.Write($"{header}\n");
            foreach (var item in removed)
            {
                Console.Out.Write($"  - {item}\n");
            }

            if (!ctx.DryRun)
            {
                Console.Out.Write("\nRestart your IDE or Copilot CLI session to apply changes.\n");
            }
        }

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
    }

    /// <summary>
    /// Same as <see cref="UninstallCopilot"/> but operates relative to an explicit base path. Port of
    /// Rust <c>uninstall_copilot_at</c> (init.rs:4006).
    /// </summary>
    /// <param name="baseDir">The base directory under which <c>.github/</c> is resolved.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns>The list of human-readable descriptions of what was removed (or would be, in dry-run).</returns>
    internal static List<string> UninstallProjectAt(string baseDir, InitContext ctx)
    {
        var githubDir = Path.Combine(baseDir, GithubDirName);
        var removed = new List<string>();

        var hookPath = Path.Combine(githubDir, HooksSubdirName, CopilotHookFileName);
        if (File.Exists(hookPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove hook config: {hookPath}\n");
            }
            else
            {
                File.Delete(hookPath);
            }

            removed.Add($"Hook config: {hookPath}");
        }

        var instructionsPath = Path.Combine(githubDir, CopilotInstructionsFileName);
        if (File.Exists(instructionsPath))
        {
            var content = File.ReadAllText(instructionsPath);
            if (content.Contains(InitArtifacts.RtkBlockStart, StringComparison.Ordinal))
            {
                var (cleaned, didRemove) = InitArtifacts.RemoveRtkBlock(content);
                if (didRemove)
                {
                    if (ctx.DryRun)
                    {
                        Console.Out.Write($"[dry-run] would remove rtk-instructions block from {instructionsPath}\n");
                    }
                    else
                    {
                        InitArtifacts.AtomicWrite(instructionsPath, cleaned);
                    }

                    removed.Add($"{CopilotInstructionsFileName}: removed rtk-instructions block");
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Entry point for <c>rtk init --uninstall --copilot --global</c>. Port of Rust
    /// <c>uninstall_copilot_global</c> (init.rs:4106).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void UninstallCopilotGlobal(InitContext ctx)
    {
        var copilotDir = CopilotUserDir();
        var removed = UninstallGlobalAt(copilotDir, ctx);

        if (removed.Count == 0)
        {
            Console.Out.Write("RTK global Copilot support was not installed (nothing to remove)\n");
        }
        else
        {
            var header = ctx.DryRun ? "[dry-run] would uninstall RTK (global GitHub Copilot):" : "RTK uninstalled (global GitHub Copilot):";
            Console.Out.Write($"{header}\n");
            foreach (var item in removed)
            {
                Console.Out.Write($"  - {item}\n");
            }

            if (!ctx.DryRun)
            {
                Console.Out.Write("\nRestart your Copilot CLI session to apply changes.\n");
            }
        }

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
    }

    /// <summary>
    /// Same as <see cref="UninstallCopilotGlobal"/> but operates against an explicit Copilot user
    /// directory. Port of Rust <c>uninstall_copilot_global_at</c> (init.rs:4134).
    /// </summary>
    /// <param name="copilotDir">The Copilot user directory (base itself — no <c>.github</c> join).</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns>The list of human-readable descriptions of what was removed (or would be, in dry-run).</returns>
    internal static List<string> UninstallGlobalAt(string copilotDir, InitContext ctx)
    {
        var removed = new List<string>();

        var hookPath = Path.Combine(copilotDir, HooksSubdirName, CopilotHookFileName);
        if (File.Exists(hookPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove hook config: {hookPath}\n");
            }
            else
            {
                File.Delete(hookPath);
            }

            removed.Add($"Hook config: {hookPath}");
        }

        var instructionsPath = Path.Combine(copilotDir, CopilotInstructionsFileName);
        if (File.Exists(instructionsPath))
        {
            var content = File.ReadAllText(instructionsPath);
            if (content.Contains(InitArtifacts.RtkBlockStart, StringComparison.Ordinal))
            {
                var (cleaned, didRemove) = InitArtifacts.RemoveRtkBlock(content);
                if (didRemove)
                {
                    if (ctx.DryRun)
                    {
                        Console.Out.Write($"[dry-run] would remove rtk-instructions block from {instructionsPath}\n");
                    }
                    else
                    {
                        InitArtifacts.AtomicWrite(instructionsPath, cleaned);
                    }

                    removed.Add($"{CopilotInstructionsFileName}: removed rtk-instructions block");
                }
            }
        }

        return removed;
    }
}
