using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Python;

/// <summary>
/// Buffered filter for mypy output: groups diagnostics by file (with attached "note:" continuation
/// lines), plus a top-error-codes summary when 2+ distinct codes are present. Faithful port of
/// <c>filter_mypy_output</c> (<c>src/cmds/python/mypy_cmd.rs</c>).
/// </summary>
public static class MypyFilters
{
    // file.py:12: error: Message [error-code]
    // file.py:12:5: error: Message [error-code]
    private static readonly Regex MypyDiag = new(
        @"^(.+?):(\d+)(?::\d+)?: (error|warning|note): (.+?)(?:\s+\[(.+)\])?$",
        RegexOptions.Compiled);

    private sealed class MypyError
    {
        public required string File { get; init; }
        public int Line { get; init; }
        public string Code { get; set; } = "";
        public string Message { get; init; } = "";
        public List<string> ContextLines { get; } = [];
    }

    /// <summary>
    /// Groups mypy diagnostics by file, attaching "note:" continuation lines to the preceding error
    /// when they share the same file, and prepending any file-less (config/import) errors verbatim.
    /// Faithful port of Rust <c>filter_mypy_output</c> (<c>mypy_cmd.rs</c>:43-212).
    /// </summary>
    /// <param name="output">The ANSI-stripped, combined stdout+stderr mypy output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterMypyOutput(string output)
    {
        var lines = SourceFilterLineSplitter.SplitLines(output);
        var errors = new List<MypyError>();
        var filelessLines = new List<string>();
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];

            // Skip mypy's own summary line
            if (line.StartsWith("Found ", StringComparison.Ordinal) && line.Contains(" error", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Skip "Success: no issues found"
            if (line.StartsWith("Success:", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            var match = MypyDiag.Match(line);
            if (match.Success)
            {
                var severity = match.Groups[3].Value;
                var file = match.Groups[1].Value;
                _ = int.TryParse(match.Groups[2].Value, out var lineNum);
                var message = match.Groups[4].Value;
                var code = match.Groups[5].Success ? match.Groups[5].Value : "";

                if (severity == "note")
                {
                    // Attach note to preceding error if same file and line
                    if (errors.Count > 0 && errors[^1].File == file)
                    {
                        errors[^1].ContextLines.Add(message);
                        i++;
                        continue;
                    }

                    // Standalone note with no parent -- display as fileless
                    filelessLines.Add(line);
                    i++;
                    continue;
                }

                var err = new MypyError { File = file, Line = lineNum, Message = message, Code = code };

                // Capture continuation note lines
                i++;
                while (i < lines.Count)
                {
                    var nextMatch = MypyDiag.Match(lines[i]);
                    if (nextMatch.Success && nextMatch.Groups[3].Value == "note" && nextMatch.Groups[1].Value == err.File)
                    {
                        err.ContextLines.Add(nextMatch.Groups[4].Value);
                        i++;
                        continue;
                    }

                    break;
                }

                errors.Add(err);
            }
            else if (line.Contains("error:", StringComparison.Ordinal) && line.Trim().Length != 0)
            {
                // File-less error (config errors, import errors)
                filelessLines.Add(line);
                i++;
            }
            else
            {
                i++;
            }
        }

        // No errors at all
        if (errors.Count == 0 && filelessLines.Count == 0)
        {
            return "mypy: No issues found";
        }

        // Group by file, preserving first-seen order (mirrors Rust's HashMap iteration order being
        // irrelevant here since files_sorted re-sorts by error count below).
        var byFile = new Dictionary<string, List<MypyError>>(StringComparer.Ordinal);
        foreach (var err in errors)
        {
            if (!byFile.TryGetValue(err.File, out var list))
            {
                list = [];
                byFile[err.File] = list;
            }

            list.Add(err);
        }

        var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var err in errors)
        {
            if (err.Code.Length != 0)
            {
                byCode[err.Code] = byCode.GetValueOrDefault(err.Code) + 1;
            }
        }

        var result = new StringBuilder();

        // File-less errors first
        foreach (var line in filelessLines)
        {
            result.Append(line);
            result.Append('\n');
        }

        if (filelessLines.Count > 0 && errors.Count > 0)
        {
            result.Append('\n');
        }

        if (errors.Count > 0)
        {
            result.Append($"mypy: {errors.Count} errors in {byFile.Count} files\n");

            // Top error codes summary (only when 2+ distinct codes)
            var codeCounts = byCode.ToList();
            codeCounts.Sort((a, b) => b.Value.CompareTo(a.Value));

            if (codeCounts.Count > 1)
            {
                var codesStr = codeCounts.Take(5).Select(kv => $"{kv.Key} ({kv.Value}x)");
                result.Append($"Top codes: {string.Join(", ", codesStr)}\n\n");
            }

            // Files sorted by error count (most errors first)
            var filesSorted = byFile.ToList();
            filesSorted.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

            foreach (var (file, fileErrors) in filesSorted)
            {
                result.Append($"{file} ({fileErrors.Count} errors)\n");

                foreach (var err in fileErrors)
                {
                    if (err.Code.Length == 0)
                    {
                        result.Append($"  L{err.Line}: {Utils.Truncate(err.Message, 120)}\n");
                    }
                    else
                    {
                        result.Append($"  L{err.Line}: [{err.Code}] {Utils.Truncate(err.Message, 120)}\n");
                    }

                    foreach (var ctx in err.ContextLines)
                    {
                        result.Append($"    {Utils.Truncate(ctx, 120)}\n");
                    }
                }

                result.Append('\n');
            }
        }

        return result.ToString().Trim();
    }
}
