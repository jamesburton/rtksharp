using RtkSharp.Core;

namespace RtkSharp.Ast;

/// <summary>
/// A real AST/semantic-parser-backed source-code summarizer for one <see cref="Language"/>,
/// used by <c>rtk read --level ast</c>. This tier has no Rust counterpart — <c>src/core/filter.rs</c>
/// is regex/heuristic only (see <see cref="Core.SourceFilter"/>'s remarks) — it is a deliberate
/// RtkSharp-only extension, disclosed in <c>docs/parity/compatibility-ledger.md</c>.
/// </summary>
/// <remarks>
/// Implementations parse real syntax (and, where practical, keep doc comments while discarding
/// ordinary ones) to produce a token-reduced summary: imports/usings, type and member
/// signatures, and top-level declarations are kept; member bodies are collapsed to a short
/// placeholder. This is intentionally a superset of what <see cref="AggressiveFilter"/> attempts
/// with regexes, not a byte-for-byte reproduction of it — there is no oracle to match against
/// for a level Rust doesn't have, so correctness here means "a faithful, lossless-signature
/// summary of the real parsed structure," verified by the implementation's own unit tests
/// against real source snippets (parsed and re-inspected), not by comparison to a reference
/// binary.
/// </remarks>
public interface IAstAnalyzer
{
    /// <summary>The single language this analyzer handles.</summary>
    Language Language { get; }

    /// <summary>
    /// Produces the AST-summarized form of <paramref name="content"/>. Implementations should
    /// return <paramref name="content"/> unchanged (not throw) if it fails to parse, so a single
    /// malformed file never blocks <c>rtk read</c> — mirroring this whole port's established
    /// fallback convention (see <c>src/cmds/*/README.md</c> "fallback pattern").
    /// </summary>
    /// <param name="content">The raw source text.</param>
    /// <returns>The summarized text, or <paramref name="content"/> unchanged if parsing failed.</returns>
    string Filter(string content);
}
