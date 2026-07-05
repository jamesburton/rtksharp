using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RtkSharp.Core;

/// <summary>
/// Shared text-processing helpers used across command filters.
/// </summary>
public static partial class Utils
{
    private static readonly Regex AnsiRegex = new Regex(@"\x1b\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled);

    /// <summary>
    /// Removes ANSI escape sequences (color/formatting codes) from text.
    /// </summary>
    /// <param name="text">The text to strip.</param>
    /// <returns>The text with ANSI escape sequences removed.</returns>
    public static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        return AnsiRegex.Replace(text, "");
    }

    /// <summary>
    /// Truncates a string to at most <paramref name="maxLen"/> Unicode scalar values, appending
    /// <c>"..."</c> when truncation occurs. Faithful port of Rust <c>utils::truncate</c>
    /// (<c>src/core/utils.rs:25-35</c>), which counts and slices by <c>char</c> — a Unicode scalar
    /// value — rather than by UTF-16 code unit, so this counts by <see cref="Rune"/> (the closest
    /// .NET equivalent) rather than by raw <see cref="char"/> index, ensuring a surrogate pair is
    /// never split.
    /// </summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="maxLen">The maximum number of Unicode scalar values to keep before truncation kicks in.</param>
    /// <returns>
    /// <paramref name="s"/> unchanged if it has at most <paramref name="maxLen"/> scalar values;
    /// otherwise the first <c>maxLen - 3</c> scalar values followed by <c>"..."</c> (or just
    /// <c>"..."</c> if <paramref name="maxLen"/> is less than 3).
    /// </returns>
    public static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }

        var runes = new List<Rune>();
        foreach (var rune in s.EnumerateRunes())
        {
            runes.Add(rune);
        }

        if (runes.Count <= maxLen)
        {
            return s;
        }

        if (maxLen < 3)
        {
            return "...";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < maxLen - 3; i++)
        {
            sb.Append(runes[i].ToString());
        }

        sb.Append("...");
        return sb.ToString();
    }

    /// <summary>
    /// Formats a token count with adaptive precision, abbreviating large counts. Faithful port of
    /// Rust <c>utils::format_tokens</c> (<c>src/core/utils.rs:78-86</c>).
    /// </summary>
    /// <param name="n">The token count to format.</param>
    /// <returns>
    /// <c>"{n/1_000_000:F1}M"</c> when <paramref name="n"/> is at least 1,000,000;
    /// <c>"{n/1_000:F1}K"</c> when at least 1,000; otherwise the plain decimal value of <paramref name="n"/>.
    /// </returns>
    public static string FormatTokens(long n)
    {
        if (n >= 1_000_000)
        {
            return (n / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + "M";
        }

        if (n >= 1_000)
        {
            return (n / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "K";
        }

        return n.ToString(CultureInfo.InvariantCulture);
    }
}
