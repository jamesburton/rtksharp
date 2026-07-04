using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace RtkSharp.Filters;

// ---------------------------------------------------------------------------
// Deserialization types (TOML schema) — faithful port of the structs in
// src/core/toml_filter.rs:43-109.
// ---------------------------------------------------------------------------

/// <summary>
/// Top-level shape of a filters TOML document (project-local, user-global, or the concatenated
/// built-in blob). Faithful port of Rust <c>TomlFilterFile</c> (<c>toml_filter.rs:72-81</c>).
/// </summary>
public sealed class TomlFilterFile
{
    /// <summary>Schema version; only <c>1</c> is currently supported.</summary>
    [TomlPropertyName("schema_version")]
    public uint SchemaVersion { get; set; }

    /// <summary>Filter definitions keyed by filter name (e.g. <c>[filters.make]</c>).</summary>
    [TomlPropertyName("filters")]
    public Dictionary<string, TomlFilterDef> Filters { get; set; } = new();

    /// <summary>
    /// Inline test cases keyed by the filter name they exercise (e.g. <c>[[tests.make]]</c>). Kept
    /// separate from <see cref="Filters"/> so a <see cref="TomlFilterDef"/>'s unknown-field rejection
    /// never sees test data.
    /// </summary>
    [TomlPropertyName("tests")]
    public Dictionary<string, List<TomlFilterTestDef>> Tests { get; set; } = new();
}

