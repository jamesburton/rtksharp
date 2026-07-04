using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace RtkSharp.Hooks;

/// <summary>
/// Control flow for <c>settings.json</c> patching during <c>rtk init</c>. Mirrors Rust
/// <c>PatchMode</c> (init.rs:76). Only <see cref="Ask"/> vs <see cref="Auto"/> vs <see cref="Skip"/>
/// selection is ported here; the settings.json patcher itself lands in a follow-up global-scope
/// init task.
/// </summary>
public enum PatchMode
{
    /// <summary>Default: prompt the user <c>[y/N]</c> before patching.</summary>
    Ask,

    /// <summary><c>--auto-patch</c>: patch without prompting.</summary>
    Auto,

    /// <summary><c>--no-patch</c>: never patch; print manual instructions instead.</summary>
    Skip,
}

/// <summary>
/// Shared context threaded through every init/uninstall operation. Mirrors Rust
/// <c>InitContext</c> (init.rs:98): replaces ad-hoc <c>verbose</c>/<c>dry_run</c> parameter pairs.
/// </summary>
/// <param name="Verbose">
/// Verbosity level (mirrors Rust's <c>u8</c> counted from <c>-v</c>/<c>-vv</c>/<c>-vvv</c>).
/// </param>
/// <param name="DryRun">
/// When <see langword="true"/>, primitives print the action they would take and touch no files.
/// </param>
public readonly record struct InitContext(int Verbose = 0, bool DryRun = false)
{
    /// <summary>The zero-value context: not verbose, not a dry run.</summary>
    public static readonly InitContext Default = new();
}

/// <summary>
/// Outcome of <see cref="InitArtifacts.UpsertRtkBlock"/>: describes what happened (or would need to
/// happen) to the marker-delimited RTK block in a file's content. Mirrors Rust
/// <c>RtkBlockUpsert</c> (init.rs:2330).
/// </summary>
internal enum RtkBlockUpsert
{
    /// <summary>No existing block was found — the desired block was appended.</summary>
    Added,

    /// <summary>An existing block was found with different content — it was replaced.</summary>
    Updated,

    /// <summary>An existing block was found with identical content — no-op.</summary>
    Unchanged,

    /// <summary>An opening marker was found without a matching closing marker — unsafe to rewrite.</summary>
    Malformed,
}

/// <summary>
/// Thrown by init primitives for the Rust <c>anyhow::bail!</c> fail-loud contract: a
/// non-recoverable abort carrying a fully-formed diagnostic message. <see cref="InitCommand"/>
/// catches this (and any other exception) at the top level and prints
/// <c>rtk: {message}\n</c> to stderr with exit code 1, mirroring <c>main.rs</c>'s
/// <c>eprintln!("rtk: {:#}", e); 1</c> handling of a propagated <c>anyhow::Error</c>.
/// </summary>
/// <param name="message">The diagnostic message (printed verbatim after the <c>rtk: </c> prefix).</param>
internal sealed class InitAbortException(string message) : Exception(message);

/// <summary>
/// File-mutation primitives and embedded templates for <c>rtk init</c>. Faithful port of the
/// idempotent-write core of Rust <c>src/hooks/init.rs</c>: atomic writes, the RTK marker-block
/// upsert (insert/replace/strip, including the "refuse to touch a malformed block" contract),
/// and the four embedded template constants. <see cref="InitCommand"/> is the verb entry point and
/// mode dispatcher built on top of these primitives; this file is deliberately dispatch-free so the
/// two concerns stay independently reviewable.
/// </summary>
public static class InitArtifacts
{
    /// <summary>Opening marker for an RTK-owned block (Rust <c>RTK_BLOCK_START</c>, init.rs:71).</summary>
    internal const string RtkBlockStart = "<!-- rtk-instructions";

    /// <summary>Closing marker for an RTK-owned block (Rust <c>RTK_BLOCK_END</c>, init.rs:72).</summary>
    internal const string RtkBlockEnd = "<!-- /rtk-instructions -->";

    /// <summary>The <c>@RTK.md</c> reference line injected by <c>patch_claude_md</c> (init.rs:68).</summary>
    internal const string RtkMdRef = "@RTK.md";

