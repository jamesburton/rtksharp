namespace RtkSharp.Core;

/// <summary>
/// Strategy plugged into <see cref="BlockStreamFilter{THandler}"/> to group a line-oriented stream
/// into "blocks" (e.g. a compiler error/warning plus its indented continuation lines), while dropping
/// noise lines outright. Faithful port of Rust's <c>BlockHandler</c> trait (<c>src/core/stream.rs</c>
/// :17-22).
/// </summary>
/// <remarks>
/// Implementations are expected to be stateful (e.g. counting compiled crates, errors, warnings as
/// lines are observed) so that <see cref="FormatSummary"/> can report totals once the stream ends.
/// Mutating state from what look like "query" methods mirrors the Rust trait exactly (its methods
/// all take <c>&amp;mut self</c> except <c>format_summary</c>, which takes <c>&amp;self</c>).
/// </remarks>
public interface IBlockHandler
{
    /// <summary>
    /// Reports whether <paramref name="line"/> is pure noise that should never be echoed nor
    /// considered for block membership (e.g. a <c>Compiling</c> progress line). May update internal
    /// counters (e.g. a crates-compiled tally) as a side effect.
    /// </summary>
    /// <param name="line">The raw line under consideration.</param>
    /// <returns>True if the line should be dropped entirely.</returns>
    bool ShouldSkip(string line);

    /// <summary>
    /// Reports whether <paramref name="line"/> opens a new block (e.g. an <c>error[E0308]:</c>
    /// diagnostic header). May update internal counters (e.g. an error/warning tally) as a side effect.
    /// </summary>
    /// <param name="line">The raw line under consideration.</param>
    /// <returns>True if this line starts a new block.</returns>
    bool IsBlockStart(string line);

    /// <summary>
    /// Reports whether <paramref name="line"/> continues the block currently being accumulated in
    /// <paramref name="block"/> (its lines so far, not yet including <paramref name="line"/>).
    /// </summary>
    /// <param name="line">The raw line under consideration.</param>
    /// <param name="block">The lines accumulated in the current block so far.</param>
    /// <returns>True if the line should be appended to the current block.</returns>
    bool IsBlockContinuation(string line, IReadOnlyList<string> block);

    /// <summary>
    /// Formats a final summary once the underlying stream has ended, using this handler's
    /// accumulated state and (optionally) the full raw merged output for a fallback re-analysis.
    /// </summary>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <param name="raw">The full raw (unfiltered) merged stdout+stderr text.</param>
    /// <returns>The summary text to append, or null if nothing should be added.</returns>
    string? FormatSummary(int exitCode, string raw);
}
