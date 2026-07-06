using System.Text;
using RtkSharp.Core;
using TsNode = TreeSitter.Node;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for TypeScript, backed by the native tree-sitter
/// "TypeScript" grammar (via <c>TreeSitter.DotNet</c>). Parses real syntax rather than regexing
/// text: keeps <c>import</c>/<c>export</c> statements, <c>interface</c>/<c>type</c>/<c>class</c>/
/// <c>function</c>/<c>enum</c> declarations with their FULL type signatures (generics, parameter
/// and return-type annotations, <c>extends</c>/<c>implements</c> clauses), and JSDoc
/// (<c>/** ... */</c>) comments; collapses function/method/accessor bodies to a
/// <c>{ /* ... */ }</c> placeholder; drops plain <c>//</c> and <c>/* ... */</c> comments. This is
/// the same "keep imports + signatures + docs, collapse bodies, drop noise" intent as
/// <see cref="CSharpAstAnalyzer"/>, done for TypeScript's grammar.
/// </summary>
/// <remarks>
/// <para>
/// Tree-sitter exposes comments as a single generic <c>comment</c> node kind with no doc/non-doc
/// distinction, so JSDoc is recognized textually — a comment whose text starts with <c>/**</c> is
/// treated as documentation and kept; every other comment (<c>//</c> line comments and plain
/// <c>/* ... */</c> blocks) is dropped. Body collapsing is done by slicing a declaration's own
/// source text up to where its <c>statement_block</c> body begins (the body is always the trailing
/// child of a function/method), so the kept signature is byte-for-byte the original — generics,
/// constraints, and return-type annotations included — without depending on byte-vs-char index
/// equivalence (the slice is computed by trailing-suffix removal on the node's text, which is
/// Unicode-safe).
/// </para>
/// <para>
/// <b>Scope / known limitations</b> (proportional to the C# reference, not exhaustive; none
/// corrupt output — they only under-summarize): a class field initialized with an arrow function
/// (<c>foo = () =&gt; { ... }</c>) is kept verbatim with its body, since fields are not
/// body-collapsed; a top-level <c>const</c> arrow function is likewise kept verbatim; a
/// <c>namespace</c>/<c>module</c> block is kept verbatim (its inner member bodies are not
/// collapsed), since it is reached via the generic verbatim fallback rather than a dedicated
/// container path; a multi-line signature's continuation lines keep their original source
/// indentation rather than being re-leveled to this analyzer's nesting depth. Anything this
/// analyzer does not specially handle is emitted verbatim rather than dropped.
/// </para>
/// </remarks>
public sealed class TypeScriptAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "{ /* ... */ }";
    private const string GrammarName = "TypeScript";

    /// <inheritdoc />
    public Language Language => Language.TypeScript;

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
                // Malformed input: fall back to raw content rather than emit a mangled summary,
                // mirroring the port's established fallback convention.
                return content;
            }

            var sb = new StringBuilder(content.Length);
            foreach (var child in root.NamedChildren)
            {
                AppendNode(sb, child, indent: string.Empty, linePrefix: string.Empty);
            }

            return sb.ToString().Trim();
        }
        catch
        {
            // Native grammar load failure, or any other unexpected parser error: never throw —
            // return the content unchanged so a single file cannot block `rtk read`.
            return content;
        }
    }

    private static void AppendNode(StringBuilder sb, TsNode node, string indent, string linePrefix)
    {
        switch (node.Type)
        {
            case "comment":
                AppendCommentIfDoc(sb, node, indent);
                break;

            case "export_statement":
                AppendExport(sb, node, indent, linePrefix);
                break;

            case "class_declaration":
            case "abstract_class_declaration":
            case "interface_declaration":
                AppendContainer(sb, node, indent, linePrefix);
                break;

            case "function_declaration":
            case "generator_function_declaration":
            case "method_definition":
                AppendCollapsibleBody(sb, node, indent, linePrefix);
                break;

            default:
                // imports, type aliases, enums, const/let, fields, interface member signatures,
                // top-level statements, and anything not specially handled are kept verbatim.
                EmitVerbatim(sb, node, indent, linePrefix);
                break;
        }
    }

    private static void AppendExport(StringBuilder sb, TsNode node, string indent, string linePrefix)
    {
        var declaration = node.GetChildForField("declaration");
        if (declaration is not { } decl)
        {
            // Re-export / `export { ... }` / `export default <expr>` / `export * from` — no inner
            // declaration to recurse into; keep the whole statement verbatim.
            EmitVerbatim(sb, node, indent, linePrefix);
            return;
        }

        // The text between the export node's start and the declaration's start is the export
        // prefix ("export ", "export default ", "export abstract ", ...) — always ASCII, so the
        // byte-offset delta equals the character count and is safe to slice from node.Text.
        var delta = decl.StartIndex - node.StartIndex;
        var text = node.Text;
        var prefix = delta > 0 && delta <= text.Length
            ? text.Substring(0, delta)
            : "export ";

        AppendNode(sb, decl, indent, linePrefix + prefix);
    }

    private static void AppendContainer(StringBuilder sb, TsNode node, string indent, string linePrefix)
    {
        var body = node.GetChildForField("body");
        if (body is not { } bodyNode)
        {
            EmitVerbatim(sb, node, indent, linePrefix);
            return;
        }

        var header = HeaderBeforeBody(node, bodyNode);
        sb.Append(indent).Append(linePrefix).Append(header).Append('\n');
        sb.Append(indent).Append("{\n");
        foreach (var member in bodyNode.NamedChildren)
        {
            AppendNode(sb, member, indent + "    ", linePrefix: string.Empty);
        }

        sb.Append(indent).Append("}\n");
    }

    private static void AppendCollapsibleBody(StringBuilder sb, TsNode node, string indent, string linePrefix)
    {
        var body = node.GetChildForField("body");
        if (body is not { Type: "statement_block" } bodyNode)
        {
            // No block body (e.g. an ambient/overload signature, or an abstract member) — it is
            // already just a signature, so keep it verbatim.
            EmitVerbatim(sb, node, indent, linePrefix);
            return;
        }

        var header = HeaderBeforeBody(node, bodyNode);
        sb.Append(indent).Append(linePrefix).Append(header).Append(' ').Append(BodyPlaceholder).Append('\n');
    }

    /// <summary>
    /// Returns the declaration's source text up to (but not including) its trailing body node,
    /// with trailing whitespace trimmed. Computed by removing the body's text as a suffix of the
    /// node's text (the body is always the final child of a function/method/class/interface), which
    /// is Unicode-safe — it never converts between byte offsets and character indices.
    /// </summary>
    private static string HeaderBeforeBody(TsNode node, TsNode body)
    {
        var text = node.Text;
        var bodyText = body.Text;
        if (text.Length >= bodyText.Length && text.EndsWith(bodyText, StringComparison.Ordinal))
        {
            return text.Substring(0, text.Length - bodyText.Length).TrimEnd();
        }

        return text.TrimEnd();
    }

    private static void EmitVerbatim(StringBuilder sb, TsNode node, string indent, string linePrefix)
    {
        var text = node.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        sb.Append(indent).Append(linePrefix).Append(text).Append('\n');
    }

    /// <summary>
    /// Appends a <c>comment</c> node only if it is JSDoc-like — its text starts with <c>/**</c>.
    /// Plain <c>//</c> line comments and plain <c>/* ... */</c> block comments are dropped. Kept
    /// comments are re-emitted line-by-line, each trimmed and re-indented to this nesting depth.
    /// </summary>
    private static void AppendCommentIfDoc(StringBuilder sb, TsNode node, string indent)
    {
        var text = node.Text;
        if (!text.StartsWith("/**", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length != 0)
            {
                sb.Append(indent).Append(line).Append('\n');
            }
        }
    }
}
