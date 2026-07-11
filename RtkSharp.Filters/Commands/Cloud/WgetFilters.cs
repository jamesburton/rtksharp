using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Cloud;

/// <summary>
/// Pure filtering/formatting logic for the <c>rtk wget</c> proxy: strips progress bars and renders a
/// compact one-line (or head-of-output) result. Extracted from
/// <c>RtkSharp.Commands.Cloud.WgetCommand</c> — signatures and logic for <see cref="FormatSize"/>/
/// <see cref="CompactUrl"/>/<see cref="ParseError"/>/<see cref="TruncateLine"/> are unchanged (moved
/// as-is, only visibility promoted from <c>internal</c> to <c>public</c> and the containing type from
/// <c>WgetCommand</c> to <c>WgetFilters</c>). <c>GetFileSize</c> did NOT move — it does real
/// file-stat I/O and stays in <c>WgetCommand.cs</c>. <c>ExtractFilenameFromOutput</c> also stays (not
/// listed in the brief's interface for this file, and used only ahead of the impure
/// <c>GetFileSize</c> call, not as part of the final message assembly).
/// </summary>
/// <remarks>
/// <see cref="FormatWgetOutput"/>/<see cref="FormatWgetFailure"/>/<see cref="FormatWgetStdoutOutput"/>
/// are new pure methods carved out of <c>WgetCommand</c>'s previously-fused <c>RunAsync</c>/
/// <c>RunStdoutAsync</c>: each mixed process-exec/Console-write with final message assembly, so only
/// the message-assembly half moved here. Three names instead of one overloaded
/// <c>FormatWgetOutput</c> because the brief's single suggested name doesn't disambiguate C#-legally
/// across the three distinct shapes (success message for <c>run</c>, shared failure message for both
/// <c>run</c>/<c>run_stdout</c>, and the stdout-content success message for <c>run_stdout</c> — the
/// latter two both taking <c>(string, string)</c>, which cannot be two same-named overloads).
/// </remarks>
public static class WgetFilters
{
    /// <summary>Faithful port of <c>format_size</c> (<c>wget_cmd.rs</c>:171-184).</summary>
    /// <param name="bytes">The byte count to format.</param>
    /// <returns>A human-readable size string.</returns>
    public static string FormatSize(ulong bytes)
    {
        if (bytes == 0)
        {
            return "?";
        }

        if (bytes < 1024)
        {
            return $"{bytes}B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1}KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):F1}MB";
        }

        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1}GB";
    }

    /// <summary>Faithful port of <c>compact_url</c> (<c>wget_cmd.rs</c>:186-202).</summary>
    /// <param name="url">The URL to compact.</param>
    /// <returns>The URL with its protocol stripped and, if long, truncated with an ellipsis.</returns>
    public static string CompactUrl(string url)
    {
        var withoutProto = url.StartsWith("https://", StringComparison.Ordinal)
            ? url["https://".Length..]
            : url.StartsWith("http://", StringComparison.Ordinal)
                ? url["http://".Length..]
                : url;

        if (withoutProto.Length <= 50)
        {
            return withoutProto;
        }

        var prefix = withoutProto[..25];
        var suffix = withoutProto[^20..];
        return $"{prefix}...{suffix}";
    }

    /// <summary>Faithful port of <c>parse_error</c> (<c>wget_cmd.rs</c>:205-247).</summary>
    /// <param name="stderr">wget's captured stderr.</param>
    /// <param name="stdout">wget's captured stdout.</param>
    /// <returns>A short, human-readable error description.</returns>
    public static string ParseError(string stderr, string stdout)
    {
        var combined = $"{stderr}\n{stdout}";

        if (combined.Contains("404"))
        {
            return "404 Not Found";
        }

        if (combined.Contains("403"))
        {
            return "403 Forbidden";
        }

        if (combined.Contains("401"))
        {
            return "401 Unauthorized";
        }

        if (combined.Contains("500"))
        {
            return "500 Server Error";
        }

        if (combined.Contains("Connection refused"))
        {
            return "Connection refused";
        }

        if (combined.Contains("unable to resolve") || combined.Contains("Name or service not known"))
        {
            return "DNS lookup failed";
        }

        if (combined.Contains("timed out"))
        {
            return "Connection timed out";
        }

        if (combined.Contains("SSL") || combined.Contains("certificate"))
        {
            return "SSL/TLS error";
        }

        foreach (var line in SourceFilterLineSplitter.SplitLines(stderr))
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0 && !trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                return trimmed.Length > 60 ? $"{trimmed[..60]}..." : trimmed;
            }
        }

        return "Unknown error";
    }

    /// <summary>Faithful port of <c>truncate_line</c> (<c>wget_cmd.rs</c>:249-256).</summary>
    /// <param name="line">The line to truncate.</param>
    /// <param name="max">The maximum length, including the ellipsis if truncated.</param>
    /// <returns>The original line, or a truncated form ending in <c>...</c>.</returns>
    public static string TruncateLine(string line, int max)
    {
        if (line.Length <= max)
        {
            return line;
        }

        var take = Math.Max(0, max - 3);
        return $"{line[..take]}...";
    }

    /// <summary>
    /// Pure success-message assembly carved out of <c>run</c> (<c>wget_cmd.rs</c>:7-49), matching
    /// <c>WgetCommand.RunAsync</c>'s pre-extraction success branch exactly:
    /// <c>"{url} ok | {filename} | {size}"</c>.
    /// </summary>
    /// <param name="url">The downloaded URL.</param>
    /// <param name="filename">The resolved output filename.</param>
    /// <param name="size">The downloaded file's size in bytes (0 if unknown).</param>
    /// <returns>The one-line success message.</returns>
    public static string FormatWgetOutput(string url, string filename, ulong size) =>
        $"{CompactUrl(url)} ok | {filename} | {FormatSize(size)}";

    /// <summary>
    /// Pure failure-message assembly shared by <c>run</c>/<c>run_stdout</c>
    /// (<c>wget_cmd.rs</c>:7-49, 52-108), matching both pre-extraction failure branches exactly:
    /// <c>"{url} FAILED: {error}"</c>.
    /// </summary>
    /// <param name="url">The URL that failed to download.</param>
    /// <param name="error">The parsed error description (from <see cref="ParseError"/>).</param>
    /// <returns>The one-line failure message.</returns>
    public static string FormatWgetFailure(string url, string error) =>
        $"{CompactUrl(url)} FAILED: {error}";

    /// <summary>
    /// Pure success-message assembly carved out of <c>run_stdout</c> (<c>wget_cmd.rs</c>:52-108),
    /// matching <c>WgetCommand.RunStdoutAsync</c>'s pre-extraction success branch exactly: either the
    /// full content (&le;20 lines) or a compact head-of-output summary (&gt;20 lines).
    /// </summary>
    /// <param name="url">The downloaded URL.</param>
    /// <param name="stdout">The captured stdout of <c>wget -q -O - ...</c>.</param>
    /// <returns>The formatted message (always ending in a trailing newline, matching the original's <c>Append('\n')</c> pattern).</returns>
    public static string FormatWgetStdoutOutput(string url, string stdout)
    {
        var lines = SourceFilterLineSplitter.SplitLines(stdout);
        var total = lines.Count;

        var rtkOutput = new StringBuilder();
        if (total > 20)
        {
            rtkOutput.Append($"{CompactUrl(url)} ok | {total} lines | {FormatSize((ulong)stdout.Length)}\n");
            rtkOutput.Append("first 10 lines:\n");
            foreach (var line in lines.Take(10))
            {
                rtkOutput.Append(TruncateLine(line, 100)).Append('\n');
            }

            rtkOutput.Append($"... +{total - 10} more lines");
        }
        else
        {
            rtkOutput.Append($"{CompactUrl(url)} ok | {total} lines\n");
            foreach (var line in lines)
            {
                rtkOutput.Append(line).Append('\n');
            }
        }

        return rtkOutput.ToString();
    }
}
