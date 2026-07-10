using System.Text;
using RtkSharp.Core;
using TsLanguage = TreeSitter.Language;
using TsNode = TreeSitter.Node;
using TsParser = TreeSitter.Parser;
using TsTree = TreeSitter.Tree;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for Java, backed by the tree-sitter Java grammar
/// (<c>TreeSitter.DotNet</c>). Structurally mirrors <see cref="CSharpAstAnalyzer"/> — Java and C#
/// are close syntactically — but uses tree-sitter's untyped node tree instead of Roslyn's typed
/// syntax API: it keeps the <c>package</c> declaration, <c>import</c> statements, and
/// <c>class</c>/<c>interface</c>/<c>enum</c>/<c>record</c> declaration headers (with their full
/// modifiers, generics, <c>extends</c>/<c>implements</c> clauses), keeps member (method /
/// constructor / field) signatures, and collapses method and constructor bodies to a single
/// <c>{ /* ... */ }</c> placeholder. Javadoc comments (<c>/** ... */</c> immediately preceding a
/// declaration) are kept; ordinary <c>//</c> line comments and non-Javadoc <c>/* */</c> block
/// comments are dropped — the same "keep docs, drop noise" intent as <see cref="MinimalFilter"/>,
/// done against the real parse tree.
/// </summary>
/// <remarks>
/// <para>
/// tree-sitter is error-tolerant: a genuinely malformed file still yields a tree, but with
/// <c>ERROR</c> nodes that set <see cref="TsNode.HasError"/>. This analyzer treats any such tree
/// as a parse failure and returns the original <paramref name="content"/> unchanged (the
/// established fallback convention), rather than emitting a partially-mangled summary. Any parse
/// exception is caught to the same effect.
/// </para>
/// <para>
/// <b>Known limitations</b> (mirroring the C# reference's documented v1 caveats): a multi-line
/// signature — e.g. an annotation such as <c>@Override</c> on the line above the method — keeps
/// its original source line breaks and indentation rather than being re-leveled to this analyzer's
/// own nesting depth, because headers are raw-sliced from the source span between a declaration's
/// start and its body's start (via <see cref="TsTree.GetText"/>). A comment sitting *inside* a
/// signature (e.g. between parameters) would likewise survive. Declaration shapes not specially
/// handled (e.g. an annotation-type <c>@interface</c>, a static initializer block) are kept
/// verbatim rather than dropped — the safer default.
/// </para>
/// </remarks>
public sealed class JavaAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "{ /* ... */ }";
    private const string Indent = "    ";

    /// <inheritdoc />
    public Language Language => Language.Java;

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
            using var language = new TsLanguage("Java");
            using var parser = new TsParser(language);
            using var tree = parser.Parse(content);
            if (tree is null)
            {
                return content;
            }

            var root = tree.RootNode;
            if (root.HasError)
            {
                // Malformed input: tree-sitter still produced a tree, but it contains ERROR
                // nodes. Fall back to raw content rather than emit a mangled summary.
                return content;
            }

            var sb = new StringBuilder(content.Length);
            AppendChildren(sb, tree, root, indent: string.Empty);
            var result = sb.ToString().Trim();
            return result.Length == 0 ? content : result;
        }
        catch
        {
            // Any failure (grammar load, native interop, unexpected node shape) must never block
            // `rtk read` — return the original content unchanged.
            return content;
        }
    }

    /// <summary>
    /// Walks the named children of <paramref name="container"/> (the <c>program</c> root, or a
    /// class/interface/record body), emitting each declaration and threading a pending Javadoc
    /// comment onto the declaration that immediately follows it.
    /// </summary>
    private static void AppendChildren(StringBuilder sb, TsTree tree, TsNode container, string indent)
    {
        string? pendingDoc = null;
        foreach (var child in container.NamedChildren)
        {
            switch (child.Type)
            {
                case "line_comment":
                    // Ordinary comment: dropped, and it breaks any pending Javadoc association.
                    pendingDoc = null;
                    break;

                case "block_comment":
                    // Keep only Javadoc-style /** ... */ blocks, attached to the next declaration.
                    pendingDoc = IsJavadoc(child) ? child.Text : null;
                    break;

                default:
                    AppendDeclaration(sb, tree, child, indent, pendingDoc);
                    pendingDoc = null;
                    break;
            }
        }
    }

    private static void AppendDeclaration(StringBuilder sb, TsTree tree, TsNode node, string indent, string? pendingDoc)
    {
        switch (node.Type)
        {
            case "package_declaration":
            case "import_declaration":
                AppendDoc(sb, pendingDoc, indent);
                AppendVerbatim(sb, node, indent);
                break;

            case "class_declaration":
            case "interface_declaration":
            case "record_declaration":
                AppendDoc(sb, pendingDoc, indent);
                AppendTypeDeclaration(sb, tree, node, indent);
                break;

            case "enum_declaration":
                AppendDoc(sb, pendingDoc, indent);
                AppendEnumDeclaration(sb, tree, node, indent);
                break;

            case "method_declaration":
            case "constructor_declaration":
            case "compact_constructor_declaration":
                AppendDoc(sb, pendingDoc, indent);
                AppendMethod(sb, tree, node, indent);
                break;

            case "field_declaration":
            case "constant_declaration":
                AppendDoc(sb, pendingDoc, indent);
                AppendVerbatim(sb, node, indent);
                break;

            default:
                // Any declaration shape not explicitly handled (annotation-type @interface, a
                // static initializer, an incomplete fragment) is kept verbatim rather than
                // silently dropped — safer than losing content this analyzer doesn't model.
                AppendDoc(sb, pendingDoc, indent);
                AppendVerbatim(sb, node, indent);
                break;
        }
    }

    /// <summary>
    /// Emits a type declaration header (everything up to its body's opening brace) followed by
    /// its recursively-summarized body. A brace-less declaration (e.g. a semicolon-terminated
    /// positional <c>record Point(int x, int y);</c>) has no body child and is kept verbatim,
    /// since it is already just a signature.
    /// </summary>
    private static void AppendTypeDeclaration(StringBuilder sb, TsTree tree, TsNode node, string indent)
    {
        if (node.GetChildForField("body") is not { } body)
        {
            AppendVerbatim(sb, node, indent);
            return;
        }

        var header = HeaderText(tree, node.StartIndex, body.StartIndex);
        sb.Append(indent).Append(header).Append('\n')
            .Append(indent).Append("{\n");
        AppendChildren(sb, tree, body, indent + Indent);
        sb.Append(indent).Append("}\n");
    }

    /// <summary>
    /// Emits an enum declaration: header, then its constants verbatim (each with its own Javadoc,
    /// if present), then any member declarations that follow the constants' terminating
    /// semicolon (methods/fields), with method bodies collapsed like any other member.
    /// </summary>
    private static void AppendEnumDeclaration(StringBuilder sb, TsTree tree, TsNode node, string indent)
    {
        if (node.GetChildForField("body") is not { } body)
        {
            AppendVerbatim(sb, node, indent);
            return;
        }

        var header = HeaderText(tree, node.StartIndex, body.StartIndex);
        sb.Append(indent).Append(header).Append('\n')
            .Append(indent).Append("{\n");

        var innerIndent = indent + Indent;
        string? pendingDoc = null;
        foreach (var child in body.NamedChildren)
        {
            switch (child.Type)
            {
                case "line_comment":
                    pendingDoc = null;
                    break;

                case "block_comment":
                    pendingDoc = IsJavadoc(child) ? child.Text : null;
                    break;

                case "enum_constant":
                    AppendDoc(sb, pendingDoc, innerIndent);
                    pendingDoc = null;
                    sb.Append(innerIndent).Append(child.Text.Trim()).Append(",\n");
                    break;

                case "enum_body_declarations":
                    // The `;` plus any methods/fields after the constants. Recurse so method
                    // bodies inside an enum are collapsed just like class members.
                    pendingDoc = null;
                    AppendChildren(sb, tree, child, innerIndent);
                    break;

                default:
                    AppendDoc(sb, pendingDoc, innerIndent);
                    pendingDoc = null;
                    AppendVerbatim(sb, child, innerIndent);
                    break;
            }
        }

        sb.Append(indent).Append("}\n");
    }

    /// <summary>
    /// Emits a method or constructor signature with its body collapsed to
    /// <see cref="BodyPlaceholder"/>. A member with no body (abstract method, interface method) is
    /// already just a signature and is kept verbatim.
    /// </summary>
    private static void AppendMethod(StringBuilder sb, TsTree tree, TsNode node, string indent)
    {
        if (node.GetChildForField("body") is not { } body)
        {
            // Abstract/interface method: no body at all, the node text ends with `;`.
            AppendVerbatim(sb, node, indent);
            return;
        }

        var header = HeaderText(tree, node.StartIndex, body.StartIndex);
        sb.Append(indent).Append(header).Append(' ').Append(BodyPlaceholder).Append('\n');
    }

    private static void AppendVerbatim(StringBuilder sb, TsNode node, string indent) =>
        sb.Append(indent).Append(node.Text.Trim()).Append('\n');

    /// <summary>
    /// Returns the trimmed source text spanning <paramref name="startIndex"/> (inclusive) to
    /// <paramref name="endIndex"/> (exclusive) — i.e. everything before a body begins: modifiers,
    /// annotations, generics, return type, name, parameters, and a <c>throws</c> clause.
    /// </summary>
    private static string HeaderText(TsTree tree, int startIndex, int endIndex) =>
        tree.GetText(startIndex, endIndex).Trim();

    /// <summary>A <c>block_comment</c> is Javadoc iff its text opens with <c>/**</c>.</summary>
    private static bool IsJavadoc(TsNode node) =>
        node.Text.StartsWith("/**", StringComparison.Ordinal);

    /// <summary>
    /// Appends a pending Javadoc comment (if any) verbatim before the declaration it documents,
    /// one line at a time so each line picks up the current indent (matching the C# reference's
    /// per-line doc-comment emission).
    /// </summary>
    private static void AppendDoc(StringBuilder sb, string? doc, string indent)
    {
        if (doc is null)
        {
            return;
        }

        foreach (var line in doc.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length != 0)
            {
                sb.Append(indent).Append(trimmed).Append('\n');
            }
        }
    }
}
