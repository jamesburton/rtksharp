using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RtkSharp.Hooks;

/// <summary>
/// Codex CLI instruction-mode artifact writers for <c>rtk init --codex</c>. Faithful port of the
/// Codex subset of Rust <c>src/hooks/init.rs</c>: <c>run_codex_mode</c>/<c>run_codex_mode_with_paths</c>
/// (init.rs:2259-2326), <c>patch_agents_md</c> (init.rs:2538), <c>uninstall_codex</c>/
/// <c>uninstall_codex_at</c> (init.rs:849-929), <c>resolve_codex_dir</c>/<c>resolve_codex_dir_from</c>
/// (init.rs:2743-2761), <c>codex_rtk_md_ref</c> (init.rs:2780), and <c>show_codex_config</c>
/// (init.rs:3531).
/// </summary>
/// <remarks>
/// <para>
/// <b>Codex has no runtime hook and no settings.json patch.</b> Unlike Claude Code, Codex mode is
/// purely two idempotent file writes: <c>RTK.md</c> (the slim Codex-flavored awareness doc,
/// <see cref="RtkSlimCodex"/>) via <see cref="InitArtifacts.WriteIfChanged"/>, and an <c>@RTK.md</c>
/// (or, in global scope, an absolute <c>@{path}</c>) reference line appended to <c>AGENTS.md</c> via
/// <see cref="PatchAgentsMd"/> — <b>not</b> the marker-block upsert
/// (<see cref="InitArtifacts.UpsertRtkBlock"/>/<see cref="InitArtifacts.WriteRtkBlock"/>) that Claude
/// Code's <c>CLAUDE.md</c> path uses for its legacy full-block mode. <see cref="InitArtifacts.RemoveRtkBlock"/>
/// is reused here only for the one-time migration of a stale inline block a very old install may have
/// left behind in <c>AGENTS.md</c>.
/// </para>
/// <para>
/// <b>Codex supports global scope</b> via <c>rtk init -g --codex</c>, resolving
/// <c>$CODEX_HOME/AGENTS.md</c> + <c>$CODEX_HOME/RTK.md</c> (falling back to <c>~/.codex</c> when
/// <c>CODEX_HOME</c> is unset or empty) — confirmed by reading Rust's <c>run_codex_mode</c> dispatch
/// (init.rs:2259-2268), which is symmetric with Claude Code's global scope, not project-scope-only.
/// In global scope, the <c>@RTK.md</c> reference written to <c>AGENTS.md</c> is an <b>absolute</b>
/// path reference (<c>codex_rtk_md_ref</c>/<see cref="CodexRtkMdRef"/>) rather than the bare
/// <c>@RTK.md</c> used in project scope — issue #892 upstream: Codex resolves <c>@</c> references
/// relative to the current working directory, not the file's own location, so a relative reference
/// in the global <c>AGENTS.md</c> would silently fail to resolve from any CWD other than
/// <c>$CODEX_HOME</c> itself.
/// </para>
/// </remarks>
public static class CodexInit
{
    private const string AgentsMdFileName = "AGENTS.md";
    private const string RtkMdFileName = "RTK.md";

    /// <summary>Environment variable honored by <see cref="ResolveCodexDir"/> (Rust <c>CODEX_HOME</c>, init.rs:2745).</summary>
    private const string CodexHomeEnvVar = "CODEX_HOME";

    /// <summary>Fallback subdirectory of the user's home directory (Rust <c>CODEX_DIR</c> = <c>".codex"</c>).</summary>
    private const string CodexDirName = ".codex";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Slim RTK.md written by Codex-mode init. Byte-for-byte copy of Rust <c>RTK_SLIM_CODEX</c>
    /// (<c>include_str!("../../hooks/codex/rtk-awareness.md")</c>, init.rs:32), loaded from an
    /// embedded resource (see <c>RtkSharp.csproj</c>) rather than a C# string literal, the same way
    /// <see cref="InitArtifacts.RtkSlim"/> loads the Claude Code flavor — preserving the exact bytes
    /// (including whatever CRLF/LF the current checkout produced) of
    /// <c>hooks/codex/rtk-awareness.md</c>.
    /// </summary>
    public static readonly string RtkSlimCodex = LoadEmbeddedResource("RtkSharp.Hooks.rtk-awareness-codex.md");

