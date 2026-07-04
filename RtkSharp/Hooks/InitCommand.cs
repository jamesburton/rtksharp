using System;

namespace RtkSharp.Hooks;

/// <summary>
/// Implements the <c>rtk init</c> CLI verb: registers the RTK hook and writes the agent-instruction
/// artifacts for AI coding agents. Faithful (partial) port of Rust <c>src/hooks/init.rs</c>'s
/// <c>run</c> entry point (init.rs:251) and CLI dispatch (<c>main.rs</c>:1876).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> This port currently implements only the <b>project-scope Claude Code</b> paths:
/// default mode (legacy full-block injection into <c>./CLAUDE.md</c> + <c>.rtk/filters.toml</c>
/// template), <c>--claude-md</c> (the same block injection without the filters template), and
/// <c>--hook-only</c> (a no-op warning, since it only makes sense with <c>--global</c>). Every other
/// mode — global scope (settings.json patching, RTK.md/@RTK.md, uninstall, <c>--show</c>), Codex,
/// Gemini, Copilot, OpenCode, Cursor, Windsurf, Cline, Kilocode, Antigravity, Pi, and Hermes — is
/// parsed (so the CLI surface matches Rust's <c>clap</c> definition) but rejected with a clear
/// "not yet implemented" diagnostic rather than silently doing nothing or guessing at behavior.
/// Those modes land in follow-up tasks; see <c>docs/superpowers/plans/2026-07-03-phase9b-init-hooks.md</c>.
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
        var ctx = new InitContext(flags.Verbose, flags.DryRun);

        if (flags.Show)
        {
            throw Deferred("--show");
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

            throw Deferred("--uninstall --global");
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
            throw Deferred("--global");
        }

        // Project-scope Claude Code dispatch — the only fully-implemented mode in this task.
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
    /// (init.rs:1489). The <c>global</c> branch (writing to the resolved Claude config directory,
    /// plus the OpenCode co-install step) is out of scope for this task.
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
        public int Verbose;
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
                case "-v":
                case "--verbose":
                    flags.Verbose++;
                    break;
                default:
                    throw new InitAbortException($"unrecognized init argument: {args[i]}");
            }
        }

        return flags;
    }
}
