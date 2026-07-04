using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RtkSharp.Hooks;

namespace RtkSharp.Filters;

// ---------------------------------------------------------------------------
// Trust-check test seam (Task 4 lands the real implementation)
// ---------------------------------------------------------------------------

/// <summary>
/// Trust state of a project-local <c>.rtk/filters.toml</c> file. Faithful port of Rust
/// <c>TrustStatus</c> (<c>src/hooks/trust.rs:38-43</c>). <see cref="ContentChanged"/> drops Rust's
/// <c>{ expected, actual }</c> hash payload — Task 3 only needs to distinguish the four states to
/// decide whether project filters load; the hash values themselves are Task 4's concern (the
/// component that actually computes and compares them).
/// </summary>
public enum FilterTrustStatus
{
    /// <summary>The file's content hash matches a previously trusted record.</summary>
    Trusted,

    /// <summary>No trust record exists for this file yet.</summary>
    Untrusted,

    /// <summary>A trust record exists, but the file's content hash no longer matches it.</summary>
    ContentChanged,

    /// <summary><c>RTK_TRUST_PROJECT_FILTERS=1</c> was honored (CI environment detected).</summary>
    EnvOverride,
}

/// <summary>
/// Result of checking a project-local filters file's trust state: the status, plus the verified
/// file content. Faithful port of Rust <c>check_trust_with_content</c>'s return shape
/// (<c>trust.rs:103</c>) — <see cref="Content"/> is populated only for <see cref="FilterTrustStatus.Trusted"/>
/// or <see cref="FilterTrustStatus.EnvOverride"/>, mirroring Rust returning content-bearing
/// <see langword="Some"/> only for those two statuses.
/// </summary>
/// <param name="Status">The resolved trust status.</param>
/// <param name="Content">The file's content, if and only if <paramref name="Status"/> permits loading it.</param>
public readonly record struct FilterTrustResult(FilterTrustStatus Status, string? Content);

/// <summary>
/// Test seam standing in for Rust's <c>check_trust_with_content</c> (<c>trust.rs:103</c>) until
/// Task 4 lands the real trust store (content hashing, the <c>trusted_filters.json</c> store, and
/// the <c>RTK_TRUST_PROJECT_FILTERS</c>/CI-detection override). <see cref="TomlFilterRegistry.Load"/>
/// takes one of these as a required parameter — callers (today, only this phase's own tests; from
/// Task 4 onward, the real production call site) supply the trust-check implementation rather than
/// the registry hard-coding one. Given the absolute path to a project-local <c>.rtk/filters.toml</c>
/// that is already known to exist, returns its trust status and (if permitted) content.
/// </summary>
/// <param name="filterPath">The path to the project-local filters file being checked.</param>
public delegate FilterTrustResult TrustChecker(string filterPath);

// ---------------------------------------------------------------------------
// Registry
// ---------------------------------------------------------------------------

/// <summary>
/// The 3-tier project/user-global/built-in filter precedence registry. Faithful port of Rust
/// <c>TomlFilterRegistry</c> and its <c>load()</c> (<c>src/core/toml_filter.rs:181-238</c>), plus the
/// <c>find_matching_filter</c> convenience wrapper (<c>toml_filter.rs:668-686</c>) that adds
/// <c>RTK_TOML_DEBUG</c> diagnostics on top of the pure <see cref="TomlFilterEngine.FindFilter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence is "earlier tier wins by list order", not "replace".</b> <see cref="Load"/>
/// accumulates all three tiers' compiled filters into <em>one</em> ordered list — project-local
/// first, then user-global, then built-in — and relies entirely on
/// <see cref="TomlFilterEngine.FindFilter"/>'s first-match-wins semantics to make an earlier tier's
/// same-matching filter shadow a later tier's. There is no de-duplication or name-based override
/// step: if project and built-in both define a filter matching <c>^make\b</c>, both compiled
/// filters end up in <see cref="Filters"/>, and the project one simply happens to be checked first.
/// </para>
/// <para>
/// <b><c>RTK_NO_TOML</c> placement.</b> In Rust, this is checked once at the <c>main.rs</c> call
/// site (<c>main.rs:1225</c>), <em>before</em> <c>find_matching_filter</c> is ever invoked — the
/// registry itself and <c>find_matching_filter</c> know nothing about it. RtkSharp has no
/// <c>main.rs</c>-equivalent CLI dispatch yet (a later phase), so that call site does not exist to
/// host the check. <see cref="FindMatchingFilter"/> therefore hoists the identical check to its own
/// entry point: it returns <see langword="null"/> immediately when <c>RTK_NO_TOML=1</c>, before
/// doing anything else (including emitting <c>RTK_TOML_DEBUG</c> diagnostics) — the same observable
/// outcome as Rust's call-site short-circuit (no filter ever matches, and no debug output is
/// produced for the bypassed lookup). When the real dispatch layer lands, this check can stay here
/// or move to that call site; either placement preserves this behavior.
/// </para>
/// </remarks>
public sealed class TomlFilterRegistry
{
    private const string ProjectFilterRelativePath = ".rtk/filters.toml";
    private const string ConfigSubDir = "rtk";
    private const string FiltersFileName = "filters.toml";

    /// <summary>Bypasses the entire TOML engine when set to exactly <c>"1"</c>; no filter is ever matched.</summary>
    private const string NoTomlEnvVar = "RTK_NO_TOML";

    /// <summary>Enables stderr diagnostics (lookup + match/no-match) when set to any value.</summary>
    private const string DebugEnvVar = "RTK_TOML_DEBUG";

    /// <summary>The combined, priority-ordered filter list: project-local, then user-global, then built-in.</summary>
    public IReadOnlyList<CompiledFilter> Filters { get; }

