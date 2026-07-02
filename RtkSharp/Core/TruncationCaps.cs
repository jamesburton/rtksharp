namespace RtkSharp.Core;

/// <summary>
/// Categorizes output content by kind, so truncation limits can be tuned per category.
/// </summary>
public enum TruncationCategory
{
    /// <summary>Error messages — terse, high-signal, moderate cap.</summary>
    Errors,

    /// <summary>Warning messages — terse, high-signal, lower cap.</summary>
    Warnings,

    /// <summary>General list output.</summary>
    Lists,

    /// <summary>Wide-but-shallow inventory listings (e.g. file trees, dependency lists), higher cap.</summary>
    Inventory,

    /// <summary>Log output.</summary>
    Logs
}

/// <summary>
/// The result of applying a truncation cap to content: the (possibly truncated) text,
/// how many lines were omitted, and an optional recovery hint pointing at the full output.
/// </summary>
/// <param name="Text">The possibly-truncated text.</param>
/// <param name="OmittedLineCount">The number of lines omitted by truncation, or 0 if unchanged.</param>
/// <param name="RecoveryHint">A hint for recovering the full output, or null if nothing was omitted.</param>
public readonly record struct TruncationResult(string Text, int OmittedLineCount, string? RecoveryHint);

/// <summary>
/// Applies per-category line-count caps to command output, truncating content that
/// exceeds the cap and attaching a tee-backed recovery hint for the omitted lines.
/// </summary>
public static class TruncationCaps
{
    // Defaults chosen to keep each category's cost under ~1-2k tokens at typical
    // line lengths, while leaving enough context to act without a recovery lookup
    // for the common case. Errors/Logs are terse and frequent, so a lower cap still
    // shows the useful signal; Inventory listings (file trees, dependency lists) are
    // wide-but-shallow and benefit from a higher cap before truncation kicks in.
    /// <summary>
    /// Gets the default maximum line count for a truncation category.
    /// </summary>
    /// <param name="category">The truncation category.</param>
    /// <returns>The default line-count cap for the category.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="category"/> is not a recognized value.</exception>
    public static int GetDefaultCap(TruncationCategory category) => category switch
    {
        TruncationCategory.Errors => 50,
        TruncationCategory.Warnings => 30,
        TruncationCategory.Lists => 100,
        TruncationCategory.Inventory => 200,
        TruncationCategory.Logs => 50,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown truncation category.")
    };

    /// <summary>
    /// Truncates content to the default line-count cap for the given category, attaching
    /// a tee-backed recovery hint when lines were omitted.
    /// </summary>
    /// <param name="content">The content to truncate.</param>
    /// <param name="category">The truncation category, which determines the cap applied.</param>
    /// <param name="commandSlug">A short slug identifying the command, used when writing a recovery tee file.</param>
    /// <param name="teeConfig">The tee configuration to use for the recovery hint, or null to use defaults.</param>
    /// <returns>The truncation result, unchanged if content did not exceed the cap.</returns>
    public static TruncationResult Truncate(
        string content,
        TruncationCategory category,
        string commandSlug,
        TeeConfig? teeConfig = null
    )
    {
        if (string.IsNullOrEmpty(content))
        {
            return new TruncationResult(content, 0, null);
        }

        var cap = GetDefaultCap(category);
        var lines = content.Split('\n');

        if (lines.Length <= cap)
        {
            return new TruncationResult(content, 0, null);
        }

        var kept = string.Join('\n', lines[..cap]);
        var omittedCount = lines.Length - cap;
        var hint = Tee.ForceTeeTailHint(content, commandSlug, cap + 1, teeConfig);

        return new TruncationResult(kept, omittedCount, hint);
    }
}
