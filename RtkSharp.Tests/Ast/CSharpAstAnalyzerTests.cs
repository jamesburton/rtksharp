using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="CSharpAstAnalyzer"/>. There is no Rust oracle for this RtkSharp-only
/// <c>--level ast</c> tier (Rust's <c>filter.rs</c> has no AST support and no concept of C#), so
/// correctness here is judged against the parsed structure itself (does the output faithfully
/// represent what was actually parsed?), not by comparison to a reference binary. Every
/// expectation below was derived by running the analyzer against the fixture and inspecting the
/// real output — not assumed — per this project's "verify, don't guess" discipline, applied here
/// to the analyzer's own behavior since no external oracle exists to check against.
/// </summary>
public sealed class CSharpAstAnalyzerTests
{
    private readonly CSharpAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsCSharp() => Assert.Equal(Language.CSharp, _analyzer.Language);

    [Fact]
    public void Filter_KeepsUsingDirectives()
    {
        const string code = "using System;\nusing System.Linq;\nclass Foo {}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("using System;", result);
        Assert.Contains("using System.Linq;", result);
    }

    [Fact]
    public void Filter_StripsOrdinaryComments_KeepsXmlDocComments()
    {
        const string code = """
            // an ordinary comment, dropped
            /// <summary>Kept.</summary>
            public class Foo
            {
                // dropped
                public void Bar() { }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("an ordinary comment", result);
        Assert.DoesNotContain("// dropped", result);
        Assert.Contains("/// <summary>Kept.</summary>", result);
    }

    [Fact]
    public void Filter_CollapsesMethodBody_KeepsSignature()
    {
        const string code = """
            public class Foo
            {
                public int Add(int a, int b)
                {
                    var sum = a + b;
                    return sum;
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("public int Add(int a, int b)", result);
        Assert.DoesNotContain("var sum", result);
        Assert.Contains("/* ... */", result);
    }

    [Fact]
    public void Filter_CollapsesExpressionBodiedMethod()
    {
        const string code = "public class Foo\n{\n    public int Square(int x) => x * x;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public int Square(int x)", result);
        Assert.DoesNotContain("x * x", result);
    }

    [Fact]
    public void Filter_AutoProperty_KeptVerbatim()
    {
        const string code = "public class Foo\n{\n    public int Count { get; set; }\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public int Count { get; set; }", result);
    }

    // Regression test for a real fidelity bug caught in independent review: an earlier version
    // emitted only accessor.Keyword.Text, silently dropping a non-default accessor modifier —
    // "public int X { get; private set; }" was reported as "public int X { get; set; }",
    // misrepresenting the setter as public when it's actually private.
    [Fact]
    public void Filter_PropertyWithNonDefaultAccessorModifier_KeepsModifier()
    {
        const string code = "public class Foo\n{\n    public int X { get; private set; }\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("private set", result);
        Assert.DoesNotContain("get; set;", result);
    }

