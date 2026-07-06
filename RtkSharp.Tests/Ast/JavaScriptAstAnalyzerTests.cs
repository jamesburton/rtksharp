using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="JavaScriptAstAnalyzer"/>. As with <see cref="CSharpAstAnalyzerTests"/>,
/// there is no Rust oracle for this RtkSharp-only <c>--level ast</c> tier, so correctness is judged
/// against the real parsed structure: every expectation below was derived by running the analyzer
/// (Acornima-backed) against the snippet and inspecting the genuine output, not assumed.
/// </summary>
public sealed class JavaScriptAstAnalyzerTests
{
    private readonly JavaScriptAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsJavaScript() => Assert.Equal(Language.JavaScript, _analyzer.Language);

    [Fact]
    public void Filter_KeepsImportDeclarations()
    {
        const string code = "import { foo } from './foo.js';\nimport bar from 'bar';\nfunction f() { return 1; }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("import { foo } from './foo.js';", result);
        Assert.Contains("import bar from 'bar';", result);
    }

    [Fact]
    public void Filter_KeepsCommonJsRequireBinding()
    {
        const string code = "const util = require('util');\nfunction f() { return 1; }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("const util = require('util');", result);
    }

    [Fact]
    public void Filter_KeepsExportStarReexport()
    {
        const string code = "export * from './other.js';\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export * from './other.js';", result);
    }

    [Fact]
    public void Filter_KeepsNamedReexportList()
    {
        const string code = "const a = 1;\nconst b = 2;\nexport { a, b };\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export { a, b };", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionBody_KeepsSignature()
    {
        const string code = "function add(a, b) {\n  const s = a + b;\n  return s;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("function add(a, b) { /* ... */ }", result);
        Assert.DoesNotContain("const s", result);
        Assert.DoesNotContain("return s", result);
    }

    [Fact]
    public void Filter_CollapsesAsyncGeneratorFunction_KeepsModifiers()
    {
        const string code = "async function* stream(n) {\n  yield n;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("async function* stream(n) { /* ... */ }", result);
        Assert.DoesNotContain("yield n", result);
    }

    [Fact]
    public void Filter_StripsOrdinaryComments_KeepsJsDoc()
    {
        const string code =
            "// an ordinary comment, dropped\n" +
            "/* also dropped */\n" +
            "/** Adds two numbers. */\n" +
            "function add(a, b) {\n  return a + b;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("an ordinary comment", result);
        Assert.DoesNotContain("also dropped", result);
        Assert.Contains("/** Adds two numbers. */", result);
        Assert.Contains("function add(a, b) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_JsDocNotImmediatelyPreceding_IsDropped()
    {
        // A JSDoc block separated from the declaration by an ordinary comment is treated as
        // not-immediately-preceding and dropped (documented v1 limitation).
        const string code =
            "/** orphaned doc */\n" +
            "// intervening ordinary comment\n" +
            "function f() { return 1; }\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("orphaned doc", result);
        Assert.Contains("function f() { /* ... */ }", result);
    }

    [Fact]
    public void Filter_CollapsesArrowFunctionAssignedToConst()
    {
        const string code = "const mul = (a, b) => {\n  return a * b;\n};\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("const mul = (a, b) => { /* ... */ };", result);
        Assert.DoesNotContain("a * b", result);
    }

    [Fact]
    public void Filter_CollapsesExpressionBodiedArrow()
    {
        const string code = "const inc = x => x + 1;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("const inc = x => { /* ... */ };", result);
        Assert.DoesNotContain("x + 1", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionExpressionAssignedToConst()
    {
        const string code = "const f = function named(a) {\n  return a;\n};\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("const f = function named(a) { /* ... */ };", result);
        Assert.DoesNotContain("return a;", result);
    }

    [Fact]
    public void Filter_PlainConstKeptVerbatim()
    {
        const string code = "const name = 'plain';\nconst nums = [1, 2, 3];\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("const name = 'plain';", result);
        Assert.Contains("const nums = [1, 2, 3];", result);
    }

    [Fact]
    public void Filter_ExportedFunction_KeepsExportPrefixAndCollapses()
    {
        const string code = "export function add(a, b) {\n  return a + b;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export function add(a, b) { /* ... */ }", result);
        Assert.DoesNotContain("return a + b", result);
    }

    [Fact]
    public void Filter_ExportedConstArrow_KeepsExportPrefixAndCollapses()
    {
        const string code = "export const mul = (a, b) => {\n  return a * b;\n};\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export const mul = (a, b) => { /* ... */ };", result);
    }

    [Fact]
    public void Filter_DefaultExportedAnonymousFunction_Collapses()
    {
        const string code = "export default function () { return 42; }\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("export default function () { /* ... */ }", result);
    }

    [Fact]
    public void Filter_DefaultExportedValue_KeptVerbatim()
    {
        const string code = "export default { a: 1, b: 2 };\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export default { a: 1, b: 2 };", result);
    }

    [Fact]
    public void Filter_Class_KeepsSignatureAndCollapsesMethodBodies()
    {
        const string code =
            "export class Widget extends Base {\n" +
            "  constructor(x) {\n    this.x = x;\n  }\n" +
            "  compute(y) {\n    return this.x + y;\n  }\n" +
            "  static make() { return new Widget(0); }\n" +
            "  count = 0;\n" +
            "}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export class Widget extends Base", result);
        Assert.Contains("constructor(x) { /* ... */ }", result);
        Assert.Contains("compute(y) { /* ... */ }", result);
        Assert.Contains("static make() { /* ... */ }", result);
        Assert.DoesNotContain("this.x = x", result);
        Assert.DoesNotContain("return this.x + y", result);
        Assert.DoesNotContain("new Widget(0)", result);
    }

    [Fact]
    public void Filter_ClassField_KeptVerbatim()
    {
        const string code = "class C {\n  count = 0;\n  #secret = 42;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("count = 0;", result);
        Assert.Contains("#secret = 42;", result);
    }

    [Fact]
    public void Filter_ClassMethodJsDoc_Kept_OrdinaryCommentDropped()
    {
        const string code =
            "class C {\n" +
            "  // internal, dropped\n" +
            "  /** computes a thing */\n" +
            "  compute(y) {\n    return y;\n  }\n" +
            "}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("/** computes a thing */", result);
        Assert.DoesNotContain("internal, dropped", result);
        Assert.Contains("compute(y) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_ClassGetterSetter_CollapseBodies()
    {
        const string code =
            "class C {\n" +
            "  get value() { return this._v; }\n" +
            "  set value(v) { this._v = v; }\n" +
            "}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("get value() { /* ... */ }", result);
        Assert.Contains("set value(v) { /* ... */ }", result);
        Assert.DoesNotContain("this._v = v", result);
    }

    [Fact]
    public void Filter_ModuleExportsAssignment_KeptVerbatim()
    {
        const string code = "function helper(x) { return x; }\nmodule.exports = { helper };\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("module.exports = { helper };", result);
        Assert.Contains("function helper(x) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_TopLevelRequireOnlyScript_ParsesViaScriptOrModule()
    {
        // A CommonJS-style file with a top-level bare require call as an expression statement.
        const string code = "require('./setup');\nfunction run() { doStuff(); }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("require('./setup');", result);
        Assert.Contains("function run() { /* ... */ }", result);
        Assert.DoesNotContain("doStuff()", result);
    }

    [Fact]
    public void Filter_WithStatement_ParsesViaScriptFallback()
    {
        // `with` is a syntax error in module (strict) mode, so this exercises the script-mode
        // fallback path — it must still parse and collapse the function body, not fall back to raw.
        const string code = "function f(obj) {\n  with (obj) { return x; }\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("function f(obj) { /* ... */ }", result);
        Assert.DoesNotContain("with (obj)", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "function ( { { {\n";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_CommentsOnlyInput_ReturnsEmpty()
    {
        // No declarations at all — ordinary comments are the only content, so all are dropped.
        const string code = "// just a comment\n/* block */\n";
        var result = _analyzer.Filter(code);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Filter_NestedFunctionBodyContent_FullyCollapsed()
    {
        const string code =
            "function outer() {\n" +
            "  function inner() { return secret(); }\n" +
            "  return inner;\n" +
            "}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("function outer() { /* ... */ }", result);
        // The nested function lives inside the collapsed body and must not leak.
        Assert.DoesNotContain("inner", result);
        Assert.DoesNotContain("secret()", result);
    }
}
