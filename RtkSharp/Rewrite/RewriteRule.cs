using System.Text.RegularExpressions;

namespace RtkSharp.Rewrite;

/// <summary>
/// A single rewrite rule that maps a raw shell command pattern to its rtk-wrapped
/// equivalent. Transcribed verbatim (in source order) from the Rust <c>RtkRule</c>
/// table in <c>src/discover/rules.rs</c>.
/// </summary>
/// <param name="Pattern">The regex source string, transcribed verbatim from rules.rs.</param>
/// <param name="CompiledPattern">The compiled form of <paramref name="Pattern"/>.</param>
/// <param name="RtkCmd">The rtk-wrapped command to rewrite to, e.g. <c>"rtk git"</c>.</param>
/// <param name="RewritePrefixes">The raw command prefixes this rule recognizes, e.g. <c>["git", "yadm"]</c>.</param>
/// <param name="Category">The category this rule belongs to, e.g. <c>"Git"</c>.</param>
/// <param name="SavingsPct">The expected token savings percentage for commands matched by this rule.</param>
public sealed record RewriteRule(
    string Pattern,
    Regex CompiledPattern,
    string RtkCmd,
    IReadOnlyList<string> RewritePrefixes,
    string Category,
    double SavingsPct
);
