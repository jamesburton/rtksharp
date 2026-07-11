using RtkSharp.Core;

namespace RtkSharp.Tests.Core;

/// <summary>
/// Tests for <see cref="SourceFilter"/>/<see cref="MinimalFilter"/>/<see cref="AggressiveFilter"/>,
/// ported 1:1 from <c>src/core/filter.rs</c>'s own <c>#[cfg(test)] mod tests</c>, plus new
/// coverage for <see cref="Language.CSharp"/> (see <see cref="FilterLevel"/>'s remarks in
/// <c>SourceFilter.cs</c> for why that addition is inert against the Rust oracle) and the
/// Rust-<c>Path::extension()</c>-semantics dotfile edge cases.
/// </summary>
public sealed class SourceFilterTests
{
    // ---- FilterLevelParser (ported: test_filter_level_parsing) ----

    [Theory]
    [InlineData("none", FilterLevel.None)]
    [InlineData("NONE", FilterLevel.None)]
    [InlineData("minimal", FilterLevel.Minimal)]
    [InlineData("aggressive", FilterLevel.Aggressive)]
    public void Parse_RecognizedLevels_CaseInsensitive(string value, FilterLevel expected) =>
        Assert.Equal(expected, FilterLevelParser.Parse(value));

    [Fact]
    public void Parse_UnknownLevel_Throws() =>
        Assert.Throws<ArgumentException>(() => FilterLevelParser.Parse("bogus"));

    // ---- LanguageExtensions.FromExtension (ported: test_language_detection) ----

    [Theory]
    [InlineData("rs", Language.Rust)]
    [InlineData("py", Language.Python)]
    [InlineData("js", Language.JavaScript)]
    [InlineData("ts", Language.TypeScript)]
    [InlineData("go", Language.Go)]
    [InlineData("c", Language.C)]
    [InlineData("cpp", Language.Cpp)]
    [InlineData("java", Language.Java)]
    [InlineData("rb", Language.Ruby)]
    [InlineData("sh", Language.Shell)]
    [InlineData("cs", Language.CSharp)]
    [InlineData("bogus", Language.Unknown)]
    public void FromExtension_DetectsLanguage(string extension, Language expected) =>
        Assert.Equal(expected, LanguageExtensions.FromExtension(extension));

