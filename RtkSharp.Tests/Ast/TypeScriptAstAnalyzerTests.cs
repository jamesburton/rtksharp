using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="TypeScriptAstAnalyzer"/>. Like <see cref="CSharpAstAnalyzerTests"/>, there
/// is no Rust oracle for this RtkSharp-only <c>--level ast</c> tier, so correctness is judged
/// against the parsed structure itself: every expectation below was derived by running the analyzer
/// against the fixture (via the native tree-sitter "TypeScript" grammar) and inspecting the real
/// output — not assumed. In particular, the analyzer keeps whole statements (imports, exported
/// <c>const</c>, <c>type</c> aliases, enums) verbatim including their trailing <c>;</c>, but
/// interface members and class fields are re-emitted from their tree-sitter named node, which does
/// NOT include the trailing <c>;</c> punctuation — the assertions reflect that observed shape.
/// </summary>
public sealed class TypeScriptAstAnalyzerTests
{
    private readonly TypeScriptAstAnalyzer _analyzer = new();

    private static int CountTokens(string text) => text.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public void Language_IsTypeScript() => Assert.Equal(Language.TypeScript, _analyzer.Language);

    [Fact]
    public void Filter_KeepsImportStatement()
    {
        const string code = "import { A, B } from \"./mod\";\nfunction f(): void { return; }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("import { A, B } from \"./mod\";", result);
    }

    [Fact]
    public void Filter_KeepsExportedConst()
    {
        const string code = "export const x = 1;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export const x = 1;", result);
    }

    [Fact]
    public void Filter_KeepsTypeAlias_WithGenerics()
    {
        const string code = "type Alias<K> = Map<K, number>;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("type Alias<K> = Map<K, number>;", result);
    }

    [Fact]
    public void Filter_Interface_KeepsGenericsAndConstraints()
    {
        const string code = "interface Repo<T extends Entity> {\n    name: string;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("interface Repo<T extends Entity>", result);
    }

    [Fact]
    public void Filter_Interface_KeepsMemberSignaturesWithTypeAnnotations()
    {
        const string code = """
            interface Repo<T> {
                find(id: string): Promise<T | null>;
                readonly name: string;
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("find(id: string): Promise<T | null>", result);
        Assert.Contains("readonly name: string", result);
    }

    [Fact]
    public void Filter_Interface_DropsRegularComment()
    {
        const string code = """
            interface Repo {
                // an ordinary comment, dropped
                name: string;
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("an ordinary comment", result);
        Assert.Contains("name: string", result);
    }

    [Fact]
    public void Filter_KeepsJsDocComment()
    {
        const string code = "/** JSDoc kept. */\nexport interface Foo {\n    a: number;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("/** JSDoc kept. */", result);
    }

    [Fact]
    public void Filter_DropsLineComment()
    {
        const string code = "// dropped line comment\nexport const x = 1;\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("dropped line comment", result);
        Assert.Contains("export const x = 1;", result);
    }

    [Fact]
    public void Filter_DropsPlainBlockComment()
    {
        const string code = "/* dropped block comment */\nexport const x = 1;\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("dropped block comment", result);
        Assert.Contains("export const x = 1;", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionBody_KeepsGenericSignature()
    {
        const string code = "export function free<T>(x: T): T {\n    return x;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export function free<T>(x: T): T", result);
        Assert.Contains("{ /* ... */ }", result);
        Assert.DoesNotContain("return x;", result);
    }

    [Fact]
    public void Filter_CollapsesMethodBody_KeepsSignatureWithAnnotations()
    {
        const string code = """
            class Service {
                async doThing(a: number, b: string): Promise<void> {
                    const y = a + 1;
                    return;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("async doThing(a: number, b: string): Promise<void> { /* ... */ }", result);
        Assert.DoesNotContain("const y = a + 1;", result);
    }

    [Fact]
    public void Filter_CollapsesGetterBody()
    {
        const string code = """
            class Foo {
                private value = 3;
                get prop(): number { return this.value; }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("get prop(): number { /* ... */ }", result);
        Assert.DoesNotContain("return this.value;", result);
    }

    [Fact]
    public void Filter_Class_KeepsExtendsImplementsAndGenerics()
    {
        const string code = """
            export class Service<T> extends Base implements IService {
                run(): void { doWork(); }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("export class Service<T> extends Base implements IService", result);
        Assert.DoesNotContain("doWork();", result);
    }

    [Fact]
    public void Filter_KeepsClassFieldWithTypeAnnotation()
    {
        const string code = "class Foo {\n    private value: number = 0;\n    run(): void { go(); }\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("private value: number = 0", result);
    }

    [Fact]
    public void Filter_AbstractClass_KeepsAbstractSignature_CollapsesConcreteBody()
    {
        const string code = """
            abstract class A {
                abstract foo(): void;
                bar(): number { return 1; }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("abstract class A", result);
        Assert.Contains("abstract foo(): void", result);
        Assert.Contains("bar(): number { /* ... */ }", result);
        Assert.DoesNotContain("return 1;", result);
    }

    [Fact]
    public void Filter_DefaultExportFunction_CollapsesBody()
    {
        const string code = "export default function f(x: number): number { return x; }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("export default function f(x: number): number { /* ... */ }", result);
        Assert.DoesNotContain("return x;", result);
    }

    [Fact]
    public void Filter_Enum_KeptVerbatim()
    {
        const string code = "export enum Color { Red, Green, Blue }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("Red", result);
        Assert.Contains("Green", result);
        Assert.Contains("Blue", result);
    }

    [Fact]
    public void Filter_TopLevelStatement_KeptVerbatim()
    {
        const string code = "console.log(\"hi\");\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("console.log(\"hi\");", result);
    }

    [Fact]
    public void Filter_NestedClassInsideExport_CollapsesInnerMemberBodies()
    {
        const string code = """
            export class Outer<T> {
                inner(x: T): T {
                    const z = x;
                    return z;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("export class Outer<T>", result);
        Assert.Contains("inner(x: T): T { /* ... */ }", result);
        Assert.DoesNotContain("const z = x;", result);
    }

    [Fact]
    public void Filter_ReducesTokenCount_OnBodyHeavyInput()
    {
        const string code = """
            export class Calculator {
                add(a: number, b: number): number {
                    const total = a + b;
                    console.log(total);
                    return total;
                }

                subtract(a: number, b: number): number {
                    const diff = a - b;
                    console.log(diff);
                    return diff;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.True(
            CountTokens(result) < CountTokens(code),
            $"expected fewer tokens, input={CountTokens(code)} output={CountTokens(result)}");
        Assert.DoesNotContain("console.log", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "class { { { not valid <<<";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() =>
        Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_ArbitraryProseIsNotCorrupted()
    {
        // Prose parses (with errors) → analyzer must not throw and must fall back to raw content.
        const string prose = "this is just some text, not typescript at all";
        var result = _analyzer.Filter(prose);
        Assert.Equal(prose, result);
    }
}
