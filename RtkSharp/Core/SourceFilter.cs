using System.Text;
using System.Text.RegularExpressions;

namespace RtkSharp.Core;

/// <summary>
/// Language-aware, regex/heuristic source-code comment and boilerplate stripper used by
/// <c>rtk read</c>'s <c>--level</c> option. Ported from <c>src/core/filter.rs</c>. This is
/// text/regex heuristics, not an AST or semantic parser — see <c>RtkSharp.Ast</c> for the
/// separate, RtkSharp-only <c>--level ast</c> tier built on top of this.
/// </summary>
/// <remarks>
/// One deliberate addition beyond the Rust spec: <see cref="Language.CSharp"/> (extension
/// <c>cs</c>) is recognized explicitly. This is inert relative to the oracle — Rust's
/// <c>Language::from_extension</c> has no <c>"cs"</c> arm, so the oracle falls through to
/// <see cref="Language.Unknown"/> for <c>.cs</c> files, and <c>Unknown</c>'s comment patterns
/// are already identical (C-style <c>//</c> / <c>/* */</c> / <c>/**</c>) to what
/// <see cref="Language.CSharp"/> uses here. Filter *output* for <c>.cs</c> files is therefore
/// byte-identical whether or not this case exists; the only difference is the enum's name,
/// which nothing in the ported <c>read</c> command surfaces (verbose language-detection
/// diagnostics are out of scope — see <c>ReadCommand.cs</c>'s class remarks). Documented in
/// <c>docs/parity/compatibility-ledger.md</c> for completeness even though there is no
/// observable divergence.
/// </remarks>
public enum FilterLevel
{
    /// <summary>No filtering — verbatim content.</summary>
    None,

    /// <summary>Strip comments/docstrings and normalize blank lines, keep everything else.</summary>
    Minimal,

    /// <summary>Keep only imports, signatures, and top-level declarations; collapse bodies.</summary>
    Aggressive,

    /// <summary>
    /// RtkSharp-only: real AST-backed signature summarization via <c>RtkSharp.Ast</c>. Has no
    /// Rust counterpart — <see cref="FilterLevelParser"/> (which mirrors Rust's
    /// <c>FilterLevel::from_str</c> exactly) deliberately does NOT recognize this value; only
    /// <c>ReadCommand</c>'s own CLI-argument parser does, since that layer is already an
    /// RtkSharp-specific superset of the ported clap definition.
    /// </summary>
    Ast,
}

/// <summary>
/// Parses the <c>--level</c> option value into a <see cref="FilterLevel"/>. Mirrors Rust's
/// <c>FilterLevel::from_str</c> exactly — recognizes only <c>none</c>/<c>minimal</c>/
/// <c>aggressive</c>, deliberately NOT the RtkSharp-only <see cref="FilterLevel.Ast"/> (see its
/// remarks). <c>ReadCommand</c>'s own <c>--level</c> parsing accepts <c>ast</c> as an additional,
/// RtkSharp-specific value on top of this.
/// </summary>
public static class FilterLevelParser
{
    /// <summary>
    /// Parses <paramref name="value"/> case-insensitively. Mirrors <c>FilterLevel::from_str</c>.
    /// </summary>
    /// <param name="value">The raw <c>--level</c> value.</param>
    /// <returns>The parsed level.</returns>
    /// <exception cref="ArgumentException">When <paramref name="value"/> is not a recognized level.</exception>
    public static FilterLevel Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToLowerInvariant() switch
        {
            "none" => FilterLevel.None,
            "minimal" => FilterLevel.Minimal,
            "aggressive" => FilterLevel.Aggressive,
            _ => throw new ArgumentException($"Unknown filter level: {value}"),
        };
    }
}

/// <summary>
/// The source language of a file, used to select comment syntax for
/// <see cref="MinimalFilter"/>/<see cref="AggressiveFilter"/>. Ported from
/// <c>src/core/filter.rs</c>'s <c>Language</c> enum, plus <see cref="CSharp"/> (see
/// <see cref="FilterLevel"/>'s remarks for why that addition is inert against the oracle).
/// </summary>
public enum Language
{
    Rust,
    Python,
    JavaScript,
    TypeScript,
    Go,
    C,
    Cpp,
    Java,
    Ruby,
    Shell,

