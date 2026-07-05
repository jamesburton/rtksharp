using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RtkSharp.Core;

/// <summary>
/// Streaming error/warning-block filter used by <c>rtk err</c>. As output lines arrive, any line
/// matching one of <see cref="ErrorPatterns"/> opens an "error block": that line and subsequent
/// indented/blank continuation lines are echoed as context, until a non-indented, non-blank line
/// ends the block. Faithful port of Rust <c>ErrorStreamFilter</c> and its <c>ERROR_PATTERNS</c>
/// table (<c>src/cmds/rust/runner.rs</c>:13-103), despite that module's <c>cmds/rust</c> path being
/// genuinely ecosystem-agnostic (confirmed by research, used by no other ecosystem filter).
/// </summary>
public sealed partial class ErrorStreamFilter : IStreamFilter
{
    // Ported verbatim from Rust's `ERROR_PATTERNS` (runner.rs:14-34). Order matches the source;
    // `feed_line` only needs "any pattern matches", so order has no behavioral effect, but is kept
    // identical for traceability.
    private static readonly Regex[] ErrorPatterns =
    [
        GenericErrorRegex(), // ^.*error[\s:\[].*$
        GenericErrRegex(), // ^.*\berr\b.*$
        GenericWarningRegex(), // ^.*warning[\s:\[].*$
        GenericWarnRegex(), // ^.*\bwarn\b.*$
        FailedRegex(), // ^.*failed.*$
        FailureRegex(), // ^.*failure.*$
        ExceptionRegex(), // ^.*exception.*$
        PanicRegex(), // ^.*panic.*$
        RustErrorCodeRegex(), // ^error\[E\d+\]:.*$
        RustLocationRegex(), // ^\s*--> .*:\d+:\d+$
        PythonTracebackRegex(), // ^Traceback.*$
        PythonFileLineRegex(), // ^\s*File ".*", line \d+.*$
        JsAtLocationRegex(), // ^\s*at .*:\d+:\d+.*$
        GoFileLineRegex(), // ^.*\.go:\d+:.*$
    ];

    private bool _inErrorBlock;
    private int _blankCount;
    private bool _emittedAny;

    // --- Generic errors ---

    [GeneratedRegex(@"^.*error[\s:\[].*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericErrorRegex();

    [GeneratedRegex(@"^.*\berr\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericErrRegex();

    [GeneratedRegex(@"^.*warning[\s:\[].*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericWarningRegex();

    [GeneratedRegex(@"^.*\bwarn\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericWarnRegex();

    [GeneratedRegex(@"^.*failed.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FailedRegex();

    [GeneratedRegex(@"^.*failure.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FailureRegex();

    [GeneratedRegex(@"^.*exception.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionRegex();

    [GeneratedRegex(@"^.*panic.*$", RegexOptions.IgnoreCase)]
    private static partial Regex PanicRegex();

    // --- Rust-specific ---

    [GeneratedRegex(@"^error\[E\d+\]:.*$", RegexOptions.IgnoreCase)]
    private static partial Regex RustErrorCodeRegex();

    [GeneratedRegex(@"^\s*--> .*:\d+:\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex RustLocationRegex();

    // --- Python ---

    [GeneratedRegex(@"^Traceback.*$", RegexOptions.IgnoreCase)]
    private static partial Regex PythonTracebackRegex();

    [GeneratedRegex("""^\s*File ".*", line \d+.*$""", RegexOptions.IgnoreCase)]
    private static partial Regex PythonFileLineRegex();

    // --- JavaScript/TypeScript ---

    [GeneratedRegex(@"^\s*at .*:\d+:\d+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex JsAtLocationRegex();

    // --- Go ---

    [GeneratedRegex(@"^.*\.go:\d+:.*$", RegexOptions.IgnoreCase)]
    private static partial Regex GoFileLineRegex();

    /// <inheritdoc />
    public string? FeedLine(string line)
    {
        var isError = IsErrorLine(line);

        if (isError)
        {
            _inErrorBlock = true;
            _blankCount = 0;
            _emittedAny = true;
            return line + "\n";
        }

        if (!_inErrorBlock)
        {
            return null;
        }

        if (line.Trim().Length == 0)
        {
            _blankCount++;
            if (_blankCount >= 2)
            {
                _inErrorBlock = false;
                return null;
            }

            _emittedAny = true;
            return line + "\n";
        }

        if (line.StartsWith(' ') || line.StartsWith('\t'))
        {
            _blankCount = 0;
            _emittedAny = true;
            return line + "\n";
        }

        _inErrorBlock = false;
        return null;
    }

    /// <inheritdoc />
    public string Flush() => string.Empty;

    /// <inheritdoc />
    public string? OnExit(int exitCode, string raw)
    {
        if (_emittedAny)
        {
            return null;
        }

        if (exitCode == 0)
        {
            return "[ok] Command completed successfully (no errors)";
        }

        var msg = new StringBuilder();
        msg.Append("[FAIL] Command failed (exit code: ").Append(exitCode).Append(")\n");

        var lines = SplitLines(raw);
        var start = Math.Max(0, lines.Count - 10);
        for (var i = start; i < lines.Count; i++)
        {
            msg.Append("  ").Append(lines[i]).Append('\n');
        }

        return msg.ToString();
    }

    /// <summary>
    /// Returns true if <paramref name="line"/> matches any of the 14 <see cref="ErrorPatterns"/>.
    /// Exposed internally so tests can exercise every pattern individually.
    /// </summary>
    /// <param name="line">The line to test.</param>
    /// <returns>True if any error pattern matched.</returns>
    internal static bool IsErrorLine(string line) => ErrorPatterns.Any(p => p.IsMatch(line));

    /// <summary>
    /// Splits text into lines exactly as Rust's <c>str::lines()</c> does (on <c>\n</c>, stripping a
    /// trailing <c>\r</c>, with no trailing empty entry after a final <c>\n</c>). Duplicated locally
    /// (rather than depending on <c>RtkSharp.Commands.System.ReadCommand.SplitLines</c>) to keep
    /// <c>RtkSharp.Core</c> free of a dependency on the <c>Commands</c> layer.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <returns>The lines, in order.</returns>
    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        if (text.Length == 0)
        {
            return result;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                result.Add(StripCarriageReturn(text, start, i));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            result.Add(StripCarriageReturn(text, start, text.Length));
        }

        return result;
    }

    private static string StripCarriageReturn(string text, int start, int end)
    {
        if (end > start && text[end - 1] == '\r')
        {
            end--;
        }

        return text[start..end];
    }
}
