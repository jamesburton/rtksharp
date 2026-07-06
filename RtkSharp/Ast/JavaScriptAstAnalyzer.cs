using System.Text;
using Acornima;
using Acornima.Ast;
using RtkSharp.Core;
using AcornimaParser = Acornima.Parser;
using AstProgram = Acornima.Ast.Program;

namespace RtkSharp.Ast;

/// <summary>
/// <see cref="IAstAnalyzer"/> implementation for JavaScript, backed by the pure-managed
/// <c>Acornima</c> parser (an acornjs/Esprima.NET crossbreed producing a real ESTree AST).
/// Parses real syntax rather than regexing text: keeps <c>import</c>/<c>export</c> statements and
/// CommonJS <c>require(...)</c> bindings, keeps top-level <c>function</c>/<c>class</c> and
/// arrow-/function-assigned-<c>const</c> declaration signatures, and keeps JSDoc (<c>/** ... */</c>)
/// comments that immediately precede a kept declaration; function/method bodies are collapsed to a
/// single <c>{ /* ... */ }</c> placeholder and ordinary <c>//</c>/<c>/* */</c> comments are dropped
/// — the same "keep docs + signatures, drop noise + bodies" intent as the C# reference
/// (<see cref="CSharpAstAnalyzer"/>), done AST-accurately.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acornima over tree-sitter.</b> Chosen for this one language specifically because Acornima
/// ships no native assets at all (pure C#, no P/Invoke), a strictly better Native-AOT story than a
/// native grammar binding — see the package comment in <c>RtkSharp.csproj</c>. Every other
/// <c>--level ast</c> language uses <c>TreeSitter.DotNet</c>; C# uses Roslyn.
/// </para>
/// <para>
/// <b>Scope.</b> JavaScript only — it does NOT attempt TypeScript type syntax (that is a separate
/// tree-sitter-backed analyzer). Parsing is tried as an ES module first (so <c>import</c>/<c>export</c>
/// are accepted), then as a classic script (so constructs a module rejects — e.g. <c>with</c>,
/// top-level <c>return</c> — still parse). If both fail, <see cref="Filter"/> returns the input
/// unchanged per the interface's fallback contract; a single malformed file never blocks
/// <c>rtk read</c>.
/// </para>
/// <para>
/// <b>Known v1 limitations</b> (judged acceptable, none corrupt output — they only under-summarize):
/// an anonymous default-exported function collapses fine, but a body is only collapsed when a
/// declaration is a <c>function</c>/<c>class</c>, a class method, or a <c>const</c>/<c>let</c>/<c>var</c>
/// bound directly to a function or arrow expression — a function returned by an IIFE, produced by a
/// higher-order call, or nested inside an object literal is kept verbatim via the generic
/// unhandled-statement fallback (kept, not dropped). A JSDoc block is attached to a declaration only
/// when nothing but whitespace sits between the comment and the declaration, so a JSDoc separated
/// from its target by an ordinary comment is treated as not-immediately-preceding and dropped.
/// Multi-line verbatim statements keep their original source indentation rather than being re-leveled.
/// </para>
/// </remarks>
public sealed class JavaScriptAstAnalyzer : IAstAnalyzer
{
    private const string BodyPlaceholder = "{ /* ... */ }";

    /// <inheritdoc />
    public Language Language => Language.JavaScript;

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
            var comments = new List<Comment>();
            var program = TryParse(content, comments);
            if (program is null)
            {
                // Malformed input (or a construct neither module nor script mode accepts): fall
                // back to raw content rather than emit a mangled summary — the interface's
                // fallback convention.
                return content;
            }

            var sb = new StringBuilder(content.Length);
            foreach (var statement in program.Body)
            {
                AppendStatement(sb, content, statement, comments, indent: string.Empty);
            }

