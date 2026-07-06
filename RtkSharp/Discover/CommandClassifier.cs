using System.Collections.Generic;
using RtkSharp.Rewrite;

namespace RtkSharp.Discover;

/// <summary>
/// Thin wrappers around the already-ported rewrite engine, exposing the two primitives
/// <c>rtk session</c> needs from Rust's <c>discover::registry</c>: <c>classify_command</c> (reduced
/// to the boolean the caller actually consumes) and <c>split_command_chain</c>.
/// </summary>
/// <remarks>
/// In Rust, <c>discover::registry::classify_command</c>/<c>split_command_chain</c> are not an
/// independent classification scheme — <c>src/hooks/rewrite_cmd.rs</c> calls
/// <c>registry::rewrite_command</c> directly, and both functions share the exact same <c>RULES</c>
/// table and matching pipeline (<c>discover/rules.rs</c>'s 75-entry table). RtkSharp already ported
/// that shared engine as <see cref="RewriteEngine.ClassifyRtkEquivalent"/>/<see cref="RewriteRules"/>
/// for the <c>rtk rewrite</c> command, so this class reuses it rather than re-porting
/// <c>discover/registry.rs</c>'s classification tables a second time.
/// </remarks>
public static class CommandClassifier
{
    /// <summary>
    /// Reports whether <paramref name="command"/> would be classified as
    /// <c>Classification::Supported</c> by Rust's <c>classify_command</c> (registry.rs:94-198).
    /// <c>rtk session</c>'s <c>count_rtk_commands</c> (session_cmd.rs:35-51) is the only caller in
    /// the Rust source, and it only ever pattern-matches on the <c>Supported</c> variant — the
    /// <c>Unsupported</c>/<c>Ignored</c> distinction and the <c>Supported</c> payload
    /// (<c>category</c>/<c>estimated_savings_pct</c>/<c>status</c>) are never consumed, so this
    /// reduces to the same boolean <see cref="RewriteEngine.ClassifyRtkEquivalent"/> already
    /// computes for the rewrite path (a non-null match is exactly Rust's <c>Supported</c> case).
    /// </summary>
    /// <param name="command">A single, already-chain-split command (no <c>&amp;&amp;</c>/<c>;</c>/<c>|</c>).</param>
    /// <returns><see langword="true"/> if rtk would rewrite/support this command.</returns>
    public static bool IsSupported(string command) => RewriteEngine.ClassifyRtkEquivalent(command) is not null;

    /// <summary>
    /// Splits a shell command into its logical parts on <c>&amp;&amp;</c>/<c>||</c>/<c>;</c>, stopping
    /// at the first <c>|</c> (pipe). Faithful port of Rust <c>split_command_chain</c>
    /// (<c>discover/registry.rs:236-248</c>): trims first (returning an empty list for an
    /// all-whitespace input), then short-circuits to a single-element list of the trimmed command
    /// when it contains a (quote-aware) heredoc or an arithmetic-expansion opener <c>$((</c> — both
    /// of which the token-based splitter would otherwise incorrectly split across — before
    /// delegating to <see cref="ShellLexer.SplitOnOperators"/> (Rust's <c>split_on_operators</c>,
    /// the same lexer <c>rewrite_command</c> uses).
    /// </summary>
    /// <param name="command">The raw shell command to split.</param>
    /// <returns>The command's logical parts, in order.</returns>
    public static List<string> SplitCommandChain(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        if (RewriteEngine.HasHeredoc(trimmed) || trimmed.Contains("$(("))
        {
            return [trimmed];
        }

        return ShellLexer.SplitOnOperators(trimmed, stopAtPipe: true);
    }
}
