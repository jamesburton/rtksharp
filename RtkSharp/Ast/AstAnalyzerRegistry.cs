using RtkSharp.Core;

namespace RtkSharp.Ast;

/// <summary>
/// Pluggable registry of <see cref="IAstAnalyzer"/> implementations, keyed by <see cref="Language"/>.
/// New languages are added by registering another analyzer here — nothing else in
/// <c>rtk read</c>'s dispatch changes.
/// </summary>
public static class AstAnalyzerRegistry
{
    private static readonly Dictionary<Language, IAstAnalyzer> Analyzers = new();

    static AstAnalyzerRegistry()
    {
        Register(new CSharpAstAnalyzer());
    }

    /// <summary>Registers (or replaces) the analyzer for its <see cref="IAstAnalyzer.Language"/>.</summary>
    /// <param name="analyzer">The analyzer to register.</param>
    public static void Register(IAstAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        Analyzers[analyzer.Language] = analyzer;
    }

    /// <summary>Attempts to find a registered analyzer for <paramref name="language"/>.</summary>
    /// <param name="language">The language to look up.</param>
    /// <param name="analyzer">The registered analyzer, if found.</param>
    /// <returns>True if an analyzer is registered for <paramref name="language"/>.</returns>
    public static bool TryGet(Language language, out IAstAnalyzer? analyzer) =>
        Analyzers.TryGetValue(language, out analyzer);

    /// <summary>The languages with a registered AST analyzer, for diagnostics/reporting.</summary>
    public static IReadOnlyCollection<Language> SupportedLanguages => Analyzers.Keys;
}
