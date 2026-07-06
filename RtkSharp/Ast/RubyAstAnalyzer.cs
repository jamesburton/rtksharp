using System.Text;
using RtkSharp.Core;
using TSNode = TreeSitter.Node;
using TSTree = TreeSitter.Tree;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for Ruby, backed by the native tree-sitter Ruby
/// grammar via <c>TreeSitter.DotNet</c>. Parses real syntax rather than regexing text: keeps
/// <c>require</c>/<c>require_relative</c> statements, <c>class</c>/<c>module</c> declaration
/// headers (recursing into their bodies), <c>def</c>/<c>def self.x</c> method signatures, and
/// <c>attr_accessor</c>/<c>attr_reader</c>/<c>attr_writer</c> declarations; method bodies are
/// collapsed to a single <c># ...</c> placeholder line followed by the method's own <c>end</c>.
/// The same "keep docs, drop noise" intent as <see cref="CSharpAstAnalyzer"/>, adapted to Ruby.
/// </summary>
/// <remarks>
/// <para>
/// <b>Doc comments.</b> Ruby has no dedicated doc-comment syntax; the RDoc convention (like Go's)
/// is that a <c>#</c> comment block sitting immediately before a <c>def</c>/<c>class</c>/
/// <c>module</c> — with no blank line between — <i>is</i> that declaration's documentation. This
/// analyzer applies exactly that adjacency rule: a comment (or a contiguous run of comment lines)
/// is kept only when it leads directly into a declaration; standalone comments and comments inside
/// a method body are dropped. Adjacency is judged on real source rows
/// (<see cref="TreeSitter.Point.Row"/>), so a single blank line severs the association.
/// </para>
/// <para>
/// <b>End-matching.</b> Ruby blocks are <c>end</c>-delimited, not brace-delimited, and a method
/// body can contain any number of nested <c>if</c>/<c>do</c>/<c>case</c>/<c>class</c> blocks each
/// with their own <c>end</c>. This analyzer never scans for a matching <c>end</c> token itself —
/// tree-sitter has already done that matching, so a <c>method</c> node's span ends exactly after
/// its own <c>end</c>, and every nested block is wholly contained within it. Collapsing a body is
/// therefore just: emit the signature (source up to the body), emit one <c># ...</c> line, then a
/// literal <c>end</c>. The nested <c>end</c> tokens are inside the discarded body span and never
/// surface. An <i>endless</i> method (<c>def foo = expr</c>) has no <c>end</c> token at all; such
/// methods are kept verbatim (they are already just a signature-plus-expression).
/// </para>
/// <para>
/// <b>Scope / caveats.</b> Only structure this analyzer specifically understands is transformed;
/// anything else (top-level assignments, <c>include</c>/<c>private</c> calls, unknown constructs)
/// is kept verbatim rather than dropped — the same safe default as the C# reference. A kept
/// verbatim multi-line statement is re-indented line-by-line to this analyzer's own nesting depth,
/// so its internal indentation is normalized rather than preserved. Source spans are sliced via
/// <see cref="TreeSitter.Tree.GetText(int,int)"/>, which decodes the grammar's byte offsets back to
/// text, so non-ASCII identifiers/comments are handled correctly. Like the whole <c>--level ast</c>
/// tier, this has no Rust oracle — correctness is verified by the analyzer's own tests against real
/// parsed snippets.
/// </para>
/// </remarks>
public sealed class RubyAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "# ...";
    private const string Indent = "  ";

    private static readonly HashSet<string> KeptCalls = new(StringComparer.Ordinal)
    {
        "require",
        "require_relative",
        "attr_accessor",
        "attr_reader",
        "attr_writer",
    };

    private static readonly HashSet<string> Declarations = new(StringComparer.Ordinal)
    {
        "method",
        "singleton_method",
        "class",
        "module",
        "singleton_class",
    };

    /// <inheritdoc />
    public Language Language => Language.Ruby;

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
            using var language = new TreeSitter.Language("Ruby");
            using var parser = new TreeSitter.Parser(language);
            using var tree = parser.Parse(content)!;
            var root = tree.RootNode;

            // Malformed input: fall back to raw content rather than emit a mangled summary —
            // mirrors CSharpAstAnalyzer's diagnostics-based fallback and the port's fallback convention.
            if (root.HasError)
            {
                return content;
            }

            var sb = new StringBuilder(content.Length);
            EmitStatements(sb, tree, CollectStatements(root), string.Empty);
            return sb.ToString().Trim();
        }
        catch
        {
            // Any unexpected failure in the native parser or traversal must never block `rtk read`.
            return content;
        }
    }

    /// <summary>
    /// Flattens a container node into its ordered stream of statements/comments. <c>program</c>
    /// holds its statements directly; a <c>module</c>/<c>class</c> wraps them in a
    /// <c>body_statement</c> child — but a leading comment on the body's first declaration can
    /// attach as a direct child of the container (before <c>body_statement</c>), so those direct
    /// comment children are merged in and the whole list is re-sorted by source position.
    /// </summary>
    private static List<TSNode> CollectStatements(TSNode container)
    {
        var list = new List<TSNode>();

        if (container.Type == "program")
        {
            foreach (var child in container.Children)
            {
                list.Add(child);
            }

            return list;
        }

        foreach (var child in container.Children)
        {
            if (child.Type == "comment")
            {
                list.Add(child);
            }
            else if (child.Type == "body_statement")
            {
                foreach (var stmt in child.Children)
                {
                    list.Add(stmt);
                }
            }
        }

        list.Sort(static (a, b) => a.StartIndex.CompareTo(b.StartIndex));
        return list;
    }

    private void EmitStatements(StringBuilder sb, TSTree tree, List<TSNode> statements, string indent)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            var node = statements[i];
            switch (node.Type)
            {
                case "comment":
                    // RDoc adjacency: keep a comment only when it leads directly into a declaration.
                    if (CommentLeadsDeclaration(statements, i))
                    {
                        AppendReindented(sb, indent, NodeText(tree, node));
                    }

                    break;

                case "module":
                case "class":
                case "singleton_class":
                    EmitContainer(sb, tree, node, indent);
                    break;

                case "method":
                case "singleton_method":
                    EmitMethod(sb, tree, node, indent);
                    break;

                case "call" when IsKeptCall(node):
                    AppendReindented(sb, indent, NodeText(tree, node));
                    break;

                default:
                    // Anything this analyzer does not specially handle (top-level assignments,
                    // include/private calls, unknown constructs) is kept verbatim, not dropped —
                    // the same safe default as the C# reference's unhandled-member fallback.
                    AppendReindented(sb, indent, NodeText(tree, node));
                    break;
            }
        }
    }

    private void EmitContainer(StringBuilder sb, TSTree tree, TSNode node, string indent)
    {
        var header = tree.GetText(node.StartIndex, HeaderEnd(node)).Trim();
        AppendReindented(sb, indent, header);
        EmitStatements(sb, tree, CollectStatements(node), indent + Indent);
        sb.Append(indent).Append("end\n");
    }

    private void EmitMethod(StringBuilder sb, TSTree tree, TSNode node, string indent)
    {
        if (!HasEndToken(node))
        {
            // Endless method definition (`def foo = expr`) — no `end` to collapse against; it is
            // already just a signature plus expression, so keep it verbatim.
            AppendReindented(sb, indent, NodeText(tree, node));
            return;
        }

        var header = tree.GetText(node.StartIndex, HeaderEnd(node)).Trim().TrimEnd(';').TrimEnd();
        AppendReindented(sb, indent, header);
        sb.Append(indent).Append(Indent).Append(BodyPlaceholder).Append('\n');
        sb.Append(indent).Append("end\n");
    }

    /// <summary>
    /// Returns the source index at which a declaration's header ends — the start of its first
    /// <c>body_statement</c> or leading body <c>comment</c> (i.e. everything before the body:
    /// the <c>def name(params)</c> or <c>class Name &lt; Super</c> signature). Falls back to the
    /// <c>end</c> token for body-less declarations, then to the node's own end.
    /// </summary>
    private static int HeaderEnd(TSNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "comment" || child.Type == "body_statement")
            {
                return child.StartIndex;
            }
        }

        foreach (var child in node.Children)
        {
            if (child.Type == "end")
            {
                return child.StartIndex;
            }
        }

        return node.EndIndex;
    }

    private static bool HasEndToken(TSNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "end")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a top-level call is one of the receiver-less declaration-style calls this
    /// analyzer keeps verbatim (<c>require</c>/<c>require_relative</c>/<c>attr_*</c>). A call with a
    /// receiver (e.g. <c>Foo.require</c>) is not treated as one of these.
    /// </summary>
    private static bool IsKeptCall(TSNode node)
    {
        if (node.GetChildForField("receiver") is not null)
        {
            return false;
        }

        return node.GetChildForField("method") is { } method
            && KeptCalls.Contains(method.Text ?? string.Empty);
    }

    /// <summary>
    /// Applies the RDoc adjacency rule: walks the contiguous run of comment lines starting at
    /// <paramref name="index"/>, requiring each to sit on the line immediately above the next
    /// statement (no blank-line gap), and returns true only if that run leads directly into a
    /// <c>def</c>/<c>class</c>/<c>module</c> declaration.
    /// </summary>
    private static bool CommentLeadsDeclaration(List<TSNode> statements, int index)
    {
        var i = index;
        while (i < statements.Count && statements[i].Type == "comment")
        {
            if (i + 1 >= statements.Count)
            {
                return false;
            }

            var next = statements[i + 1];

            // A blank line between this comment and the following statement severs the association.
            if (next.StartPosition.Row != statements[i].EndPosition.Row + 1)
            {
                return false;
            }

            if (next.Type != "comment")
            {
                return Declarations.Contains(next.Type);
            }

            i++;
        }

        return false;
    }

    private static string NodeText(TSTree tree, TSNode node) => tree.GetText(node.StartIndex, node.EndIndex);

    /// <summary>
    /// Appends <paramref name="text"/> with each line re-indented to <paramref name="indent"/>.
    /// Blank lines are emitted as a bare newline; non-blank lines are left-trimmed and re-prefixed,
    /// normalizing a multi-line span's internal indentation to this analyzer's nesting depth.
    /// </summary>
    private static void AppendReindented(StringBuilder sb, string indent, string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimEnd();
            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }

            sb.Append(indent).Append(line.TrimStart()).Append('\n');
        }
    }
}
