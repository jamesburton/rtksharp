using System.Text;
using RtkSharp.Core;
using TsLanguage = TreeSitter.Language;
using TsNode = TreeSitter.Node;
using TsParser = TreeSitter.Parser;
using TsTree = TreeSitter.Tree;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> for shell scripts (<c>.sh</c>/<c>.bash</c>/<c>.zsh</c>), backed by
/// the native tree-sitter <c>Bash</c> grammar via <c>TreeSitter.DotNet</c>. Summarizes a script by
/// collapsing the bodies of clearly-recognized function definitions to a comment placeholder while
/// keeping everything else verbatim.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately conservative.</b> Shell is extremely sensitive to exact syntax (quoting,
/// heredocs, pipes, word-splitting), so — unlike <see cref="CSharpAstAnalyzer"/>, which restructures
/// C# quite aggressively — this analyzer touches as little as possible. It keeps the shebang line,
/// all top-level variable assignments, declaration commands (<c>readonly</c>/<c>local</c>/<c>declare</c>/
/// <c>export</c>), top-level commands, and top-level control flow (<c>if</c>/<c>for</c>/<c>while</c>/
/// <c>case</c>) <b>byte-for-byte verbatim</b>. The <em>only</em> thing it rewrites is a top-level
/// <c>function_definition</c> whose body is a brace-delimited <c>compound_statement</c>: its
/// signature is kept and its body is replaced by <c>{ # ... }</c> (a comment placeholder that is
/// valid inside a shell block). A function whose body is anything else (e.g. a subshell
/// <c>foo() ( ... )</c>) is kept verbatim rather than risk corrupting semantics this analyzer does
/// not confidently understand. Functions nested inside kept-verbatim control flow are likewise left
/// untouched, since only direct children of the root <c>program</c> node are considered.
/// </para>
/// <para>
/// <b>Comment handling</b> mirrors Go's godoc adjacency convention: shell has no formal doc-comment
/// syntax, so a run of <c>#</c> comment lines <em>immediately</em> preceding a function definition
/// (no blank line between the comments or between the last comment and the function) is treated as
/// that function's documentation and kept; every other standalone/inline comment — including
/// comments inside a collapsed body and comments separated from a function by a blank line — is
/// dropped. The shebang (a leading <c>#!</c> comment on the first line) is always kept.
/// </para>
/// <para>
/// <b>Fallback.</b> Per this port's established convention, a script that fails to parse (tree-sitter
/// reports a syntax error anywhere in the tree) is returned unchanged rather than summarized, so a
/// single malformed file never blocks <c>rtk read</c>. Any unexpected exception from the native
/// binding is caught and also falls back to the raw content.
/// </para>
/// </remarks>
public sealed class ShellAstAnalyzer : IAstAnalyzer
{
    private const string GrammarName = "Bash";

    /// <inheritdoc />
    public Language Language => Language.Shell;

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
            using var tree = parser.Parse(content)!;
            var root = tree.RootNode;

            if (root.HasError)
            {
                // Malformed input: fall back to raw content rather than emit a mangled summary.
                return content;
            }

            var sb = new StringBuilder(content.Length);
            AppendProgram(sb, tree, root);
            return sb.ToString().Trim();
        }
        catch
        {
            // Any failure in the native binding falls back to unchanged content — a filter must
            // never block the user (mirrors the whole port's fallback convention).
            return content;
        }
    }

    private static void AppendProgram(StringBuilder sb, TsTree tree, TsNode root)
    {
        // Snapshot the top-level children once so we can look ahead (for comment→function adjacency).
        var children = new List<TsNode>(root.Children);

        var i = 0;
        while (i < children.Count)
        {
            var node = children[i];

            switch (node.Type)
            {
                case "comment":
                    i = AppendOrSkipComments(sb, children, i);
                    break;

                case "function_definition":
                    AppendFunction(sb, tree, node);
                    i++;
                    break;

                default:
                    // Everything else (variable assignments, declaration commands, top-level
                    // commands, control flow, etc.) is kept exactly as written — shell semantics
                    // are too fragile to safely abbreviate.
                    AppendVerbatim(sb, node);
                    i++;
                    break;
            }
        }
    }

    /// <summary>
    /// Handles a comment (or contiguous run of comment lines) starting at <paramref name="start"/>.
    /// The shebang is kept. A contiguous run (no blank line between successive comments) that is
    /// immediately followed by a function definition on the next line is kept as that function's
    /// documentation; any other comment is dropped. Returns the index of the first child not
    /// consumed here.
    /// </summary>
    private static int AppendOrSkipComments(StringBuilder sb, List<TsNode> children, int start)
    {
        // The shebang line (`#!...` on the very first line) is always preserved.
        if (start == 0 && children[start].Text.StartsWith("#!", StringComparison.Ordinal))
        {
            AppendVerbatim(sb, children[start]);
            return start + 1;
        }

        // Gather a contiguous run of comment lines with no blank line between successive comments.
        var end = start;
        while (end + 1 < children.Count
               && children[end + 1].Type == "comment"
               && AdjacentLines(children[end], children[end + 1]))
        {
            end++;
        }

        // Keep the run only if it directly documents the next function definition.
        var next = end + 1 < children.Count ? children[end + 1] : (TsNode?)null;
        if (next is { Type: "function_definition" } fn && AdjacentLines(children[end], fn))
        {
            for (var k = start; k <= end; k++)
            {
                AppendVerbatim(sb, children[k]);
            }
        }

        // Otherwise the whole run is dropped (standalone/inline noise).
        return end + 1;
    }

    private static void AppendFunction(StringBuilder sb, TsTree tree, TsNode function)
    {
        var body = function.GetChildForField("body");
        if (body is not { Type: "compound_statement" })
        {
            // Non-brace body (e.g. a subshell `foo() ( ... )`) or a shape we don't recognize:
            // keep the whole definition verbatim rather than risk corrupting it.
            AppendVerbatim(sb, function);
            return;
        }

        // Slice the signature via tree.GetText, which decodes tree-sitter's native BYTE offsets
        // back to a .NET string correctly. An earlier version computed the header length as a
        // byte-offset delta (body.StartIndex - function.StartIndex) and applied it as a .NET
        // CHAR count via Text.Substring — those diverge for any signature preceded by a
        // multi-byte UTF-8 character, corrupting the header (or throwing). Suffix-removal on
        // function.Text (the approach some sibling analyzers use) is NOT safe here either: a
        // shell function can be followed by redirections after its closing brace (e.g.
        // `f() { ...; } > log`), so the body is not guaranteed to be the node's trailing text.
        var header = tree.GetText(function.StartIndex, body.StartIndex).Trim();
        sb.Append(header).Append(" {\n    # ...\n}\n");
    }

    private static void AppendVerbatim(StringBuilder sb, TsNode node) =>
        sb.Append(node.Text).Append('\n');

    /// <summary>
    /// Returns true if <paramref name="below"/> starts on the line immediately after (or on the same
    /// line as) <paramref name="above"/> ends — i.e. there is no blank line between them.
    /// </summary>
    private static bool AdjacentLines(TsNode above, TsNode below) =>
        below.StartPosition.Row - above.EndPosition.Row <= 1;
}