            return sb.ToString().Trim();
        }
        catch
        {
            // TryParse already catches its own parse failures; this wraps the AST-walking pass
            // too, so an unexpected node shape or slicing edge case can never escape Filter as an
            // exception — every other analyzer wraps its whole body this way, and the interface
            // contract requires falling back to raw content, never throwing.
            return content;
        }
    }

    /// <summary>
    /// Parses <paramref name="content"/> as an ES module first, then as a classic script, collecting
    /// the successful parse's comments into <paramref name="comments"/>. Returns <see langword="null"/>
    /// only if both parse modes throw.
    /// </summary>
    private static AstProgram? TryParse(string content, List<Comment> comments)
    {
        // Module mode first: it is the only mode that accepts import/export syntax.
        var moduleComments = new List<Comment>();
        var moduleParser = new AcornimaParser(new ParserOptions { OnComment = (in Comment c) => moduleComments.Add(c) });
        try
        {
            var module = moduleParser.ParseModule(content);
            comments.AddRange(moduleComments);
            return module;
        }
        catch
        {
            // Fall through to script mode.
        }

        // Script mode second: accepts a handful of constructs a module rejects (with-statement,
        // top-level return, HTML-style comments, some reserved-word usages).
        var scriptComments = new List<Comment>();
        var scriptParser = new AcornimaParser(new ParserOptions { OnComment = (in Comment c) => scriptComments.Add(c) });
        try
        {
            var script = scriptParser.ParseScript(content);
            comments.AddRange(scriptComments);
            return script;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendStatement(
        StringBuilder sb, string src, Statement statement, List<Comment> comments, string indent)
    {
        AppendLeadingJsDoc(sb, src, statement.Start, comments, indent);

        switch (statement)
        {
            case ImportDeclaration:
            case ExportAllDeclaration:
                // `import ... from '...'` / `export * from '...'` — pure module wiring, kept verbatim.
                AppendVerbatim(sb, src, statement, indent);
                break;

            case ExportNamedDeclaration named:
                AppendExportNamed(sb, src, named, comments, indent);
                break;

            case ExportDefaultDeclaration @default:
                AppendExportDefault(sb, src, @default, comments, indent);
                break;

            case FunctionDeclaration function:
                AppendFunctionDeclaration(sb, src, function, indent);
                break;

            case ClassDeclaration @class:
                AppendClassDeclaration(sb, src, @class, comments, indent);
                break;

            case VariableDeclaration variable:
                AppendVariableDeclaration(sb, src, variable, indent);
                break;

            default:
                // Anything not specially handled (bare expression statements such as a top-level
                // `require(...)` call or `module.exports = ...`, control flow, etc.) is kept
                // verbatim rather than silently dropped — the safe default this analyzer shares
                // with the C# reference.
                AppendVerbatim(sb, src, statement, indent);
                break;
        }
    }

    private static void AppendExportNamed(
        StringBuilder sb, string src, ExportNamedDeclaration named, List<Comment> comments, string indent)
    {
        switch (named.Declaration)
        {
            case FunctionDeclaration function:
                AppendFunctionDeclaration(sb, src, function, indent, "export ");
                break;
            case ClassDeclaration @class:
                AppendClassDeclaration(sb, src, @class, comments, indent, "export ");
                break;
            case VariableDeclaration variable:
                AppendVariableDeclaration(sb, src, variable, indent, "export ");
                break;
            default:
                // `export { a, b }` / `export { a } from '...'` — no wrapped declaration; verbatim.
                AppendVerbatim(sb, src, named, indent);
                break;
        }
    }

    private static void AppendExportDefault(
        StringBuilder sb, string src, ExportDefaultDeclaration @default, List<Comment> comments, string indent)
    {
        switch (@default.Declaration)
        {
            case FunctionDeclaration function:
                AppendFunctionDeclaration(sb, src, function, indent, "export default ");
                break;
            case ClassDeclaration @class:
                AppendClassDeclaration(sb, src, @class, comments, indent, "export default ");
                break;
            default:
                // `export default <expression>;` (a value, object literal, arrow, etc.) — kept
                // verbatim; these are typically short and have no signature/body to separate.
                AppendVerbatim(sb, src, @default, indent);
                break;
        }
    }

    private static void AppendFunctionDeclaration(
        StringBuilder sb, string src, FunctionDeclaration function, string indent, string prefix = "")
    {
        // Header is everything from the declaration start up to the body's opening brace: modifiers
        // (async), the `function`/`function*` keyword, name, and parameter list.
        var header = Slice(src, function.Start, function.Body.Start).TrimEnd();
        sb.Append(indent).Append(prefix).Append(header).Append(' ').Append(BodyPlaceholder).Append('\n');
    }

    private static void AppendClassDeclaration(
        StringBuilder sb, string src, ClassDeclaration @class, List<Comment> comments, string indent, string prefix = "")
    {
        // Header up to the class body's `{`: `class Name`, plus any `extends Super`.
        var header = Slice(src, @class.Start, @class.Body.Start).TrimEnd();
        sb.Append(indent).Append(prefix).Append(header).Append('\n').Append(indent).Append("{\n");

        var innerIndent = indent + "    ";
        foreach (var element in @class.Body.Body)
        {
            AppendClassElement(sb, src, element, comments, innerIndent);
        }

        sb.Append(indent).Append("}\n");
    }

    private static void AppendClassElement(
        StringBuilder sb, string src, Node element, List<Comment> comments, string indent)
    {
        AppendLeadingJsDoc(sb, src, element.Start, comments, indent);

        if (element is MethodDefinition { Value.Body: { } methodBody } method)
        {
            // Method / getter / setter / constructor: keep the signature, collapse the body.
            var header = Slice(src, method.Start, methodBody.Start).TrimEnd();
            sb.Append(indent).Append(header).Append(' ').Append(BodyPlaceholder).Append('\n');
            return;
        }

        // PropertyDefinition (class field), static initialization block, or any element shape not
        // specially handled: keep verbatim rather than lose it.
        AppendVerbatim(sb, src, element, indent);
    }

    private static void AppendVariableDeclaration(
        StringBuilder sb, string src, VariableDeclaration variable, string indent, string prefix = "")
    {
        // Collapse only the common single-binding-to-a-function shape
        // (`const foo = (a, b) => { ... }` / `const foo = function () { ... }`); everything else
        // (plain values, `require(...)` bindings, destructuring, multi-declarator lists) is short
        // enough to keep verbatim.
        if (variable.Declarations.Count == 1)
        {
            var init = variable.Declarations[0].Init;
            int? bodyStart = init switch
            {
                ArrowFunctionExpression arrow => arrow.Body.Start,
                FunctionExpression function => function.Body.Start,
                _ => null,
            };

            if (bodyStart is { } start)
            {
                // Header spans the keyword + name + `=` + params + `=>` (arrow) or the function
                // keyword/params (function expression), up to the body.
                var header = Slice(src, variable.Start, start).TrimEnd();
                sb.Append(indent).Append(prefix).Append(header).Append(' ')
                    .Append(BodyPlaceholder).Append(";\n");
                return;
            }
        }

        AppendVerbatim(sb, src, variable, indent, prefix);
    }

    private static void AppendVerbatim(StringBuilder sb, string src, Node node, string indent, string prefix = "")
    {
        var text = Slice(src, node.Start, node.End).Trim();
        sb.Append(indent).Append(prefix).Append(text).Append('\n');
    }

    /// <summary>
    /// Appends any JSDoc (<c>/** ... */</c>) block comment that immediately precedes the node
    /// starting at <paramref name="declStart"/> (nothing but whitespace between the comment and the
    /// node). Ordinary <c>//</c>/<c>/* */</c> comments — and JSDoc blocks not directly adjacent to a
    /// declaration — are not emitted, mirroring the "keep doc comments, drop the rest" intent of the
    /// C# reference, applied here via Acornima's out-of-band comment channel and the JSDoc
    /// (<c>/**</c>) convention.
    /// </summary>
    private static void AppendLeadingJsDoc(
        StringBuilder sb, string src, int declStart, List<Comment> comments, string indent)
    {
        foreach (var comment in comments)
        {
            if (comment.Kind != CommentKind.Block || comment.End > declStart)
            {
                continue;
            }

            var text = Slice(src, comment.Start, comment.End);

            // JSDoc convention: a block comment opening with `/**` (but not the empty `/**/`).
            if (!text.StartsWith("/**", StringComparison.Ordinal) || text.Length <= 4)
            {
                continue;
            }

            // Only "immediately preceding" — the gap to the declaration must be whitespace only.
            if (Slice(src, comment.End, declStart).Trim().Length != 0)
            {
                continue;
            }

            foreach (var line in text.Split('\n'))
            {
                sb.Append(indent).Append(line.Trim()).Append('\n');
            }
        }
    }

    private static string Slice(string src, int start, int end) => src.Substring(start, Math.Max(0, end - start));
}
