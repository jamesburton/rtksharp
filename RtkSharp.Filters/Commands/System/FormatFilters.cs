using System.Text;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure filtering logic for the <c>rtk format</c> CLI verb's <c>black</c> dispatch branch:
/// condenses <c>black --check</c> output down to a "files needing formatting" summary. Ported
/// from Rust <c>src/cmds/system/format_cmd.rs</c>. The formatter auto-detection, process
/// execution, and Prettier/ruff dispatch logic lives in
/// <see cref="RtkSharp.Commands.System.FormatCommand"/> (<c>RtkSharp</c>).
/// </summary>
public static class FormatFilters
{
    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs:7</c>) — the black-formatter per-report file cap (<c>MAX_FORMAT_FILES</c>, <c>format_cmd.rs</c>:190).</summary>
    private const int CapWarnings = 10;

    /// <summary>
    /// Filters Black output down to a "files needing formatting" summary. Faithful port of
    /// <c>filter_black_output</c> (<c>format_cmd.rs</c>:129-231).
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>black --check</c>.</param>
    /// <returns>The condensed summary.</returns>
    public static string FilterBlackOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var filesToFormat = new List<string>();
        var filesUnchanged = 0;
        var filesWouldReformat = 0;
        var allDone = false;
        var ohNo = false;

        foreach (var line in ReadFilters.SplitLines(output))
        {
            var trimmed = line.Trim();
            var lower = trimmed.ToLowerInvariant();

            // Check for "would reformat" lines.
            if (lower.StartsWith("would reformat:", StringComparison.Ordinal))
            {
                // Extract filename from "would reformat: path/to/file.py" - splits on the FIRST ':'
                // only, matching Rust's `trimmed.split(':').nth(1)` (a Windows path like "C:\foo.py"
                // would be mis-split here too - preserved faithfully, not "fixed").
                var parts = trimmed.Split(':');
                if (parts.Length > 1)
                {
                    filesToFormat.Add(parts[1].Trim());
                }
            }

            // Parse summary line like "2 files would be reformatted, 3 files would be left unchanged."
            if (lower.Contains("would be reformatted", StringComparison.Ordinal) || lower.Contains("would be left unchanged", StringComparison.Ordinal))
            {
                // Split by comma to handle both parts.
                foreach (var part in trimmed.Split(','))
                {
                    var partLower = part.ToLowerInvariant();
                    var words = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                    if (partLower.Contains("would be reformatted", StringComparison.Ordinal))
                    {
                        // Parse "X file(s) would be reformatted".
                        for (var i = 0; i < words.Length; i++)
                        {
                            if ((words[i] == "file" || words[i] == "files") && i > 0 && int.TryParse(words[i - 1], out var count))
                            {
                                filesWouldReformat = count;
                                break;
                            }
                        }
                    }

                    if (partLower.Contains("would be left unchanged", StringComparison.Ordinal))
                    {
                        // Parse "X file(s) would be left unchanged".
                        for (var i = 0; i < words.Length; i++)
                        {
                            if ((words[i] == "file" || words[i] == "files") && i > 0 && int.TryParse(words[i - 1], out var count))
                            {
                                filesUnchanged = count;
                                break;
                            }
                        }
                    }
                }
            }

            // Check for "left unchanged" (standalone).
            if (lower.Contains("left unchanged", StringComparison.Ordinal) && !lower.Contains("would be", StringComparison.Ordinal))
            {
                var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < words.Length; i++)
                {
                    if ((words[i] == "file" || words[i] == "files") && i > 0 && int.TryParse(words[i - 1], out var count))
                    {
                        filesUnchanged = count;
                        break;
                    }
                }
            }

            // Check for success/failure indicators.
            if (lower.Contains("all done!", StringComparison.Ordinal) || lower.Contains("all done ✨", StringComparison.Ordinal))
            {
                allDone = true;
            }

            if (lower.Contains("oh no!", StringComparison.Ordinal))
            {
                ohNo = true;
            }
        }

        // Build output.
        var result = new StringBuilder();

        // Determine if all files are formatted.
        var needsFormatting = filesToFormat.Count > 0 || filesWouldReformat > 0 || ohNo;

        if (!needsFormatting && (allDone || filesUnchanged > 0))
        {
            // All files formatted correctly.
            result.Append("Format (black): All files formatted");
            if (filesUnchanged > 0)
            {
                result.Append($" ({filesUnchanged} files checked)");
            }
        }
        else if (needsFormatting)
        {
            // Files need formatting.
            var count = filesToFormat.Count > 0 ? filesToFormat.Count : filesWouldReformat;

            result.Append($"Format (black): {count} files need formatting\n");

            if (filesToFormat.Count > 0)
            {
                var index = 0;
                foreach (var file in filesToFormat.Take(CapWarnings))
                {
                    index++;
                    result.Append($"{index}. {CompactPath(file)}\n");
                }

                if (filesToFormat.Count > CapWarnings)
                {
                    result.Append($"\n... +{filesToFormat.Count - CapWarnings} more files\n");
                }
            }

            if (filesUnchanged > 0)
            {
                result.Append($"\n{filesUnchanged} files already formatted\n");
            }

            result.Append("\n[hint] Run `black .` to format these files\n");
        }
        else
        {
            // Fallback: show raw output.
            result.Append(output.Trim());
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Shortens a file path by keeping only the portion from the last <c>src/</c>/<c>lib/</c>/
    /// <c>tests/</c> segment onward, or just the file name if none is present. Faithful port of
    /// <c>compact_path</c> (<c>format_cmd.rs</c>:234-247).
    /// </summary>
    /// <param name="path">The file path to shorten.</param>
    /// <returns>The shortened path.</returns>
    public static string CompactPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.Replace('\\', '/');

        var srcPos = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
        if (srcPos >= 0)
        {
            return "src/" + normalized[(srcPos + 5)..];
        }

        var libPos = normalized.LastIndexOf("/lib/", StringComparison.Ordinal);
        if (libPos >= 0)
        {
            return "lib/" + normalized[(libPos + 5)..];
        }

        var testsPos = normalized.LastIndexOf("/tests/", StringComparison.Ordinal);
        if (testsPos >= 0)
        {
            return "tests/" + normalized[(testsPos + 7)..];
        }

        var slashPos = normalized.LastIndexOf('/');
        return slashPos >= 0 ? normalized[(slashPos + 1)..] : normalized;
    }
}