    /// <summary>
    /// Test/parity-only override for the global config directory (the .NET equivalent of Rust's
    /// <c>dirs::config_dir()</c>), consulted by <see cref="ResolveGlobalConfigDir"/> before falling
    /// back to the real OS config directory. This is <b>never a user-facing flag</b> — it exists
    /// solely so parity/unit tests can redirect the global <c>filters.toml</c> template path
    /// deterministically on every OS, including Windows, where <c>dirs::config_dir()</c> resolves
    /// via a Win32 known-folder API that (confirmed empirically against the Rust oracle) ignores an
    /// overridden <c>%APPDATA%</c> process environment variable. Do not read this from user-facing
    /// documentation or CLI help text.
    /// </summary>
    internal const string ConfigDirOverrideEnvVar = "RTK_CONFIG_DIR_OVERRIDE";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Template written by <c>rtk init</c> to <c>.rtk/filters.toml</c> when none exists yet.
    /// Byte-for-byte copy of Rust <c>FILTERS_TEMPLATE</c> (init.rs:35), normalized to a single
    /// trailing <c>\n</c> (rustc's raw-string lexer already normalizes any CRLF in the source file
    /// to LF at compile time, regardless of the checkout's line-ending settings, so this is safe on
    /// every OS/checkout).
    /// </summary>
    public static readonly string FiltersTemplate = NormalizeTemplate(RawFiltersTemplate);

    /// <summary>
    /// Template for the user-global <c>~/.config/rtk/filters.toml</c> (or platform equivalent).
    /// Byte-for-byte copy of Rust <c>FILTERS_GLOBAL_TEMPLATE</c> (init.rs:51). Not written by any
    /// Task-1 code path (that is <c>generate_global_filters_template</c>'s job, part of the
    /// global-scope init task) — embedded here now per the primitives split so downstream tasks
    /// reuse the same constant.
    /// </summary>
    public static readonly string FiltersGlobalTemplate = NormalizeTemplate(RawFiltersGlobalTemplate);

    /// <summary>
    /// Legacy full RTK instructions block injected into <c>CLAUDE.md</c> by <c>--claude-md</c> mode
    /// (and by default project-scope init — see <see cref="InitCommand"/>). Byte-for-byte copy of
    /// Rust <c>RTK_INSTRUCTIONS</c> (init.rs:109), normalized the same way as
    /// <see cref="FiltersTemplate"/>.
    /// </summary>
    public static readonly string RtkInstructions = NormalizeTemplate(RawRtkInstructions);

    /// <summary>
    /// Slim RTK.md written by global-scope default-mode init. Byte-for-byte copy of Rust
    /// <c>RTK_SLIM</c> (<c>include_str!("../../hooks/claude/rtk-awareness.md")</c>, init.rs:31),
    /// loaded from an embedded resource (see <c>RtkSharp.csproj</c>) rather than a C# string literal
    /// so it tracks the exact bytes of <c>hooks/claude/rtk-awareness.md</c> in the current checkout —
    /// including whatever CRLF/LF `core.autocrlf` produced — the same way Rust's <c>include_str!</c>
    /// does not normalize line endings for external files (unlike inline raw-string literals). Not
    /// written by any Task-1 code path; embedded now for reuse by the global-scope init task.
    /// </summary>
    public static readonly string RtkSlim = LoadRtkSlim();

    private const string RawFiltersTemplate = """
        # Project-local RTK filters — commit this file with your repo.
        # Filters here override user-global and built-in filters.
        # Docs: https://github.com/rtk-ai/rtk#custom-filters
        schema_version = 1

        # Example: suppress build noise from a custom tool
        # [filters.my-tool]
        # description = "Compact my-tool output"
        # match_command = "^my-tool\\s+build"
        # strip_ansi = true
        # strip_lines_matching = ["^\\s*$", "^Downloading", "^Installing"]
        # max_lines = 30
        # on_empty = "my-tool: ok"
        """;

