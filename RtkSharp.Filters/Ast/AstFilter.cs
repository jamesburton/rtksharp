using RtkSharp.Core;

namespace RtkSharp.Ast;

/// <summary>
/// Entry point for <c>rtk read --level ast</c>: dispatches to the registered
/// <see cref="IAstAnalyzer"/> for the detected <see cref="Language"/>, falling back to
/// <see cref="AggressiveFilter"/> (with a stderr note) for languages with no analyzer yet.
/// </summary>
public static class AstFilter
{
    /// <summary>
    /// Filters <paramref name="content"/> using the AST analyzer registered for
    /// <paramref name="language"/>, or falls back to the regex-based
    /// <see cref="AggressiveFilter"/> if none is registered.
    /// </summary>
    /// <param name="content">The raw source text.</param>
    /// <param name="language">The detected language.</param>
    /// <returns>The filtered text.</returns>
    public static string Filter(string content, Language language)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (AstAnalyzerRegistry.TryGet(language, out var analyzer))
        {
            return analyzer!.Filter(content);
        }

        Console.Error.WriteLine(
            $"rtk: warning: --level ast has no analyzer for {language} yet, falling back to " +
            "aggressive (regex-based) filtering");
        return new AggressiveFilter().Filter(content, language);
    }
}
