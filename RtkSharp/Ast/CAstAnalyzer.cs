using System.Text;
using RtkSharp.Core;
using TreeSitter;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for C, backed by the native <c>tree-sitter-c</c>
/// grammar via <see cref="TreeSitter"/> (the <c>TreeSitter.DotNet</c> package). Parses real
/// syntax rather than regexing text: keeps preprocessor directives (<c>#include</c>/<c>#define</c>/
/// <c>#ifdef</c>…<c>#endif</c>), top-level function signatures, and <c>struct</c>/<c>union</c>/
/// <c>enum</c>/<c>typedef</c> declarations; collapses each function body's
/// <c>compound_statement</c> to a single <c>{ /* ... */ }</c> placeholder. Doxygen-style doc
/// comments (<c>/** … */</c>, <c>/*! … */</c>, <c>///</c>, <c>//!</c>) immediately preceding a
/// kept declaration are preserved; all other comments are dropped — the same "keep docs, drop
/// noise" intent as the C# reference (<see cref="CSharpAstAnalyzer"/>), done AST-accurately.
/// </summary>
/// <remarks>
/// <para>
/// <b>Preprocessor conditionals are recursed into, not kept opaque.</b> This matters because C
/// header files are almost universally wrapped in an include guard
/// (<c>#ifndef FOO_H / #define FOO_H / … / #endif</c>); treating the guard's
/// <c>preproc_ifdef</c> node as verbatim text would keep the entire header body unsummarized,
/// defeating the whole point for the most common C input. Instead the guard/<c>#if</c>/<c>#else</c>
/// directive lines are re-emitted and the guarded declarations are summarized normally, so a
/// function defined inside <c>#ifdef DEBUG</c> still has its body collapsed.
/// </para>
/// <para>
/// <b>Scope &amp; known limitations</b> (proportional to the C# reference, not exhaustive; none
/// corrupt output — they only under-summarize or keep slightly more detail): <c>struct</c>/
/// <c>union</c>/<c>enum</c> bodies are kept in full (their fields are the declaration's signature,
/// analogous to the C# reference keeping fields); a comment sitting <i>inside</i> a function
/// signature survives, since the header is raw-sliced rather than trivia-stripped; a <c>#endif</c>
/// trailing comment (e.g. <c>#endif // FOO_H</c>) is
/// re-emitted as a bare <c>#endif</c>. On any parse error (<see cref="Node.HasError"/>) or
/// exception the raw <paramref name="content"/> is returned unchanged, per this port's fallback
/// convention.
/// </para>
/// </remarks>
public sealed class CAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "{ /* ... */ }";

    /// <inheritdoc />
    public Core.Language Language => Core.Language.C;

    /// <inheritdoc />
    public string Filter(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            return content;
        }

        try
        {
            using var language = new TreeSitter.Language("C");
            using var parser = new TreeSitter.Parser(language);
            using var tree = parser.Parse(content);
            if (tree is null)
            {
                return content;
            }

            var root = tree.RootNode;
            if (root.HasError)
            {
                // Malformed input: fall back to raw content rather than emit a mangled summary.
                return content;
            }

            var sb = new StringBuilder();
            AppendChildList(sb, root.Children.ToList());
            return sb.ToString().Trim();
        }
        catch
        {
            // Any failure in the native parser or our walk must never block `rtk read`.
            return content;
        }
    }

    /// <summary>
    /// Walks an ordered sibling list (the translation unit, or a preprocessor conditional's body),
    /// emitting a summarized form of each declaration and pairing preceding Doxygen doc comments
    /// with the declaration they document.
    /// </summary>
    private static void AppendChildList(StringBuilder sb, IReadOnlyList<Node> children)
    {
        // Buffers comments seen since the last emitted declaration, so a doc comment can be
        // attached to the declaration that immediately follows it (and ordinary comments dropped).
        var commentBuffer = new List<Node>();

        for (var i = 0; i < children.Count; i++)
        {
            var node = children[i];
            var type = node.Type;

            if (type == "comment")
            {
                commentBuffer.Add(node);
                continue;
            }

            // Stray anonymous punctuation (e.g. a lone ';' not consumed by a preceding record) —
            // skip without disturbing the pending comment buffer.
            if (!node.IsNamed)
            {
                continue;
            }

            switch (type)
            {
                case "preproc_ifdef":
                case "preproc_ifndef":
                case "preproc_if":
                    FlushDocComments(sb, commentBuffer, node);
                    AppendPreprocConditional(sb, node, emitEndif: true);
                    break;

                case "function_definition":
                    FlushDocComments(sb, commentBuffer, node);
                    AppendFunctionDefinition(sb, node);
                    break;

                case "struct_specifier":
                case "union_specifier":
                case "enum_specifier":
                    FlushDocComments(sb, commentBuffer, node);
                    // These nodes exclude the trailing ';' terminator, which is a sibling — pull it
                    // in so a kept record declaration stays syntactically complete.
                    var text = (node.Text ?? string.Empty).Trim();
                    if (i + 1 < children.Count && children[i + 1].Type == ";")
                    {
                        text += ";";
                        i++;
                    }

                    sb.Append(text).Append('\n');
                    break;

                default:
                    // preproc_include / preproc_def / preproc_function_def / preproc_call /
                    // type_definition (typedef) / declaration (prototypes, globals) and any other
                    // top-level construct we don't specially handle: keep verbatim rather than
                    // drop content this analyzer doesn't understand.
                    FlushDocComments(sb, commentBuffer, node);
                    sb.Append((node.Text ?? string.Empty).Trim()).Append('\n');
                    break;
            }

            commentBuffer.Clear();
        }
    }

    /// <summary>
    /// Re-emits a preprocessor conditional's directive line(s) and recursively summarizes the
    /// guarded declarations, so an include guard or <c>#ifdef</c> block does not keep its whole
    /// body verbatim. <paramref name="emitEndif"/> is <see langword="true"/> only for the opening
    /// <c>#ifdef</c>/<c>#ifndef</c>/<c>#if</c> (which owns the single closing <c>#endif</c>), and
    /// <see langword="false"/> for a nested <c>#else</c>/<c>#elif</c> alternative.
    /// </summary>
    private static void AppendPreprocConditional(StringBuilder sb, Node node, bool emitEndif)
    {
        var openRow = node.StartPosition.Row;
        sb.Append(FirstLine(node.Text)).Append('\n');

        var body = new List<Node>();
        Node? alternative = null;

        foreach (var child in node.Children)
        {
            // Directive tokens (#ifdef/#ifndef/#if/#elif/#else/#endif) are anonymous and begin
            // with '#'; the condition/name tokens sit on the opening directive line. Neither is
            // part of the guarded body.
            if (!child.IsNamed && (child.Text ?? string.Empty).TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (child.Type is "preproc_else" or "preproc_elif")
            {
                alternative = child;
                continue;
            }

            if (child.StartPosition.Row == openRow)
            {
                continue;
            }

            body.Add(child);
        }

        AppendChildList(sb, body);

        if (alternative is not null)
        {
            AppendPreprocConditional(sb, alternative, emitEndif: false);
        }

        if (emitEndif)
        {
            sb.Append("#endif").Append('\n');
        }
    }

    /// <summary>
    /// Emits a function's signature (everything up to its body) followed by a collapsed
    /// <see cref="BodyPlaceholder"/>. Falls back to keeping the whole definition verbatim if the
    /// body can't be located or the header span can't be sliced cleanly.
    /// </summary>
    private static void AppendFunctionDefinition(StringBuilder sb, Node node)
    {
        Node? body = null;
        foreach (var child in node.Children)
        {
            if (child.Type == "compound_statement")
            {
                body = child;
                break;
            }
        }

        var full = node.Text ?? string.Empty;
        var bodyText = body?.Text ?? string.Empty;

        // Slice by trailing-suffix removal on decoded (UTF-16) Node.Text, NOT by subtracting
        // tree-sitter's native byte offsets (StartIndex/EndIndex) and applying that byte delta as
        // a .NET char count — those diverge for any signature containing a multi-byte UTF-8
        // character, silently mis-slicing past the true header boundary. The compound_statement
        // body is always the trailing element of a function_definition, so EndsWith-based suffix
        // removal is exact and encoding-safe (the same approach GoAstAnalyzer/CppAstAnalyzer use).
        if (body is null || bodyText.Length == 0 || !full.EndsWith(bodyText, StringComparison.Ordinal))
        {
            sb.Append(full.Trim()).Append('\n');
            return;
        }

        var header = full[..^bodyText.Length].TrimEnd();
        sb.Append(header).Append(' ').Append(BodyPlaceholder).Append('\n');
    }

    /// <summary>
    /// Emits the Doxygen doc comments from <paramref name="commentBuffer"/> that document
    /// <paramref name="declaration"/> — i.e. a contiguous run of doc-style comments ending
    /// immediately above the declaration with no blank line in between. Non-doc comments, and doc
    /// comments separated by a blank line, are dropped.
    /// </summary>
    private static void FlushDocComments(StringBuilder sb, List<Node> commentBuffer, Node declaration)
    {
        var attached = new List<string>();
        var prevStartRow = declaration.StartPosition.Row;

        for (var k = commentBuffer.Count - 1; k >= 0; k--)
        {
            var comment = commentBuffer[k];
            var text = comment.Text ?? string.Empty;

            // Adjacent means the next thing starts on the very next line (gap of 1) or the same
            // line (gap of 0); a gap of 2+ is a blank line, which detaches the comment.
            var adjacent = prevStartRow - comment.EndPosition.Row <= 1;
            if (!IsDocComment(text) || !adjacent)
            {
                break;
            }

            attached.Insert(0, text.Trim());
            prevStartRow = comment.StartPosition.Row;
        }

        foreach (var doc in attached)
        {
            sb.Append(doc).Append('\n');
        }
    }

    /// <summary>
    /// Determines whether a comment is a Doxygen-style documentation comment: <c>/** … */</c>
    /// (but not the empty <c>/**/</c>), <c>/*! … */</c>, <c>///</c>, or <c>//!</c>.
    /// </summary>
    private static bool IsDocComment(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("/**", StringComparison.Ordinal))
        {
            return !trimmed.StartsWith("/**/", StringComparison.Ordinal);
        }

        return trimmed.StartsWith("/*!", StringComparison.Ordinal)
            || trimmed.StartsWith("///", StringComparison.Ordinal)
            || trimmed.StartsWith("//!", StringComparison.Ordinal);
    }

    /// <summary>Returns the first physical line of <paramref name="text"/>, trimmed.</summary>
    private static string FirstLine(string? text)
    {
        var value = text ?? string.Empty;
        var newline = value.IndexOf('\n');
        var firstLine = newline < 0 ? value : value[..newline];
        return firstLine.TrimEnd('\r').Trim();
    }
}