    /// <summary>
    /// Loads an embedded resource's exact bytes (no line-ending normalization), decoded as UTF-8.
    /// </summary>
    /// <param name="logicalName">The embedded resource's logical name (see <c>RtkSharp.csproj</c>).</param>
    /// <returns>The exact file content.</returns>
    private static string LoadEmbeddedResource(string logicalName)
    {
        var assembly = typeof(CodexInit).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InitAbortException($"Embedded resource {logicalName} is missing");
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Resolves the Codex config directory: <c>$CODEX_HOME</c> if set and non-empty, else
    /// <c>{home}/.codex</c>. Port of Rust <c>resolve_codex_dir</c> (init.rs:2743), which delegates to
    /// <see cref="ResolveCodexDirFrom"/> (the pure, test-friendly core, ported as
    /// <c>resolve_codex_dir_from</c>, init.rs:2750).
    /// </summary>
    /// <returns>The resolved Codex config directory path.</returns>
    internal static string ResolveCodexDir() =>
        ResolveCodexDirFrom(
            Environment.GetEnvironmentVariable(CodexHomeEnvVar),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Pure core of <see cref="ResolveCodexDir"/>: prefers <paramref name="codexHome"/> (if
    /// non-empty), else falls back to <c>{homeDir}/.codex</c>. Port of Rust
    /// <c>resolve_codex_dir_from</c> (init.rs:2750).
    /// </summary>
    /// <param name="codexHome">The <c>$CODEX_HOME</c> value, or <see langword="null"/>/empty if unset.</param>
    /// <param name="homeDir">The user's home directory, or <see langword="null"/> if undeterminable.</param>
    /// <returns>The resolved Codex config directory path.</returns>
    internal static string ResolveCodexDirFrom(string? codexHome, string? homeDir)
    {
        if (!string.IsNullOrEmpty(codexHome))
        {
            return codexHome;
        }

        if (string.IsNullOrEmpty(homeDir))
        {
            throw new InitAbortException("Cannot determine Codex config directory. Set $CODEX_HOME or $HOME.");
        }

        return Path.Combine(homeDir, CodexDirName);
    }

    /// <summary>
    /// Builds the absolute <c>@{path}</c> reference used in global-scope <c>AGENTS.md</c> (issue
    /// #892: Codex resolves <c>@</c> references relative to CWD, not the file's location). Port of
    /// Rust <c>codex_rtk_md_ref</c> (init.rs:2780).
    /// </summary>
    /// <param name="codexDir">The Codex config directory containing <c>RTK.md</c>.</param>
    /// <returns>The absolute reference string, e.g. <c>@/home/user/.codex/RTK.md</c>.</returns>
    internal static string CodexRtkMdRef(string codexDir) => $"@{Path.Combine(codexDir, RtkMdFileName)}";

    /// <summary>
    /// Runs <c>rtk init --codex</c> (project scope) or <c>rtk init -g --codex</c> (global scope):
    /// resolves the target <c>AGENTS.md</c>/<c>RTK.md</c> paths and delegates to
    /// <see cref="RunWithPaths"/>. Port of Rust <c>run_codex_mode</c> (init.rs:2259).
    /// </summary>
    /// <param name="global">Whether this is the global-scope (<c>-g</c>) invocation.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void Run(bool global, InitContext ctx)
    {
        string agentsMdPath;
        string rtkMdPath;
        if (global)
        {
            var codexDir = ResolveCodexDir();
            agentsMdPath = Path.Combine(codexDir, AgentsMdFileName);
            rtkMdPath = Path.Combine(codexDir, RtkMdFileName);
        }
        else
        {
            agentsMdPath = AgentsMdFileName;
            rtkMdPath = RtkMdFileName;
        }

        RunWithPaths(agentsMdPath, rtkMdPath, global, ctx);
    }

    /// <summary>
    /// Writes the Codex <c>RTK.md</c> and patches <c>AGENTS.md</c> with the appropriate <c>@RTK.md</c>
    /// reference, then (outside dry-run) prints the success report. Port of Rust
    /// <c>run_codex_mode_with_paths</c> (init.rs:2270).
    /// </summary>
    /// <param name="agentsMdPath">The target <c>AGENTS.md</c> path.</param>
    /// <param name="rtkMdPath">The target <c>RTK.md</c> path.</param>
    /// <param name="global">Whether this is the global-scope invocation (controls directory creation and the absolute-vs-relative reference).</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void RunWithPaths(string agentsMdPath, string rtkMdPath, bool global, InitContext ctx)
    {
        if (global && !ctx.DryRun)
        {
            var parent = Path.GetDirectoryName(agentsMdPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        // ISSUE #892: in global mode, use an absolute path so @RTK.md resolves from any CWD
        // (worktrees, nested projects) — Codex resolves @ references relative to CWD, not the
        // AGENTS.md file location.
        var rtkMdRef = global
            ? CodexRtkMdRef(Path.GetDirectoryName(rtkMdPath)
                ?? throw new InitAbortException("RTK.md path missing parent directory"))
            : InitArtifacts.RtkMdRef;

        InitArtifacts.WriteIfChanged(rtkMdPath, RtkSlimCodex, RtkMdFileName, ctx);
        var addedRef = PatchAgentsMd(agentsMdPath, rtkMdRef, ctx);

        if (ctx.DryRun)
        {
            return;
        }

        Console.Out.Write("\nRTK configured for Codex CLI.\n\n");
        Console.Out.Write($"  RTK.md:    {rtkMdPath}\n");
        Console.Out.Write(addedRef
            ? $"  AGENTS.md: {rtkMdRef} reference added\n"
            : $"  AGENTS.md: {rtkMdRef} reference already present\n");
        Console.Out.Write(global
            ? $"\n  Codex global instructions path: {agentsMdPath}\n"
            : $"\n  Codex project instructions path: {agentsMdPath}\n");
    }

    /// <summary>
    /// Patches <c>AGENTS.md</c>: migrates a stale inline RTK block out first (if present), migrates a
    /// bare <c>@RTK.md</c> reference to the absolute form if <paramref name="rtkMdRef"/> requires it,
    /// and otherwise appends <paramref name="rtkMdRef"/> if no reference is present yet. Port of Rust
    /// <c>patch_agents_md</c> (init.rs:2538).
    /// </summary>
    /// <param name="path">The <c>AGENTS.md</c> path to patch.</param>
    /// <param name="rtkMdRef">The desired reference line (<c>@RTK.md</c> or an absolute <c>@{path}</c>).</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if a reference was added or migrated to the absolute form.</returns>
    internal static bool PatchAgentsMd(string path, string rtkMdRef, InitContext ctx)
    {
        var content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        var migrated = false;
        if (content.Contains(InitArtifacts.RtkBlockStart, StringComparison.Ordinal))
        {
            var (contentWithoutBlock, didMigrate) = InitArtifacts.RemoveRtkBlock(content);
            if (didMigrate)
            {
                content = contentWithoutBlock;
                migrated = true;
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write("Migrated: removed old RTK block from AGENTS.md\n");
                }
            }
        }

        // ISSUE #892: check for both relative and absolute @RTK.md references.
        if (content.Contains(InitArtifacts.RtkMdRef, StringComparison.Ordinal) ||
            content.Contains(rtkMdRef, StringComparison.Ordinal))
        {
            if (ctx.Verbose > 0)
            {
                Console.Error.Write($"{rtkMdRef} reference already present in AGENTS.md\n");
            }

            // ISSUE #892: migrate an old relative @RTK.md to the absolute path if needed.
            if (rtkMdRef != InitArtifacts.RtkMdRef &&
                content.Contains(InitArtifacts.RtkMdRef, StringComparison.Ordinal) &&
                !content.Contains(rtkMdRef, StringComparison.Ordinal))
            {
                content = content.Replace(InitArtifacts.RtkMdRef, rtkMdRef, StringComparison.Ordinal);
                if (ctx.DryRun)
                {
                    Console.Out.Write($"[dry-run] would migrate {InitArtifacts.RtkMdRef} to {rtkMdRef} in {path}\n");
                }
                else
                {
                    InitArtifacts.AtomicWrite(path, content);
                    if (ctx.Verbose > 0)
                    {
                        Console.Error.Write($"Migrated {InitArtifacts.RtkMdRef} to {rtkMdRef}\n");
                    }
                }

                return true;
            }

            if (migrated)
            {
                if (ctx.DryRun)
                {
                    Console.Out.Write($"[dry-run] would write migrated AGENTS.md: {path}\n");
                }
                else
                {
                    InitArtifacts.AtomicWrite(path, content);
                }
            }

            return false;
        }

        var newContent = content.Length == 0 ? $"{rtkMdRef}\n" : $"{content.Trim()}\n\n{rtkMdRef}\n";

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would add {rtkMdRef} reference to AGENTS.md: {path}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{newContent}\n");
            }
        }
        else
        {
            InitArtifacts.AtomicWrite(path, newContent);
            if (ctx.Verbose > 0)
            {
                Console.Error.Write($"Added {rtkMdRef} reference to AGENTS.md\n");
            }
        }

        return true;
    }

    /// <summary>
    /// Returns whether any line of <paramref name="content"/>, trimmed, exactly equals one of
    /// <paramref name="refs"/>. Port of Rust <c>has_rtk_reference</c> (init.rs:2624).
    /// </summary>
    /// <param name="content">The content to scan.</param>
    /// <param name="refs">The candidate reference strings.</param>
    /// <returns><see langword="true"/> if a matching line was found.</returns>
    internal static bool HasRtkReference(string content, params string[] refs) =>
        InitArtifacts.SplitRustLines(content).Select(line => line.Trim()).Any(refs.Contains);

    /// <summary>
    /// Removes every line of <paramref name="path"/> that (trimmed) exactly matches one of
    /// <paramref name="refs"/>, collapsing resulting blank-line runs. Port of Rust
    /// <c>remove_rtk_reference_from_agents</c> (init.rs:2631).
    /// </summary>
    /// <param name="path">The <c>AGENTS.md</c> path.</param>
    /// <param name="refs">The reference strings to remove (bare and/or absolute forms).</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if a reference was found and removed (or would be, in dry-run).</returns>
    internal static bool RemoveRtkReferenceFromAgents(string path, string[] refs, InitContext ctx)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var content = File.ReadAllText(path);
        if (!HasRtkReference(content, refs))
        {
            return false;
        }

        var newContent = string.Join("\n", InitArtifacts.SplitRustLines(content).Where(line => !refs.Contains(line.Trim())));
        var cleaned = InitArtifacts.CleanDoubleBlanks(newContent);

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would remove RTK.md reference from AGENTS.md: {path}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{cleaned}\n");
            }

            return true;
        }