/// <summary>
/// A single filter's declarative pipeline configuration, as authored in TOML. Faithful port of Rust
/// <c>TomlFilterDef</c> (<c>toml_filter.rs:83-109</c>), including its <c>deny_unknown_fields</c>
/// contract — enforced manually by <see cref="TomlFilterCompiler.ParseAndCompile"/> since Tomlyn has
/// no built-in equivalent (verified empirically: an unrecognized key here is silently ignored by
/// Tomlyn's deserializer unless the caller checks for it, unlike serde's
/// <c>#[serde(deny_unknown_fields)]</c>).
/// </summary>
public sealed class TomlFilterDef
{
    /// <summary>Human-readable description of what this filter compacts.</summary>
    [TomlPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Regex matched against the invoked command line; first matching filter wins.</summary>
    [TomlPropertyName("match_command")]
    public string MatchCommand { get; set; } = string.Empty;

    /// <summary>Stage 1: strip ANSI escape codes before any other stage runs.</summary>
    [TomlPropertyName("strip_ansi")]
    public bool StripAnsi { get; set; }

    /// <summary>Stage 2: line-by-line regex substitutions, chained sequentially.</summary>
    [TomlPropertyName("replace")]
    public List<ReplaceRule> Replace { get; set; } = [];

    /// <summary>Stage 3: full-blob short-circuit rules (first matching, non-<c>unless</c>'d rule wins).</summary>
    [TomlPropertyName("match_output")]
    public List<MatchOutputRule> MatchOutput { get; set; } = [];

    /// <summary>Stage 4a: drop any line matching one of these regexes. Mutually exclusive with <see cref="KeepLinesMatching"/>.</summary>
    [TomlPropertyName("strip_lines_matching")]
    public List<string> StripLinesMatching { get; set; } = [];

    /// <summary>Stage 4b: keep only lines matching one of these regexes. Mutually exclusive with <see cref="StripLinesMatching"/>.</summary>
    [TomlPropertyName("keep_lines_matching")]
    public List<string> KeepLinesMatching { get; set; } = [];

    /// <summary>Stage 5: per-line Unicode-safe truncation length, or <see langword="null"/> to skip.</summary>
    [TomlPropertyName("truncate_lines_at")]
    public int? TruncateLinesAt { get; set; }

    /// <summary>Stage 6: number of leading lines to keep, or <see langword="null"/> to skip.</summary>
    [TomlPropertyName("head_lines")]
    public int? HeadLines { get; set; }

    /// <summary>Stage 6: number of trailing lines to keep, or <see langword="null"/> to skip.</summary>
    [TomlPropertyName("tail_lines")]
    public int? TailLines { get; set; }

    /// <summary>Stage 7: absolute line cap applied after stage 6 (the omission marker counts toward it).</summary>
    [TomlPropertyName("max_lines")]
    public int? MaxLines { get; set; }

    /// <summary>Stage 8: replacement message when the fully-processed result is blank.</summary>
    [TomlPropertyName("on_empty")]
    public string? OnEmpty { get; set; }

    /// <summary>When <see langword="true"/>, the runner should capture stderr and merge it with stdout before filtering.</summary>
    [TomlPropertyName("filter_stderr")]
    public bool FilterStderr { get; set; }
}

/// <summary>
/// A regex substitution applied line-by-line (stage 2). Rules are chained sequentially: rule N+1
/// operates on the output of rule N. Faithful port of Rust <c>ReplaceRule</c> (<c>toml_filter.rs:55-60</c>).
/// </summary>
public sealed class ReplaceRule
{
    /// <summary>The regex pattern to match.</summary>
    [TomlPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    /// <summary>The replacement text; supports <c>$1</c>-style backreferences.</summary>
    [TomlPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;
}

/// <summary>
/// A stage-3 match-output rule: if <see cref="Pattern"/> matches anywhere in the full output blob,
/// the filter short-circuits and returns <see cref="Message"/> immediately (first matching rule wins).
/// If <see cref="Unless"/> is set and also matches the blob, the rule is skipped instead (prevents
/// short-circuiting when errors or warnings are present). Faithful port of Rust
/// <c>MatchOutputRule</c> (<c>toml_filter.rs:43-50</c>).
/// </summary>
public sealed class MatchOutputRule
{
    /// <summary>The regex pattern matched against the full output blob.</summary>
    [TomlPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    /// <summary>The message returned in place of the output when this rule wins.</summary>
    [TomlPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional guard regex; if it also matches the blob, this rule is skipped.</summary>
    [TomlPropertyName("unless")]
    public string? Unless { get; set; }
}

/// <summary>
/// An inline test case attached to a filter, living in <c>[[tests.&lt;filter-name&gt;]]</c> sections
/// separate from <c>[filters.*]</c>. Faithful port of Rust <c>TomlFilterTestDef</c>
/// (<c>toml_filter.rs:62-70</c>).
/// </summary>
public sealed class TomlFilterTestDef
{
    /// <summary>Short human-readable name for the test case.</summary>
    [TomlPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw input fed to <see cref="TomlFilterEngine.ApplyFilter"/>.</summary>
    [TomlPropertyName("input")]
    public string Input { get; set; } = string.Empty;

    /// <summary>Expected output (trailing newlines are trimmed before comparison, per TOML multiline-string convention).</summary>
    [TomlPropertyName("expected")]
    public string Expected { get; set; } = string.Empty;
}

/// <summary>
/// Source-generation context for AOT-safe <see cref="TomlFilterFile"/> deserialization (see
/// <see cref="TomlFilterCompiler.ParseAndCompile"/>), analogous to <c>ConfigTomlContext</c> in
/// <c>RtkSharp/Core/Config.cs</c> — required because <c>RtkSharp.csproj</c> sets
/// <c>PublishAot=true</c>, which disables Tomlyn's reflection-based binding by default.
/// </summary>
[TomlSerializable(typeof(TomlFilterFile))]
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class TomlFilterFileContext : TomlSerializerContext;

// ---------------------------------------------------------------------------
// Compiled types (post-validation, ready to apply)
// ---------------------------------------------------------------------------

/// <summary>Which of the mutually-exclusive stage-4 line filters (if any) is active.</summary>
public enum LineFilterMode
{
    /// <summary>Neither <c>strip_lines_matching</c> nor <c>keep_lines_matching</c> is set.</summary>
    None,

    /// <summary>Drop lines matching any pattern in <see cref="CompiledFilter.LineFilterPatterns"/>.</summary>
    Strip,

    /// <summary>Keep only lines matching any pattern in <see cref="CompiledFilter.LineFilterPatterns"/>.</summary>
    Keep,
}

/// <summary>A compiled (regex-ready) stage-2 replace rule.</summary>
/// <param name="Pattern">The compiled regex.</param>
/// <param name="Replacement">The replacement text (may contain <c>$1</c>-style backreferences).</param>
public sealed record CompiledReplaceRule(Regex Pattern, string Replacement);

/// <summary>A compiled (regex-ready) stage-3 match-output rule.</summary>
/// <param name="Pattern">The compiled regex matched against the full output blob.</param>
/// <param name="Message">The message returned in place of the output when this rule wins.</param>
/// <param name="Unless">Optional compiled guard regex; if it also matches, this rule is skipped.</param>
public sealed record CompiledMatchOutputRule(Regex Pattern, string Message, Regex? Unless);

/// <summary>
/// A filter that has been parsed and compiled — all regexes are ready to apply. Faithful port of Rust
/// <c>CompiledFilter</c> (<c>toml_filter.rs:136-154</c>).
/// </summary>
/// <remarks>
/// <b>Dynamic-regex exception.</b> Every <see cref="Regex"/> reachable from this type (
/// <see cref="MatchRegex"/>, <see cref="Replace"/>'s and <see cref="MatchOutput"/>'s patterns,
/// <see cref="LineFilterPatterns"/>) is compiled at runtime from arbitrary TOML file content — a
/// project's <c>.rtk/filters.toml</c>, a user's <c>~/.config/rtk/filters.toml</c>, or one of the 63
/// embedded built-in <c>.toml</c> files — and therefore cannot use the <c>[GeneratedRegex]</c>
/// source generator, which requires a compile-time string literal. <see cref="TomlFilterCompiler"/>
/// constructs all of these via <c>new Regex(pattern, RegexOptions.Compiled)</c> instead, matching
/// this phase's documented Global Constraints exception for TOML-sourced patterns.
/// </remarks>
public sealed class CompiledFilter
{
    /// <summary>The filter's name (the TOML table key, e.g. <c>"make"</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description, if any.</summary>
    public string? Description { get; init; }

    /// <summary>Regex matched against the invoked command line.</summary>
    public required Regex MatchRegex { get; init; }

    /// <summary>Whether stage 1 (ANSI stripping) is enabled.</summary>
    public bool StripAnsi { get; init; }

    /// <summary>Compiled stage-2 replace rules, in declaration order.</summary>
    public IReadOnlyList<CompiledReplaceRule> Replace { get; init; } = [];

    /// <summary>Compiled stage-3 match-output rules, in declaration order.</summary>
    public IReadOnlyList<CompiledMatchOutputRule> MatchOutput { get; init; } = [];

    /// <summary>Which stage-4 line filter (if any) is active.</summary>
    public LineFilterMode LineFilterMode { get; init; } = LineFilterMode.None;

    /// <summary>
    /// Compiled stage-4 line-filter patterns (the Rust port's <c>RegexSet</c> equivalent — a line
    /// matches this filter if it matches <em>any</em> pattern in the list). Empty when
    /// <see cref="LineFilterMode"/> is <see cref="Filters.LineFilterMode.None"/>.
    /// </summary>
    public IReadOnlyList<Regex> LineFilterPatterns { get; init; } = [];

    /// <summary>Stage 5: per-line Unicode-safe truncation length, or <see langword="null"/> to skip.</summary>
    public int? TruncateLinesAt { get; init; }

    /// <summary>Stage 6: number of leading lines to keep, or <see langword="null"/> to skip.</summary>
    public int? HeadLines { get; init; }

    /// <summary>Stage 6: number of trailing lines to keep, or <see langword="null"/> to skip.</summary>
    public int? TailLines { get; init; }

    /// <summary>Stage 7: absolute line cap applied after stage 6.</summary>
    public int? MaxLines { get; init; }

    /// <summary>Stage 8: replacement message when the fully-processed result is blank.</summary>
    public string? OnEmpty { get; init; }

    /// <summary>Whether the runner should capture stderr and merge it with stdout before filtering.</summary>
    public bool FilterStderr { get; init; }
}

// ---------------------------------------------------------------------------
// Exceptions
// ---------------------------------------------------------------------------

/// <summary>
/// A whole-document parse/validation failure: malformed TOML, an unsupported <c>schema_version</c>,
/// or an unrecognized field in a <c>deny_unknown_fields</c>-equivalent position. Mirrors Rust's
/// <c>parse_and_compile</c> returning <c>Result::Err</c> for the entire file — as opposed to a
/// single bad filter definition, which is warned about and skipped (see
/// <see cref="TomlFilterCompiler.ParseAndCompile"/>'s remarks).
/// </summary>
public sealed class TomlFilterParseException(string message) : Exception(message);

/// <summary>
/// A single filter definition's compile-time validation failure (invalid regex, or
/// <c>strip_lines_matching</c>/<c>keep_lines_matching</c> both set). Caught internally by
/// <see cref="TomlFilterCompiler.ParseAndCompile"/>, which reports it as a warning and skips just
/// that filter — mirroring Rust's <c>compile_filter</c> returning <c>Result::Err</c> for one filter
/// while the rest of the file continues to load.
/// </summary>
internal sealed class FilterCompileException(string message) : Exception(message);

// ---------------------------------------------------------------------------
// Compiler
// ---------------------------------------------------------------------------

/// <summary>
/// Parses and compiles a TOML filters document into ready-to-apply <see cref="CompiledFilter"/>s.
/// Pure parse/compile logic only — this type does not know about the 3-tier project/global/builtin
/// precedence registry (a later task) or command dispatch (also a later task).
/// </summary>
public static class TomlFilterCompiler
{
    /// <summary>
    /// Commands already handled by dedicated RtkSharp command modules (routed before the TOML engine
    /// is ever reached). A TOML filter whose <c>match_command</c> matches one of these will never
    /// activate. Copied verbatim from Rust's <c>RUST_HANDLED_COMMANDS</c> (<c>toml_filter.rs:265-315</c>)
    /// — the name is kept as-is (rather than renamed to something like "RtkSharpHandledCommands") so
    /// the list is trivially diffable against the oracle.
    /// </summary>
    private static readonly string[] RustHandledCommands =
    [
        "ls",
        "tree",
        "read",
        "smart",
        "git",
        "gh",
        "aws",
        "psql",
        "pnpm",
        "err",
        "test",
        "json",
        "deps",
        "env",
        "find",
        "diff",
        "log",
        "docker",
        "kubectl",
        "summary",
        "grep",
        "init",
        "wget",
        "wc",
        "gain",
        "config",
        "vitest",
        "prisma",
        "tsc",
        "next",
        "lint",
        "prettier",
        "format",
        "playwright",
        "cargo",
        "npm",
        "npx",
        "curl",
        "discover",
        "ruff",
        "pytest",
        "mypy",
        "pip",
        "go",
        "golangci-lint",
        "rewrite",
        "proxy",
        "verify",
        "learn",
    ];

    private static readonly HashSet<string> FilterDefKnownFields = new(StringComparer.Ordinal)
    {
        "description",
        "match_command",
        "strip_ansi",
        "replace",
        "match_output",
        "strip_lines_matching",
        "keep_lines_matching",
        "truncate_lines_at",
        "head_lines",
        "tail_lines",
        "max_lines",
        "on_empty",
        "filter_stderr",
    };

    private static readonly HashSet<string> ReplaceRuleKnownFields = new(StringComparer.Ordinal) { "pattern", "replacement" };

    private static readonly HashSet<string> MatchOutputRuleKnownFields = new(StringComparer.Ordinal) { "pattern", "message", "unless" };

    private static readonly HashSet<string> TestDefKnownFields = new(StringComparer.Ordinal) { "name", "input", "expected" };

    /// <summary>
    /// Parses <paramref name="content"/> as a filters TOML document and compiles every filter
    /// definition it contains. Filter-level failures (invalid regex, mutual exclusion) are reported
    /// to <paramref name="warnings"/> and that filter is skipped; document-level failures (bad TOML,
    /// unsupported <c>schema_version</c>, an unrecognized field) throw
    /// <see cref="TomlFilterParseException"/> for the whole call. Faithful port of Rust
    /// <c>TomlFilterRegistry::parse_and_compile</c> (<c>toml_filter.rs:240-259</c>).
    /// </summary>
    /// <param name="content">The TOML document text.</param>
    /// <param name="source">A short label identifying the document's origin, used in error/warning text (e.g. <c>"builtin"</c>, <c>"project"</c>).</param>
    /// <param name="warnings">Where per-filter warnings are written; defaults to <see cref="Console.Error"/>.</param>
    /// <returns>The compiled filters, in filter-name order (mirroring Rust's <c>BTreeMap</c> iteration order).</returns>
    /// <exception cref="TomlFilterParseException">Thrown for a whole-document failure.</exception>
    public static List<CompiledFilter> ParseAndCompile(string content, string source, TextWriter? warnings = null)
    {
        warnings ??= Console.Error;

        TomlFilterFile file;
        try
        {
            file = TomlSerializer.Deserialize(content, TomlFilterFileContext.Default.TomlFilterFile)
                ?? throw new TomlFilterParseException($"TOML parse error in {source}: empty document");
        }
        catch (TomlException ex)
        {
            throw new TomlFilterParseException($"TOML parse error in {source}: {ex.Message}");
        }

        if (file.SchemaVersion != 1)
        {
            throw new TomlFilterParseException(
                $"unsupported schema_version {file.SchemaVersion} in {source} (expected 1)");
        }

        // deny_unknown_fields equivalent: Tomlyn has no built-in support for this (verified
        // empirically — see TomlFilterDef's remarks), so unknown keys are detected by walking a
        // second, untyped parse of the same document.
        ValidateNoUnknownFields(content, source);

        var compiled = new List<CompiledFilter>();
        foreach (var name in file.Filters.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var def = file.Filters[name];
            try
            {
                compiled.Add(CompileFilter(name, def, warnings));
            }
            catch (FilterCompileException ex)
            {
                warnings.Write($"[rtk] warning: filter '{name}' in {source}: {ex.Message}\n");
            }
        }

        return compiled;
    }

    /// <summary>
    /// Walks a second, untyped parse of <paramref name="content"/> to detect fields that
    /// <see cref="TomlFilterDef"/>, <see cref="ReplaceRule"/>, <see cref="MatchOutputRule"/>, or
    /// <see cref="TomlFilterTestDef"/> do not recognize — the manual equivalent of serde's
    /// <c>#[serde(deny_unknown_fields)]</c>, which Tomlyn does not provide.
    /// </summary>
    private static void ValidateNoUnknownFields(string content, string source)
    {
        // Deserializes into the untyped TomlTable model via the AOT-safe context overload (rather
        // than the TomlSerializerOptions overload, which is unconditionally annotated
        // RequiresUnreferencedCode/RequiresDynamicCode regardless of the options instance passed —
        // verified empirically to still trip IL2026/IL3050 trim warnings even with a non-reflection
        // resolver). TomlTable must still be explicitly registered via
        // [TomlSerializable(typeof(TomlTable))] on TomlFilterFileContext below — verified empirically
        // that without it, the context-overload path throws at runtime ("No generated metadata is
        // available for type 'Tomlyn.Model.TomlTable' in the provided context"), even though it
        // compiles without warnings either way.
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(content, TomlFilterFileContext.Default)
                ?? throw new TomlFilterParseException($"TOML parse error in {source}: empty document");
        }
        catch (TomlException ex)
        {
            throw new TomlFilterParseException($"TOML parse error in {source}: {ex.Message}");
        }

        if (root.TryGetValue("filters", out var filtersObj) && filtersObj is TomlTable filtersTable)
        {
            foreach (var (filterName, filterObj) in filtersTable)
            {
                if (filterObj is not TomlTable filterDef)
                {
                    continue;
                }

                CheckUnknownKeys(filterDef, FilterDefKnownFields, $"filter '{filterName}' in {source}");

                if (filterDef.TryGetValue("replace", out var replaceObj) && replaceObj is TomlArray replaceArr)
                {
                    foreach (var item in replaceArr)
                    {
                        if (item is TomlTable ruleTable)
                        {
                            CheckUnknownKeys(ruleTable, ReplaceRuleKnownFields, $"filter '{filterName}' replace rule in {source}");
                        }
                    }
                }

                if (filterDef.TryGetValue("match_output", out var moObj) && moObj is TomlArray moArr)
                {
                    foreach (var item in moArr)
                    {
                        if (item is TomlTable ruleTable)
                        {
                            CheckUnknownKeys(ruleTable, MatchOutputRuleKnownFields, $"filter '{filterName}' match_output rule in {source}");
                        }
                    }
                }
            }
        }

        if (root.TryGetValue("tests", out var testsObj) && testsObj is TomlTable testsTable)
        {
            foreach (var (testFilterName, testsArrObj) in testsTable)
            {
                if (testsArrObj is not TomlTableArray testArr)
                {
                    continue;
                }

                foreach (var item in testArr)
                {
                    CheckUnknownKeys(item, TestDefKnownFields, $"test in [[tests.{testFilterName}]] in {source}");
                }
            }
        }
    }

    private static void CheckUnknownKeys(TomlTable table, HashSet<string> known, string context)
    {
        foreach (var key in table.Keys)
        {
            if (!known.Contains(key))
            {
                throw new TomlFilterParseException($"unknown field '{key}' in {context}");
            }
        }
    }

    private static CompiledFilter CompileFilter(string name, TomlFilterDef def, TextWriter warnings)
    {
        // Mutual exclusion: strip and keep cannot both be set.
        if (def.StripLinesMatching.Count > 0 && def.KeepLinesMatching.Count > 0)
        {
            throw new FilterCompileException("strip_lines_matching and keep_lines_matching are mutually exclusive");
        }

        var matchRegex = CompileDynamicRegex(def.MatchCommand, "invalid match_command regex");

        // Shadow warning: if match_command matches an RtkSharp-handled command, this filter will
        // never activate (dispatch routes before the TOML fallback engine is reached). Warn the
        // author but do not fail — mirrors Rust's non-fatal eprintln! (toml_filter.rs:328-336).
        foreach (var cmd in RustHandledCommands)
        {
            if (matchRegex.IsMatch(cmd))
            {
                warnings.Write(
                    $"[rtk] warning: filter '{name}' match_command matches '{cmd}' which is already " +
                    "handled by a Rust module — this filter will never activate for that command\n");
                break;
            }
        }

        var replace = new List<CompiledReplaceRule>();
        foreach (var rule in def.Replace)
        {
            var pattern = CompileDynamicRegex(rule.Pattern, $"invalid replace pattern '{rule.Pattern}'");
            replace.Add(new CompiledReplaceRule(pattern, rule.Replacement));
        }

        var matchOutput = new List<CompiledMatchOutputRule>();
        foreach (var rule in def.MatchOutput)
        {
            var pattern = CompileDynamicRegex(rule.Pattern, $"invalid match_output pattern '{rule.Pattern}'");
            Regex? unless = rule.Unless is { } u
                ? CompileDynamicRegex(u, $"invalid match_output unless pattern '{u}'")
                : null;
            matchOutput.Add(new CompiledMatchOutputRule(pattern, rule.Message, unless));
        }

        var lineFilterMode = LineFilterMode.None;
        var lineFilterPatterns = new List<Regex>();
        if (def.StripLinesMatching.Count > 0)
        {
            lineFilterMode = LineFilterMode.Strip;
            foreach (var p in def.StripLinesMatching)
            {
                lineFilterPatterns.Add(CompileDynamicRegex(p, "invalid strip_lines_matching regex"));
            }
        }
        else if (def.KeepLinesMatching.Count > 0)
        {
            lineFilterMode = LineFilterMode.Keep;
            foreach (var p in def.KeepLinesMatching)
            {
                lineFilterPatterns.Add(CompileDynamicRegex(p, "invalid keep_lines_matching regex"));
            }
        }

        return new CompiledFilter
        {
            Name = name,
            Description = def.Description,
            MatchRegex = matchRegex,
            StripAnsi = def.StripAnsi,
            Replace = replace,
            MatchOutput = matchOutput,
            LineFilterMode = lineFilterMode,
            LineFilterPatterns = lineFilterPatterns,
            TruncateLinesAt = def.TruncateLinesAt,
            HeadLines = def.HeadLines,
            TailLines = def.TailLines,
            MaxLines = def.MaxLines,
            OnEmpty = def.OnEmpty,
            FilterStderr = def.FilterStderr,
        };
    }

    /// <summary>
    /// Compiles a regex pattern sourced from arbitrary TOML file content. Per this phase's Global
    /// Constraints, dynamic (runtime-sourced) patterns cannot use <c>[GeneratedRegex]</c> — that
    /// source generator requires a compile-time string literal — so <c>RegexOptions.Compiled</c> is
    /// used directly instead.
    /// </summary>
    private static Regex CompileDynamicRegex(string pattern, string errorPrefix)
    {
        try
        {
            return new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            throw new FilterCompileException($"{errorPrefix}: {ex.Message}");
        }
    }
}

// ---------------------------------------------------------------------------
// Built-in filter loading
// ---------------------------------------------------------------------------

/// <summary>
/// Loads the 63 built-in <c>.toml</c> filter definitions embedded as resources (see
/// <c>RtkSharp.csproj</c>) and concatenates them into a single TOML document, mirroring Rust's
/// <c>build.rs</c>-time concatenation into one <c>include_str!</c> blob (<c>build.rs:15-65</c>) —
/// a single parse pass rather than 63 independent documents, so cross-file behavior (e.g. a
/// duplicate <c>match_command</c> key) matches the oracle exactly.
/// </summary>
public static class TomlFilterBuiltins
{
    private const string ResourcePrefix = "RtkSharp.Filters.Builtin.";

    /// <summary>
    /// Reads every embedded built-in filter resource, sorts them alphabetically by file name
    /// (matching Rust's <c>files.sort_by_key(|e| e.file_name())</c>), and concatenates them behind a
    /// single <c>schema_version = 1</c> header — exactly as <c>build.rs</c> assembles
    /// <c>builtin_filters.toml</c>.
    /// </summary>
    /// <returns>The concatenated built-in filters TOML document.</returns>
    public static string LoadConcatenated()
    {
        var assembly = typeof(TomlFilterBuiltins).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("schema_version = 1\n\n");

        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"embedded resource '{name}' not found");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var fileContent = reader.ReadToEnd();
            var fileName = name[ResourcePrefix.Length..];

            sb.Append("# --- ").Append(fileName).Append(" ---\n");
            sb.Append(fileContent);
            sb.Append("\n\n");
        }

        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// Pure pipeline functions
// ---------------------------------------------------------------------------

/// <summary>
/// Finds a matching filter and applies its 8-stage pipeline. Pure, stateless functions — this type
/// holds no registry state and does not know about the project/global/builtin precedence tiers (a
/// later task) or command dispatch (also a later task).
/// </summary>
public static class TomlFilterEngine
{
    /// <summary>
    /// Finds the first filter in <paramref name="filters"/> whose <c>match_command</c> regex matches
    /// <paramref name="command"/>. O(N) on the number of filters. Faithful port of Rust
    /// <c>find_filter_in</c> (<c>toml_filter.rs:419-424</c>).
    /// </summary>
    /// <param name="command">The invoked command line.</param>
    /// <param name="filters">The filters to search, in priority order.</param>
    /// <returns>The first matching filter, or <see langword="null"/> if none match.</returns>
    public static CompiledFilter? FindFilter(string command, IReadOnlyList<CompiledFilter> filters)
    {
        foreach (var filter in filters)
        {
            if (filter.MatchRegex.IsMatch(command))
            {
                return filter;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies a compiled filter's 8-stage pipeline to raw command output. Pure
    /// <see langword="string"/> -&gt; <see langword="string"/> transform — no I/O, no global state.
    /// Faithful port of Rust <c>apply_filter</c> (<c>toml_filter.rs:437-535</c>), stage-for-stage and
    /// in the exact same order (stage order is semantically load-bearing — e.g. <c>max_lines</c>
    /// (stage 7) applies <i>after</i> <c>head_lines</c>/<c>tail_lines</c> (stage 6), and the
    /// omission-marker line stage 6 inserts counts toward stage 7's cap).
    /// </summary>
    /// <param name="filter">The compiled filter to apply.</param>
    /// <param name="stdout">The raw command output to filter.</param>
    /// <returns>The filtered output.</returns>
    public static string ApplyFilter(CompiledFilter filter, string stdout)
    {
        var lines = ReadCommand.SplitLines(stdout);

        // 1. strip_ansi
        if (filter.StripAnsi)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i] = Utils.StripAnsi(lines[i]);
            }
        }

        // 2. replace — line-by-line, rules chained sequentially
        if (filter.Replace.Count > 0)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                foreach (var rule in filter.Replace)
                {
                    line = rule.Pattern.Replace(line, rule.Replacement);
                }

                lines[i] = line;
            }
        }

        // 3. match_output — short-circuit on full blob match (first rule wins).
        //    If `unless` is set and also matches the blob, the rule is skipped.
        if (filter.MatchOutput.Count > 0)
        {
            var blob = string.Join("\n", lines);
            foreach (var rule in filter.MatchOutput)
            {
                if (!rule.Pattern.IsMatch(blob))
                {
                    continue;
                }

                if (rule.Unless is { } unlessRe && unlessRe.IsMatch(blob))
                {
                    continue; // errors/warnings present — skip this rule
                }

                return rule.Message;
            }
        }

        // 4. strip OR keep (mutually exclusive)
        switch (filter.LineFilterMode)
        {
            case LineFilterMode.Strip:
                lines = lines.Where(l => !filter.LineFilterPatterns.Any(p => p.IsMatch(l))).ToList();
                break;
            case LineFilterMode.Keep:
                lines = lines.Where(l => filter.LineFilterPatterns.Any(p => p.IsMatch(l))).ToList();
                break;
            case LineFilterMode.None:
            default:
                break;
        }

        // 5. truncate_lines_at — Unicode-safe (Utils.Truncate operates on Unicode scalar values, not
        //    UTF-16 code units, so a surrogate pair is never split).
        if (filter.TruncateLinesAt is { } maxChars)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i] = Utils.Truncate(lines[i], maxChars);
            }
        }

