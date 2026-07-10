using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="CppAstAnalyzer"/>. As with the C# analyzer, there is no Rust oracle for
/// this RtkSharp-only <c>--level ast</c> tier, so correctness is judged against the parsed
/// structure itself: does the summary faithfully keep signatures/declarations while collapsing
/// bodies and dropping ordinary comments? Every expectation below was derived by running the
/// analyzer (backed by the real <c>tree-sitter-cpp</c> grammar) against the fixture and inspecting
/// the genuinely-observed output — not assumed — per this project's "verify, don't guess" rule.
/// </summary>
public sealed class CppAstAnalyzerTests
{
    private readonly CppAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsCpp() => Assert.Equal(Language.Cpp, _analyzer.Language);

    [Fact]
    public void Filter_KeepsPreprocessorDirectives()
    {
        const string code = "#include <vector>\n#define MAX 10\nint x = MAX;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("#include <vector>", result);
        Assert.Contains("#define MAX 10", result);
    }

    [Fact]
    public void Filter_KeepsNamespaceBlock_AndMembers()
    {
        const string code = "namespace demo {\nvoid f() { work(); }\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("namespace demo", result);
        Assert.Contains("{", result);
        Assert.Contains("}", result);
        Assert.Contains("void f()", result);
    }

    [Fact]
    public void Filter_CollapsesFreeFunctionBody_KeepsSignature()
    {
        const string code = "int add(int a, int b) {\n    return a + b;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("int add(int a, int b)", result);
        Assert.DoesNotContain("return a + b", result);
        Assert.Contains("/* ... */", result);
    }

    [Fact]
    public void Filter_KeepsClassWithMemberSignatures_CollapsesMethodBodies()
    {
        const string code = """
            class Foo {
            public:
                void bar();
                int baz() { return 1; }
            };
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("class Foo", result);
        Assert.Contains("void bar();", result);
        Assert.Contains("int baz()", result);
        Assert.DoesNotContain("return 1", result);
        Assert.Contains("/* ... */", result);
    }

    [Fact]
    public void Filter_KeepsAccessSpecifiers()
    {
        const string code = "class Foo {\npublic:\n    int x;\nprivate:\n    int y;\n};\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("public:", result);
        Assert.Contains("private:", result);
    }

    [Fact]
    public void Filter_KeepsStructMembers()
    {
        const string code = "struct Point {\n    int x;\n    int y;\n};\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("struct Point", result);
        Assert.Contains("int x;", result);
        Assert.Contains("int y;", result);
    }

    [Fact]
    public void Filter_KeepsUnionMembers()
    {
        const string code = "union U {\n    int i;\n    float f;\n};\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("union U", result);
        Assert.Contains("int i;", result);
        Assert.Contains("float f;", result);
    }

    [Fact]
    public void Filter_KeepsEnumVerbatim()
    {
        const string code = "enum Color { Red, Green, Blue };\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("enum Color", result);
        Assert.Contains("Red", result);
        Assert.Contains("Green", result);
        Assert.Contains("Blue", result);
    }

    [Fact]
    public void Filter_KeepsTemplateFunction_CollapsesBody()
    {
        const string code = "template<typename T>\nT identity(T v) { return v; }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("template<typename T>", result);
        Assert.Contains("T identity(T v)", result);
        Assert.DoesNotContain("return v", result);
        Assert.Contains("/* ... */", result);
    }

    [Fact]
    public void Filter_KeepsTemplateClass_CollapsesMethodBodies()
    {
        const string code = """
            template<typename T>
            class Box {
            public:
                T value;
                T get() const { return value; }
            };
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("template<typename T>", result);
        Assert.Contains("class Box", result);
        Assert.Contains("T value;", result);
        Assert.Contains("T get() const", result);
        Assert.DoesNotContain("return value", result);
        // Regression guard: an earlier version leaked a stray "c" before "class" because the
        // template_declaration span includes a trailing ";" that the inner class span omits.
        Assert.DoesNotContain("\nc\n", "\n" + result + "\n");
    }

