using System;
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
}
