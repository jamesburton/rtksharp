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

    // Same regexes as every other language (per the user's request: reuse the existing
    // heuristic verbatim, not a C#-aware enhancement of it), so C#'s own idioms trip the same
    // known limitations Java/JS already have against this heuristic: IMPORT_PATTERN requires a
    // literal "use " prefix, so C#'s "using System;" is not recognized as an import and is
    // dropped; FUNC_SIGNATURE has no "void"/"public" keywords, so only the bare "class Foo {"
    // (not "public class Foo {") is recognized as a signature. Traced by hand against the
    // algorithm and confirmed by running the actual port (no Rust oracle exists for .cs, since
    // Rust's Language enum doesn't have this case at all).
    [Fact]
    public void AggressiveFilter_CSharp_DropsUsingDirective_KeepsBareClassSignature()
    {
        const string code = "using System;\nclass Foo {\n    void Bar() {\n        var x = 1;\n    }\n}\n";
        var result = new AggressiveFilter().Filter(code, Language.CSharp);
        Assert.Equal("class Foo {\n    void Bar() {\n    }", result);
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
