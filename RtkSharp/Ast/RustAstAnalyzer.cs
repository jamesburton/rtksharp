using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for Rust, backed by the native tree-sitter grammar
/// (<c>TreeSitter.DotNet</c>, grammar <c>"Rust"</c>). Parses real syntax rather than regexing
/// text: keeps <c>use</c>/<c>mod</c> declarations, <c>struct</c>/<c>enum</c>/<c>trait</c>/
/// <c>impl</c>/<c>const</c>/<c>static</c> item signatures and free-function signatures, and
/// collapses function bodies (the <c>block</c> after a signature) to <c>{ /* ... */ }</c>. Outer
/// (<c>///</c>) and inner (<c>//!</c>) doc comments are kept; ordinary <c>//</c> / <c>/* */</c>
/// comments are dropped — the same "keep docs, drop noise" intent as <see cref="MinimalFilter"/>,
/// done against the parsed tree instead of by trimming trivia mid-line, and the direct analogue of
/// the Roslyn-backed <see cref="CSharpAstAnalyzer"/> for the C# case.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fallback convention.</b> Per <see cref="IAstAnalyzer.Filter(string)"/>, this never throws on
/// malformed input: if the grammar cannot be loaded, parsing throws, or the parsed tree contains a
/// syntax error (<see cref="TreeSitter.Node.HasError"/>), the original <c>content</c> is returned
/// unchanged rather than emitting a mangled summary — mirroring the whole port's established
/// fallback pattern.
/// </para>
/// <para>
/// <b>Doc-comment detection.</b> tree-sitter exposes every comment as a generic
/// <c>line_comment</c>/<c>block_comment</c> node, but the Rust grammar additionally attaches an
/// <c>outer_doc_comment_marker</c> (for <c>///</c>) or <c>inner_doc_comment_marker</c> (for
/// <c>//!</c>) child to doc comments specifically. This analyzer keys off those structured markers
/// (falling back to a textual <c>///</c>/<c>//!</c>/<c>/**</c>/<c>/*!</c> prefix check), so a plain
/// <c>////</c> line — which is an ordinary comment in Rust, not a doc comment — is correctly
/// dropped, something a naive <c>StartsWith("///")</c> test would get wrong.
/// </para>
/// <para>
/// <b>Header extraction is text-based, not offset-based.</b> A signature "header" (everything
/// before a body) is derived by taking an item's own <see cref="TreeSitter.Node.Text"/> and
/// stripping its body node's <see cref="TreeSitter.Node.Text"/> off the end (see
/// <see cref="HeaderBeforeBody"/>). This deliberately avoids slicing the source by the node's
/// reported <c>StartIndex</c>/<c>EndIndex</c>: in this binding those offsets and the source string
/// desynchronize once multi-byte UTF-8 text (an accented identifier, an emoji in a doc comment)
/// appears earlier in the file, which was observed to corrupt header slices (dropping a return
/// type, injecting stray characters). <see cref="TreeSitter.Node.Text"/> itself is Unicode-correct,
/// so building headers purely from it is robust regardless of the offsets' unit.
/// </para>
/// <para>
/// <b>Known v1 limitations</b> (proportional to the C# reference's — none corrupt output, they
/// only under-summarize or keep a little extra): a <c>struct</c>/<c>enum</c> is kept verbatim
/// (its fields/variants <em>are</em> its signature), which means an ordinary comment sitting
/// <em>inside</em> a field list survives, just as a comment inside a C# signature does; a
/// multi-line kept-verbatim item's continuation lines retain their original source indentation
/// rather than being re-leveled to this analyzer's nesting depth; a function-like <c>macro_rules!</c>
/// definition or any item type not explicitly handled is kept verbatim (full text) rather than
/// dropped — never silent loss; and a default trait-method / inline-<c>mod</c> body is collapsed
/// only one level deep via the same recursion the top level uses.
/// </para>
/// </remarks>
public sealed class RustAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "{ /* ... */ }";
    private const string GrammarName = "Rust";
    private const string IndentUnit = "    ";

    /// <inheritdoc />
    public Language Language => Language.Rust;

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
            using var language = new TreeSitter.Language(GrammarName);
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

            var sb = new StringBuilder(content.Length);
            AppendItems(sb, root, string.Empty);
            return sb.ToString().Trim();
        }
        catch
        {
            // Grammar load / P/Invoke / parse failure — never block `rtk read` on one file.
            return content;
        }
    }

    /// <summary>Appends every child item of <paramref name="parent"/> (in source order) at <paramref name="indent"/>.</summary>
    private static void AppendItems(StringBuilder sb, TreeSitter.Node parent, string indent)
    {
        foreach (var child in parent.Children)
        {
            AppendItem(sb, child, indent);
        }
    }

    private static void AppendItem(StringBuilder sb, TreeSitter.Node node, string indent)
    {
        if (IsComment(node))
        {
            AppendCommentIfDoc(sb, node, indent);
            return;
        }

        // Skip anonymous punctuation tokens (braces, semicolons) that appear as direct children of
        // a declaration_list — the recursion handles their container's shape explicitly.
        if (!node.IsNamed)
        {
            return;
        }

        switch (node.Type)
        {
            case "use_declaration":
            case "extern_crate_declaration":
            case "const_item":
            case "static_item":
            case "type_item":
            case "attribute_item":
            case "inner_attribute_item":
            case "associated_type":
            case "macro_invocation":
                AppendVerbatim(sb, node, indent);
                break;

            case "function_signature_item":
                // Already just a signature ending in `;` (e.g. a trait method requirement) — keep as-is.
                AppendVerbatim(sb, node, indent);
                break;

            case "struct_item":
            case "enum_item":
            case "union_item":
                // Fields/variants ARE the signature-level information worth keeping, so these are
                // preserved verbatim rather than body-collapsed.
                AppendVerbatim(sb, node, indent);
                break;

            case "function_item":
                AppendFunctionWithCollapsedBody(sb, node, indent);
                break;

            case "impl_item":
            case "trait_item":
                AppendContainerWithRecursedBody(sb, node, indent);
                break;

            case "mod_item":
                AppendModule(sb, node, indent);
                break;

            default:
                // Any item shape not explicitly handled is kept verbatim rather than silently
                // dropped — safer than losing content this analyzer doesn't yet model.
                AppendVerbatim(sb, node, indent);
                break;
        }
    }

    /// <summary>Emits a function's signature (everything before its <c>block</c> body) plus a collapsed-body placeholder.</summary>
    private static void AppendFunctionWithCollapsedBody(StringBuilder sb, TreeSitter.Node node, string indent)
    {
        if (FindChildByType(node, "block") is { } body)
        {
            var header = HeaderBeforeBody(node, body);
            sb.Append(indent).Append(header).Append(' ').Append(BodyPlaceholder).Append('\n');
            return;
        }

        // No block body found (unexpected for a function_item) — keep verbatim rather than guess.
        AppendVerbatim(sb, node, indent);
    }

    /// <summary>
    /// Emits an <c>impl</c>/<c>trait</c> header line, then recurses into its <c>declaration_list</c>
    /// body (collapsing nested function bodies, keeping doc comments and signatures).
    /// </summary>
    private static void AppendContainerWithRecursedBody(StringBuilder sb, TreeSitter.Node node, string indent)
    {
        if (FindChildByType(node, "declaration_list") is { } body)
        {
            var header = HeaderBeforeBody(node, body);
            sb.Append(indent).Append(header).Append(" {\n");
            AppendItems(sb, body, indent + IndentUnit);
            sb.Append(indent).Append("}\n");
            return;
        }

        // e.g. `trait Foo;`-style with no body — keep verbatim.
        AppendVerbatim(sb, node, indent);
    }

    /// <summary>Handles both <c>mod name;</c> (verbatim) and an inline <c>mod name { ... }</c> (recursed).</summary>
    private static void AppendModule(StringBuilder sb, TreeSitter.Node node, string indent)
    {
        if (FindChildByType(node, "declaration_list") is { } body)
        {
            var header = HeaderBeforeBody(node, body);
            sb.Append(indent).Append(header).Append(" {\n");
            AppendItems(sb, body, indent + IndentUnit);
            sb.Append(indent).Append("}\n");
            return;
        }

        AppendVerbatim(sb, node, indent);
    }

    /// <summary>
    /// Returns an item's signature text: its full <see cref="TreeSitter.Node.Text"/> with
    /// <paramref name="body"/>'s trailing text removed. Purely text-based (see the class remarks on
    /// why node offsets are not used).
    /// </summary>
    private static string HeaderBeforeBody(TreeSitter.Node node, TreeSitter.Node body)
    {
        var full = node.Text;
        var bodyText = body.Text;
        return full.EndsWith(bodyText, StringComparison.Ordinal)
            ? full[..^bodyText.Length].TrimEnd()
            : full.Trim();
    }

    /// <summary>
    /// Appends a comment node only if it is a doc comment (<c>///</c> outer / <c>//!</c> inner, or a
    /// <c>/**</c> / <c>/*!</c> block doc); ordinary comments are dropped entirely.
    /// </summary>
    private static void AppendCommentIfDoc(StringBuilder sb, TreeSitter.Node node, string indent)
    {
        if (!IsDocComment(node))
        {
            return;
        }

        foreach (var line in node.Text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                sb.Append(indent).Append(trimmed).Append('\n');
            }
        }
    }

    /// <summary>Appends a node's exact source text, prefixing the first line with <paramref name="indent"/>.</summary>
    private static void AppendVerbatim(StringBuilder sb, TreeSitter.Node node, string indent) =>
        sb.Append(indent).Append(node.Text.Trim()).Append('\n');

    private static bool IsComment(TreeSitter.Node node) =>
        node.Type is "line_comment" or "block_comment";

    /// <summary>
    /// True if a comment node is a Rust doc comment. Primary signal is the grammar's structured
    /// <c>outer_doc_comment_marker</c>/<c>inner_doc_comment_marker</c> child; a textual prefix check
    /// is used as a fallback (and correctly rejects a plain <c>////</c> ruler line, which is an
    /// ordinary comment, not a doc comment).
    /// </summary>
    private static bool IsDocComment(TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type is "outer_doc_comment_marker" or "inner_doc_comment_marker")
            {
                return true;
            }
        }

        var text = node.Text.TrimStart();
        if (text.StartsWith("///", StringComparison.Ordinal))
        {
            // `///` is a doc comment, but `////` (or more) is an ordinary comment in Rust.
            return !text.StartsWith("////", StringComparison.Ordinal);
        }

        return text.StartsWith("//!", StringComparison.Ordinal)
            || text.StartsWith("/**", StringComparison.Ordinal)
            || text.StartsWith("/*!", StringComparison.Ordinal);
    }

    private static TreeSitter.Node? FindChildByType(TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type)
            {
                return child;
            }
        }

        return null;
    }
}