        // 6. head + tail
        var total = lines.Count;
        if (filter.HeadLines is { } head && filter.TailLines is { } tail)
        {
            if (total > head + tail)
            {
                var result = lines.GetRange(0, head);
                result.Add($"... ({total - head - tail} lines omitted)");
                result.AddRange(lines.GetRange(total - tail, tail));
                lines = result;
            }
        }
        else if (filter.HeadLines is { } headOnly)
        {
            if (total > headOnly)
            {
                lines = lines.GetRange(0, headOnly);
                lines.Add($"... ({total - headOnly} lines omitted)");
            }
        }
        else if (filter.TailLines is { } tailOnly)
        {
            if (total > tailOnly)
            {
                var omitted = total - tailOnly;
                lines = lines.GetRange(omitted, tailOnly);
                lines.Insert(0, $"... ({omitted} lines omitted)");
            }
        }

        // 7. max_lines — absolute cap applied after head/tail (includes the omit-marker line, if any)
        if (filter.MaxLines is { } max && lines.Count > max)
        {
            var truncated = lines.Count - max;
            lines = lines.GetRange(0, max);
            lines.Add($"... ({truncated} lines truncated)");
        }

        // 8. on_empty
        var final = string.Join("\n", lines);
        if (string.IsNullOrWhiteSpace(final) && filter.OnEmpty is { } message)
        {
            return message;
        }

        return final;
    }
}
