using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="JavaAstAnalyzer"/>. As with <see cref="CSharpAstAnalyzerTests"/>, there is
/// no Rust oracle for this RtkSharp-only <c>--level ast</c> tier, so correctness is judged against
/// the parsed structure itself: every expectation below was derived by running the analyzer over a
/// real Java snippet and inspecting the genuinely-observed output, not assumed from memory.
/// </summary>
public sealed class JavaAstAnalyzerTests
{
    private readonly JavaAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsJava() => Assert.Equal(Language.Java, _analyzer.Language);

    [Fact]
    public void Filter_KeepsPackageDeclaration()
    {
        const string code = "package com.example.demo;\n\npublic class Foo {}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("package com.example.demo;", result);
    }

    [Fact]
    public void Filter_KeepsImportDeclarations_IncludingStatic()
    {
        const string code = """
            import java.util.List;
            import static java.util.Objects.requireNonNull;
            class Foo {}
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("import java.util.List;", result);
        Assert.Contains("import static java.util.Objects.requireNonNull;", result);
    }

    [Fact]
    public void Filter_KeepsClassHeaderWithGenericsExtendsImplements()
    {
        const string code = "public class Widget<T> extends Base implements Runnable {\n    int x;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public class Widget<T> extends Base implements Runnable", result);
    }

    [Fact]
    public void Filter_CollapsesMethodBody_KeepsSignature()
    {
        const string code = """
            public class Foo {
                public int add(int a, int b) {
                    int sum = a + b;
                    return sum;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("public int add(int a, int b)", result);
        Assert.Contains("/* ... */", result);
        Assert.DoesNotContain("int sum = a + b", result);
        Assert.DoesNotContain("return sum", result);
    }

    [Fact]
    public void Filter_KeepsGenericMethodAndThrowsInSignature()
    {
        const string code = """
            public class Foo {
                public <R> R compute(int a, String b) throws IOException {
                    return null;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("public <R> R compute(int a, String b) throws IOException", result);
        Assert.DoesNotContain("return null", result);
    }

    [Fact]
    public void Filter_CollapsesConstructorBody_KeepsSignature()
    {
        const string code = """
            public class Foo {
                private int x;
                public Foo(int x) {
                    this.x = x;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("public Foo(int x)", result);
        Assert.DoesNotContain("this.x = x", result);
    }

    [Fact]
    public void Filter_KeepsFieldsVerbatim()
    {
        const string code = """
            public class Foo {
                private final int x = 5;
                public static final String NAME = "w";
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("private final int x = 5;", result);
        Assert.Contains("public static final String NAME = \"w\";", result);
    }

    [Fact]
    public void Filter_KeepsJavadoc_DropsOrdinaryComments()
    {
        const string code = """
            /** Javadoc for Foo. */
            public class Foo {
                // an ordinary line comment, dropped
                /* an ordinary block comment, dropped */
                /** Javadoc for bar. */
                public void bar() { int y = 1; }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("/** Javadoc for Foo. */", result);
        Assert.Contains("/** Javadoc for bar. */", result);
        Assert.DoesNotContain("ordinary line comment", result);
        Assert.DoesNotContain("ordinary block comment", result);
    }

    [Fact]
    public void Filter_KeepsAnnotationInMethodSignature()
    {
        const string code = """
            public class Foo {
                @Override
                public String toString() {
                    return "foo";
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("@Override", result);
        Assert.Contains("public String toString()", result);
        Assert.DoesNotContain("return \"foo\"", result);
    }

    [Fact]
    public void Filter_Interface_KeepsMethodSignatureWithoutBody()
    {
        const string code = """
            interface Service {
                int doThing(String name);
                String CONST = "c";
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("interface Service", result);
        Assert.Contains("int doThing(String name);", result);
        Assert.Contains("String CONST = \"c\";", result);
    }

    [Fact]
    public void Filter_Interface_CollapsesDefaultMethodBody()
    {
        const string code = """
            interface Service {
                default int answer() {
                    return 42;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("default int answer()", result);
        Assert.Contains("/* ... */", result);
        Assert.DoesNotContain("return 42", result);
    }

    [Fact]
    public void Filter_Enum_KeepsConstantsVerbatim()
    {
        const string code = "enum Color {\n    RED,\n    GREEN,\n    BLUE\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("enum Color", result);
        Assert.Contains("RED", result);
        Assert.Contains("GREEN", result);
        Assert.Contains("BLUE", result);
    }

    [Fact]
    public void Filter_Enum_KeepsConstantJavadoc_AndCollapsesMethodBody()
    {
        const string code = """
            enum Color {
                /** warm */
                RED,
                GREEN;

                public String label() { return name(); }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("/** warm */", result);
        Assert.Contains("public String label()", result);
        Assert.DoesNotContain("return name()", result);
    }

    [Fact]
    public void Filter_Record_KeepsHeaderComponents_CollapsesMethodBody()
    {
        const string code = """
            record Point(int x, int y) {
                public int sum() { return x + y; }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("record Point(int x, int y)", result);
        Assert.Contains("public int sum()", result);
        Assert.DoesNotContain("return x + y", result);
    }

    [Fact]
    public void Filter_BracelessRecord_PreservedVerbatim()
    {
        // The tree-sitter Java grammar (1.x) does not accept a body-less "compact" record
        // declaration — it parses `record` as an identifier and flags an ERROR node — so the
        // analyzer falls back to returning the content unchanged (its malformed-input path).
        // Either way the declaration is preserved verbatim, which is the point of this test.
        const string code = "public record Point(int x, int y);\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public record Point(int x, int y);", result);
    }

    [Fact]
    public void Filter_NestedClass_CollapsesInnerMemberBodies()
    {
        const string code = """
            public class Outer {
                public class Inner {
                    public void method() {
                        int x = 1;
                    }
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("public class Inner", result);
        Assert.Contains("public void method()", result);
        Assert.DoesNotContain("int x = 1", result);
    }

    [Fact]
    public void Filter_AbstractMethod_KeptWithoutBody()
    {
        const string code = """
            public abstract class Foo {
                public abstract int compute(int a);
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("public abstract int compute(int a);", result);
        Assert.DoesNotContain("/* ... */", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "this is not valid java { { {\n";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() =>
        Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_AchievesTokenSavings()
    {
        const string code = """
            package com.example;

            import java.util.List;

            public class Calculator {
                private int total;

                public int add(int a, int b) {
                    int result = a + b;
                    this.total = result;
                    return result;
                }

                public int subtract(int a, int b) {
                    int result = a - b;
                    this.total = result;
                    return result;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        static int Tokens(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var savings = 100.0 - (Tokens(result) / (double)Tokens(code) * 100.0);
        Assert.True(savings > 0, $"expected positive token savings, got {savings:F1}%");
        Assert.DoesNotContain("int result = a + b", result);
        Assert.Contains("public int add(int a, int b)", result);
    }
}