    private const string RawFiltersGlobalTemplate = """
        # User-global RTK filters — apply to all your projects.
        # Project-local .rtk/filters.toml takes precedence over these.
        # Docs: https://github.com/rtk-ai/rtk#custom-filters
        schema_version = 1

        # Example: suppress noise from a tool you use everywhere
        # [filters.my-global-tool]
        # description = "Compact my-global-tool output"
        # match_command = "^my-global-tool\\b"
        # strip_ansi = true
        # strip_lines_matching = ["^\\s*$"]
        # max_lines = 40
        """;

    private const string RawRtkInstructions = """
        <!-- rtk-instructions v2 -->
        # RTK (Rust Token Killer) - Token-Optimized Commands

        ## Golden Rule

        **Always prefix commands with `rtk`**. If RTK has a dedicated filter, it uses it. If not, it passes through unchanged. This means RTK is always safe to use.

        **Important**: Even in command chains with `&&`, use `rtk`:
        ```bash
        # ❌ Wrong
        git add . && git commit -m "msg" && git push

        # ✅ Correct
        rtk git add . && rtk git commit -m "msg" && rtk git push
        ```

        ## RTK Commands by Workflow

        ### Build & Compile (80-90% savings)
        ```bash
        rtk cargo build         # Cargo build output
        rtk cargo check         # Cargo check output
        rtk cargo clippy        # Clippy warnings grouped by file (80%)
        rtk tsc                 # TypeScript errors grouped by file/code (83%)
        rtk lint                # ESLint/Biome violations grouped (84%)
        rtk prettier --check    # Files needing format only (70%)
        rtk next build          # Next.js build with route metrics (87%)
        ```

        ### Test (60-99% savings)
        ```bash
        rtk cargo test          # Cargo test failures only (90%)
        rtk go test             # Go test failures only (90%)
        rtk jest                # Jest failures only (99.5%)
        rtk vitest              # Vitest failures only (99.5%)
        rtk playwright test     # Playwright failures only (94%)
        rtk pytest              # Python test failures only (90%)
        rtk rake test           # Ruby test failures only (90%)
        rtk rspec               # RSpec test failures only (60%)
        rtk test <cmd>          # Generic test wrapper - failures only
        ```

        ### Git (59-80% savings)
        ```bash
        rtk git status          # Compact status
        rtk git log             # Compact log (works with all git flags)
        rtk git diff            # Compact diff (80%)
        rtk git show            # Compact show (80%)
        rtk git add             # Ultra-compact confirmations (59%)
        rtk git commit          # Ultra-compact confirmations (59%)
        rtk git push            # Ultra-compact confirmations
        rtk git pull            # Ultra-compact confirmations
        rtk git branch          # Compact branch list
        rtk git fetch           # Compact fetch
        rtk git stash           # Compact stash
        rtk git worktree        # Compact worktree
        ```

        Note: Git passthrough works for ALL subcommands, even those not explicitly listed.

        ### GitHub (26-87% savings)
        ```bash
        rtk gh pr view <num>    # Compact PR view (87%)
        rtk gh pr checks        # Compact PR checks (79%)
        rtk gh run list         # Compact workflow runs (82%)
        rtk gh issue list       # Compact issue list (80%)
        rtk gh api              # Compact API responses (26%)
        ```

        ### JavaScript/TypeScript Tooling (70-90% savings)
        ```bash
        rtk pnpm list           # Compact dependency tree (70%)
        rtk pnpm outdated       # Compact outdated packages (80%)
        rtk pnpm install        # Compact install output (90%)
        rtk npm run <script>    # Compact npm script output
        rtk npx <cmd>           # Compact npx command output
        rtk prisma              # Prisma without ASCII art (88%)
        ```

        ### Files & Search (60-75% savings)
        ```bash
        rtk ls <path>           # Tree format, compact (65%)
        rtk read <file>         # Code reading with filtering (60%)
        rtk grep <pattern>      # Search grouped by file (75%). Format flags (-c, -l, -L, -o, -Z) run raw.
        rtk find <pattern>      # Find grouped by directory (70%)
        ```

        ### Analysis & Debug (70-90% savings)
        ```bash
        rtk err <cmd>           # Filter errors only from any command
        rtk log <file>          # Deduplicated logs with counts
        rtk json <file>         # JSON structure without values
        rtk deps                # Dependency overview
        rtk env                 # Environment variables compact
        rtk summary <cmd>       # Smart summary of command output
        rtk diff                # Ultra-compact diffs
        ```

        ### Infrastructure (85% savings)
        ```bash
        rtk docker ps           # Compact container list
        rtk docker images       # Compact image list
        rtk docker logs <c>     # Deduplicated logs
        rtk kubectl get         # Compact resource list
        rtk kubectl logs        # Deduplicated pod logs
        ```

        ### Network (65-70% savings)
        ```bash
        rtk curl <url>          # Compact HTTP responses (70%)
        rtk wget <url>          # Compact download output (65%)
        ```

        ### Meta Commands
        ```bash
        rtk gain                # View token savings statistics
        rtk gain --history      # View command history with savings
        rtk discover            # Analyze Claude Code sessions for missed RTK usage
        rtk proxy <cmd>         # Run command without filtering (for debugging)
        rtk init                # Add RTK instructions to CLAUDE.md
        rtk init --global       # Add RTK to ~/.claude/CLAUDE.md
        ```

        ## Token Savings Overview

        | Category | Commands | Typical Savings |
        |----------|----------|-----------------|
        | Tests | vitest, playwright, cargo test | 90-99% |
        | Build | next, tsc, lint, prettier | 70-87% |
        | Git | status, log, diff, add, commit | 59-80% |
        | GitHub | gh pr, gh run, gh issue | 26-87% |
        | Package Managers | pnpm, npm, npx | 70-90% |
        | Files | ls, read, grep, find | 60-75% |
        | Infrastructure | docker, kubectl | 85% |
        | Network | curl, wget | 65-70% |

        Overall average: **60-90% token reduction** on common development operations.
        <!-- /rtk-instructions -->
        """;

