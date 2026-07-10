using System.Text;
using RtkSharp.Core;
using TsLanguage = TreeSitter.Language;
using TsNode = TreeSitter.Node;
using TsParser = TreeSitter.Parser;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for Go, backed by the native <c>tree-sitter-go</c>
/// grammar via <c>TreeSitter.DotNet</c>. Parses real syntax rather than regexing text: keeps the
/// <c>package</c> clause, <c>import</c> declarations/blocks, and top-level
/// <c>func</c>/method/<c>type</c>/<c>const</c>/<c>var</c> declarations; collapses function and
/// method bodies to a single <c>{ /* ... */ }</c> placeholder. This mirrors the intent of the
/// reference <see cref="CSharpAstAnalyzer"/> ("keep imports + signatures, collapse bodies, keep
/// doc comments, drop noise"), adapted to Go's grammar and doc-comment convention.
/// </summary>
/// <remarks>
/// <para>
/// <b>Godoc comment handling.</b> Go has no dedicated doc-comment marker (no <c>///</c> or
/// <c>/** */</c> as in C#/Java/Rust). Its "godoc" convention is purely positional: a comment
/// block immediately preceding a top-level declaration — with no blank line between the comment
/// and the declaration — <i>is</i> that declaration's documentation. This analyzer applies exactly
/// that adjacency rule: a run of consecutive comment lines is kept only when it sits directly
/// above (no intervening blank line) a kept declaration. Comments that stand alone (separated from
/// any following declaration by a blank line) and comments inside function bodies are dropped. In
/// the tree-sitter-go grammar, comments are their own top-level siblings (not leading trivia
/// attached to a node), so adjacency is computed from source row positions.
/// </para>
/// <para>
/// <b>Scope.</b> Only the syntax tree is used — no type checking, no package resolution. Struct
/// field lists and interface method sets are kept verbatim (they are signature-level shape, not
/// bodies); only <c>function_declaration</c>/<c>method_declaration</c> bodies are collapsed. Any
/// top-level construct this analyzer does not specially recognize is kept verbatim rather than
/// dropped, matching the reference implementation's safe-fallback default. On any parse failure
/// (thrown exception, or a tree containing syntax errors) the original <paramref name="content"/>
/// is returned unchanged, per the interface contract.
/// </para>
/// </remarks>
public sealed class GoAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "{ /* ... */ }";

    /// <inheritdoc />
    public Language Language => Language.Go;

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
            using var language = new TsLanguage("Go");
            using var parser = new TsParser(language);
            using var tree = parser.Parse(content);
            if (tree is null)
            {
                return content;
            }

            var root = tree.RootNode;
            if (root.HasError)
            {
                // Malformed input: fall back to raw content rather than emit a mangled summary,
                // mirroring the reference analyzer's error-diagnostic fallback.
                return content;
            }

            var sb = new StringBuilder(content.Length);

            // Buffer of consecutive comment nodes that might be a godoc block for the next
            // declaration. Flushed (emitted) only when a declaration follows adjacently; dropped
            // otherwise. Each element is kept only while contiguous (no blank line) with the prior.
            var pendingComments = new List<TsNode>();

            foreach (var child in root.NamedChildren)
            {
                if (child.Type == "comment")
                {
                    // Break the run if this comment is not directly below the previous one (a blank
                    // line separates them) — only the contiguous block nearest a declaration counts.
                    if (pendingComments.Count > 0 && !IsAdjacent(pendingComments[^1], child))
                    {
                        pendingComments.Clear();
                    }

                    pendingComments.Add(child);
                    continue;
                }

                // A real declaration (or an unrecognized construct): decide whether the buffered
                // comment block documents it (adjacent, no blank line) before rendering it.
                if (pendingComments.Count > 0 && IsAdjacent(pendingComments[^1], child))
                {
                    foreach (var comment in pendingComments)
                    {
                        sb.Append(comment.Text).Append('\n');
                    }
                }

                pendingComments.Clear();
                AppendDeclaration(sb, child);
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return content;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="later"/> begins on the row immediately after
    /// <paramref name="earlier"/> ends — i.e. there is no blank line between them (Go's godoc
    /// adjacency rule).
    /// </summary>
    private static bool IsAdjacent(TsNode earlier, TsNode later) =>
        later.StartPosition.Row == earlier.EndPosition.Row + 1;

    private static void AppendDeclaration(StringBuilder sb, TsNode node)
    {
        switch (node.Type)
        {
            case "function_declaration":
            case "method_declaration":
                AppendSignatureWithCollapsedBody(sb, node);
                break;

            default:
                // package_clause, import_declaration, var/const/type declarations, and any
                // construct not specially handled: keep the real source verbatim. Struct fields
                // and interface method sets are signature-level shape and ride along here.
                sb.Append(node.Text.Trim()).Append('\n');
                break;
        }
    }

    private static void AppendSignatureWithCollapsedBody(StringBuilder sb, TsNode node)
    {
        var body = node.GetChildForField("body");
        var declText = node.Text;
        if (body is { } bodyNode && bodyNode.Text is { Length: > 0 } bodyText
            && declText.Length > bodyText.Length && declText.EndsWith(bodyText, StringComparison.Ordinal))
        {
            // The body block is the trailing element of a func/method declaration, so the header
            // (receiver, name, type params, params, result) is exactly the declaration's own source
            // with that trailing body text removed. Slicing by the body's .NET string length keeps
            // this correct even for non-ASCII source, where tree-sitter's byte indices would not
            // line up with UTF-16 char offsets.
            var header = declText[..^bodyText.Length].Trim();
            sb.Append(header).Append(' ').Append(BodyPlaceholder).Append('\n');
            return;
        }

        // No body block found (e.g. a forward declaration for a linked/asm function): already just
        // a signature, keep verbatim rather than fabricate a placeholder body.
        sb.Append(node.Text.Trim()).Append('\n');
    }
}
