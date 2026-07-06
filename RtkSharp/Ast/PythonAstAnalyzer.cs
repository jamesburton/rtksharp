using System.Text;
using RtkSharp.Core;
using TsLanguage = TreeSitter.Language;
using TsNode = TreeSitter.Node;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for Python, backed by the native tree-sitter Python
/// grammar via <c>TreeSitter.DotNet</c>. Parses real syntax rather than regexing text: keeps
/// <c>import</c>/<c>from ... import</c> statements, <c>def</c>/<c>class</c> signatures (with their
/// decorators), and module/function/class docstrings; collapses function/method bodies to a single
/// <c>...</c> placeholder line. Plain <c>#</c> comments are dropped; any top-level statement this
/// analyzer does not specially handle (assignments, <c>if __name__ == ...</c> guards, etc.) is kept
/// verbatim rather than silently dropped — the same "keep signatures + docs, drop noise, never lose
/// unknown content" intent as <see cref="CSharpAstAnalyzer"/>, done AST-accurately for Python.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placeholder choice.</b> Python is indentation-sensitive, so a brace-style placeholder (as C#
/// uses) would be syntactically meaningless. Collapsed bodies are replaced with a single
/// <c>...</c> (the <c>Ellipsis</c> stub Python itself accepts as a body) at the correct indent,
/// e.g.:
/// <code>
/// def greet(name: str) -&gt; str:
///     "Docstring kept."
///     ...
/// </code>
/// The result is still valid, re-parseable Python. A function's docstring (if present) is emitted
/// above the <c>...</c> so the collapse never discards documentation.
/// </para>
/// <para>
/// <b>Docstrings vs comments.</b> A Python docstring is not a comment — it is a real string-literal
/// statement that is the first statement of a module/function/class body (in this grammar, a bare
/// <c>string</c> node as the first non-<c>comment</c> child of a <c>block</c>/<c>module</c>). Those
/// are kept. Ordinary <c>#</c> <c>comment</c> nodes are structurally distinct and are dropped.
/// </para>
/// <para>
/// <b>Fallback.</b> If tree-sitter cannot parse the input into an error-free tree (the root node
/// reports <c>HasError</c>), or anything throws, <see cref="Filter"/> returns the original content
/// unchanged — mirroring this port's established fallback convention so one malformed file never
/// blocks <c>rtk read</c>.
/// </para>
/// <para>
/// <b>Known limitations</b> (kept-more-than-necessary, never corruption): a trailing inline comment
/// on a <c>def</c>/<c>class</c> header line survives, since the signature is raw-sliced from source
/// up to the body block; a multi-line signature's or a kept multi-line statement's continuation
/// lines retain their original source indentation rather than being re-leveled to this analyzer's
/// nesting depth; an unhandled compound statement (e.g. a module-level <c>if</c>/<c>for</c>/<c>with</c>)
/// is kept verbatim with its full body rather than being collapsed.
/// </para>
/// </remarks>
public sealed class PythonAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "...";
    private const string IndentUnit = "    ";

    /// <inheritdoc />
    public Language Language => Language.Python;

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
            using var language = new TsLanguage("Python");
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

            var sb = new StringBuilder(content.Length / 2);
            AppendStatements(sb, root, indent: string.Empty);
            return sb.ToString().Trim();
        }
        catch
        {
            // Any parser/native failure falls back to the raw content (never throw, never block).
            return content;
        }
    }

    /// <summary>
    /// Appends the summarized form of every statement in <paramref name="container"/>'s body
    /// (a <c>module</c> or <c>block</c> node), at <paramref name="indent"/>. Returns whether any
    /// output was produced (used by the caller to emit a <c>...</c> stub for an otherwise-empty
    /// class body, which would be invalid Python if left truly empty).
    /// </summary>
    private static bool AppendStatements(StringBuilder sb, TsNode container, string indent)
    {
        var emittedAny = false;
        foreach (var statement in container.NamedChildren)
        {
            if (AppendStatement(sb, statement, indent))
            {
                emittedAny = true;
            }
        }

        return emittedAny;
    }

    private static bool AppendStatement(StringBuilder sb, TsNode statement, string indent)
    {
        switch (statement.Type)
        {
            case "comment":
                // Ordinary '#' comment — dropped (docstrings are 'string' nodes, handled below).
                return false;

            case "import_statement":
            case "import_from_statement":
                AppendVerbatim(sb, statement, indent);
                return true;

            case "decorated_definition":
                AppendDecoratedDefinition(sb, statement, indent);
                return true;

            case "function_definition":
                AppendFunction(sb, statement, indent);
                return true;

            case "class_definition":
                AppendClass(sb, statement, indent);
                return true;

            default:
                // Docstrings ('string'), module/class-level assignments, and any compound
                // statement this analyzer does not specially handle are kept verbatim rather
                // than dropped — safer than losing content we don't summarize.
                AppendVerbatim(sb, statement, indent);
                return true;
        }
    }

    private static void AppendDecoratedDefinition(StringBuilder sb, TsNode decorated, string indent)
    {
        foreach (var child in decorated.NamedChildren)
        {
            if (child.Type == "decorator")
            {
                AppendVerbatim(sb, child, indent);
            }
        }

        if (decorated.GetChildForField("definition") is { } definition)
        {
            AppendStatement(sb, definition, indent);
        }
    }

    private static void AppendFunction(StringBuilder sb, TsNode function, string indent)
    {
        if (function.GetChildForField("body") is not { } body)
        {
            // No body to collapse (shouldn't happen for a well-formed def) — keep verbatim.
            AppendVerbatim(sb, function, indent);
            return;
        }

        AppendSignature(sb, function, body, indent);

        var bodyIndent = indent + IndentUnit;
        if (FirstMeaningfulStatement(body) is { Type: "string" } docstring)
        {
            AppendVerbatim(sb, docstring, bodyIndent);
        }

        sb.Append(bodyIndent).Append(BodyPlaceholder).Append('\n');
    }

    private static void AppendClass(StringBuilder sb, TsNode classNode, string indent)
    {
        if (classNode.GetChildForField("body") is not { } body)
        {
            AppendVerbatim(sb, classNode, indent);
            return;
        }

        AppendSignature(sb, classNode, body, indent);

        // A class body is NOT wholesale-collapsed: member signatures/docstrings are recursively
        // summarized. Only if that produces nothing at all do we emit a '...' stub, since a class
        // with a truly empty body is a syntax error.
        var bodyIndent = indent + IndentUnit;
        var emitted = AppendStatements(sb, body, bodyIndent);
        if (!emitted)
        {
            sb.Append(bodyIndent).Append(BodyPlaceholder).Append('\n');
        }
    }

    /// <summary>
    /// Appends the <c>def</c>/<c>class</c> signature — everything from the definition's start up to
    /// (but not including) its body block — raw-sliced from source and right-trimmed, so the
    /// verbatim header (name, parameters, return annotation, base list, trailing colon) is kept
    /// while the body is dropped.
    /// </summary>
    private static void AppendSignature(StringBuilder sb, TsNode definition, TsNode body, string indent)
    {
        var header = definition.Tree.GetText(definition.StartIndex, SignatureEndIndex(definition, body)).TrimEnd();
        sb.Append(indent).Append(header).Append('\n');
    }

    /// <summary>
    /// Returns the source index at which the signature ends: the end of the <c>:</c> token that
    /// introduces the body. Slicing merely up to the body block's start would sweep in a comment
    /// (or blank lines) sitting on its own line between the header colon and the first body
    /// statement, since tree-sitter attaches such a comment to the definition, not the block.
    /// </summary>
    private static int SignatureEndIndex(TsNode definition, TsNode body)
    {
        foreach (var child in definition.Children)
        {
            if (child.Type == ":" && child.StartIndex < body.StartIndex)
            {
                return child.EndIndex;
            }
        }

        return body.StartIndex;
    }

    /// <summary>
    /// Returns the first non-<c>comment</c> named child of <paramref name="block"/> (its first real
    /// statement), or <see langword="null"/> if it has none — used to detect a leading docstring.
    /// </summary>
    private static TsNode? FirstMeaningfulStatement(TsNode block)
    {
        foreach (var child in block.NamedChildren)
        {
            if (child.Type != "comment")
            {
                return child;
            }
        }

        return null;
    }

    private static void AppendVerbatim(StringBuilder sb, TsNode node, string indent) =>
        sb.Append(indent).Append(node.Text).Append('\n');
}