    /// <summary>C# — not present in the Rust oracle; see <see cref="FilterLevel"/> remarks.</summary>
    CSharp,

    /// <summary>Data formats (JSON, YAML, TOML, XML, CSV, etc.) — never comment-stripped.</summary>
    Data,
    Unknown,
}

/// <summary>The comment-syntax markers used to strip comments for a given <see cref="Language"/>.</summary>
/// <param name="Line">The single-line comment prefix, or null if the language has none.</param>
/// <param name="BlockStart">The block-comment start marker, or null.</param>
/// <param name="BlockEnd">The block-comment end marker, or null.</param>
/// <param name="DocLine">The single-line doc-comment prefix kept even in minimal mode, or null.</param>
/// <param name="DocBlockStart">The block doc-comment start marker exempted from stripping, or null.</param>
public readonly record struct CommentPatterns(
    string? Line,
    string? BlockStart,
    string? BlockEnd,
    string? DocLine,
    string? DocBlockStart);

/// <summary>Extension methods for <see cref="Language"/> mirroring <c>filter.rs</c>'s free functions.</summary>
public static class LanguageExtensions
{
    /// <summary>
    /// Maps a file extension (without the leading dot, any case) to a <see cref="Language"/>.
    /// Mirrors <c>Language::from_extension</c>.
    /// </summary>
    /// <param name="extension">The file extension, without the leading dot.</param>
    /// <returns>The detected language, or <see cref="Language.Unknown"/> if unrecognized.</returns>
    public static Language FromExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return extension.ToLowerInvariant() switch
        {
            "rs" => Language.Rust,
            "py" or "pyw" => Language.Python,
            "js" or "mjs" or "cjs" => Language.JavaScript,
            "ts" or "tsx" => Language.TypeScript,
            "go" => Language.Go,
            "c" or "h" => Language.C,
            "cpp" or "cc" or "cxx" or "hpp" or "hh" => Language.Cpp,
            "java" => Language.Java,
            "rb" => Language.Ruby,
            "sh" or "bash" or "zsh" => Language.Shell,
            "cs" => Language.CSharp,
            "json" or "jsonc" or "json5" or "yaml" or "yml" or "toml" or "xml" or "csv" or "tsv"
                or "graphql" or "gql" or "sql" or "md" or "markdown" or "txt" or "env" or "lock" =>
                Language.Data,
            _ => Language.Unknown,
        };
    }

    /// <summary>
    /// Returns the comment-syntax markers for <paramref name="language"/>. Mirrors
    /// <c>Language::comment_patterns</c>.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns>The comment patterns for that language.</returns>
    public static CommentPatterns CommentPatterns(this Language language) => language switch
    {
        Language.Rust => new CommentPatterns("//", "/*", "*/", "///", "/**"),
        Language.Python => new CommentPatterns("#", "\"\"\"", "\"\"\"", null, "\"\"\""),
        Language.JavaScript or Language.TypeScript or Language.Go or Language.C or Language.Cpp
            or Language.Java => new CommentPatterns("//", "/*", "*/", null, "/**"),
        Language.Ruby => new CommentPatterns("#", "=begin", "=end", null, null),
        Language.Shell => new CommentPatterns("#", null, null, null, null),
        Language.Data => new CommentPatterns(null, null, null, null, null),
        // CSharp is grouped with Unknown, NOT the JS/Go/C/Cpp/Java family above, despite sharing
        // their line/block markers — DocBlockStart must also match Unknown's (null, falling back
        // to the "###" sentinel in MinimalFilter) for the two to be genuinely output-identical.
        // Giving CSharp "/**" instead (as the JS family has) would exempt JavaDoc-style /** blocks
        // from being treated as block-comment starts in .cs files, while the oracle (which sees
        // .cs as Unknown) WOULD strip them — a real, oracle-verified divergence caught in review.
        Language.CSharp or Language.Unknown => new CommentPatterns("//", "/*", "*/", null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };
}

