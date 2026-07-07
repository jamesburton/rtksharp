using System;
using System.IO;
using System.Text;

namespace RtkSharp.Hooks;

/// <summary>
/// "Workspace rules file" artifact writers for the four simplest deferred <c>rtk init --agent</c>
/// targets: <b>Cline</b> (<c>.clinerules</c>), <b>Windsurf</b> (<c>.windsurfrules</c>), <b>Kilo Code</b>
/// (<c>.kilocode/rules/rtk-rules.md</c>), and <b>Google Antigravity</b>
/// (<c>.agents/rules/antigravity-rtk-rules.md</c>). Faithful port of Rust
/// <c>run_cline_mode</c> (init.rs:1556), <c>run_windsurf_mode</c> (init.rs:1600),
/// <c>run_kilocode_mode</c>/<c>run_kilocode_mode_at</c> (init.rs:1649-1701), and
/// <c>run_antigravity_mode</c>/<c>run_antigravity_mode_at</c> (init.rs:1707-1757).
/// </summary>
/// <remarks>
/// <para>
/// <b>No marker-block upsert.</b> Unlike <c>CLAUDE.md</c>'s legacy full-block mode, these four agents
/// use a much simpler idempotency check: <c>existing.contains("RTK") || existing.contains("rtk")</c>.
/// If either substring is present anywhere in the file, RTK considers itself "already configured" and
/// touches nothing further (even if the file was hand-edited and the substring match is coincidental).
/// Otherwise the embedded rules block is appended verbatim (with a blank line separator if the file
/// already had content) via a <b>plain</b> <see cref="File.WriteAllText(string, string, Encoding)"/> —
/// none of these four writes go through <see cref="InitArtifacts.AtomicWrite"/>; Rust's own
/// implementation uses a plain <c>fs::write</c> here, not <c>atomic_write</c>, for all four agents.
/// </para>
/// <para>
/// <b>No global scope.</b> All four write to a project-relative path; none of these Rust functions
/// take a <c>global</c> parameter, and none resolve a home/config directory.
/// </para>
/// <para>
/// <b>Windsurf's <c>-g</c> guard is intentionally not reproduced.</b> Rust's dispatcher
/// (<c>hooks::init::run</c>, init.rs:293-295) bails with
/// <c>"Windsurf support is global-only. Use: rtk init -g --agent windsurf"</c> before ever calling
/// <c>run_windsurf_mode</c> — even though that function's write target (<c>.windsurfrules</c> in the
/// current directory) is completely independent of the <c>global</c> flag and is identical to the
/// project-scope-only Cline path. This is documented as an adjudicated Rust bug in
/// <c>docs/superpowers/plans/2026-07-03-phase9b-init-hooks.md</c> ("Windsurf Bug Adjudication"): the
/// guard is dropped in this port, so <see cref="RunWindsurf"/> behaves exactly like
/// <see cref="RunCline"/> — always project-scoped, never gated on a <c>-g</c> flag. Whoever wires this
/// class into <c>InitCommand</c>'s dispatcher should likewise not add that guard.
/// </para>
/// <para>
/// <b>Kilo Code and Antigravity are project-scope-only by dispatcher guard, not by this class.</b> In
/// Rust, <c>main.rs</c>'s CLI dispatch (not <c>init.rs</c>) bails with
/// <c>"Kilo Code is project-scoped. Use: rtk init --agent kilocode"</c> /
/// <c>"Antigravity is project-scoped. Use: rtk init --agent antigravity"</c> when <c>-g</c> is passed,
/// <b>before</b> calling <c>run_kilocode_mode</c>/<c>run_antigravity_mode</c> — those two functions
/// themselves take no <c>global</c> parameter at all, exactly like <c>run_cline_mode</c>/
/// <c>run_windsurf_mode</c>. Reproducing that guard belongs to the future <c>InitCommand</c> dispatch
/// wiring (out of scope here per this task's file boundaries), not to this class; <see cref="RunKilocode"/>
/// and <see cref="RunAntigravity"/> match the Rust function signatures exactly (context only, no
/// <c>global</c> flag).
/// </para>
/// <para>
/// <b>No Rust uninstall or <c>--show</c> support exists for any of these four agents.</b> Confirmed by
/// reading <c>uninstall()</c> (init.rs:620) end to end and <c>show_claude_config</c>/<c>show_config</c>
/// (init.rs:3300 onward): neither dispatches on Cline, Windsurf, Kilo Code, or Antigravity in any form —
/// there are no <c>remove_cline</c>/<c>remove_windsurf</c>/<c>remove_kilocode</c>/<c>remove_antigravity</c>
/// helpers, and <c>--show</c> never reports on these paths. <b>Judgment call</b> (flagged for central
/// reconciliation): since the task brief requires uninstall counterparts and a round-trip test, this
/// port adds an original (non-Rust-derived) <see cref="UninstallCline"/>/<see cref="UninstallWindsurf"/>/
/// <see cref="UninstallKilocode"/>/<see cref="UninstallAntigravity"/> implementation that strips the
/// exact embedded rules block back out of the file (deleting it if nothing else remains), with original
/// stdout wording — not a byte-for-byte oracle match, because no oracle behavior exists to match.
/// </para>
/// </remarks>
public static class WorkspaceRulesInit
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Embedded RTK rules for Cline. Byte-for-byte copy of <c>hooks/cline/rules.md</c> (Rust <c>CLINE_RULES</c>, init.rs:1552).</summary>
    public static readonly string ClineRules = Normalize(RawClineRules);

    /// <summary>Embedded RTK rules for Windsurf. Byte-for-byte copy of <c>hooks/windsurf/rules.md</c> (Rust <c>WINDSURF_RULES</c>, init.rs:1549).</summary>
    public static readonly string WindsurfRules = Normalize(RawWindsurfRules);

    /// <summary>Embedded RTK rules for Kilo Code. Byte-for-byte copy of <c>hooks/kilocode/rules.md</c> (Rust <c>KILOCODE_RULES</c>, init.rs:1647).</summary>
    public static readonly string KilocodeRules = Normalize(RawKilocodeRules);

    /// <summary>Embedded RTK rules for Google Antigravity. Byte-for-byte copy of <c>hooks/antigravity/rules.md</c> (Rust <c>ANTIGRAVITY_RULES</c>, init.rs:1705).</summary>
    public static readonly string AntigravityRules = Normalize(RawAntigravityRules);

    private const string RawClineRules = """
        # RTK - Rust Token Killer (Cline)

        **Usage**: Token-optimized CLI proxy for shell commands.

        ## Rule

        Always prefix shell commands with `rtk` to minimize token consumption.

        Examples:

        ```bash
        rtk git status
        rtk cargo test
        rtk ls src/
        rtk grep "pattern" src/
        rtk find "*.rs" .
        rtk docker ps
        rtk gh pr list
        ```

        ## Meta Commands

        ```bash
        rtk gain              # Show token savings
        rtk gain --history    # Command history with savings
        rtk discover          # Find missed RTK opportunities
        rtk proxy <cmd>       # Run raw (no filtering, for debugging)
        ```

        ## Why

        RTK filters and compresses command output before it reaches the LLM context, saving 60-90% tokens on common operations. Always use `rtk <cmd>` instead of raw commands.
        """;

    private const string RawWindsurfRules = """
        # RTK - Rust Token Killer (Windsurf)

        **Usage**: Token-optimized CLI proxy for shell commands.

        ## Rule

        Always prefix shell commands with `rtk` to minimize token consumption.

        Examples:

        ```bash
        rtk git status
        rtk cargo test
        rtk ls src/
        rtk grep "pattern" src/
        rtk find "*.rs" .
        rtk docker ps
        rtk gh pr list
        ```

        ## Meta Commands

        ```bash
        rtk gain              # Show token savings
        rtk gain --history    # Command history with savings
        rtk discover          # Find missed RTK opportunities
        rtk proxy <cmd>       # Run raw (no filtering, for debugging)
        ```

        ## Why

        RTK filters and compresses command output before it reaches the LLM context, saving 60-90% tokens on common operations. Always use `rtk <cmd>` instead of raw commands.
        """;

    private const string RawKilocodeRules = """
        # RTK - Rust Token Killer (Kilo Code)

        **Usage**: Token-optimized CLI proxy for shell commands.

        ## Rule

        Always prefix shell commands with `rtk` to minimize token consumption.

        Examples:

        ```bash
        rtk git status
        rtk cargo test
        rtk ls src/
        rtk grep "pattern" src/
        rtk find "*.rs" .
        rtk docker ps
        rtk gh pr list
        ```

        ## Meta Commands

        ```bash
        rtk gain              # Show token savings
        rtk gain --history    # Command history with savings
        rtk discover          # Find missed RTK opportunities
        rtk proxy <cmd>       # Run raw (no filtering, for debugging)
        ```

        ## Why

        RTK filters and compresses command output before it reaches the LLM context, saving 60-90% tokens on common operations. Always use `rtk <cmd>` instead of raw commands.
        """;

    private const string RawAntigravityRules = """
        # RTK - Rust Token Killer (Google Antigravity)

        **Usage**: Token-optimized CLI proxy for shell commands.

        ## Rule

        Always prefix shell commands with `rtk` to minimize token consumption.

        Examples:

        ```bash
        rtk git status
        rtk cargo test
        rtk ls src/
        rtk grep "pattern" src/
        rtk find "*.rs" .
        rtk docker ps
        rtk gh pr list
        ```

        ## Meta Commands

        ```bash
        rtk gain              # Show token savings
        rtk gain --history    # Command history with savings
        rtk discover          # Find missed RTK opportunities
        rtk proxy <cmd>       # Run raw (no filtering, for debugging)
        ```

        ## Why

        RTK filters and compresses command output before it reaches the LLM context, saving 60-90% tokens on common operations. Always use `rtk <cmd>` instead of raw commands.
        """;

    /// <summary>
    /// Normalizes an embedded raw-string template to LF-only line endings with exactly one trailing
    /// newline. These four blocks are inline Rust raw strings loaded via <c>include_str!</c> from
    /// <c>hooks/*/rules.md</c> in the oracle; this port stores them as C# raw string literals instead
    /// (see <see cref="InitArtifacts.FiltersTemplate"/> for the identical rationale/approach used
    /// elsewhere in this port).
    /// </summary>
    /// <param name="raw">The raw template text as captured by the C# raw string literal.</param>
    /// <returns>The template with LF line endings and exactly one trailing newline.</returns>
    private static string Normalize(string raw) => raw.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    // ───────────────────────────── Cline / Roo Code ─────────────────────────────

    /// <summary>
    /// Runs <c>rtk init --agent cline</c>: idempotently appends <see cref="ClineRules"/> to
    /// <c>.clinerules</c> in the current directory. Port of Rust <c>run_cline_mode</c> (init.rs:1556).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void RunCline(InitContext ctx) =>
        RunRulesFile(
            rulesPath: ".clinerules",
            rulesDisplay: ".clinerules",
            parentDir: null,
            rulesContent: ClineRules,
            agentAlready: "Cline",
            agentConfigured: "Cline",
            writeContext: "Failed to write .clinerules",
            footerText: "  Cline will now use rtk commands for token savings.\n  Test with: git status\n\n",
            explicitDryRunFooter: false,
            ctx);

    /// <summary>
    /// Removes the <see cref="ClineRules"/> block from <c>.clinerules</c> in the current directory (or
    /// deletes the file entirely if nothing else remains). See this class's remarks:
    /// <b>not derived from any Rust oracle behavior</b> — Rust has no Cline uninstall path.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if anything was removed (or would be, in dry-run).</returns>
    public static bool UninstallCline(InitContext ctx) =>
        UninstallRulesFile(".clinerules", ClineRules, "Cline", ctx);

    // ───────────────────────────── Windsurf ─────────────────────────────

    /// <summary>
    /// Runs <c>rtk init --agent windsurf</c>: idempotently appends <see cref="WindsurfRules"/> to
    /// <c>.windsurfrules</c> in the current directory. Port of Rust <c>run_windsurf_mode</c>
    /// (init.rs:1600). See this class's remarks for why the Rust <c>-g</c> guard is deliberately not
    /// reproduced.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void RunWindsurf(InitContext ctx) =>
        RunRulesFile(
            rulesPath: ".windsurfrules",
            rulesDisplay: ".windsurfrules",
            parentDir: null,
            rulesContent: WindsurfRules,
            agentAlready: "Windsurf",
            agentConfigured: "Windsurf Cascade",
            writeContext: "Failed to write .windsurfrules",
            footerText: "  Cascade will now use rtk commands for token savings.\n  Restart Windsurf. Test with: git status\n\n",
            explicitDryRunFooter: false,
            ctx);

    /// <summary>
    /// Removes the <see cref="WindsurfRules"/> block from <c>.windsurfrules</c> in the current
    /// directory (or deletes the file entirely if nothing else remains). See this class's remarks:
    /// <b>not derived from any Rust oracle behavior</b> — Rust has no Windsurf uninstall path.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if anything was removed (or would be, in dry-run).</returns>
    public static bool UninstallWindsurf(InitContext ctx) =>
        UninstallRulesFile(".windsurfrules", WindsurfRules, "Windsurf", ctx);

    // ───────────────────────────── Kilo Code ─────────────────────────────

    /// <summary>
    /// Runs <c>rtk init --agent kilocode</c>: resolves the current directory and delegates to
    /// <see cref="RunKilocodeAt"/>. Port of Rust <c>run_kilocode_mode</c> (init.rs:1649).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void RunKilocode(InitContext ctx) =>
        RunKilocodeAt(Directory.GetCurrentDirectory(), ctx);

    /// <summary>
    /// Idempotently appends <see cref="KilocodeRules"/> to <c>{baseDir}/.kilocode/rules/rtk-rules.md</c>,
    /// creating the parent directory if needed. Port of Rust <c>run_kilocode_mode_at</c> (init.rs:1653).
    /// </summary>
    /// <param name="baseDir">The project root to resolve <c>.kilocode/rules/</c> under.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void RunKilocodeAt(string baseDir, InitContext ctx)
    {
        var targetDir = Path.Combine(baseDir, ".kilocode", "rules");
        var rulesPath = Path.Combine(targetDir, "rtk-rules.md");
        RunRulesFile(
            rulesPath: rulesPath,
            rulesDisplay: ".kilocode/rules/rtk-rules.md",
            parentDir: targetDir,
            rulesContent: KilocodeRules,
            agentAlready: "Kilo Code",
            agentConfigured: "Kilo Code",
            writeContext: "Failed to write .kilocode/rules/rtk-rules.md",
            footerText: "  Kilo Code will now use rtk commands for token savings.\n  Test with: git status\n\n",
            explicitDryRunFooter: true,
            ctx);
    }

    /// <summary>
    /// Removes the <see cref="KilocodeRules"/> block from <c>.kilocode/rules/rtk-rules.md</c> in the
    /// current directory (or deletes the file entirely if nothing else remains). See this class's
    /// remarks: <b>not derived from any Rust oracle behavior</b> — Rust has no Kilo Code uninstall path.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if anything was removed (or would be, in dry-run).</returns>
    public static bool UninstallKilocode(InitContext ctx) => UninstallKilocodeAt(Directory.GetCurrentDirectory(), ctx);

    /// <summary>Testable core of <see cref="UninstallKilocode"/>, parametrized on the project root.</summary>
    /// <param name="baseDir">The project root <c>.kilocode/rules/</c> was resolved under.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if anything was removed (or would be, in dry-run).</returns>
    internal static bool UninstallKilocodeAt(string baseDir, InitContext ctx) =>
        UninstallRulesFile(Path.Combine(baseDir, ".kilocode", "rules", "rtk-rules.md"), KilocodeRules, "Kilo Code", ctx);

    // ───────────────────────────── Google Antigravity ─────────────────────────────

    /// <summary>
    /// Runs <c>rtk init --agent antigravity</c>: resolves the current directory and delegates to
    /// <see cref="RunAntigravityAt"/>. Port of Rust <c>run_antigravity_mode</c> (init.rs:1707).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void RunAntigravity(InitContext ctx) =>
        RunAntigravityAt(Directory.GetCurrentDirectory(), ctx);

    /// <summary>
    /// Idempotently appends <see cref="AntigravityRules"/> to
    /// <c>{baseDir}/.agents/rules/antigravity-rtk-rules.md</c>, creating the parent directory if
    /// needed. Port of Rust <c>run_antigravity_mode_at</c> (init.rs:1711).
    /// </summary>
    /// <param name="baseDir">The project root to resolve <c>.agents/rules/</c> under.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void RunAntigravityAt(string baseDir, InitContext ctx)
    {
        var targetDir = Path.Combine(baseDir, ".agents", "rules");
        var rulesPath = Path.Combine(targetDir, "antigravity-rtk-rules.md");
        RunRulesFile(
            rulesPath: rulesPath,
            rulesDisplay: ".agents/rules/antigravity-rtk-rules.md",
            parentDir: targetDir,
            rulesContent: AntigravityRules,
            agentAlready: "Antigravity",
            agentConfigured: "Google Antigravity",
            writeContext: "Failed to write .agents/rules/antigravity-rtk-rules.md",
            footerText: "  Antigravity will now use rtk commands for token savings.\n  Test with: git status\n\n",
            explicitDryRunFooter: true,
            ctx);
    }

    /// <summary>
    /// Removes the <see cref="AntigravityRules"/> block from
    /// <c>.agents/rules/antigravity-rtk-rules.md</c> in the current directory (or deletes the file
    /// entirely if nothing else remains). See this class's remarks: <b>not derived from any Rust
    /// oracle behavior</b> — Rust has no Antigravity uninstall path.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if anything was removed (or would be, in dry-run).</returns>
    public static bool UninstallAntigravity(InitContext ctx) => UninstallAntigravityAt(Directory.GetCurrentDirectory(), ctx);

    /// <summary>Testable core of <see cref="UninstallAntigravity"/>, parametrized on the project root.</summary>
    /// <param name="baseDir">The project root <c>.agents/rules/</c> was resolved under.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if anything was removed (or would be, in dry-run).</returns>
    internal static bool UninstallAntigravityAt(string baseDir, InitContext ctx) =>
        UninstallRulesFile(Path.Combine(baseDir, ".agents", "rules", "antigravity-rtk-rules.md"), AntigravityRules, "Google Antigravity", ctx);

    // ───────────────────────────── Shared core ─────────────────────────────

    /// <summary>
    /// Shared core of all four <c>Run*</c> entry points: idempotently appends
    /// <paramref name="rulesContent"/> to <paramref name="rulesPath"/>, reproducing each Rust
    /// function's exact stdout/stderr sequencing (including the "already configured + dry-run = print
    /// nothing" quirk for Cline/Windsurf, and the "explicit inner <c>print_dry_run_footer</c> call" for
    /// Kilo Code/Antigravity — see the two functions' differing structure in init.rs:1556-1758).
    /// </summary>
    /// <param name="rulesPath">The file path to check/write (relative for Cline/Windsurf, joined with a base dir for Kilo Code/Antigravity).</param>
    /// <param name="rulesDisplay">The literal path string Rust hardcodes into its printed messages (may differ in form from <paramref name="rulesPath"/>, e.g. always relative even when <paramref name="rulesPath"/> is absolute).</param>
    /// <param name="parentDir">The parent directory to create before writing, or <see langword="null"/> if none is needed (Cline/Windsurf write directly into an existing project root).</param>
    /// <param name="rulesContent">The embedded rules block to append.</param>
    /// <param name="agentAlready">The agent name used in the "already configured for X" message.</param>
    /// <param name="agentConfigured">The agent name used in the "configured for X" message (may differ from <paramref name="agentAlready"/>, e.g. Windsurf vs. Windsurf Cascade).</param>
    /// <param name="writeContext">The context prefix for a write-failure <see cref="InitAbortException"/>.</param>
    /// <param name="footerText">The fully pre-formatted success footer (only Kilo Code/Antigravity use <see cref="InitArtifacts.PrintDryRunFooter"/> instead, in dry-run).</param>
    /// <param name="explicitDryRunFooter">Whether this agent calls <see cref="InitArtifacts.PrintDryRunFooter"/> itself on dry-run (Kilo Code/Antigravity) rather than relying on an outer wrapper (Cline/Windsurf).</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    private static void RunRulesFile(
        string rulesPath,
        string rulesDisplay,
        string? parentDir,
        string rulesContent,
        string agentAlready,
        string agentConfigured,
        string writeContext,
        string footerText,
        bool explicitDryRunFooter,
        InitContext ctx)
    {
        var existing = File.Exists(rulesPath) ? File.ReadAllText(rulesPath) : string.Empty;
        var alreadyConfigured = existing.Contains("RTK", StringComparison.Ordinal) || existing.Contains("rtk", StringComparison.Ordinal);

        if (alreadyConfigured)
        {
            if (!ctx.DryRun)
            {
                Console.Out.Write($"\nRTK already configured for {agentAlready} in this project.\n\n");
                Console.Out.Write($"  Rules: {rulesDisplay} (already present)\n");
            }
        }
        else
        {
            var trimmedExisting = existing.Trim();
            var newContent = trimmedExisting.Length == 0 ? rulesContent : $"{trimmedExisting}\n\n{rulesContent}";

            if (ctx.DryRun)
            {
                Console.Out.Write(parentDir is null
                    ? $"[dry-run] would write {rulesDisplay}: {rulesPath}\n"
                    : $"[dry-run] would write {rulesPath}: (and create parent dir if missing)\n");
                if (ctx.Verbose > 0)
                {
                    Console.Out.Write($"[dry-run] content:\n{newContent}\n");
                }
            }
            else
            {
                if (parentDir is not null)
                {
                    Directory.CreateDirectory(parentDir);
                }

                try
                {
                    File.WriteAllText(rulesPath, newContent, Utf8NoBom);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InitAbortException($"{writeContext}: {ex.Message}");
                }

                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"Wrote {rulesDisplay}\n");
                }

                Console.Out.Write($"\nRTK configured for {agentConfigured}.\n\n");
                Console.Out.Write($"  Rules: {rulesDisplay} (installed)\n");
            }
        }

        if (explicitDryRunFooter && ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
        else if (!ctx.DryRun)
        {
            Console.Out.Write(footerText);
        }
    }

    /// <summary>
    /// Shared core of all four <c>Uninstall*</c> entry points (see this class's remarks: an original,
    /// non-oracle-derived addition). Strips the exact <paramref name="rulesContent"/> substring back
    /// out of <paramref name="path"/>, collapsing the surrounding blank-line gap, and deletes the file
    /// entirely if nothing else remains. If the file does not exist, or exists but does not contain
    /// the exact embedded block (e.g. it was hand-edited), reports nothing to remove rather than
    /// guessing at a partial match.
    /// </summary>
    /// <param name="path">The rules file path.</param>
    /// <param name="rulesContent">The embedded rules block originally written by the matching <c>Run*</c> method.</param>
    /// <param name="agentLabel">The agent name used in printed messages.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if anything was removed (or would be, in dry-run).</returns>
    private static bool UninstallRulesFile(string path, string rulesContent, string agentLabel, InitContext ctx)
    {
        if (!File.Exists(path))
        {
            Console.Out.Write($"RTK was not installed for {agentLabel} (nothing to remove)\n");
            return false;
        }

        var content = File.ReadAllText(path);
        var needle = rulesContent.Trim();
        var idx = content.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0)
        {
            Console.Out.Write($"RTK was not installed for {agentLabel} (nothing to remove)\n");
            return false;
        }

        var before = content[..idx].TrimEnd();
        var after = content[(idx + needle.Length)..].TrimStart();
        var remaining = (before.Length == 0, after.Length == 0) switch
        {
            (true, true) => string.Empty,
            (true, false) => after,
            (false, true) => before,
            (false, false) => $"{before}\n\n{after}",
        };

        if (ctx.DryRun)
        {
            Console.Out.Write(remaining.Length == 0
                ? $"[dry-run] would remove {path} (empty after cleanup)\n"
                : $"[dry-run] would update {path} (remove RTK rules)\n");
            return true;
        }

        if (remaining.Length == 0)
        {
            File.Delete(path);
            Console.Out.Write($"RTK uninstalled for {agentLabel}: removed {path}\n");
        }
        else
        {
            File.WriteAllText(path, remaining, Utf8NoBom);
            Console.Out.Write($"RTK uninstalled for {agentLabel}: removed rules from {path}\n");
        }

        return true;
    }
}
