using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="GoAstAnalyzer"/>. As with the C# reference analyzer, this is a
/// RtkSharp-only <c>--level ast</c> tier with no Rust oracle, so correctness is judged against the
/// parsed structure itself: every expectation below was derived by running the analyzer over the
/// fixture and inspecting its genuine output (via a throwaway tree-sitter probe of the real Go
/// grammar), not assumed from memory of Go's syntax.
/// </summary>
public sealed class GoAstAnalyzerTests
{
    private readonly GoAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsGo() => Assert.Equal(Language.Go, _analyzer.Language);

    [Fact]
    public void Filter_KeepsPackageClause()
    {
        const string code = "package main\n\nfunc main() {}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("package main", result);
    }

    [Fact]
    public void Filter_KeepsSingleImport()
    {
        const string code = "package main\n\nimport \"fmt\"\n\nfunc main() {}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("import \"fmt\"", result);
    }

    [Fact]
    public void Filter_KeepsImportBlockVerbatim()
    {
        const string code = "package main\n\nimport (\n\t\"fmt\"\n\t\"errors\"\n)\n\nfunc main() {}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("import (", result);
        Assert.Contains("\"fmt\"", result);
        Assert.Contains("\"errors\"", result);
        Assert.Contains(")", result);
    }

    [Fact]
    public void Filter_KeepsFuncSignature_CollapsesBody()
    {
        const string code = "package main\n\nfunc Add(a int, b int) int {\n\tsum := a + b\n\treturn sum\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("func Add(a int, b int) int { /* ... */ }", result);
        Assert.DoesNotContain("sum := a + b", result);
        Assert.DoesNotContain("return sum", result);
    }

    [Fact]
    public void Filter_KeepsMethodReceiver_CollapsesBody()
    {
        const string code = "package main\n\nfunc (p Point) Dist() float64 {\n\treturn 0\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("func (p Point) Dist() float64 { /* ... */ }", result);
        Assert.DoesNotContain("return 0", result);
    }

    [Fact]
    public void Filter_KeepsPointerReceiver()
    {
        const string code = "package main\n\nfunc (p *Point) Scale(f float64) {\n\tp.X *= f\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("func (p *Point) Scale(f float64) { /* ... */ }", result);
        Assert.DoesNotContain("p.X *= f", result);
    }

    [Fact]
    public void Filter_KeepsStructTypeWithFields()
    {
        const string code = "package main\n\ntype Point struct {\n\tX int\n\tY int\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("type Point struct {", result);
        Assert.Contains("X int", result);
        Assert.Contains("Y int", result);
    }

    [Fact]
    public void Filter_KeepsInterfaceWithMethodSignatures()
    {
        const string code = "package main\n\ntype Shape interface {\n\tArea() float64\n\tPerimeter() float64\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("type Shape interface {", result);
        Assert.Contains("Area() float64", result);
        Assert.Contains("Perimeter() float64", result);
    }

    [Fact]
    public void Filter_KeepsTypeAlias()
    {
        const string code = "package main\n\ntype Celsius float64\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("type Celsius float64", result);
    }

    [Fact]
    public void Filter_KeepsConstDeclaration()
    {
        const string code = "package main\n\nconst Pi = 3.14\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("const Pi = 3.14", result);
    }

    [Fact]
    public void Filter_KeepsConstBlockVerbatim()
    {
        const string code = "package main\n\nconst (\n\tA = 1\n\tB = 2\n)\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("const (", result);
        Assert.Contains("A = 1", result);
        Assert.Contains("B = 2", result);
    }

    [Fact]
    public void Filter_KeepsVarDeclaration()
    {
        const string code = "package main\n\nvar count int = 0\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("var count int = 0", result);
    }

    [Fact]
    public void Filter_KeepsGodocCommentImmediatelyBeforeFunc()
    {
        const string code = "package main\n\n// Add returns the sum.\nfunc Add(a int, b int) int {\n\treturn a + b\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("// Add returns the sum.", result);
        Assert.Contains("func Add(a int, b int) int { /* ... */ }", result);
    }

    [Fact]
    public void Filter_KeepsMultiLineGodocBlock()
    {
        const string code = "package main\n\n// Add returns the sum.\n// It takes two ints.\nfunc Add(a int, b int) int {\n\treturn a + b\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("// Add returns the sum.", result);
        Assert.Contains("// It takes two ints.", result);
    }

    [Fact]
    public void Filter_KeepsGodocOnTypeDeclaration()
    {
        const string code = "package main\n\n// Point is a 2D point.\ntype Point struct {\n\tX int\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("// Point is a 2D point.", result);
        Assert.Contains("type Point struct {", result);
    }

    [Fact]
    public void Filter_DropsStandaloneCommentSeparatedByBlankLine()
    {
        // A comment with a blank line between it and the following declaration is NOT godoc.
        const string code = "package main\n\n// this is a free-floating note\n\nfunc Add() int {\n\treturn 1\n}\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("free-floating note", result);
        Assert.Contains("func Add() int { /* ... */ }", result);
    }

    [Fact]
    public void Filter_DropsCommentInsideFunctionBody()
    {
        const string code = "package main\n\nfunc Add() int {\n\t// compute the answer\n\treturn 42\n}\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("compute the answer", result);
        Assert.DoesNotContain("return 42", result);
        Assert.Contains("func Add() int { /* ... */ }", result);
    }

    [Fact]
    public void Filter_DropsTrailingStandaloneComment()
    {
        // Only the contiguous block nearest a declaration is godoc: a blank line breaks the run,
        // so the earlier standalone comment is dropped while the adjacent one is kept.
        const string code = "package main\n\n// far comment\n\n// near comment\nfunc Add() int {\n\treturn 1\n}\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("far comment", result);
        Assert.Contains("// near comment", result);
    }

    [Fact]
    public void Filter_KeepsPackageDocComment()
    {
        const string code = "// Package math provides helpers.\npackage math\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("// Package math provides helpers.", result);
        Assert.Contains("package math", result);
    }

    [Fact]
    public void Filter_MultipleFunctions_AllBodiesCollapsed()
    {
        const string code = "package main\n\nfunc A() int {\n\treturn 1\n}\n\nfunc B() int {\n\treturn 2\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("func A() int { /* ... */ }", result);
        Assert.Contains("func B() int { /* ... */ }", result);
        Assert.DoesNotContain("return 1", result);
        Assert.DoesNotContain("return 2", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "this is not @@ valid go >>><<< {{{\n";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_AchievesTokenReductionOnFunctionBodies()
    {
        const string code = "package main\n\nfunc Compute(a int, b int) int {\n\tx := a * b\n\ty := x + a\n\tz := y - b\n\treturn z\n}\n";
        var result = _analyzer.Filter(code);
        Assert.True(CountTokens(result) < CountTokens(code));
        Assert.Contains("func Compute(a int, b int) int { /* ... */ }", result);
    }

    private static int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