        InitArtifacts.AtomicWrite(path, cleaned);
        if (ctx.Verbose > 0)
        {
            Console.Error.Write($"Removed RTK.md reference from AGENTS.md: {path}\n");
        }

        return true;
    }

    /// <summary>
    /// Full uninstall for global-scope Codex artifacts: removes <c>RTK.md</c> and cleans
    /// <c>AGENTS.md</c> (stale inline block and/or reference line). Port of Rust
    /// <c>uninstall_codex</c> (init.rs:849). Project scope is not supported (matches Rust's own
    /// <c>!global</c> bail here).
    /// </summary>
    /// <param name="global">Must be <see langword="true"/>; otherwise this throws.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void Uninstall(bool global, InitContext ctx)
    {
        if (!global)
        {
            throw new InitAbortException(
                "Uninstall only works with --global flag. For local projects, manually remove RTK from AGENTS.md");
        }

        var codexDir = ResolveCodexDir();
        var removed = UninstallCodexAt(codexDir, ctx);

        if (removed.Count == 0)
        {
            Console.Out.Write("RTK was not installed for Codex CLI (nothing to remove)\n");
            return;
        }

        var header = ctx.DryRun ? "[dry-run] would uninstall RTK for Codex CLI:" : "RTK uninstalled for Codex CLI:";
        Console.Out.Write($"{header}\n");
        foreach (var item in removed)
        {
            Console.Out.Write($"  - {item}\n");
        }
    }

    /// <summary>
    /// Removes <c>RTK.md</c> and cleans <c>AGENTS.md</c> under <paramref name="codexDir"/>. Port of
    /// Rust <c>uninstall_codex_at</c> (init.rs:877).
    /// </summary>
    /// <remarks>
    /// The stale-inline-block removal step below deliberately does <b>not</b> check
    /// <c>ctx.DryRun</c> before writing — this mirrors an apparent inconsistency in the Rust oracle
    /// itself (init.rs:913-917 unconditionally calls <c>atomic_write</c> here, unlike every other step
    /// in this function and unlike <see cref="RemoveRtkReferenceFromAgents"/>'s own dry-run guard).
    /// Byte-exact ground-truth parity takes precedence over "fixing" what looks like an oracle quirk.
    /// </remarks>
    /// <param name="codexDir">The Codex config directory.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns>The list of human-readable descriptions of what was removed (or would be, in dry-run).</returns>
    internal static List<string> UninstallCodexAt(string codexDir, InitContext ctx)
    {
        var removed = new List<string>();
        var absoluteRtkMdRef = CodexRtkMdRef(codexDir);

        var rtkMdPath = Path.Combine(codexDir, RtkMdFileName);
        if (File.Exists(rtkMdPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove RTK.md: {rtkMdPath}\n");
            }
            else
            {
                File.Delete(rtkMdPath);
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"Removed RTK.md: {rtkMdPath}\n");
                }
            }

            removed.Add($"RTK.md: {rtkMdPath}");
        }

        var agentsMdPath = Path.Combine(codexDir, AgentsMdFileName);
        if (File.Exists(agentsMdPath))
        {
            var working = File.ReadAllText(agentsMdPath);
            var changed = false;

            if (working.Contains(InitArtifacts.RtkBlockStart, StringComparison.Ordinal))
            {
                var (cleaned, didRemove) = InitArtifacts.RemoveRtkBlock(working);
                if (didRemove)
                {
                    working = cleaned;
                    changed = true;
                    removed.Add("AGENTS.md: removed rtk-instructions block");
                }
            }

            if (changed)
            {
                // See this method's <remarks>: Rust does not gate this write on dry_run.
                InitArtifacts.AtomicWrite(agentsMdPath, working);
            }
        }

        if (RemoveRtkReferenceFromAgents(agentsMdPath, [InitArtifacts.RtkMdRef, absoluteRtkMdRef], ctx))
        {
            removed.Add("AGENTS.md: removed @RTK.md reference");
        }

        return removed;
    }

    /// <summary>
    /// Prints current Codex CLI configuration status (both global and local scope, unconditionally).
    /// Port of Rust <c>show_codex_config</c> (init.rs:3531).
    /// </summary>
    public static void ShowConfig()
    {
        var codexDir = ResolveCodexDir();
        var globalAgentsMd = Path.Combine(codexDir, AgentsMdFileName);
        var globalRtkMd = Path.Combine(codexDir, RtkMdFileName);
        var globalRtkMdRef = CodexRtkMdRef(codexDir);
        const string localAgentsMd = AgentsMdFileName;
        const string localRtkMd = RtkMdFileName;

        Console.Out.Write("rtk Configuration (Codex CLI):\n\n");

        Console.Out.Write(File.Exists(globalRtkMd)
            ? $"[ok] Global RTK.md: {globalRtkMd}\n"
            : "[--] Global RTK.md: not found\n");

        if (File.Exists(globalAgentsMd))
        {
            var content = File.ReadAllText(globalAgentsMd);
            if (HasRtkReference(content, InitArtifacts.RtkMdRef, globalRtkMdRef))
            {
                Console.Out.Write("[ok] Global AGENTS.md: RTK.md reference\n");
            }
            else if (content.Contains(InitArtifacts.RtkBlockStart, StringComparison.Ordinal))
            {
                Console.Out.Write("[!!] Global AGENTS.md: old inline RTK block\n");
            }
            else
            {
                Console.Out.Write("[--] Global AGENTS.md: exists but rtk not configured\n");
            }
        }
        else
        {
            Console.Out.Write("[--] Global AGENTS.md: not found\n");
        }

        Console.Out.Write(File.Exists(localRtkMd)
            ? $"[ok] Local RTK.md: {localRtkMd}\n"
            : "[--] Local RTK.md: not found\n");

        if (File.Exists(localAgentsMd))
        {
            var content = File.ReadAllText(localAgentsMd);
            if (HasRtkReference(content, InitArtifacts.RtkMdRef))
            {
                Console.Out.Write("[ok] Local AGENTS.md: @RTK.md reference\n");
            }
            else if (content.Contains(InitArtifacts.RtkBlockStart, StringComparison.Ordinal))
            {
                Console.Out.Write("[!!] Local AGENTS.md: old inline RTK block\n");
            }
            else
            {
                Console.Out.Write("[--] Local AGENTS.md: exists but rtk not configured\n");
            }
        }
        else
        {
            Console.Out.Write("[--] Local AGENTS.md: not found\n");
        }

        Console.Out.Write("\nUsage:\n");
        Console.Out.Write("  rtk init --codex              # Configure local AGENTS.md + RTK.md\n");
        Console.Out.Write("  rtk init -g --codex           # Configure $CODEX_HOME/AGENTS.md + $CODEX_HOME/RTK.md (or ~/.codex/)\n");
        Console.Out.Write("  rtk init -g --codex --uninstall  # Remove global Codex RTK artifacts\n");
    }
}