    // ---- LanguageExtensions.FromExtension data formats (ported: test_language_detection_data_formats) ----

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("yml")]
    [InlineData("toml")]
    [InlineData("xml")]
    [InlineData("csv")]
    [InlineData("md")]
    [InlineData("lock")]
    public void FromExtension_DataFormats_MapToData(string extension) =>
        Assert.Equal(Language.Data, LanguageExtensions.FromExtension(extension));

    // ---- MinimalFilter (ported: test_json_no_comment_stripping, regression #464) ----

    [Fact]
    public void MinimalFilter_Json_NeverStripsCommentLikePatterns()
    {
        const string json = """
            {
              "workspaces": {
                "packages": [
                  "packages/*"
                ]
              },
              "scripts": {
                "build": "bun run --workspaces build"
              },
              "lint-staged": {
                "**/package.json": [
                  "sort-package-json"
                ]
              }
            }
            """;
        var result = new MinimalFilter().Filter(json, Language.Data);
        Assert.Contains("packages/*", result);
        Assert.Contains("scripts", result);
        Assert.Contains("lint-staged", result);
        Assert.Contains("**/package.json", result);
    }

    // ---- AggressiveFilter (ported: test_json_aggressive_filter_preserves_structure) ----

    [Fact]
    public void AggressiveFilter_Json_NeverStripsCommentLikePatterns()
    {
        const string json = """
            {
              "name": "my-app",
              "dependencies": {
                "react": "^18.0.0"
              },
              "scripts": {
                "dev": "next dev /* not a comment */"
              }
            }
            """;
        var result = new AggressiveFilter().Filter(json, Language.Data);
        Assert.Contains("/* not a comment */", result);
    }

    // ---- MinimalFilter (ported: test_minimal_filter_removes_comments) ----

    [Fact]
    public void MinimalFilter_RemovesLineComments_KeepsCode()
    {
        const string code = "\n// This is a comment\nfn main() {\n    println!(\"Hello\");\n}\n";
        var result = new MinimalFilter().Filter(code, Language.Rust);
        Assert.DoesNotContain("// This is a comment", result);
        Assert.Contains("fn main()", result);
    }

    // ---- MinimalFilter: doc comments kept ----

    [Fact]
    public void MinimalFilter_KeepsRustDocComments()
    {
        const string code = "/// A doc comment\nfn documented() {}\n// a regular comment\n";
        var result = new MinimalFilter().Filter(code, Language.Rust);
        Assert.Contains("/// A doc comment", result);
        Assert.DoesNotContain("// a regular comment", result);
    }

    // ---- MinimalFilter: Python docstrings kept ----
    // Oracle-verified (target/release/rtk.exe read --level minimal doc.py) genuine quirk: a
    // single-line docstring (both `"""` delimiters on the same line) toggles `in_docstring` on
    // but never back off within that line — Rust's own guard only checks
    // `trimmed.starts_with("\"\"\"")` once per line, with no same-line-close detection — so
    // every subsequent line, including `# a comment`, is treated as still inside the docstring
    // and kept verbatim rather than stripped. Faithfully preserved, not "fixed".
    [Fact]
    public void MinimalFilter_Python_SingleLineDocstringLeavesDocstringModeStuckOn()
    {
        const string code = "\"\"\"Module docstring.\"\"\"\n# a comment\ndef f():\n    pass\n";
        var result = new MinimalFilter().Filter(code, Language.Python);
        Assert.Equal(
            "\"\"\"Module docstring.\"\"\"\n# a comment\ndef f():\n    pass",
            result);
    }

    // ---- MinimalFilter: block comments stripped ----

    [Fact]
    public void MinimalFilter_StripsBlockComments()
    {
        const string code = "/* block\n   comment */\nfn main() {}\n";
        var result = new MinimalFilter().Filter(code, Language.Rust);
        Assert.DoesNotContain("block", result);
        Assert.Contains("fn main()", result);
    }

    // ---- MinimalFilter: blank-line normalization ----

    [Fact]
    public void MinimalFilter_CollapsesThreeOrMoreBlankLinesToTwo()
    {
        var code = "a\n\n\n\n\nb\n";
        var result = new MinimalFilter().Filter(code, Language.Unknown);
        Assert.Equal("a\n\nb", result);
    }

    // ---- AggressiveFilter: keeps imports/signatures, collapses bodies ----
    // Oracle-verified (target/release/rtk.exe read --level aggressive) against this exact
    // input. The brace_depth counter resets to 0 on the signature line itself (not 1, even
    // though that line ends with '{'), so the very next body line — even with no braces of its
    // own — immediately satisfies "brace_depth <= 0" and ends body-collapse tracking after only
    // one line; the line after that then falls through to the bare "let "-prefix keep-rule and
    // is printed verbatim, and the function's own closing brace is dropped entirely. This is a
    // genuine heuristic artifact of the naive brace counter, not a hypothesis — verified against
    // the real oracle, not just reasoned from source.
    [Fact]
    public void AggressiveFilter_KeepsImportsAndSignatures_CollapsesBody()
    {
        const string code = "use foo;\nfn main() {\n    let x = 1;\n    let y = 2;\n}\n";
        var result = new AggressiveFilter().Filter(code, Language.Rust);
        Assert.Equal("use foo;\nfn main() {\n    // ... implementation\n    let y = 2;", result);
    }

    // ---- CSharp: identical output to Unknown (see SourceFilter.cs remarks) ----

    [Fact]
    public void MinimalFilter_CSharp_SameCommentPatternsAsUnknown()
    {
        const string code = "// a comment\n/* block */\nnamespace Foo;\n";
        var csharpResult = new MinimalFilter().Filter(code, Language.CSharp);
        var unknownResult = new MinimalFilter().Filter(code, Language.Unknown);
        Assert.Equal(unknownResult, csharpResult);
        Assert.DoesNotContain("a comment", csharpResult);
        Assert.Contains("namespace Foo;", csharpResult);
    }

    // Regression test for a real bug caught in independent review: an earlier version gave
    // CSharp the JS/Java family's DocBlockStart ("/**"), not Unknown's (null). That mattered
    // ONLY for lines starting with "/**" specifically — the fixture above (plain "/* block */")
    // never exercised the divergent path, so it passed even with the bug present. This fixture
    // deliberately starts a block comment with "/**" to lock in the fix.
    [Fact]
    public void MinimalFilter_CSharp_JavaDocStyleBlockComment_StrippedSameAsUnknown()
    {
        const string code = "/**\n * doc\n */\nclass Foo {}\n";
        var csharpResult = new MinimalFilter().Filter(code, Language.CSharp);
        var unknownResult = new MinimalFilter().Filter(code, Language.Unknown);
        Assert.Equal(unknownResult, csharpResult);
        Assert.Equal("class Foo {}", csharpResult);
    }

    // C# gets its own signature/import patterns (CSharpTypeSignature/CSharpMemberSignature/
    // CSharpImport in AggressiveFilter), NOT the generic FuncSignature/ImportPattern every other
    // language shares. An earlier version of this filter reused the shared heuristic verbatim for
    // C# too, but that heuristic only recognizes Rust/Python/JS-family keywords ("pub",
    // "fn"/"def"/"func", "use "/"import ") — none of which appear in idiomatic C#
    // ("using System;", "public static class Foo") — so real .cs files collapsed to near-empty
    // output instead of a useful signature outline. Since Language.CSharp has no Rust oracle
    // counterpart (RtkSharp-only addition — see this file's Language enum remarks), a
    // C#-specific pattern here carries zero parity risk; every other language's tests below and
    // in the "ported" sections above are unaffected (unchanged FuncSignature/ImportPattern path).
    [Fact]
    public void AggressiveFilter_CSharp_KeepsUsingDirective_AndBareClassSignature()
    {
        const string code = "using System;\nclass Foo {\n    void Bar() {\n        var x = 1;\n    }\n}\n";
        var result = new AggressiveFilter().Filter(code, Language.CSharp);
        // "using System;" is now kept (previously dropped). "void Bar()" has no access modifier
        // (a local-function-style declaration with none is uncommon but legal), so it isn't
        // recognized by CSharpMemberSignature (which requires >=1 modifier to avoid
        // misclassifying call expressions like "Foo(bar)" as signatures) — its braces still
        // survive via the generic brace-tracking fallback, just merged into the class's own body
        // region rather than getting a distinct signature line + collapse marker of its own.
        Assert.Equal("using System;\nclass Foo {\n    void Bar() {\n    }", result);
    }

    [Fact]
    public void AggressiveFilter_CSharp_KeepsModifierQualifiedTypeAndMemberSignatures()
    {
        const string code =
            "using System;\nusing System.Collections.Generic;\n\n" +
            "namespace RtkSharp.Demo;\n\n" +
            "public static class Widget\n{\n" +
            "    private const int MaxCount = 10;\n\n" +
            "    public static string Format(string input, int count)\n    {\n" +
            "        var result = input.Trim();\n" +
            "        for (var i = 0; i < count; i++)\n        {\n" +
            "            result += \".\";\n" +
            "        }\n" +
            "        return result;\n" +
            "    }\n\n" +
            "    internal Widget()\n    {\n" +
            "        Console.WriteLine(\"created\");\n" +
            "    }\n" +
            "}\n";

        var result = new AggressiveFilter().Filter(code, Language.CSharp);

        Assert.Contains("using System;", result);
        Assert.Contains("using System.Collections.Generic;", result);
        Assert.Contains("namespace RtkSharp.Demo;", result);
        Assert.Contains("public static class Widget", result);
        Assert.Contains("public static string Format(string input, int count)", result);
        Assert.Contains("internal Widget()", result);
        // Body statements are excluded — the actual point of "aggressive": a signature outline,
        // not the implementation.
        Assert.DoesNotContain("input.Trim()", result);
        Assert.DoesNotContain("Console.WriteLine", result);
        Assert.DoesNotContain("result +=", result);
        // Known, pre-existing heuristic limitation shared with every other language (not
        // introduced by the C# patterns above): a class/struct-body-scope field or const
        // declaration that isn't itself a recognized signature is silently dropped while nested
        // one level inside a matched type's body-tracking region — the trailing
        // const/static-prefix fallback below only fires when NOT already inside a tracked body.
        // Documented here rather than "fixed" because fixing it means restructuring the shared
        // brace-tracking state machine every ported language's oracle-verified output also
        // depends on.
        Assert.DoesNotContain("MaxCount", result);
    }

    [Fact]
    public void AggressiveFilter_CSharp_SemicolonTerminatedDeclaration_DoesNotSwallowFollowingLine()
    {
        // A self-contained declaration (no body to collapse) must not put the filter into
        // body-tracking mode — otherwise the next unrelated top-level line gets misread as the
        // end of a (nonexistent) body and dropped or mislabeled.
        const string code =
            "internal sealed partial class GainJsonContext : JsonSerializerContext;\n" +
            "public static class NextThing\n{\n}\n";

        var result = new AggressiveFilter().Filter(code, Language.CSharp);

        Assert.Contains("internal sealed partial class GainJsonContext : JsonSerializerContext;", result);
        Assert.Contains("public static class NextThing", result);
    }

    [Fact]
    public void AggressiveFilter_CSharp_DoesNotMatchArbitraryCallExpressionAsSignature()
    {
        // "Console.WriteLine(x);" has no modifier prefix, so CSharpMemberSignature must not treat
        // it as a method declaration — this is the guard that keeps the heuristic from exploding
        // back into "everything looks like a signature" territory.
        const string code = "public static void Run()\n{\n    Console.WriteLine(\"hi\");\n}\n";
        var result = new AggressiveFilter().Filter(code, Language.CSharp);
        Assert.DoesNotContain("Console.WriteLine", result);
    }

    // ---- SourceFilter.GetFilter selects the right strategy ----

    [Fact]
    public void GetFilter_None_ReturnsContentUnchanged()
    {
        var filter = SourceFilter.GetFilter(FilterLevel.None);
        Assert.IsType<NoFilter>(filter);
        Assert.Equal("// x\n", filter.Filter("// x\n", Language.Rust));
    }

    [Fact]
    public void GetFilter_Minimal_ReturnsMinimalFilter() =>
        Assert.IsType<MinimalFilter>(SourceFilter.GetFilter(FilterLevel.Minimal));

    [Fact]
    public void GetFilter_Aggressive_ReturnsAggressiveFilter() =>
        Assert.IsType<AggressiveFilter>(SourceFilter.GetFilter(FilterLevel.Aggressive));
}
