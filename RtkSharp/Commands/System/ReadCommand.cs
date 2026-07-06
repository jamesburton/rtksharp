using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Commands.System;

/// <summary>
/// Reads one or more files (or stdin via <c>-</c>) and prints their contents, optionally
/// windowed to the first N lines (<c>--max-lines</c>/<c>-m</c>) or last N lines
/// (<c>--tail-lines</c>) and optionally prefixed with right-aligned line numbers
/// (<c>-n</c>/<c>--line-numbers</c>). Multiple files are concatenated with no separator
/// headers, exactly like <c>cat</c>. Unlike the other Phase 5a system commands this reads
/// files natively and prints directly rather than wrapping an external tool.
/// </summary>
/// <remarks>
/// Ported from <c>src/cmds/system/read.rs</c> together with the multi-file dispatch loop in
/// <c>src/main.rs</c> (<c>Commands::Read</c>), including all three <c>--level</c> filter tiers
/// (<c>none</c>/<c>minimal</c>/<c>aggressive</c>) via <see cref="Core.SourceFilter"/>
/// (<c>src/core/filter.rs</c>), language detection by file extension, and the
/// filter-emptied-non-empty-content safety fallback (read.rs's own
/// <c>if filtered.trim().is_empty() &amp;&amp; !content.trim().is_empty()</c> guard). One
/// intentional scope limit remains:
/// <list type="bullet">
///   <item>
///     <b>Verbose diagnostics.</b> The registered handler signature is
///     <c>RunAsync(string[])</c> and receives no verbosity level, so read.rs's stderr
///     <c>eprintln!</c> diagnostics (reduction stats, detected language, "Reading: ...") are
///     omitted; they never affect printed output. The empty-output safety warning IS ported
///     (unconditional in Rust, not gated on verbosity).
///   </item>
/// </list>
/// Token-savings tracking (read.rs's <c>TimedExecution</c>) is also omitted: native commands
/// are not wired to a tracker through the registry, and tracking is a metrics side effect that
/// does not change output.
/// </remarks>
public static partial class ReadCommand
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
    /// Parses the arguments following the <c>read</c> verb, reads each file (or stdin for
    /// <c>-</c>), and prints the (optionally windowed and numbered) contents. Returns 1 if any
    /// file could not be read, otherwise 0. Requesting a non-default filter level, or supplying
    /// no files or conflicting window flags, prints an error to stderr and returns 2.
    /// </summary>
    /// <param name="args">The arguments following the <c>read</c> verb.</param>
    /// <returns>0 on success, 1 if any file failed to read, 2 on a usage/scope error.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        ReadArgs parsed;
        try
        {
            parsed = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"rtk: read: {ex.Message}");
            return Task.FromResult(2);
        }

        var hadError = false;
        var stdinSeen = false;

        foreach (var file in parsed.Files)
        {
            if (file == "-")
            {
                if (stdinSeen)
                {
                    Console.Error.WriteLine("rtk: warning: stdin specified more than once");
                    continue;
                }

                stdinSeen = true;
                var stdinContent = Console.In.ReadToEnd();
                // stdin has no extension and, per read.rs's run_stdin, no empty-output safety
                // fallback (that guard exists only in run(), the file path) — filePath: null.
                var rendered = Render(
                    stdinContent, Language.Unknown, parsed.Level, parsed.MaxLines, parsed.TailLines,
                    parsed.LineNumbers, filePath: null);
                Console.Out.Write(rendered);
                continue;
            }

            try
            {
                var content = File.ReadAllText(file);
                var language = LanguageExtensions.FromExtension(GetExtension(file));
                var rendered = Render(
                    content, language, parsed.Level, parsed.MaxLines, parsed.TailLines, parsed.LineNumbers,
                    filePath: file);
                Console.Out.Write(rendered);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
            {
                // Mirrors main.rs's `eprintln!("cat: {}: {}", file, root_cause)`. The exact OS
                // error text is runtime-specific and not oracle-matched; the shape is.
                Console.Error.WriteLine($"cat: {file}: {ex.Message}");
                hadError = true;
            }
        }

        return Task.FromResult(hadError ? 1 : 0);
    }

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
    internal static string GetExtension(string path)
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
    internal static List<string> SplitLines(string text) => SourceFilterLineSplitter.SplitLines(text);

    /// <summary>Parsed <c>read</c> arguments.</summary>
    /// <param name="Files">The files to read, in order; <c>-</c> denotes stdin.</param>
    /// <param name="Level">The requested filter level.</param>
    /// <param name="MaxLines">The <c>--max-lines</c> value, or null.</param>
    /// <param name="TailLines">The <c>--tail-lines</c> value, or null.</param>
    /// <param name="LineNumbers">Whether <c>-n</c>/<c>--line-numbers</c> was given.</param>
    internal sealed record ReadArgs(
        IReadOnlyList<string> Files,
        FilterLevel Level,
        int? MaxLines,
        int? TailLines,
        bool LineNumbers
    );

    /// <summary>
    /// Parses the <c>read</c> argument vector into a <see cref="ReadArgs"/>. Supports
    /// <c>--level</c>/<c>-l</c>, <c>--max-lines</c>/<c>-m</c>, <c>--tail-lines</c>, and
    /// <c>-n</c>/<c>--line-numbers</c>, in both <c>--flag value</c> and <c>--flag=value</c>
    /// forms, mirroring the clap definition in main.rs.
    /// </summary>
    /// <param name="args">The raw argument vector following the verb.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="ArgumentException">On unknown flags, missing values, conflicting window flags, or no files.</exception>
    internal static ReadArgs ParseArgs(string[] args)
    {
        var files = new List<string>();
        var level = FilterLevel.None;
        int? maxLines = null;
        int? tailLines = null;
        var lineNumbers = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (TryTakeValue(args, ref i, arg, "--level", "-l", out var levelValue))
            {
                level = ParseLevel(levelValue);
            }
            else if (TryTakeValue(args, ref i, arg, "--max-lines", "-m", out var maxValue))
            {
                maxLines = ParseCount(maxValue, "--max-lines");
            }
            else if (TryTakeValue(args, ref i, arg, "--tail-lines", null, out var tailValue))
            {
                tailLines = ParseCount(tailValue, "--tail-lines");
            }
            else if (arg is "-n" or "--line-numbers")
            {
                lineNumbers = true;
            }
            else if (arg.Length > 1 && arg.StartsWith('-') && arg != "-")
            {
                throw new ArgumentException($"unexpected argument '{arg}'");
            }
            else
            {
                files.Add(arg);
            }
        }

        if (maxLines is not null && tailLines is not null)
        {
            throw new ArgumentException("the argument '--max-lines' cannot be used with '--tail-lines'");
        }

        if (files.Count == 0)
        {
            throw new ArgumentException("the following required arguments were not provided: <FILES>");
        }

        return new ReadArgs(files, level, maxLines, tailLines, lineNumbers);
    }

    /// <summary>
    /// Attempts to consume an option that takes a value at position <paramref name="i"/>,
    /// supporting both <c>--flag=value</c> and <c>--flag value</c> (and the short <c>-x value</c>)
    /// forms. Advances <paramref name="i"/> past a consumed separate value.
    /// </summary>
    private static bool TryTakeValue(
        string[] args, ref int i, string arg, string longName, string? shortName, out string value)
    {
        if (arg == longName || (shortName is not null && arg == shortName))
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"a value is required for '{longName}' but none was supplied");
            }

            value = args[++i];
            return true;
        }

        var prefix = longName + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static FilterLevel ParseLevel(string value) =>
        value.ToLowerInvariant() switch
        {
            "none" => FilterLevel.None,
            "minimal" => FilterLevel.Minimal,
            "aggressive" => FilterLevel.Aggressive,
            // RtkSharp-only extra tier, on top of the three Rust recognizes — see FilterLevel.Ast.
            "ast" => FilterLevel.Ast,
            _ => throw new ArgumentException($"invalid value '{value}' for '--level'")
        };

    private static int ParseCount(string value, string flag)
    {
        if (!int.TryParse(value, global::System.Globalization.NumberStyles.None,
                global::System.Globalization.CultureInfo.InvariantCulture, out var count))
        {
            throw new ArgumentException($"invalid value '{value}' for '{flag}': not a non-negative integer");
        }

        return count;
    }
}
