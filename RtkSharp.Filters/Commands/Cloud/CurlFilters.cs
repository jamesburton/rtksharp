using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Cloud;

/// <summary>
/// Pure filtering logic for the <c>rtk curl</c> proxy: decides whether to pass a curl response body
/// through unchanged or truncate it with a tee-file recovery hint. Extracted verbatim from
/// <c>RtkSharp.Commands.Cloud.CurlCommand</c> — signatures and logic are unchanged, only visibility
/// moved from <c>internal</c> to <c>public</c> and the containing type from <c>CurlCommand</c> to
/// <c>CurlFilters</c>, including its internal <see cref="Tee.ForceTeeHint"/> call (a disclosed side
/// effect kept as-is, not split apart — same pattern as the Rubocop/Rspec
/// <c>Console.Error.Write</c> exception). See <c>RtkSharp.Commands.Cloud.CurlCommand</c> for the
/// process-execution/dispatch code that calls this method, and for the original Rust source pointer
/// (<c>src/cmds/cloud/curl_cmd.rs</c>) preserved below.
/// </summary>
public static class CurlFilters
{
    private const int MaxResponseSize = 500;

    /// <summary>
    /// Decides whether to pass a curl response body through unchanged or truncate it with a tee-file
    /// recovery hint. Faithful port of <c>filter_curl_output</c> (<c>curl_cmd.rs</c>:105-154).
    /// </summary>
    /// <param name="raw">The raw, UTF-8-decoded response body.</param>
    /// <param name="isTty">Whether stdout is an interactive terminal.</param>
    /// <returns>The content to print and an optional tee-recovery hint line.</returns>
    public static FilterResult FilterCurlOutput(string raw, bool isTty)
    {
        var trimmed = raw.Trim();
        var trimmedBytes = Encoding.UTF8.GetBytes(trimmed);

        // Heuristic: looks like a top-level JSON document. Numbers/booleans/null are always under
        // MAX_RESPONSE_SIZE so they don't need detection here.
        var looksLikeJson =
            (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            || (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            || (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2);

        // Pass through unchanged when the body looks like JSON (mid-stream truncation produces
        // invalid JSON, #1536), stdout is not a terminal (pipes/redirects need the full body, #1282),
        // or the body fits under the truncation threshold. Do NOT tee on this path — no recovery file
        // is needed when the consumer already receives the full body.
        if (!isTty || looksLikeJson || trimmedBytes.Length < MaxResponseSize)
        {
            return new FilterResult(trimmed, null);
        }

        // About to truncate for a human reader — write a tee file so the recovery hint can restore
        // the full body.
        var hint = Tee.ForceTeeHint(raw, "curl");
        if (hint is null)
        {
            // Tee disabled (RTK_TEE=0 or below the minimum tee size): nowhere to point a recovery
            // hint, so pass through rather than emit an unrecoverable truncation marker.
            return new FilterResult(trimmed, null);
        }

        // Rust's `is_char_boundary(end)` treats `end == len` as always a valid boundary (there's no
        // byte to inspect there); the `end < trimmedBytes.Length` guard reproduces that instead of
        // indexing past the end of the array when the body is exactly MaxResponseSize bytes long.
        var end = MaxResponseSize;
        while (end > 0 && end < trimmedBytes.Length && IsUtf8ContinuationByte(trimmedBytes[end]))
        {
            end--;
        }

        var truncated = Encoding.UTF8.GetString(trimmedBytes, 0, end);
        var content = $"{truncated}... ({trimmedBytes.Length} bytes total)";
        return new FilterResult(content, hint);
    }

    private static bool IsUtf8ContinuationByte(byte b) => (b & 0b1100_0000) == 0b1000_0000;

    /// <summary>The result of <see cref="FilterCurlOutput"/>: the content to print and an optional tee-recovery hint.</summary>
    /// <param name="Content">The (possibly truncated) content to print.</param>
    /// <param name="TeeHint">The recovery-hint line to print after <paramref name="Content"/>, or <see langword="null"/> if none.</param>
    public readonly record struct FilterResult(string Content, string? TeeHint);
}
