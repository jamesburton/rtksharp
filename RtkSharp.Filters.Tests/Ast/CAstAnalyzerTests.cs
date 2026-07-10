using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="CAstAnalyzer"/>. Like <see cref="CSharpAstAnalyzerTests"/>, there is no
/// Rust oracle for this RtkSharp-only <c>--level ast</c> tier, so correctness is judged against
/// the parsed structure itself — does the output faithfully represent what tree-sitter actually
/// parsed? Every expectation below was derived by running the analyzer against the fixture and
/// inspecting the real observed output (via a standalone probe harness), not assumed, per this
/// project's "verify, don't guess" discipline applied to the analyzer's own behavior.
/// </summary>
public sealed class CAstAnalyzerTests
{
    private readonly CAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsC() => Assert.Equal(Language.C, _analyzer.Language);

    // Regression test for a real bug caught in independent review: header slicing originally
    // subtracted tree-sitter's native BYTE offsets and applied that delta as a .NET char count,
    // which mis-slices (leaking a stray brace / corrupting the header) for any signature preceded
    // by a multi-byte UTF-8 character. Fixed via UTF-16-safe suffix removal.
    [Fact]
    public void Filter_NonAsciiInSignature_DoesNotCorruptHeader()
    {
        const string code = "int café(int x) {\n    return x;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("int café(int x) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_KeepsIncludeDirectives()
    {
        const string code = "#include <stdio.h>\n#include \"local.h\"\nint x = 1;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("#include <stdio.h>", result);
        Assert.Contains("#include \"local.h\"", result);
    }

    [Fact]
    public void Filter_KeepsDefineDirectives()
    {
        const string code = "#define MAX 100\n#define LOG(x) printf(x)\nint y;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("#define MAX 100", result);
        Assert.Contains("#define LOG(x) printf(x)", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionBody_KeepsSignature()
    {
        const string code = """
            int add(int a, int b) {
                int sum = a + b;
                return sum;
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("int add(int a, int b)", result);
        Assert.Contains("{ /* ... */ }", result);
        Assert.DoesNotContain("int sum = a + b", result);
        Assert.DoesNotContain("return sum", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionWithStorageSpecifier()
    {
        const string code = "static void helper(void) {\n    do_thing();\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("static void helper(void) { /* ... */ }", result);
        Assert.DoesNotContain("do_thing", result);
    }

    [Fact]
    public void Filter_MultiLineSignature_PreservedWithCollapsedBody()
    {
        const string code = """
            char *dup(const char *s,
                      int n) {
                return copy(s, n);
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("char *dup(const char *s,", result);
        Assert.Contains("int n) { /* ... */ }", result);
        Assert.DoesNotContain("return copy", result);
    }

    [Fact]
    public void Filter_KeepsStructWithFieldsAndTrailingSemicolon()
    {
        const string code = "struct Point {\n    int x;\n    int y;\n};\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("struct Point {", result);
        Assert.Contains("int x;", result);
        Assert.Contains("int y;", result);
        Assert.EndsWith("};", result);
    }

    [Fact]
    public void Filter_KeepsUnionDeclaration()
    {
        const string code = "union U { int i; float f; };\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("union U { int i; float f; };", result);
    }

    [Fact]
    public void Filter_KeepsEnumDeclaration()
    {
        const string code = "enum Color { RED, GREEN, BLUE };\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("enum Color { RED, GREEN, BLUE };", result);
    }

    [Fact]
    public void Filter_KeepsTypedef()
    {
        const string code = "typedef struct Point Point;\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("typedef struct Point Point;", result);
    }

    [Fact]
    public void Filter_KeepsFunctionPrototype()
    {
        const string code = "void proto(int x);\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("void proto(int x);", result);
    }

    [Fact]
    public void Filter_KeepsGlobalVariable()
    {
        const string code = "int global_var = 5;\n";
        var result = _analyzer.Filter(code);
        Assert.Equal("int global_var = 5;", result);
    }

    [Fact]
    public void Filter_KeepsDoxygenBlockDocComment_BeforeDeclaration()
    {
        const string code = """
            /** Doxygen doc for add. */
            int add(int a, int b) {
                return a + b;
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("/** Doxygen doc for add. */", result);
        Assert.Contains("int add(int a, int b) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_KeepsTripleSlashDocComment()
    {
        const string code = "/// triple-slash doc\nvoid helper(void) {\n    do_thing();\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("/// triple-slash doc", result);
    }

    [Fact]
    public void Filter_DropsOrdinaryLineComment()
    {
        const string code = """
            // a regular comment, dropped
            int add(int a, int b) {
                return a + b;
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("a regular comment", result);
        Assert.Contains("int add(int a, int b) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_DropsCommentInsideCollapsedBody()
    {
        const string code = "int f(void) {\n    // inner comment\n    return 0;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("inner comment", result);
        Assert.Contains("int f(void) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_DropsDocComment_WhenSeparatedByBlankLine()
    {
        // A doc comment detached from the declaration by a blank line is not documentation of it,
        // so it is dropped (only contiguous doc comments are attached).
        const string code = "/** detached doc */\n\nint f(void) {\n    return 0;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("detached doc", result);
        Assert.Contains("int f(void) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_KeepsRegularComment_ThatIsNotDoc_Dropped_EvenWhenAdjacent()
    {
        // A regular /* */ block comment adjacent to a declaration is still dropped: only Doxygen
        // styles (/** */, /*! */, ///, //!) count as documentation.
        const string code = "/* just a note */\nint g(void) {\n    return 1;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("just a note", result);
        Assert.Contains("int g(void) { /* ... */ }", result);
    }

    [Fact]
    public void Filter_IncludeGuard_RecursedNotKeptVerbatim()
    {
        // The critical header case: a whole-file include guard must be recursed into, otherwise
        // the guarded function's body would survive verbatim.
        const string code = """
            #ifndef FOO_H
            #define FOO_H

            int guarded_fn(int y) {
                return y * 2;
            }

            #endif
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("#ifndef FOO_H", result);
        Assert.Contains("#define FOO_H", result);
        Assert.Contains("int guarded_fn(int y) { /* ... */ }", result);
        Assert.Contains("#endif", result);
        Assert.DoesNotContain("return y * 2", result);
    }

    [Fact]
    public void Filter_NestedIfdefElse_BothBranchesRecursed()
    {
        const string code = """
            #ifdef DEBUG
            int dbg(int x) {
                return x + 1;
            }
            #else
            int dbg(int x) {
                return x;
            }
            #endif
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("#ifdef DEBUG", result);
        Assert.Contains("#else", result);
        Assert.Contains("#endif", result);
        // Both branch bodies collapsed.
        Assert.DoesNotContain("return x + 1", result);
        Assert.DoesNotContain("return x;", result);
        Assert.Equal(2, CountOccurrences(result, "int dbg(int x) { /* ... */ }"));
    }

    [Fact]
    public void Filter_DocCommentOnGuardedStruct_KeptInsideConditional()
    {
        const string code = """
            #ifndef FOO_H
            #define FOO_H

            /** A guarded struct. */
            struct S { int a; };

            #endif
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("/** A guarded struct. */", result);
        Assert.Contains("struct S { int a; };", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "this is not valid C at all { { {\n";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_MultipleDeclarations_AllSummarized()
    {
        const string code = """
            #include <stdio.h>

            struct Point { int x; int y; };

            /** adds. */
            int add(int a, int b) {
                return a + b;
            }

            int sub(int a, int b) {
                return a - b;
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("#include <stdio.h>", result);
        Assert.Contains("struct Point { int x; int y; };", result);
        Assert.Contains("/** adds. */", result);
        Assert.Contains("int add(int a, int b) { /* ... */ }", result);
        Assert.Contains("int sub(int a, int b) { /* ... */ }", result);
        Assert.DoesNotContain("return a + b", result);
        Assert.DoesNotContain("return a - b", result);
    }

    [Fact]
    public void Filter_ProducesTokenSavings_OnBodyHeavyInput()
    {
        const string code = """
            /** Computes something involved. */
            int compute(int a, int b, int c) {
                int total = 0;
                for (int i = 0; i < a; i++) {
                    total += b * i;
                    total -= c;
                }
                if (total < 0) {
                    total = 0;
                }
                return total;
            }
            """;
        var result = _analyzer.Filter(code);
        var input = CountTokens(code);
        var output = CountTokens(result);
        var savings = 100.0 - ((double)output / input * 100.0);
        Assert.True(savings >= 50.0, $"Expected >=50% savings on a body-heavy function, got {savings:F1}%");
        Assert.Contains("int compute(int a, int b, int c) { /* ... */ }", result);
    }

    private static int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