/// <summary>A source-filtering strategy for a given <see cref="FilterLevel"/>.</summary>
public interface IFilterStrategy
{
    /// <summary>Filters <paramref name="content"/> according to this strategy.</summary>
    /// <param name="content">The raw source text.</param>
    /// <param name="language">The detected language.</param>
    /// <returns>The filtered text.</returns>
    string Filter(string content, Language language);
}

/// <summary>Passthrough strategy for <see cref="FilterLevel.None"/> — returns content unchanged.</summary>
public sealed class NoFilter : IFilterStrategy
{
    /// <inheritdoc />
    public string Filter(string content, Language language) => content;
}

/// <summary>
/// Strips comments/docstrings (keeping doc comments) and normalizes runs of 3+ blank lines
/// down to 2, leaving everything else untouched. Ported from <c>filter.rs</c>'s
/// <c>MinimalFilter</c>.
/// </summary>
public sealed partial class MinimalFilter : IFilterStrategy
{
    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BuildMultipleBlankLinesRegex();

    private static readonly Regex MultipleBlankLines = BuildMultipleBlankLinesRegex();

    /// <inheritdoc />
    public string Filter(string content, Language language)
    {
        ArgumentNullException.ThrowIfNull(content);
        var patterns = language.CommentPatterns();
        var result = new StringBuilder(content.Length);
        var inBlockComment = false;
        var inDocstring = false;

        foreach (var line in SplitLines(content))
        {
            var trimmed = line.Trim();

            if (patterns.BlockStart is { } blockStart && patterns.BlockEnd is { } blockEnd)
            {
                if (!inDocstring
                    && trimmed.Contains(blockStart, StringComparison.Ordinal)
                    && !trimmed.StartsWith(patterns.DocBlockStart ?? "###", StringComparison.Ordinal))
                {
                    inBlockComment = true;
                }

                if (inBlockComment)
                {
                    if (trimmed.Contains(blockEnd, StringComparison.Ordinal))
                    {
                        inBlockComment = false;
                    }

                    continue;
                }
            }

            if (language == Language.Python && trimmed.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                inDocstring = !inDocstring;
                result.Append(line).Append('\n');
                continue;
            }

            if (inDocstring)
            {
                result.Append(line).Append('\n');
                continue;
            }

            if (patterns.Line is { } lineComment && trimmed.StartsWith(lineComment, StringComparison.Ordinal))
            {
                if (patterns.DocLine is { } doc && trimmed.StartsWith(doc, StringComparison.Ordinal))
                {
                    result.Append(line).Append('\n');
                }

                continue;
            }

            if (trimmed.Length == 0)
            {
                result.Append('\n');
                continue;
            }

            result.Append(line).Append('\n');
        }

        var normalized = MultipleBlankLines.Replace(result.ToString(), "\n\n");
        return normalized.Trim();
    }

    /// <summary>
    /// Splits text into lines exactly as Rust's <c>str::lines()</c> does — used internally so
    /// this class does not depend on any other command module.
    /// </summary>
    internal static List<string> SplitLines(string text) => SourceFilterLineSplitter.SplitLines(text);
}