    private TomlFilterRegistry(IReadOnlyList<CompiledFilter> filters) => Filters = filters;

    /// <summary>
    /// Loads all three filter tiers in priority order, accumulating them into one ordered list.
    /// Faithful port of Rust <c>TomlFilterRegistry::load</c> (<c>toml_filter.rs:188-238</c>).
    /// </summary>
    /// <param name="trustChecker">
    /// The project-local trust check (see <see cref="TrustChecker"/>'s remarks) — invoked only when
    /// <c>.rtk/filters.toml</c> exists relative to the current directory.
    /// </param>
    /// <param name="warnings">Where per-tier and per-filter warnings are written; defaults to <see cref="Console.Error"/>.</param>
    /// <returns>The loaded registry.</returns>
    public static TomlFilterRegistry Load(TrustChecker trustChecker, TextWriter? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(trustChecker);
        warnings ??= Console.Error;

        var filters = new List<CompiledFilter>();

        // Tier 1: project-local .rtk/filters.toml (trust-gated).
        if (File.Exists(ProjectFilterRelativePath))
        {
            var result = trustChecker(ProjectFilterRelativePath);
            switch (result.Status)
            {
                case FilterTrustStatus.Trusted:
                case FilterTrustStatus.EnvOverride:
                    if (result.Content is { } content)
                    {
                        try
                        {
                            filters.AddRange(TomlFilterCompiler.ParseAndCompile(content, "project", warnings));
                        }
                        catch (TomlFilterParseException ex)
                        {
                            warnings.Write($"[rtk] warning: .rtk/filters.toml: {ex.Message}\n");
                        }
                    }

                    break;

                case FilterTrustStatus.Untrusted:
                    warnings.Write("[rtk] WARNING: untrusted project filters (.rtk/filters.toml)\n");
                    warnings.Write("[rtk] Filters NOT applied. Run `rtk trust` to review and enable.\n");
                    break;

                case FilterTrustStatus.ContentChanged:
                    warnings.Write("[rtk] WARNING: .rtk/filters.toml changed since trusted.\n");
                    warnings.Write("[rtk] Filters NOT applied. Run `rtk trust` to re-review.\n");
                    break;
            }
        }

        // Tier 2: user-global ~/.config/rtk/filters.toml (or platform equivalent) — unconditional,
        // no trust gate. Reuses Task 1's config-dir resolution (InitArtifacts.ResolveGlobalConfigDir),
        // the same directory ~/.config/rtk/config.toml resolves against — only the file name differs.
        var globalPath = Path.Combine(InitArtifacts.ResolveGlobalConfigDir(), ConfigSubDir, FiltersFileName);
        if (File.Exists(globalPath))
        {
            var content = File.ReadAllText(globalPath);
            try
            {
                filters.AddRange(TomlFilterCompiler.ParseAndCompile(content, "user-global", warnings));
            }
            catch (TomlFilterParseException ex)
            {
                warnings.Write($"[rtk] warning: {globalPath}: {ex.Message}\n");
            }
        }

        // Tier 3: built-in (embedded at compile time).
        try
        {
            filters.AddRange(TomlFilterCompiler.ParseAndCompile(TomlFilterBuiltins.LoadConcatenated(), "builtin", warnings));
        }
        catch (TomlFilterParseException ex)
        {
            warnings.Write($"[rtk] warning: builtin filters: {ex.Message}\n");
        }

        return new TomlFilterRegistry(filters);
    }

    /// <summary>
    /// Finds the matching filter for <paramref name="command"/> across all three loaded tiers, with
    /// optional <c>RTK_TOML_DEBUG</c> diagnostics. Faithful port of Rust <c>find_matching_filter</c>
    /// (<c>toml_filter.rs:670-686</c>), except that the <c>RTK_NO_TOML</c> short-circuit (checked at
    /// Rust's <c>main.rs</c> call site, not inside <c>find_matching_filter</c> itself) is hoisted
    /// into this method — see this type's remarks for why.
    /// </summary>
    /// <param name="command">The invoked command line to look up.</param>
    /// <param name="debugOutput">Where <c>RTK_TOML_DEBUG</c> diagnostics are written; defaults to <see cref="Console.Error"/>.</param>
    /// <returns>The first matching filter, or <see langword="null"/> if none match (or if <c>RTK_NO_TOML=1</c>).</returns>
    public CompiledFilter? FindMatchingFilter(string command, TextWriter? debugOutput = null)
    {
        if (Environment.GetEnvironmentVariable(NoTomlEnvVar) == "1")
        {
            return null;
        }

        debugOutput ??= Console.Error;
        var debugEnabled = Environment.GetEnvironmentVariable(DebugEnvVar) is not null;

        if (debugEnabled)
        {
            debugOutput.Write(
                $"[rtk:toml] looking up filter for: {FormatRustDebugString(command)} ({Filters.Count} filters loaded)\n");
        }

        var result = TomlFilterEngine.FindFilter(command, Filters);

        if (debugEnabled)
        {
            debugOutput.Write(result is { } matched
                ? $"[rtk:toml] matched filter: '{matched.Name}'\n"
                : "[rtk:toml] no filter matched — passthrough\n");
        }

        return result;
    }

    /// <summary>
    /// Renders <paramref name="s"/> the way Rust's <c>{:?}</c> (<c>Debug</c>) formatter would for a
    /// <c>&amp;str</c>: wrapped in double quotes, with <c>"</c>, <c>\</c>, and the common single-line
    /// control characters escaped. Covers exactly the cases relevant to a command-line string (this
    /// debug line's only real-world input) rather than Rust's full <c>Debug for str</c> escaping
    /// table (e.g. arbitrary Unicode control points via a <c>\u</c>-escape sequence).
    /// </summary>
    private static string FormatRustDebugString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
