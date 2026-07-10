using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic for the <c>tsc</c> CLI proxy. Extracted from
/// <c>RtkSharp.Commands.Js.TscCommand</c> (Task 7 of the filters-library extraction) — everything
/// here is a pure function of already-captured text, with no process execution or file I/O.
/// </summary>
public static partial class TscFilters
{
    /// <summary>
    /// Matches a single tsc diagnostic line: <c>file(line,col): error|warning TSxxxx: message</c>.
    /// Faithful port of <c>TSC_ERROR</c> (<c>tsc_cmd.rs</c>:11-14).
    /// </summary>
    [GeneratedRegex(@"^(.+?)\((\d+),(\d+)\):\s+(error|warning)\s+(TS\d+):\s+(.+)$")]
    internal static partial Regex TscErrorRegex();

    /// <summary>
    /// A single parsed tsc diagnostic: the matched file/line/code/message plus any indented
    /// continuation lines tsc printed immediately after it. Faithful port of the private <c>TsError</c>
    /// struct nested inside <c>filter_tsc_output</c> (<c>tsc_cmd.rs</c>:108-114).
    /// </summary>
    private sealed class TsError
    {
        public required string File { get; init; }

        public required int Line { get; init; }

        public required string Code { get; init; }

        public required string Message { get; init; }

        public List<string> ContextLines { get; } = [];
    }

    /// <summary>
    /// Buffered filter for tsc's raw diagnostic output: parses every <see cref="TscErrorRegex"/> match
    /// (plus its indented continuation lines) into a <see cref="TsError"/>, groups by file (sorted by
    /// per-file error count descending, every error shown, no cap), and prepends a summary line plus
    /// (when more than one distinct error code appears) a top-5-by-frequency codes line. Faithful port
    /// of <c>filter_tsc_output</c> (<c>tsc_cmd.rs</c>:107-214).
    /// </summary>
    /// <param name="output">The raw <c>tsc</c> output (stdout+stderr) to filter.</param>
    /// <returns>The grouped, summarized output.</returns>
    public static string FilterTscOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var errors = new List<TsError>();
        var lines = SourceFilterLineSplitter.SplitLines(output);
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];
            var match = TscErrorRegex().Match(line);

            if (match.Success)
            {
                var groups = match.Groups;
                var lineNumber = int.TryParse(groups[2].Value, out var parsed) ? parsed : 0;

                var err = new TsError
                {
                    File = groups[1].Value,
                    Line = lineNumber,
                    Code = groups[5].Value,
                    Message = groups[6].Value,
                };

                // Capture continuation lines (indented context from tsc).
                i++;
                while (i < lines.Count)
                {
                    var next = lines[i];
                    if (next.Length != 0
                        && (next.StartsWith("  ", StringComparison.Ordinal) || next.StartsWith('\t'))
                        && !TscErrorRegex().IsMatch(next))
                    {
                        err.ContextLines.Add(next.Trim());
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                errors.Add(err);
            }
            else
            {
                i++;
            }
        }

        if (errors.Count == 0)
        {
            // Always report "No errors found" on zero-error output, matching the streaming
            // TscHandler::format_summary path real `rtk tsc` invocations actually use (it returns
            // this message unconditionally whenever error_count == 0, with no substring check on
            // the raw output). A plain `tsc --noEmit` on a clean project typically prints nothing
            // at all, so gating this message on a "Found 0 errors" substring (as Rust's buffered
            // filter_tsc_output does) produced the wrong, never-actually-observed message for the
            // most common success case. See TscCommand's remarks for the full correction.
            return "TypeScript: No errors found";
        }

        // Group by file.
        var byFile = new Dictionary<string, List<TsError>>(StringComparer.Ordinal);
        foreach (var err in errors)
        {
            if (!byFile.TryGetValue(err.File, out var list))
            {
                list = [];
                byFile[err.File] = list;
            }

            list.Add(err);
        }

        // Count by error code for the summary.
        var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var err in errors)
        {
            byCode[err.Code] = byCode.GetValueOrDefault(err.Code) + 1;
        }

        var result = new StringBuilder();
        result.Append($"TypeScript: {errors.Count} errors in {byFile.Count} files\n");

        // Top error codes summary (compact, one line) - only when more than one distinct code appears.
        if (byCode.Count > 1)
        {
            var codesStr = byCode
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key} ({kv.Value}x)");
            result.Append($"Top codes: {string.Join(", ", codesStr)}\n\n");
        }

        // Files sorted by error count (most errors first). Show every error per file - no limits.
        var filesSorted = byFile.OrderByDescending(kv => kv.Value.Count);

        foreach (var (file, fileErrors) in filesSorted)
        {
            result.Append($"{file} ({fileErrors.Count} errors)\n");

            foreach (var err in fileErrors)
            {
                result.Append($"  L{err.Line}: {err.Code} {Utils.Truncate(err.Message, 120)}\n");
                foreach (var ctx in err.ContextLines)
                {
                    result.Append($"    {Utils.Truncate(ctx, 120)}\n");
                }
            }

            result.Append('\n');
        }

        return result.ToString().Trim();
    }
}