/// <summary>
/// Keeps only imports, top-level signatures/declarations, and constants; collapses everything
/// else (function/type bodies) to their opening/closing braces plus an
/// <c>// ... implementation</c> marker. Data-format languages are never body-collapsed — they
/// fall back to <see cref="MinimalFilter"/>. Ported from <c>filter.rs</c>'s <c>AggressiveFilter</c>.
/// </summary>
public sealed partial class AggressiveFilter : IFilterStrategy
{
    [GeneratedRegex(
        @"^(use |import |from |require\(|#include)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BuildImportPatternRegex();

    [GeneratedRegex(
        @"^(pub\s+)?(async\s+)?(fn|def|function|func|class|struct|enum|trait|interface|type)\s+\w+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BuildFuncSignatureRegex();

    private static readonly Regex ImportPattern = BuildImportPatternRegex();
    private static readonly Regex FuncSignature = BuildFuncSignatureRegex();
    private static readonly MinimalFilter Minimal = new();

    /// <inheritdoc />
    public string Filter(string content, Language language)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (language == Language.Data)
        {
            return Minimal.Filter(content, language);
        }

        var minimal = Minimal.Filter(content, language);
        var result = new StringBuilder(minimal.Length / 2);
        var braceDepth = 0;
        var inImplBody = false;

        foreach (var line in MinimalFilter.SplitLines(minimal))
        {
            var trimmed = line.Trim();

            if (ImportPattern.IsMatch(trimmed))
            {
                result.Append(line).Append('\n');
                continue;
            }

            if (FuncSignature.IsMatch(trimmed))
            {
                result.Append(line).Append('\n');
                inImplBody = true;
                braceDepth = 0;
                continue;
            }

            var openBraces = trimmed.Count(c => c == '{');
            var closeBraces = trimmed.Count(c => c == '}');

            if (inImplBody)
            {
                braceDepth += openBraces;
                braceDepth -= closeBraces;

                if (braceDepth <= 1 && (trimmed == "{" || trimmed == "}" || trimmed.EndsWith('{')))
                {
                    result.Append(line).Append('\n');
                }

                if (braceDepth <= 0)
                {
                    inImplBody = false;
                    if (trimmed.Length != 0 && trimmed != "}")
                    {
                        result.Append("    // ... implementation\n");
                    }
                }

                continue;
            }

            if (trimmed.StartsWith("const ", StringComparison.Ordinal)
                || trimmed.StartsWith("static ", StringComparison.Ordinal)
                || trimmed.StartsWith("let ", StringComparison.Ordinal)
                || trimmed.StartsWith("pub const ", StringComparison.Ordinal)
                || trimmed.StartsWith("pub static ", StringComparison.Ordinal))
            {
                result.Append(line).Append('\n');
            }
        }

        return result.ToString().Trim();
    }
}

/// <summary>Selects the <see cref="IFilterStrategy"/> for a <see cref="FilterLevel"/>.</summary>
public static class SourceFilter
{
    /// <summary>
    /// Returns the strategy instance for <paramref name="level"/>. Mirrors <c>get_filter</c>.
    /// <see cref="FilterLevel.Ast"/> is out of scope here (it needs the detected
    /// <c>Language</c> to pick an analyzer, not just the level) — callers route it to
    /// <c>RtkSharp.Ast.AstFilter.Filter</c> instead of calling this method.
    /// </summary>
    /// <param name="level">The requested filter level.</param>
    /// <returns>The corresponding strategy.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is <see cref="FilterLevel.Ast"/> or otherwise unrecognized.</exception>
    public static IFilterStrategy GetFilter(FilterLevel level) => level switch
    {
        FilterLevel.None => new NoFilter(),
        FilterLevel.Minimal => new MinimalFilter(),
        FilterLevel.Aggressive => new AggressiveFilter(),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}

/// <summary>Shared Rust-<c>str::lines()</c>-semantics line splitter used by the filter strategies.</summary>
internal static class SourceFilterLineSplitter
{
    /// <summary>
    /// Splits text into lines on <c>\n</c>, stripping a trailing <c>\r</c> only from segments
    /// that were actually terminated by that <c>\n</c> (i.e. an interior <c>\r\n</c>), with no
    /// trailing empty entry after a final <c>\n</c>. A bare trailing <c>\r</c> with no following
    /// <c>\n</c> (the final, unterminated segment) is kept as-is — Rust's <c>str::lines()</c>
    /// only strips a <c>\r</c> when it immediately precedes a <c>\n</c>; e.g. <c>"a\nb\r"</c>
    /// yields <c>["a", "b\r"]</c>, not <c>["a", "b"]</c>. An empty string yields no lines.
    /// </summary>
    public static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        if (text.Length == 0)
        {
            return result;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i;
                if (end > start && text[end - 1] == '\r')
                {
                    end--;
                }

                result.Add(text[start..end]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            // Final, unterminated segment: no following '\n', so no '\r' stripping at all.
            result.Add(text[start..]);
        }

        return result;
    }
}