    [Fact]
    public void Filter_KeepsTemplateMethodInsideClass()
    {
        const string code = """
            class Widget {
            public:
                template<typename T>
                T get(T a) const { return a; }
            };
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("template<typename T>", result);
        Assert.Contains("T get(T a) const", result);
        Assert.DoesNotContain("return a", result);
    }

    [Fact]
    public void Filter_KeepsDoxygenDocComment_DropsPlainComment()
    {
        const string code = """
            // ordinary comment, dropped
            /// keep me
            void f();
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("/// keep me", result);
        Assert.DoesNotContain("ordinary comment", result);
        Assert.Contains("void f();", result);
    }

    [Fact]
    public void Filter_KeepsDoxygenBlockComment()
    {
        const string code = """
            /**
             * Doxygen for Widget.
             */
            class Widget {
            };
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("Doxygen for Widget", result);
        Assert.Contains("class Widget", result);
    }

    [Fact]
    public void Filter_DropsPlainBlockComment()
    {
        const string code = "/* internal note, dropped */\nvoid f();\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("internal note", result);
        Assert.Contains("void f();", result);
    }

    [Fact]
    public void Filter_KeepsFunctionPrototypeVerbatim()
    {
        const string code = "void prototypeOnly(int x);\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("void prototypeOnly(int x);", result);
    }

    [Fact]
    public void Filter_KeepsIncludeGuard_AndRecursesInside()
    {
        const string code = """
            #ifndef FOO_H
            #define FOO_H
            class Foo {
            public:
                int baz() { return 1; }
            };
            #endif
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("#ifndef FOO_H", result);
        Assert.Contains("#define FOO_H", result);
        Assert.Contains("#endif", result);
        Assert.Contains("class Foo", result);
        Assert.Contains("int baz()", result);
        Assert.DoesNotContain("return 1", result);
    }

    [Fact]
    public void Filter_KeepsNestedPreprocIfdef()
    {
        const string code = "#ifdef DEBUG\nvoid debugOnly();\n#endif\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("#ifdef DEBUG", result);
        Assert.Contains("void debugOnly();", result);
        Assert.Contains("#endif", result);
    }

    [Fact]
    public void Filter_KeepsDefaultAndDeleteMembersVerbatim()
    {
        const string code = """
            class C {
            public:
                C() = default;
                C(const C&) = delete;
            };
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("= default", result);
        Assert.Contains("= delete", result);
    }

    [Fact]
    public void Filter_KeepsConstructorInitializerList_CollapsesBody()
    {
        const string code = """
            class C {
            public:
                C(int x) : x_(x) { doStuff(); }
            private:
                int x_;
            };
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("C(int x) : x_(x)", result);
        Assert.DoesNotContain("doStuff", result);
        Assert.Contains("int x_;", result);
    }

    [Fact]
    public void Filter_KeepsNestedNamespaces_CollapsesInnerBody()
    {
        const string code = "namespace a {\nnamespace b {\nvoid f() { int z = 1; }\n}\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("namespace a", result);
        Assert.Contains("namespace b", result);
        Assert.Contains("void f()", result);
        Assert.DoesNotContain("int z = 1", result);
    }

    [Fact]
    public void Filter_KeepsUsingAndTypedefVerbatim()
    {
        const string code = "using namespace std;\nusing MyInt = int;\ntypedef unsigned long ulong_t;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("using namespace std;", result);
        Assert.Contains("using MyInt = int;", result);
        Assert.Contains("typedef unsigned long ulong_t;", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "this is not valid C++ at all { { {";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_ReducesTokenCount_OnBodyHeavyInput()
    {
        const string code = """
            int compute(int a, int b) {
                int sum = 0;
                for (int i = a; i < b; ++i) {
                    sum += i * i;
                }
                return sum;
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("int compute(int a, int b)", result);
        Assert.True(result.Length < code.Length, "AST summary should be shorter than a body-heavy source.");
    }
}