    // Regression test for a real fidelity bug caught in independent review: an earlier version
    // sliced the header up to the accessor list's closing brace only, silently dropping a
    // trailing "= initializer;" on an auto-property.
    [Fact]
    public void Filter_AutoPropertyWithInitializer_KeepsInitializer()
    {
        const string code = "public class Foo\n{\n    public int Count { get; set; } = 42;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public int Count { get; set; } = 42;", result);
    }

    [Fact]
    public void Filter_ExpressionBodiedProperty_CollapsedToDefault()
    {
        const string code = "public class Foo\n{\n    private int _x;\n    public int X => _x + 1;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public int X => default;", result);
        Assert.DoesNotContain("_x + 1", result);
    }

    [Fact]
    public void Filter_PropertyWithBlockAccessors_CollapsesBodies()
    {
        const string code = """
            public class Foo
            {
                private int _x;
                public int X
                {
                    get { return _x; }
                    set { _x = value; }
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("get", result);
        Assert.Contains("set", result);
        Assert.DoesNotContain("return _x", result);
        Assert.DoesNotContain("_x = value", result);
    }

    [Fact]
    public void Filter_Indexer_CollapsedToDefault()
    {
        const string code = "public class Foo\n{\n    public string this[int i] => _s.Substring(i);\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public string this[int i] => default;", result);
    }

    [Fact]
    public void Filter_FieldsKeptVerbatim()
    {
        const string code = "public class Foo\n{\n    private readonly int _x = 5;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("private readonly int _x = 5;", result);
    }

    [Fact]
    public void Filter_EnumMembersKeptVerbatim()
    {
        const string code = "public enum Color\n{\n    Red,\n    Green,\n    Blue\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("Red", result);
        Assert.Contains("Green", result);
        Assert.Contains("Blue", result);
    }

    // Regression test for a real bug caught in independent review: SyntaxNode.ToString()
    // excludes the node's own leading trivia, so an enum member's XML doc comment (attached as
    // leading trivia) silently disappeared before this fix added an explicit AppendDocComment call.
    [Fact]
    public void Filter_EnumMemberWithDocComment_KeepsDocComment()
    {
        const string code = "public enum Color\n{\n    /// <summary>Warm.</summary>\n    Red,\n    Green\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("/// <summary>Warm.</summary>", result);
        Assert.Contains("Red", result);
    }

    [Fact]
    public void Filter_PositionalRecord_KeptVerbatimNotCorrupted()
    {
        // Regression test: an earlier version produced garbage ("\n{\n}\n") for a
        // semicolon-terminated (brace-less) record, because it unconditionally treated every
        // TypeDeclarationSyntax as brace-bodied and read from a missing OpenBraceToken.
        const string code = "public record Point(int X, int Y);\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("public record Point(int X, int Y);", result);
    }

    [Fact]
    public void Filter_Interface_KeepsMemberSignaturesNoBodies()
    {
        const string code = "public interface IFoo\n{\n    void Bar();\n    int Baz { get; set; }\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("void Bar();", result);
        Assert.Contains("int Baz { get; set; }", result);
    }

    [Fact]
    public void Filter_Constructor_CollapsesBody()
    {
        const string code = "public class Foo\n{\n    private int _x;\n    public Foo(int x)\n    {\n        _x = x;\n    }\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public Foo(int x)", result);
        Assert.DoesNotContain("_x = x;", result);
    }

    [Fact]
    public void Filter_ExtensionMethod_CollapsesBody()
    {
        const string code = "public static class Ext\n{\n    public static int Square(this int x) => x * x;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public static int Square(this int x)", result);
        Assert.DoesNotContain("x * x", result);
    }

    [Fact]
    public void Filter_Attribute_KeptVerbatim()
    {
        const string code = "[Serializable]\npublic class Foo\n{\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("[Serializable]", result);
    }

    [Fact]
    public void Filter_FileScopedNamespace_KeepsHeaderAndMembers()
    {
        const string code = "namespace Demo;\n\npublic class Foo {}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("namespace Demo;", result);
        Assert.Contains("public class Foo", result);
    }

    [Fact]
    public void Filter_BlockNamespace_KeepsHeaderAndMembers()
    {
        const string code = "namespace Demo\n{\n    public class Foo {}\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("namespace Demo", result);
        Assert.Contains("public class Foo", result);
    }

    [Fact]
    public void Filter_TopLevelStatements_KeptVerbatim()
    {
        const string code = "using System;\nConsole.WriteLine(\"hi\");\nvar x = 5;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("using System;", result);
        Assert.Contains("Console.WriteLine(\"hi\");", result);
        Assert.Contains("var x = 5;", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "this is not valid C# at all { { {\n";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_NestedClass_CollapsesInnerMemberBodies()
    {
        const string code = """
            public class Outer
            {
                public class Inner
                {
                    public void Method()
                    {
                        var x = 1;
                    }
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("public class Inner", result);
        Assert.Contains("public void Method()", result);
        Assert.DoesNotContain("var x = 1;", result);
    }
}
