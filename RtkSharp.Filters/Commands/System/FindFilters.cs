namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Parsed arguments from either native <c>find</c> or RTK <c>find</c> syntax, shared between
/// <see cref="RtkSharp.Commands.System.FindCommand"/>'s argument parsing/filesystem walk and
/// <see cref="FindFilters.FormatFindResults"/>'s pure grouping/summary logic.
/// </summary>
/// <param name="Pattern">The filename glob to match (defaults to <c>*</c>).</param>
/// <param name="Path">The search root (defaults to <c>.</c>).</param>
/// <param name="MaxResults">The maximum number of individual files to display (defaults to 50).</param>
/// <param name="MaxDepth">The maximum walk depth (root = 0), or null for unlimited.</param>
/// <param name="FileType">The type filter: <c>f</c> for files, <c>d</c> for directories.</param>
/// <param name="CaseInsensitive">Whether the pattern matches case-insensitively (<c>-iname</c>).</param>
public sealed record FindArgs(
    string Pattern,
    string Path,
    int MaxResults,
    int? MaxDepth,
    string FileType,
    bool CaseInsensitive
)
{
    /// <summary>The default <c>--max</c>/results cap (50), mirroring find_cmd.rs's <c>DEFAULT_MAX_RESULTS</c>.</summary>
    public const int DefaultMaxResults = 50;

    /// <summary>Creates the default arguments (<c>*</c> in <c>.</c>, 50 files, type <c>f</c>).</summary>
    /// <returns>The default <see cref="FindArgs"/>.</returns>
    public static FindArgs Default() => new("*", ".", DefaultMaxResults, null, "f", false);
}

/// <summary>
/// Pure result-formatting logic for the <c>rtk find</c> CLI verb: groups matched paths by
/// directory, caps the result budget, and appends a per-extension summary. Ported from Rust
/// <c>src/cmds/system/find_cmd.rs</c>. The filesystem walk, gitignore engine, and argument
/// parsing live in <see cref="RtkSharp.Commands.System.FindCommand"/> (<c>RtkSharp</c>), since
/// they require real filesystem I/O.
/// </summary>
public static class FindFilters
{
    /// <summary>
    /// Groups <paramref name="paths"/> by directory, caps the result budget at
    /// <paramref name="args"/>'s <see cref="FindArgs.MaxResults"/>, and appends a per-extension
    /// summary (when more than one extension is present). Mirrors the body of find_cmd.rs's
    /// <c>run</c> function, after its filesystem walk.
    /// </summary>
    /// <param name="paths">
    /// The display paths (relative to the search root) returned by the caller's filesystem walk,
    /// in any order — this method sorts them itself, matching find_cmd.rs's own
    /// <c>files.sort()</c> step.
    /// </param>
    /// <param name="args">The parsed find arguments (used for the pattern display and result cap).</param>
    /// <returns>The rendered, newline-terminated result block.</returns>
    public static string FormatFindResults(IReadOnlyList<string> paths, FindArgs args)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(args);

        // Treat "." as match-all, mirroring find_cmd.rs's own pattern normalization.
        var effectivePattern = args.Pattern == "." ? "*" : args.Pattern;

        var files = paths.ToList();
        files.Sort(StringComparer.Ordinal);

        if (files.Count == 0)
        {
            return $"0 for '{effectivePattern}'\n";
        }

        // Group by directory.
        var byDir = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir))
            {
                dir = ".";
            }

            var filename = Path.GetFileName(file);
            if (!byDir.TryGetValue(dir, out var bucket))
            {
                bucket = [];
                byDir[dir] = bucket;
            }

            bucket.Add(filename);
        }

        var dirs = byDir.Keys.ToList();
        dirs.Sort(StringComparer.Ordinal);
        var dirsCount = dirs.Count;
        var totalFiles = files.Count;

        var lines = new List<string>
        {
            $"{totalFiles}F {dirsCount}D:",
            string.Empty
        };

        // Display with --max limiting (counting individual files).
        var shown = 0;
        foreach (var dir in dirs)
        {
            if (shown >= args.MaxResults)
            {
                break;
            }

            var filesInDir = byDir[dir];
            var dirDisplay = dir.Length > 50 ? "..." + dir[^47..] : dir;

            var remainingBudget = args.MaxResults - shown;
            if (filesInDir.Count <= remainingBudget)
            {
                lines.Add($"{dirDisplay}/ {string.Join(' ', filesInDir)}");
                shown += filesInDir.Count;
            }
            else
            {
                var partial = filesInDir.Take(remainingBudget).ToList();
                lines.Add($"{dirDisplay}/ {string.Join(' ', partial)}");
                shown += partial.Count;
                break;
            }
        }

        if (shown < totalFiles)
        {
            lines.Add($"+{totalFiles - shown} more");
        }

        // Extension summary (only when more than one extension is present).
        var byExt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var ext = ExtensionOf(file);
            byExt[ext] = byExt.GetValueOrDefault(ext) + 1;
        }

        if (byExt.Count > 1)
        {
            lines.Add(string.Empty);

            // The `.ThenBy(Ordinal)` tie-break is an intentional determinism choice, not a literal
            // port: find_cmd.rs accumulates counts in a std::collections::HashMap and its iteration
            // order (used to break ties among equal counts) is randomized per-process by Rust's
            // SipHash-based default hasher. There is no single "correct" Rust tie order to match —
            // the oracle itself is nondeterministic here, so a fresh oracle run can legitimately
            // differ from another oracle run on tied-count extensions. RtkSharp instead picks a
            // fixed, reproducible order (alphabetical) rather than reproducing that nondeterminism.
            var extParts = byExt
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(kv => $".{kv.Key}({kv.Value})");
            lines.Add($"ext: {string.Join(' ', extParts)}");
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Returns the extension of <paramref name="file"/> without the leading dot, or <c>none</c>
    /// when it has no extension — mirroring Rust's <c>Path::extension()</c> (which yields
    /// <c>None</c> for dotfiles and extensionless names).
    /// </summary>
    private static string ExtensionOf(string file)
    {
        var ext = Path.GetExtension(file);
        return ext.Length > 0 ? ext[1..] : "none";
    }
}
