using System;
using System.Text.RegularExpressions;

namespace RtkSharp.Core;

public static partial class Utils
{
    private static readonly Regex AnsiRegex = new Regex(@"\x1b\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled);

    public static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        return AnsiRegex.Replace(text, "");
    }
}
