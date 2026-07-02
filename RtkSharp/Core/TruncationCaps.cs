namespace RtkSharp.Core;

public enum TruncationCategory
{
    Errors,
    Warnings,
    Lists,
    Inventory,
    Logs
}

public readonly record struct TruncationResult(string Text, int OmittedLineCount, string? RecoveryHint);

public static class TruncationCaps
{
    // Defaults chosen to keep each category's cost under ~1-2k tokens at typical
    // line lengths, while leaving enough context to act without a recovery lookup
    // for the common case. Errors/Logs are terse and frequent, so a lower cap still
    // shows the useful signal; Inventory listings (file trees, dependency lists) are
    // wide-but-shallow and benefit from a higher cap before truncation kicks in.
    public static int GetDefaultCap(TruncationCategory category) => category switch
    {
        TruncationCategory.Errors => 50,
        TruncationCategory.Warnings => 30,
        TruncationCategory.Lists => 100,
        TruncationCategory.Inventory => 200,
        TruncationCategory.Logs => 50,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown truncation category.")
    };

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
