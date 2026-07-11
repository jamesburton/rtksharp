using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure filtering logic for the <c>rtk read</c> CLI verb: applies the requested filter level,
/// line window, and line numbering to a file's (or stdin's) content. Ported from
/// <c>src/cmds/system/read.rs</c>. The process-entry / argument-parsing / file-I/O logic lives
/// in <see cref="RtkSharp.Commands.System.ReadCommand"/> (<c>RtkSharp</c>).
/// </summary>
public static partial class ReadFilters
{
    // ^(pub )?(async )?<kw> <name> — structurally important signature lines that smart_truncate
    // always keeps even past the visible-line budget. Mirrors filter.rs's FUNC_SIGNATURE.
    private static readonly Regex FuncSignatureRegex = BuildFuncSignatureRegex();

    // ^(use |import |from |require(|#include) — import lines smart_truncate always keeps.
    // Mirrors filter.rs's IMPORT_PATTERN.
    private static readonly Regex ImportPatternRegex = BuildImportPatternRegex();

    [GeneratedRegex(
        @"^(pub\s+)?(async\s+)?(fn|def|function|func|class|struct|enum|trait|interface|type)\s+\w+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BuildFuncSignatureRegex();

    [GeneratedRegex(
        @"^(use |import |from |require\(|#include)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BuildImportPatternRegex();

    /// <summary>
    /// Produces the printed form of a single file's content: applies the requested
    /// <paramref name="level"/> filter, then the line window (tail/max), then optional line
    /// numbering. With <see cref="FilterLevel.None"/>, no window, and no numbering, the content
    /// is returned verbatim (byte-for-byte, preserving CRLF).
    /// </summary>
    /// <param name="content">The raw file (or stdin) content.</param>
    /// <param name="language">The detected source language, used by <paramref name="level"/>'s filter.</param>
    /// <param name="level">The requested filter level.</param>
    /// <param name="maxLines">Keep only the first N lines via <see cref="SmartTruncate"/>, or null.</param>
    /// <param name="tailLines">Keep only the last N lines, or null. Mutually exclusive with <paramref name="maxLines"/>.</param>
    /// <param name="lineNumbers">When true, prefix each line with a right-aligned line number.</param>
    /// <param name="filePath">
    /// The file's display path, used only for the empty-output safety warning below. Pass
    /// <c>null</c> for stdin — read.rs's <c>run_stdin</c> has no such guard, only <c>run</c> does.
    /// </param>
    /// <returns>The rendered text ready to print.</returns>
    /// <remarks>
    /// Disclosed caveat carried over from the pre-split <c>ReadCommand.Render</c>: when
    /// <paramref name="filePath"/> is supplied and the filter produces empty output for
    /// non-empty content, this method writes a warning directly to
    /// <see cref="Console.Error"/> before falling back to the raw content — a deliberate,
    /// previously-approved exception to this library's "no I/O" convention (matching the
    /// precedent set for Rubocop/Rspec in Task 11 and Curl in Task 13).
    /// </remarks>
    public static string Render(
        string content, Language language, FilterLevel level, int? maxLines, int? tailLines, bool lineNumbers,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        var filtered = level == FilterLevel.Ast
            ? AstFilter.Filter(content, language)
            : SourceFilter.GetFilter(level).Filter(content, language);
        if (filePath is not null && filtered.Trim().Length == 0 && content.Trim().Length != 0)
        {
            // content.len() in Rust is a UTF-8 byte count, not a UTF-16 char count.
            var byteCount = Encoding.UTF8.GetByteCount(content);
            Console.Error.WriteLine(
                $"rtk: warning: filter produced empty output for {filePath} ({byteCount} bytes), " +
                "showing raw content");
            filtered = content;
        }

        var windowed = ApplyLineWindow(filtered, maxLines, tailLines);
        return lineNumbers ? FormatWithLineNumbers(windowed) : windowed;
    }

    /// <summary>
    /// Applies the tail-lines or max-lines window to <paramref name="content"/>. Tail takes
    /// precedence (mirrors read.rs); with neither set the content is returned unchanged.
    /// </summary>
    /// <param name="content">The content to window.</param>
    /// <param name="maxLines">Keep the first N lines via <see cref="SmartTruncate"/>, or null.</param>
    /// <param name="tailLines">Keep the last N lines, or null.</param>
    /// <returns>The windowed content.</returns>
    public static string ApplyLineWindow(string content, int? maxLines, int? tailLines)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (tailLines is { } tail)
        {
            if (tail == 0)
            {
                return string.Empty;
            }

            var lines = SplitLines(content);
            var start = Math.Max(0, lines.Count - tail);
            var result = string.Join("\n", lines.Skip(start));
            if (content.EndsWith('\n'))
            {
                result += "\n";
            }

            return result;
        }

        if (maxLines is { } max)
        {
            return SmartTruncate(content, max);
        }

        return content;
    }

    /// <summary>
    /// Prefixes each line with a 1-based, right-aligned line number and a <c>│</c> separator,
    /// terminating every line with <c>\n</c>. The number column width is the digit count of the
    /// total line count. Mirrors read.rs's <c>format_with_line_numbers</c>.
    /// </summary>
    /// <param name="content">The content to number.</param>
    /// <returns>The numbered text.</returns>
    public static string FormatWithLineNumbers(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = SplitLines(content);
        var width = lines.Count.ToString(global::System.Globalization.CultureInfo.InvariantCulture).Length;
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append((i + 1).ToString(global::System.Globalization.CultureInfo.InvariantCulture)
                .PadLeft(width));
            sb.Append(" │ ");
            sb.Append(lines[i]);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Keeps up to <paramref name="maxLines"/> lines, prioritizing structurally important lines
    /// (function/type signatures, imports, <c>pub</c>/<c>export</c> declarations, and lone
    /// braces) so the visible window stays useful, then appends a single <c>[N more lines]</c>
    /// marker. Returns the content unchanged when it already fits. Mirrors filter.rs's
    /// <c>smart_truncate</c> (the language argument is unused there and omitted here).
    /// </summary>
    /// <param name="content">The content to truncate.</param>
    /// <param name="maxLines">The soft line budget.</param>
    /// <returns>The truncated content, or the original if it fits.</returns>
    public static string SmartTruncate(string content, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = SplitLines(content);
        if (lines.Count <= maxLines)
        {
            return content;
        }

        var result = new List<string>(maxLines + 1);
        var keptLines = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            var isImportant = FuncSignatureRegex.IsMatch(trimmed)
                || ImportPatternRegex.IsMatch(trimmed)
                || trimmed.StartsWith("pub ", StringComparison.Ordinal)
                || trimmed.StartsWith("export ", StringComparison.Ordinal)
                || trimmed == "}"
                || trimmed == "{";

            if (isImportant || keptLines < maxLines / 2)
            {
                result.Add(line);
                keptLines++;
            }

            // maxLines is Rust's usize; `max_lines - 1` at max_lines == 0 wraps to usize::MAX
            // there, so the break condition effectively never fires and the oracle keeps every
            // structurally-important line instead of stopping after the first. int subtraction
            // doesn't wrap the same way, so replicate the never-breaks behavior explicitly.
            if (maxLines != 0 && keptLines >= maxLines - 1)
            {
                break;
            }
        }

        result.Add($"[{lines.Count - keptLines} more lines]");
        return string.Join("\n", result);
    }

    /// <summary>
    /// Extracts a file extension with Rust's <c>Path::extension()</c> semantics: the substring
    /// after the last <c>.</c> in the file name, or empty if there is no <c>.</c> — critically,
    /// also empty when the <c>.</c> is the file name's first character and there is no other
    /// (a "dotfile" like <c>.gitignore</c> or <c>.env</c> has no extension in Rust, unlike
    /// .NET's own <see cref="Path.GetExtension(string)"/>, which would return <c>.env</c> whole
    /// and — if the leading dot were naively stripped — misclassify it as
    /// <see cref="Language.Data"/> instead of <see cref="Language.Unknown"/>).
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The extension without the leading dot, or empty.</returns>
    public static string GetExtension(string path)
    {
        var fileName = Path.GetFileName(path);
        var lastDot = fileName.LastIndexOf('.');
        return lastDot <= 0 ? string.Empty : fileName[(lastDot + 1)..];
    }

    /// <summary>
    /// Splits text into lines exactly as Rust's <c>str::lines()</c> does. Delegates to the
    /// single shared implementation in <see cref="SourceFilterLineSplitter"/> so this logic —
    /// including the trailing-bare-<c>\r</c>-is-kept edge case — exists in one place.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <returns>The lines.</returns>
    public static List<string> SplitLines(string text) => SourceFilterLineSplitter.SplitLines(text);
}
