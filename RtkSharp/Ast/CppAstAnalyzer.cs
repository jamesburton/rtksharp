using System.Text;
using RtkSharp.Core;
using TsLanguage = TreeSitter.Language;
using TsNode = TreeSitter.Node;
using TsParser = TreeSitter.Parser;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for C++, backed by the native tree-sitter grammar
/// (<c>tree-sitter-cpp</c>) via the <c>TreeSitter.DotNet</c> bindings. Parses real syntax rather
/// than regexing text: keeps preprocessor directives (<c>#include</c>/<c>#define</c>/…),
/// <c>namespace</c> blocks, <c>class</c>/<c>struct</c>/<c>union</c>/<c>enum</c> declarations with
/// their member signatures, free-function signatures and <c>template</c> declarations; collapses
/// function/method bodies to a single <c>{ /* ... */ }</c> placeholder. Doxygen-style doc comments
/// (<c>/** … */</c>, <c>///</c>, <c>/*! … */</c>, <c>//!</c>) sitting immediately before a
/// declaration are kept; ordinary <c>//</c> / <c>/* */</c> comments are dropped — the same
/// "keep docs, drop noise" intent as <see cref="CSharpAstAnalyzer"/>, done AST-accurately.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grammar-name string.</b> The bindings load a native grammar by prefixing the id with
/// <c>tree-sitter-</c> (library) and <c>tree_sitter_</c> (entry point); the bundled DLL is
/// <c>tree-sitter-cpp.dll</c>, so the id <c>"Cpp"</c> is used (resolved case-insensitively on
/// Windows to <c>tree-sitter-cpp.dll</c>). Verified empirically: <c>new TreeSitter.Language("Cpp")</c>
/// loads successfully, whereas <c>"CPlusPlus"</c> throws <see cref="DllNotFoundException"/>.
/// </para>
/// <para>
/// <b>Scope / known limitations</b> (intentional — none corrupt output, they only under-summarize
/// or keep more than strictly necessary): a construct this analyzer does not special-case (e.g.
/// <c>extern "C" { … }</c> linkage blocks, top-level variable definitions with large initializers)
/// is emitted verbatim rather than dropped or mangled, because C++ syntax is too large to model
/// exhaustively and keeping-verbatim is the safe fallback. Continuation lines of a multi-line
/// signature retain their original source indentation rather than being re-leveled to this
/// analyzer's nesting depth (same limitation as <see cref="CSharpAstAnalyzer"/>). Because
/// tree-sitter is highly error-tolerant, a file that only <em>mostly</em> parses may still set
/// <see cref="TreeSitter.Node.HasError"/>; when it does, the whole file is returned verbatim
/// (fallback) rather than emitting a partial, possibly-misleading summary.
/// </para>
/// </remarks>
public sealed class CppAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "{ /* ... */ }";
    private const string GrammarName = "Cpp";
    private const string Indent = "    ";

    /// <inheritdoc />
    public Core.Language Language => Core.Language.Cpp;

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
            using var language = new TsLanguage(GrammarName);
            using var parser = new TsParser(language);
            using var tree = parser.Parse(content);
            if (tree is null)
            {
                return content;
            }

            var root = tree.RootNode;
            if (root is null)
            {
                return content;
            }

            if (root.HasError)
            {
                // Malformed input: fall back to raw content rather than emit a mangled summary.
                return content;
            }

            var sb = new StringBuilder(content.Length);
            AppendChildren(sb, NamedChildren(root), string.Empty);
            var result = sb.ToString().Trim();

            // Defensive: if the summary somehow came out empty for non-empty, meaningful input,
            // fall back to the original rather than silently returning nothing.
            return result.Length == 0 && content.Trim().Length != 0 ? content : result;
        }
        catch
        {
            // Never throw: any parser/native failure falls back to unchanged content, per the
            // IAstAnalyzer contract and this project's established fallback convention.
            return content;
        }
    }

    /// <summary>
    /// Appends the summarized form of each named child in <paramref name="children"/>, buffering
    /// Doxygen doc comments so they are emitted immediately before the declaration they document
    /// (and dropped otherwise, along with all ordinary comments).
    /// </summary>
    private static void AppendChildren(StringBuilder sb, List<TsNode> children, string indent)
    {
        var pendingDocs = new List<string>();
        foreach (var child in children)
        {
            if (child.Type == "comment")
            {
                if (IsDocComment(child.Text))
                {
                    pendingDocs.Add(child.Text);
                }
                else
                {
                    // A plain comment breaks adjacency between a preceding doc comment and any
                    // following declaration, so drop whatever docs were buffered too.
                    pendingDocs.Clear();
                }

                continue;
            }

            // Any non-comment node "consumes" the buffered doc comments as its documentation.
            foreach (var doc in pendingDocs)
            {
                AppendDoc(sb, doc, indent);
            }

            pendingDocs.Clear();
            AppendNode(sb, child, indent);
        }

        // Trailing doc comments document nothing (no following declaration) — drop them.
    }

    private static void AppendNode(StringBuilder sb, TsNode node, string indent)
    {
        switch (node.Type)
        {
            case "preproc_ifdef" or "preproc_if":
                AppendConditional(sb, node, indent, emitEndif: true);
                break;

            case "preproc_else" or "preproc_elif":
                AppendConditional(sb, node, indent, emitEndif: false);
                break;

            case "preproc_include" or "preproc_def" or "preproc_function_def"
                or "preproc_call" or "preproc_import":
                sb.Append(indent).Append(node.Text.Trim()).Append('\n');
                break;

            case "namespace_definition":
                AppendNamespace(sb, node, indent);
                break;

            case "class_specifier" or "struct_specifier" or "union_specifier":
                AppendRecord(sb, node, indent);
                break;

            case "function_definition":
                AppendFunction(sb, node, indent);
                break;

            case "template_declaration":
                AppendTemplate(sb, node, indent);
                break;

            case "access_specifier":
                // e.g. `public` / `private` / `protected` — the trailing `:` is a separate
                // anonymous sibling not included in this node's text, so re-add it.
                sb.Append(indent).Append(node.Text.Trim()).Append(":\n");
                break;

            case "enum_specifier":
                // Enumerators are already just names/values (no bodies to collapse); keep the
                // whole declaration verbatim and re-add the trailing `;`.
                sb.Append(indent).Append(node.Text.Trim()).Append(";\n");
                break;

            default:
                // declaration / field_declaration (prototypes, fields, typedefs, using-directives)
                // and anything not explicitly modeled: keep verbatim rather than risk corrupting
                // C++ syntax this analyzer does not understand.
                sb.Append(indent).Append(node.Text.Trim()).Append('\n');
                break;
        }
    }

    private static void AppendNamespace(StringBuilder sb, TsNode node, string indent)
    {
        var body = node.GetChildForField("body");
        var header = HeaderBeforeBody(node, body);
        sb.Append(indent).Append(header).Append('\n')
            .Append(indent).Append("{\n");
        if (body is not null)
        {
            AppendChildren(sb, NamedChildren(body), indent + Indent);
        }

        sb.Append(indent).Append("}\n");
    }

    private static void AppendRecord(StringBuilder sb, TsNode node, string indent)
    {
        var body = node.GetChildForField("body");
        if (body is null)
        {
            // Forward declaration (`class Foo;`) or an opaque form — keep verbatim, re-adding `;`.
            sb.Append(indent).Append(node.Text.Trim()).Append(";\n");
            return;
        }

        var header = HeaderBeforeBody(node, body);
        sb.Append(indent).Append(header).Append('\n')
            .Append(indent).Append("{\n");
        AppendChildren(sb, NamedChildren(body), indent + Indent);
        sb.Append(indent).Append("};\n");
    }

    private static void AppendFunction(StringBuilder sb, TsNode node, string indent)
    {
        var body = node.GetChildForField("body");
        if (body is null || body.Type != "compound_statement")
        {
            // No ordinary brace body to collapse (e.g. `= default;` / `= delete;`, or a
            // pure-specifier form) — keep the declaration verbatim.
            sb.Append(indent).Append(node.Text.Trim()).Append('\n');
            return;
        }

        var header = HeaderBeforeBody(node, body);
        sb.Append(indent).Append(header).Append(' ').Append(BodyPlaceholder).Append('\n');
    }

    private static void AppendTemplate(StringBuilder sb, TsNode node, string indent)
    {
        // A template_declaration wraps exactly one inner declaration (function/class/struct/…)
        // after its `template<...>` parameter list. Emit the `template<...>` prefix, then render
        // the inner declaration so its body still gets collapsed / its members still recursed.
        var inner = InnerTemplateDeclaration(node);
        if (inner is null)
        {
            sb.Append(indent).Append(node.Text.Trim()).Append('\n');
            return;
        }

        // Slice the `template<...>` prefix as everything before the inner declaration begins.
        // Subtracting the inner node's length from the end would be off-by-one when the
        // template_declaration span includes a trailing `;` that the inner class/struct span
        // does not (true for templated type declarations, not for templated functions), so locate
        // the inner declaration's text directly instead.
        var full = node.Text;
        var innerText = inner.Text;
        var idx = full.IndexOf(innerText, StringComparison.Ordinal);
        var prefix = idx > 0 ? full[..idx].Trim() : "template";

        sb.Append(indent).Append(prefix).Append('\n');
        AppendNode(sb, inner, indent);
    }

    private static void AppendConditional(StringBuilder sb, TsNode node, string indent, bool emitEndif)
    {
        // Preprocessor conditional blocks (`#ifndef FOO_H` include guards, `#ifdef DEBUG`, …)
        // commonly wrap real declarations — emit the directive line, then recurse into the
        // guarded declarations so they are still summarized (not kept verbatim wholesale).
        sb.Append(indent).Append(FirstLine(node.Text)).Append('\n');

        var name = node.GetChildForField("name");
        var condition = node.GetChildForField("condition");
        var bodyChildren = new List<TsNode>();
        foreach (var child in NamedChildren(node))
        {
            if ((name is not null && child.Equals(name)) || (condition is not null && child.Equals(condition)))
            {
                continue;
            }

            bodyChildren.Add(child);
        }

        AppendChildren(sb, bodyChildren, indent);

        if (emitEndif)
        {
            sb.Append(indent).Append("#endif\n");
        }
    }

    /// <summary>
    /// Returns the trimmed source text of <paramref name="node"/> with its trailing
    /// <paramref name="body"/> span removed — i.e. everything before the body/brace begins
    /// (modifiers, return type, name, parameters, template/base clauses, cv-qualifiers, …).
    /// Operates on decoded node text (not raw byte offsets), so it is Unicode-safe.
    /// </summary>
    private static string HeaderBeforeBody(TsNode node, TsNode? body)
    {
        var full = node.Text;
        if (body is null)
        {
            return full.Trim();
        }

        var bodyText = body.Text;
        return full.Length >= bodyText.Length
            ? full[..(full.Length - bodyText.Length)].Trim()
            : full.Trim();
    }

    private static TsNode? InnerTemplateDeclaration(TsNode node)
    {
        var parameters = node.GetChildForField("parameters");
        TsNode? inner = null;
        foreach (var child in NamedChildren(node))
        {
            if (child.Type == "comment"
                || child.Type == "template_parameter_list"
                || (parameters is not null && child.Equals(parameters)))
            {
                continue;
            }

            inner = child;
        }

        return inner;
    }

    private static void AppendDoc(StringBuilder sb, string docText, string indent)
    {
        foreach (var line in docText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                sb.Append(indent).Append(trimmed).Append('\n');
            }
        }
    }

    private static bool IsDocComment(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("/**", StringComparison.Ordinal)
            || t.StartsWith("///", StringComparison.Ordinal)
            || t.StartsWith("/*!", StringComparison.Ordinal)
            || t.StartsWith("//!", StringComparison.Ordinal);
    }

    private static string FirstLine(string text)
    {
        var i = text.IndexOf('\n');
        return (i < 0 ? text : text[..i]).Trim();
    }

    private static List<TsNode> NamedChildren(TsNode node)
    {
        var result = new List<TsNode>();
        foreach (var child in node.NamedChildren)
        {
            result.Add(child);
        }

        return result;
    }
}
