namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure filtering logic for the <c>rtk tree</c> CLI verb. Ported from
/// <c>src/cmds/system/tree.rs</c>. The process-execution entry point (<c>RunAsync</c>) lives in
/// <see cref="RtkSharp.Commands.System.TreeCommand"/>.
/// </summary>
public static class TreeFilters
{
    /// <summary>
    /// Removes the trailing summary line (e.g. <c>5 directories, 23 files</c>) and trailing blank
    /// lines from raw <c>tree</c> output, preserving the tree structure. An empty input yields a
    /// single newline. Mirrors tree.rs's <c>filter_tree_output</c>.
    /// </summary>
    /// <param name="raw">Raw stdout from <c>tree</c>.</param>
    /// <returns>The compacted output, always terminated by a single newline.</returns>
    public static string FilterTreeOutput(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var lines = ReadFilters.SplitLines(raw);
        if (lines.Count == 0)
        {
            return "\n";
        }

        var filtered = new List<string>();
        foreach (var line in lines)
        {
            // Skip the final summary line (e.g., "5 directories, 23 files").
            if (line.Contains("director", StringComparison.Ordinal) &&
                line.Contains("file", StringComparison.Ordinal))
            {
                continue;
            }

            // Skip leading blank lines.
            if (line.Trim().Length == 0 && filtered.Count == 0)
            {
                continue;
            }

            filtered.Add(line);
        }

        // Remove trailing blank lines.
        while (filtered.Count > 0 && filtered[^1].Trim().Length == 0)
        {
            filtered.RemoveAt(filtered.Count - 1);
        }

        return string.Join("\n", filtered) + "\n";
    }
}
