namespace RtkSharp.Core;

/// <summary>
/// Line-oriented streaming filter contract used by <see cref="RtkSharp.Execution.LineFilteringExecutor"/>.
/// Faithful port of Rust's <c>StreamFilter</c> trait (<c>src/core/stream.rs</c>:8-14): a filter is fed
/// one complete line at a time (stdout and stderr already merged into a single ordered sequence) and
/// decides, per line, whether anything should be echoed to the console.
/// </summary>
/// <remarks>
/// This is deliberately a different shape from the byte-chunk, full-passthrough semantics used by
/// <c>rtk proxy</c>'s <see cref="RtkSharp.Execution.StreamingExecutor"/> — see that type's remarks for
/// the full comparison. A <see cref="IStreamFilter"/> implementation only ever sees whole lines and can
/// suppress any of them.
/// </remarks>
public interface IStreamFilter
{
    /// <summary>
    /// Feeds a single complete line (stdout/stderr already merged, in arrival order, with no
    /// trailing newline) to the filter.
    /// </summary>
    /// <param name="line">The line of output, without its trailing newline.</param>
    /// <returns>
    /// The text to echo to the console (typically <paramref name="line"/> plus a trailing
    /// <c>"\n"</c>), or <see langword="null"/> if this line should be suppressed.
    /// </returns>
    string? FeedLine(string line);

    /// <summary>
    /// Called once after the underlying process's output stream has been fully drained (but
    /// before the process's exit code is known), to flush any buffered tail content.
    /// </summary>
    /// <returns>Any trailing text to echo (may be empty).</returns>
    string Flush();

    /// <summary>
    /// Called once the child process has exited, after <see cref="Flush"/>, giving the filter a
    /// chance to emit a final summary line based on the exit code and the full raw (merged,
    /// unfiltered) output.
    /// </summary>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <param name="raw">The full raw (unfiltered) merged stdout+stderr text.</param>
    /// <returns>Text to echo, or <see langword="null"/> if nothing should be added.</returns>
    string? OnExit(int exitCode, string raw) => null;
}
