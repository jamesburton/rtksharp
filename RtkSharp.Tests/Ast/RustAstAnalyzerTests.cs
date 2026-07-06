using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="RustAstAnalyzer"/>. As with <see cref="CSharpAstAnalyzerTests"/>, there is
/// no Rust oracle for this RtkSharp-only <c>--level ast</c> tier, so correctness is judged against
/// the parsed structure itself: every expectation below was derived by running the analyzer
/// (backed by the real native tree-sitter <c>Rust</c> grammar) against the fixture and inspecting
/// the actual output — not assumed — per this project's "verify, don't guess" discipline.
/// </summary>
public sealed class RustAstAnalyzerTests
{
    private readonly RustAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsRust() => Assert.Equal(Language.Rust, _analyzer.Language);

    [Fact]
    public void Filter_KeepsUseDeclarations()
    {
        const string code = "use std::collections::HashMap;\nuse std::fmt;\nfn main() {}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("use std::collections::HashMap;", result);
        Assert.Contains("use std::fmt;", result);
    }

    [Fact]
    public void Filter_KeepsModDeclaration()
    {
        const string code = "mod submod;\nfn main() {}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("mod submod;", result);
    }

    [Fact]
    public void Filter_StripsOrdinaryLineComment_KeepsOuterDocComment()
    {
        const string code = """
            // an ordinary comment, dropped
            /// A kept doc comment.
            pub fn f() {
                let x = 1;
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("an ordinary comment", result);
        Assert.Contains("/// A kept doc comment.", result);
    }

    [Fact]
    public void Filter_KeepsInnerDocComment()
    {
        const string code = "//! module-level inner doc\nuse std::fmt;\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("//! module-level inner doc", result);
    }

    [Fact]
    public void Filter_FourSlashComment_TreatedAsOrdinary_Dropped()
    {
        // In Rust `////` is an ordinary comment, NOT a doc comment — a naive StartsWith("///")
        // would wrongly keep it; the grammar's doc-comment marker (and the `////` guard) reject it.
        const string code = "//// not a doc comment\npub fn f() {\n    let x = 1;\n}\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("not a doc comment", result);
        Assert.Contains("pub fn f()", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionBody_KeepsSignature()
    {
        const string code = """
            pub fn add(a: i32, b: i32) -> i32 {
                let sum = a + b;
                sum
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("pub fn add(a: i32, b: i32) -> i32 { /* ... */ }", result);
        Assert.DoesNotContain("let sum", result);
    }

    [Fact]
    public void Filter_GenericFunctionWithWhereClause_SignaturePreserved()
    {
        const string code = "pub fn map<T, U>(x: T) -> U where T: Clone {\n    todo!()\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("pub fn map<T, U>(x: T) -> U where T: Clone { /* ... */ }", result);
        Assert.DoesNotContain("todo!()", result);
    }

    [Fact]
    public void Filter_StructKeptVerbatimWithFields()
    {
        const string code = "pub struct Point {\n    pub x: i32,\n    y: i32,\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("pub struct Point {", result);
        Assert.Contains("pub x: i32,", result);
        Assert.Contains("y: i32,", result);
    }

    [Fact]
    public void Filter_EnumKeptVerbatimWithVariants()
    {
        const string code = "pub enum Color {\n    Red,\n    Green,\n    Blue,\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("pub enum Color {", result);
        Assert.Contains("Red,", result);
        Assert.Contains("Green,", result);
        Assert.Contains("Blue,", result);
    }

    [Fact]
    public void Filter_Trait_KeepsMethodSignature_CollapsesDefaultBody()
    {
        const string code = """
            pub trait Shape {
                /// area doc
                fn area(&self) -> f64;
                fn name(&self) -> String {
                    "shape".to_string()
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("pub trait Shape {", result);
        Assert.Contains("/// area doc", result);
        Assert.Contains("fn area(&self) -> f64;", result);
        Assert.Contains("fn name(&self) -> String { /* ... */ }", result);
        Assert.DoesNotContain("\"shape\"", result);
    }

    [Fact]
    public void Filter_Impl_KeepsHeaderAndDocComment_CollapsesMethodBody()
    {
        const string code = """
            impl Point {
                /// Doc for new.
                pub fn new(x: i32, y: i32) -> Self {
                    Point { x, y }
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("impl Point {", result);
        Assert.Contains("/// Doc for new.", result);
        Assert.Contains("pub fn new(x: i32, y: i32) -> Self { /* ... */ }", result);
        Assert.DoesNotContain("Point { x, y }", result);
    }

    [Fact]
    public void Filter_KeepsConstAndStatic()
    {
        const string code = "const MAX: usize = 100;\nstatic NAME: &str = \"rtk\";\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("const MAX: usize = 100;", result);
        Assert.Contains("static NAME: &str = \"rtk\";", result);
    }

    [Fact]
    public void Filter_KeepsAttributeItem()
    {
        const string code = "#[derive(Debug)]\npub struct Point {\n    x: i32,\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("#[derive(Debug)]", result);
        Assert.Contains("pub struct Point {", result);
    }

    [Fact]
    public void Filter_InlineModule_CollapsesInnerFunctionBodies()
    {
        const string code = """
            mod inner {
                pub fn hidden() {
                    secret();
                }
            }
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("mod inner {", result);
        Assert.Contains("pub fn hidden() { /* ... */ }", result);
        Assert.DoesNotContain("secret();", result);
    }

    [Fact]
    public void Filter_UnicodeIdentifiersAndDoc_PreservedWithReturnType()
    {
        // Regression guard: a byte-offset-based header slice corrupted this (dropped `-> i32`,
        // injected stray characters); the text-based header extraction handles multi-byte source.
        const string code = "/// café ☕ docs\npub fn café(名前: i32) -> i32 {\n    名前 + 1\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("/// café ☕ docs", result);
        Assert.Contains("pub fn café(名前: i32) -> i32 { /* ... */ }", result);
        Assert.DoesNotContain("名前 + 1", result);
    }

    [Fact]
    public void Filter_BlockDocComment_Kept_PlainBlockComment_Dropped()
    {
        const string code = "/** block doc */\npub fn g() { work(); }\n/* plain block */\npub fn h() { work(); }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("/** block doc */", result);
        Assert.DoesNotContain("plain block", result);
        Assert.Contains("pub fn g() { /* ... */ }", result);
        Assert.Contains("pub fn h() { /* ... */ }", result);
    }

    [Fact]
    public void Filter_MacroRulesDefinition_KeptVerbatim_NotDropped()
    {
        // A function-like macro_rules! definition is not specially modelled; it is kept verbatim
        // (never silently dropped) via the unhandled-item fallback.
        const string code = "macro_rules! my_macro {\n    () => {};\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("macro_rules! my_macro", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "fn broken( { { {\nthis is not rust\n";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_FreeFunctionDocComment_Kept()
    {
        const string code = "/// Doc for a free function.\npub fn add(a: i32, b: i32) -> i32 {\n    a + b\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("/// Doc for a free function.", result);
        Assert.Contains("pub fn add(a: i32, b: i32) -> i32 { /* ... */ }", result);
    }
}