    /// <summary>
    /// Normalizes an embedded raw-string template to exactly one trailing <c>\n</c> with LF-only
    /// line endings, regardless of how the C# raw string literal captured trailing whitespace or
    /// what EOL style the source file happened to have on disk. Mirrors the fact that rustc's
    /// string-literal lexer normalizes CRLF to LF for inline raw strings (verified empirically: the
    /// oracle's compiled output for these three constants is always LF-only even when
    /// <c>src/hooks/init.rs</c> itself is checked out with CRLF).
    /// </summary>
    /// <param name="raw">The raw template text as captured by the C# raw string literal.</param>
    /// <returns>The template with LF line endings and exactly one trailing newline.</returns>
    private static string NormalizeTemplate(string raw) =>
        raw.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    /// <summary>
    /// Loads <see cref="RtkSlim"/> from the embedded resource copy of
    /// <c>hooks/claude/rtk-awareness.md</c>, preserving its exact bytes (no line-ending
    /// normalization — see <see cref="RtkSlim"/>'s remarks).
    /// </summary>
    /// <returns>The exact file content, decoded as UTF-8.</returns>
    private static string LoadRtkSlim()
    {
        var assembly = typeof(InitArtifacts).Assembly;
        using var stream = assembly.GetManifestResourceStream("RtkSharp.Hooks.rtk-awareness.md")
            ?? throw new InitAbortException("Embedded resource RtkSharp.Hooks.rtk-awareness.md is missing");
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Resolves the global config directory — the .NET equivalent of Rust's
    /// <c>dirs::config_dir()</c> (<c>%APPDATA%</c> on Windows, <c>~/Library/Application Support</c>
    /// on macOS, <c>$XDG_CONFIG_HOME</c> or <c>~/.config</c> on Linux) — honoring
    /// <see cref="ConfigDirOverrideEnvVar"/> first. See that constant's remarks: the override is
    /// test/parity-only plumbing, never a user-facing flag.
    /// </summary>
    /// <returns>The resolved global config directory path.</returns>
    internal static string ResolveGlobalConfigDir()
    {
        var overridden = Environment.GetEnvironmentVariable(ConfigDirOverrideEnvVar);
        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden;
        }

        if (OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return xdg;
        }

        var linuxHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(linuxHome, ".config");
    }

