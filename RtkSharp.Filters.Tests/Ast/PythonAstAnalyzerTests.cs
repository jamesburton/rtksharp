using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="PythonAstAnalyzer"/>. Like <see cref="CSharpAstAnalyzerTests"/>, there is
/// no Rust oracle for this RtkSharp-only <c>--level ast</c> tier, so correctness is judged against
/// the parsed structure itself: every expectation below was derived by running the analyzer against
/// the fixture and inspecting its real, observed tree-sitter-backed output — not by guessing what
/// the output "should" look like.
/// </summary>
public sealed class PythonAstAnalyzerTests
{
    private readonly PythonAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsPython() => Assert.Equal(Language.Python, _analyzer.Language);

    [Fact]
    public void Filter_KeepsImportStatement()
    {
        var result = _analyzer.Filter("import os\nimport sys\n");
        Assert.Contains("import os", result);
        Assert.Contains("import sys", result);
    }

    [Fact]
    public void Filter_KeepsFromImport()
    {
        var result = _analyzer.Filter("from sys import path, argv\n");
        Assert.Contains("from sys import path, argv", result);
    }

    [Fact]
    public void Filter_KeepsModuleDocstring()
    {
        const string code = "\"Module docstring.\"\nimport os\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("\"Module docstring.\"", result);
    }

    [Fact]
    public void Filter_KeepsModuleLevelAssignmentVerbatim()
    {
        var result = _analyzer.Filter("TOP = 1\nNAME = \"rtk\"\n");
        Assert.Contains("TOP = 1", result);
        Assert.Contains("NAME = \"rtk\"", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionBody_KeepsSignature()
    {
        const string code = "def greet(name):\n    x = 1\n    return \"hi \" + name\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def greet(name):", result);
        Assert.Contains("...", result);
        Assert.DoesNotContain("x = 1", result);
        Assert.DoesNotContain("return", result);
    }

    [Fact]
    public void Filter_KeepsReturnTypeAndParameterAnnotations()
    {
        const string code = "def greet(name: str) -> str:\n    return name\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def greet(name: str) -> str:", result);
    }

    [Fact]
    public void Filter_KeepsFunctionDocstring_WhenCollapsingBody()
    {
        const string code = "def greet(name):\n    \"This is a docstring.\"\n    return name\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("\"This is a docstring.\"", result);
        Assert.Contains("...", result);
        Assert.DoesNotContain("return name", result);
    }

    [Fact]
    public void Filter_FunctionWithoutDocstring_StillGetsEllipsisPlaceholder()
    {
        var result = _analyzer.Filter("def add(a, b):\n    return a + b\n");
        Assert.Contains("def add(a, b):", result);
        Assert.Contains("...", result);
        Assert.DoesNotContain("a + b", result);
    }

    [Fact]
    public void Filter_KeepsDecorator()
    {
        const string code = "@decorator\ndef greet():\n    pass\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("@decorator", result);
        Assert.Contains("def greet():", result);
    }

    [Fact]
    public void Filter_KeepsMultipleDecorators()
    {
        const string code = "@decorator\n@app.route(\"/x\")\ndef greet():\n    return 1\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("@decorator", result);
        Assert.Contains("@app.route(\"/x\")", result);
    }

    [Fact]
    public void Filter_DropsPlainComment()
    {
        const string code = "# a leading comment\nimport os\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("a leading comment", result);
        Assert.Contains("import os", result);
    }

    [Fact]
    public void Filter_DropsCommentInsideClassBody_KeepsMembers()
    {
        const string code = "class Foo:\n    # internal comment\n    CONST = 42\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("internal comment", result);
        Assert.Contains("CONST = 42", result);
    }

    [Fact]
    public void Filter_KeepsClassSignatureAndDocstring()
    {
        const string code = "class Foo(Base):\n    '''Class docstring.'''\n    x = 1\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("class Foo(Base):", result);
        Assert.Contains("'''Class docstring.'''", result);
    }

    [Fact]
    public void Filter_KeepsClassLevelAssignmentVerbatim()
    {
        const string code = "class Foo:\n    CONST = 42\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("CONST = 42", result);
    }

    [Fact]
    public void Filter_CollapsesMethodBody_KeepsMethodSignature()
    {
        const string code = "class Foo:\n    def bar(self):\n        return self._x\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def bar(self):", result);
        Assert.Contains("...", result);
        Assert.DoesNotContain("self._x", result);
    }

    [Fact]
    public void Filter_KeepsDecoratedMethod()
    {
        const string code = "class Foo:\n    @property\n    def bar(self):\n        return self._x\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("@property", result);
        Assert.Contains("def bar(self):", result);
        Assert.DoesNotContain("self._x", result);
    }

    [Fact]
    public void Filter_KeepsNestedClass()
    {
        const string code = "class Outer:\n    class Inner:\n        pass\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("class Outer:", result);
        Assert.Contains("class Inner:", result);
    }

    [Fact]
    public void Filter_KeepsAsyncFunctionSignature()
    {
        const string code = "async def fetch(url):\n    await get(url)\n    return 1\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("async def fetch(url):", result);
        Assert.Contains("...", result);
        Assert.DoesNotContain("await get", result);
    }

    [Fact]
    public void Filter_KeepsUnhandledCompoundStatementVerbatim()
    {
        // A module-level guard is not specially handled, so it is kept verbatim (full body).
        const string code = "if __name__ == \"__main__\":\n    main()\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("if __name__ == \"__main__\":", result);
        Assert.Contains("main()", result);
    }

    [Fact]
    public void Filter_PreservesUnicodeDocstringAndSignature()
    {
        const string code = "def f():\n    \"日本語 docstring\"\n    return 'ключ'\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def f():", result);
        Assert.Contains("日本語 docstring", result);
        Assert.Contains("...", result);
        Assert.DoesNotContain("ключ", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "def foo(\n  this is not python {{{";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() =>
        Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_WhitespaceOnlyInput_ReturnsEmptyAfterTrim() =>
        Assert.Equal(string.Empty, _analyzer.Filter("\n\n   \n"));

    [Fact]
    public void Filter_ProducesValidLookingPythonForFullModule()
    {
        const string code = """
            import os

            @decorator
            def greet(name: str) -> str:
                "Docstring."
                return name

            class Foo(Base):
                CONST = 42

                def bar(self):
                    return self._x
            """;
        var result = _analyzer.Filter(code);

        // Signatures, decorator, imports, docstring, and class constant survive...
        Assert.Contains("import os", result);
        Assert.Contains("@decorator", result);
        Assert.Contains("def greet(name: str) -> str:", result);
        Assert.Contains("\"Docstring.\"", result);
        Assert.Contains("class Foo(Base):", result);
        Assert.Contains("CONST = 42", result);
        Assert.Contains("def bar(self):", result);

        // ...while both bodies are collapsed to the ellipsis placeholder.
        Assert.DoesNotContain("return name", result);
        Assert.DoesNotContain("self._x", result);
        Assert.Contains("...", result);
    }
}
