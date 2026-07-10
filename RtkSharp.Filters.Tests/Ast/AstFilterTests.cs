using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>Tests for <see cref="AstAnalyzerRegistry"/> and <see cref="AstFilter"/>.</summary>
public sealed class AstFilterTests
{
    [Fact]
    public void Registry_HasCSharpAnalyzerRegisteredByDefault()
    {
        Assert.True(AstAnalyzerRegistry.TryGet(Language.CSharp, out var analyzer));
        Assert.IsType<CSharpAstAnalyzer>(analyzer);
        Assert.Contains(Language.CSharp, AstAnalyzerRegistry.SupportedLanguages);
    }

    [Fact]
    public void Registry_TryGet_UnregisteredLanguage_ReturnsFalse()
    {
        // Rust itself has no analyzer registered for it (yet) at time of writing.
        var found = AstAnalyzerRegistry.TryGet(Language.Rust, out var analyzer);
        if (!found)
        {
            Assert.Null(analyzer);
        }
    }

    [Fact]
    public void Filter_RegisteredLanguage_UsesAnalyzer()
    {
        const string code = "public class Foo\n{\n    public void Bar()\n    {\n        var x = 1;\n    }\n}\n";
        var result = AstFilter.Filter(code, Language.CSharp);
        Assert.Contains("public void Bar()", result);
        Assert.DoesNotContain("var x = 1;", result);
        Assert.Contains("/* ... */", result);
    }

    [Fact]
    public void Filter_UnregisteredLanguage_FallsBackToAggressiveFilterAndWarns()
    {
        var origErr = Console.Error;
        var se = new StringWriter();
        try
        {
            Console.SetError(se);
            // Language.Unknown has no registered AST analyzer (unlike Rust, which now does —
            // see RustAstAnalyzer) and is a realistic case for a file with an unrecognized
            // extension, so it's a stable target for exercising the fallback path.
            const string code = "// a comment\nvoid main() {\n    int x = 1;\n}\n";
            var aggressiveResult = new AggressiveFilter().Filter(code, Language.Unknown);
            var astResult = AstFilter.Filter(code, Language.Unknown);
            Assert.Equal(aggressiveResult, astResult);
            Assert.Contains("falling back to aggressive", se.ToString());
        }
        finally
        {
            Console.SetError(origErr);
        }
    }
}