    /// <summary>
    /// Idempotent file write: creates or updates <paramref name="path"/> if its content differs
    /// from <paramref name="content"/>. In dry-run mode, prints the intended action to stdout and
    /// touches no files. Port of Rust <c>write_if_changed</c> (init.rs:343).
    /// </summary>
    /// <param name="path">The file path to write.</param>
    /// <param name="content">The desired file content.</param>
    /// <param name="name">A human-readable label used in printed messages (e.g. <c>"RTK.md"</c>).</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if the file was created/updated (or would be, in dry-run).</returns>
    internal static bool WriteIfChanged(string path, string content, string name, InitContext ctx)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            if (existing == content)
            {
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"{name} already up to date: {path}\n");
                }

                return false;
            }

            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would update {name}: {path}\n");
                if (ctx.Verbose > 0)
                {
                    Console.Out.Write($"[dry-run] content:\n{content}\n");
                }
            }
            else
            {
                AtomicWrite(path, content);
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"Updated {name}: {path}\n");
                }
            }

            return true;
        }

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would create {name}: {path}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{content}\n");
            }
        }
        else
        {
            AtomicWrite(path, content);
            if (ctx.Verbose > 0)
            {
                Console.Error.Write($"Created {name}: {path}\n");
            }
        }

        return true;
    }

    /// <summary>
    /// Atomically writes <paramref name="content"/> to <paramref name="path"/> via a temp file in
    /// the same directory followed by a rename, preventing truncation/corruption on crash or
    /// interrupt. Port of Rust <c>atomic_write</c> (init.rs:395).
    /// </summary>
    /// <remarks>
    /// Rust's <c>resolve_atomic_target</c> follows symlinks via <c>fs::canonicalize</c> so the
    /// rename lands on the real file and the symlink itself is preserved. .NET has no equivalent
    /// symlink-follow-then-atomic-rename API pair, so this port takes the brief's allowed
    /// best-effort shortcut: <see cref="Path.GetFullPath(string)"/> resolution followed by
    /// <see cref="File.Replace(string, string, string?)"/> (when the target exists) or
    /// <see cref="File.Move(string, string)"/> (when it does not). A broken/missing symlink target
    /// falls back to writing the literal path.
    /// </remarks>
    /// <param name="path">The destination file path.</param>
    /// <param name="content">The content to write.</param>
    internal static void AtomicWrite(string path, string content)
    {
        var target = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InitAbortException($"Cannot write to {target}: path has no parent directory");
        }

        var tempFile = Path.Combine(parent, $".rtk-tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempFile, content, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InitAbortException($"Failed to create temp file in {parent}: {ex.Message}");
        }

        try
        {
            if (File.Exists(target))
            {
                File.Replace(tempFile, target, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempFile, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempFile);
            throw new InitAbortException($"Failed to atomically replace {target} (disk full?): {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp file; the AtomicWrite failure is already surfaced.
        }
    }

    /// <summary>
    /// Inserts or replaces the RTK instructions block in <paramref name="content"/>. Port of Rust
    /// <c>upsert_rtk_block</c> (init.rs:2346). Pure function: the caller decides whether/how to
    /// persist the result based on the returned action.
    /// </summary>
    /// <param name="content">The existing file content (empty string if the file does not exist).</param>
    /// <param name="block">The desired block content (including its start/end markers).</param>
    /// <returns>The new content and the action that was taken.</returns>
    internal static (string Content, RtkBlockUpsert Action) UpsertRtkBlock(string content, string block)
    {
        var startIdx = content.IndexOf(RtkBlockStart, StringComparison.Ordinal);
        if (startIdx >= 0)
        {
            var endIdx = content.IndexOf(RtkBlockEnd, startIdx, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                // Opening marker without a closing marker — malformed, refuse to touch.
                return (content, RtkBlockUpsert.Malformed);
            }

            var endPos = endIdx + RtkBlockEnd.Length;
            var currentBlock = content[startIdx..endPos].Trim();
            var desiredBlock = block.Trim();

            if (currentBlock == desiredBlock)
            {
                return (content, RtkBlockUpsert.Unchanged);
            }

            var before = content[..startIdx].TrimEnd();
            var after = content[endPos..].TrimStart();

            var result = (before.Length == 0, after.Length == 0) switch
            {
                (true, true) => desiredBlock,
                (true, false) => $"{desiredBlock}\n\n{after}",
                (false, true) => $"{before}\n\n{desiredBlock}",
                (false, false) => $"{before}\n\n{desiredBlock}\n\n{after}",
            };

            return (result, RtkBlockUpsert.Updated);
        }

        var trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return (block, RtkBlockUpsert.Added);
        }

        return ($"{trimmed}\n\n{block.Trim()}", RtkBlockUpsert.Added);
    }

    /// <summary>
    /// Idempotently writes an RTK-owned marker block into <paramref name="path"/>, preserving any
    /// surrounding user content. Reads the file (if any), passes it through
    /// <see cref="UpsertRtkBlock"/>, and writes the result via <see cref="AtomicWrite"/>. Port of
    /// Rust <c>write_rtk_block</c> (init.rs:2403).
    /// </summary>
    /// <remarks>
    /// <b>Fail-loud contract:</b> when the existing file has an opening marker without a matching
    /// closing marker, this method does <b>not</b> silently skip or overwrite — it prints the exact
    /// diagnostic (warning, line number, recovery hint) to stderr and throws
    /// <see cref="InitAbortException"/>, which <see cref="InitCommand"/> surfaces as a non-zero
    /// exit. This mirrors Rust's <c>anyhow::bail!</c> in the <c>RtkBlockUpsert::Malformed</c> arm —
    /// it is not the "never block the user" runtime-hook fallback pattern; <c>init</c> is a user
    /// command, and correctness here matters more than availability.
    /// </remarks>
    /// <param name="path">The file to upsert the block into.</param>
    /// <param name="block">The desired block content.</param>
    /// <param name="label">A human-readable label for printed messages (e.g. <c>"rtk instructions"</c>).</param>
    /// <param name="recoveryCmd">The command to suggest re-running after manual cleanup of a malformed block.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns>The <see cref="RtkBlockUpsert"/> action taken, so callers can branch on whether anything changed.</returns>
    internal static RtkBlockUpsert WriteRtkBlock(string path, string block, string label, string recoveryCmd, InitContext ctx)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var (newContent, action) = UpsertRtkBlock(existing, block);

        switch (action)
        {
            case RtkBlockUpsert.Added:
                if (ctx.DryRun)
                {
                    Console.Out.Write($"[dry-run] would add {label} to {path}\n");
                }
                else
                {
                    AtomicWrite(path, newContent);
                    Console.Out.Write($"[ok] Added {label} to {path}\n");
                }

                break;

            case RtkBlockUpsert.Updated:
                if (ctx.DryRun)
                {
                    Console.Out.Write($"[dry-run] would update {label} in {path}\n");
                }
                else
                {
                    AtomicWrite(path, newContent);
                    Console.Out.Write($"[ok] Updated {label} in {path}\n");
                }

                break;

            case RtkBlockUpsert.Unchanged:
                if (!ctx.DryRun)
                {
                    Console.Out.Write($"[ok] {label} already up to date in {path}\n");
                }

                break;

            case RtkBlockUpsert.Malformed:
                Console.Error.Write($"[warn] Found '{RtkBlockStart}' without closing marker in {path}\n");
                var lineNumber = FindLineNumber(existing, RtkBlockStart);
                if (lineNumber is { } n)
                {
                    Console.Error.Write($"    Location: line {n}\n");
                }

                Console.Error.Write("    Action: Manually remove the incomplete block, then re-run:\n");
                Console.Error.Write($"            {recoveryCmd}\n");
                throw new InitAbortException($"Refusing to modify malformed {label} at {path}");

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unhandled RtkBlockUpsert action");
        }

        return action;
    }

    /// <summary>
    /// Removes an old RTK block from <paramref name="content"/> (the <c>CLAUDE.md</c>/<c>AGENTS.md</c>
    /// migration helper). Port of Rust <c>remove_rtk_block</c> (init.rs:2678).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="WriteRtkBlock"/>, a malformed block here is <b>not</b> a hard failure: this
    /// is the softer migration-detection path (used by <c>patch_claude_md</c>), which only warns to
    /// stderr and returns the content unchanged with <c>DidRemove = false</c> — exactly mirroring
    /// Rust, which does not <c>bail!</c> in this function.
    /// </remarks>
    /// <param name="content">The file content to scan.</param>
    /// <returns>The (possibly unchanged) content, and whether a block was actually removed.</returns>
    internal static (string Content, bool DidRemove) RemoveRtkBlock(string content)
    {
        var startIdx = content.IndexOf(RtkBlockStart, StringComparison.Ordinal);
        var endIdx = content.IndexOf(RtkBlockEnd, StringComparison.Ordinal);

        if (startIdx >= 0 && endIdx >= 0)
        {
            var endPos = endIdx + RtkBlockEnd.Length;
            var before = content[..startIdx].TrimEnd();
            var after = content[endPos..].TrimStart();

            var result = after.Length == 0 ? $"{before}\n" : $"{before}\n\n{after}";
            return (result, true);
        }

        if (startIdx >= 0)
        {
            Console.Error.Write($"[warn] Warning: Found '{RtkBlockStart}' without closing marker.\n");
            Console.Error.Write("    This can happen if CLAUDE.md was manually edited.\n");
            var lineNumber = FindLineNumber(content, RtkBlockStart);
            if (lineNumber is { } n)
            {
                Console.Error.Write($"    Location: line {n}\n");
            }

            Console.Error.Write("    Action: Manually remove the incomplete block, then re-run:\n");
            Console.Error.Write("            rtk init -g\n");
            return (content, false);
        }

        return (content, false);
    }

    /// <summary>
    /// Patches <c>CLAUDE.md</c>: adds the <c>@RTK.md</c> reference line, migrating an old inline
    /// RTK block out first if one is present. Port of Rust <c>patch_claude_md</c> (init.rs:2471).
    /// Not called by any Task-1 dispatch path (that only happens in global-scope default-mode init);
    /// exposed and unit-tested here as a primitive for reuse by the global-scope init task.
    /// </summary>
    /// <param name="path">The <c>CLAUDE.md</c> path to patch.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if an old block was migrated out.</returns>
    internal static bool PatchClaudeMd(string path, InitContext ctx)
    {
        var content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var migrated = false;

        if (content.Contains(RtkBlockStart, StringComparison.Ordinal))
        {
            var (newContent, didMigrate) = RemoveRtkBlock(content);
            if (didMigrate)
            {
                content = newContent;
                migrated = true;
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write("Migrated: removed old RTK block from CLAUDE.md\n");
                }
            }
        }

        if (content.Contains(RtkMdRef, StringComparison.Ordinal))
        {
            if (ctx.Verbose > 0)
            {
                Console.Error.Write("@RTK.md reference already present in CLAUDE.md\n");
            }

            if (migrated)
            {
                if (ctx.DryRun)
                {
                    Console.Out.Write($"[dry-run] would migrate old RTK block in CLAUDE.md: {path}\n");
                }
                else
                {
                    File.WriteAllText(path, content, Utf8NoBom);
                }
            }

            return migrated;
        }

        var newFullContent = content.Length == 0 ? "@RTK.md\n" : $"{content.Trim()}\n\n@RTK.md\n";

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would add @RTK.md reference to CLAUDE.md: {path}\n");
            if (ctx.Verbose > 0)
            {
                Console.Out.Write($"[dry-run] content:\n{newFullContent}\n");
            }
        }
        else
        {
            File.WriteAllText(path, newFullContent, Utf8NoBom);
            if (ctx.Verbose > 0)
            {
                Console.Error.Write("Added @RTK.md reference to CLAUDE.md\n");
            }
        }

        return migrated;
    }

    /// <summary>
    /// Collapses runs of 3+ blank lines down to at most 2. Port of Rust
    /// <c>clean_double_blanks</c> (init.rs:1036); used after stripping lines out of a file (e.g. an
    /// <c>@RTK.md</c> reference or an RTK block) to avoid leaving excess whitespace behind.
    /// </summary>
    /// <param name="content">The content to clean.</param>
    /// <returns>The cleaned content.</returns>
    internal static string CleanDoubleBlanks(string content)
    {
        var lines = SplitRustLines(content);
        var result = new List<string>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                var blankCount = 0;
                while (i < lines.Length && lines[i].Trim().Length == 0)
                {
                    blankCount++;
                    i++;
                }

                var keep = Math.Min(blankCount, 2);
                for (var k = 0; k < keep; k++)
                {
                    result.Add(string.Empty);
                }
            }
            else
            {
                result.Add(line);
                i++;
            }
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Generates the <c>.rtk/filters.toml</c> project-local template if it does not already exist.
    /// Port of Rust <c>generate_project_filters_template</c> (init.rs:1352). Path is relative to the
    /// current working directory, matching Rust's use of a bare <c>.rtk</c> relative path.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void GenerateProjectFiltersTemplate(InitContext ctx)
    {
        const string rtkDir = ".rtk";
        var path = Path.Combine(rtkDir, "filters.toml");

        if (File.Exists(path))
        {
            if (ctx.Verbose > 0)
            {
                Console.Error.Write(".rtk/filters.toml already exists, skipping template\n");
            }

            return;
        }

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would create .rtk/filters.toml template: {path}\n");
            return;
        }

        Directory.CreateDirectory(rtkDir);
        File.WriteAllText(path, FiltersTemplate, Utf8NoBom);

        Console.Out.Write($"  filters:   {path} (template, edit to add project filters)\n");
    }

    /// <summary>
    /// Generates the user-global <c>{configDir}/rtk/filters.toml</c> template if it does not already
    /// exist. Port of Rust <c>generate_global_filters_template</c> (init.rs:1385). Unlike
    /// <see cref="GenerateProjectFiltersTemplate"/>'s local <c>.rtk</c>, this directory is created
    /// eagerly (matching Rust's unconditional <c>fs::create_dir_all</c> here — the global-scope
    /// default/hook-only modes deliberately do <b>not</b> auto-create <c>resolve_claude_dir()</c>
    /// itself, but this sibling function does create its own directory).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void GenerateGlobalFiltersTemplate(InitContext ctx)
    {
        var rtkDir = Path.Combine(ResolveGlobalConfigDir(), "rtk");
        var path = Path.Combine(rtkDir, "filters.toml");

        if (File.Exists(path))
        {
            if (ctx.Verbose > 0)
            {
                Console.Error.Write($"{path} already exists, skipping template\n");
            }

            return;
        }

        if (ctx.DryRun)
        {
            Console.Out.Write($"[dry-run] would create global filters template: {path}\n");
            return;
        }

        Directory.CreateDirectory(rtkDir);
        File.WriteAllText(path, FiltersGlobalTemplate, Utf8NoBom);

        Console.Out.Write($"  filters:   {path} (template, edit to add user-global filters)\n");
    }

    /// <summary>
    /// Prints the shared dry-run footer emitted at the end of every init sub-mode. Port of Rust
    /// <c>print_dry_run_footer</c> (init.rs:104).
    /// </summary>
    internal static void PrintDryRunFooter() => Console.Out.Write("\n[dry-run] Nothing written.\n");

    /// <summary>
    /// Finds the 1-based line number of the first line containing <paramref name="needle"/>.
    /// </summary>
    /// <param name="content">The content to scan.</param>
    /// <param name="needle">The substring to search for.</param>
    /// <returns>The 1-based line number, or <see langword="null"/> if not found.</returns>
    private static int? FindLineNumber(string content, string needle)
    {
        var lines = SplitRustLines(content);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(needle, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// Splits <paramref name="content"/> the way Rust's <c>str::lines()</c> does: on <c>\n</c>
    /// (with a trailing <c>\r</c> stripped from each line), and — unlike a naive
    /// <see cref="string.Split(char)"/> — without yielding a trailing empty element when the
    /// content ends with a newline.
    /// </summary>
    /// <param name="content">The content to split.</param>
    /// <returns>The content's lines, with no trailing empty line.</returns>
    internal static string[] SplitRustLines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var normalized = content.Replace("\r\n", "\n");
        var parts = normalized.Split('\n');
        return normalized.EndsWith('\n') ? parts[..^1] : parts;
    }
}
