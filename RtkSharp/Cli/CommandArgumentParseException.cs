namespace RtkSharp.Cli;

/// <summary>
/// Thrown by a registered command handler to signal that its argument parsing failed in a way
/// Rust's <c>clap</c> would have rejected <b>before</b> the subcommand's own body ever ran.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>main.rs</c>'s <c>run_cli()</c>: <c>Cli::try_parse()</c> either succeeds (dispatch
/// proceeds into the matched subcommand's own code) or fails, in which case <c>run_fallback</c>
/// runs — for any subcommand classified as <c>PASSTHROUGH</c> (wraps a real external tool; see
/// <c>src/main.rs</c>'s <c>test_every_subcommand_is_classified</c> for the authoritative list),
/// that means a TOML-filter lookup on the verb, then a raw PATH passthrough exec of the entire
/// original argv (typically exiting 127, since native RTK verbs like <c>read</c>/<c>docker</c>
/// have no real same-named binary on PATH) — never the subcommand's own usage message. Commands
/// classified as <c>RTK_META_COMMANDS</c> (e.g. <c>gain</c>, <c>init</c>, <c>session</c>,
/// <c>telemetry</c>) are explicitly exempt in Rust: their own parse failure exits via clap's
/// own error (not the fallback), which is what those commands' existing bespoke
/// exit-2-with-message handling already replicates — <b>do not</b> throw this exception from a
/// <c>RTK_META_COMMANDS</c> command; its current behavior is already correct.
/// </para>
/// <para>
/// <see cref="RtkProgram"/> catches this exception around every registered handler invocation and
/// re-dispatches through the exact same TOML-fallback/raw-passthrough path already used for
/// unregistered commands (<see cref="RtkProgram.TryTomlFallbackAsync"/> then raw PATH exec) — so
/// there is exactly one place that mechanism is implemented, reused for both "no module handles
/// this verb at all" and "the module's own parsing rejected these specific args."
/// </para>
/// <para>
/// <b>Scope discipline.</b> Only throw this for the specific error shapes that would genuinely
/// fail at Rust's clap layer for that exact subcommand — an unrecognized flag, a missing required
/// value/positional, a value that fails its declared type/enum parse, or two flags declared
/// mutually exclusive via <c>conflicts_with</c>. Do NOT throw this for errors that arise inside
/// the Rust subcommand's own function body after clap successfully parsed (e.g. a file-not-found,
/// a business-logic validation failure, an explicit user-facing "abort" case like
/// <c>init</c>/<c>verify</c>'s own bail conditions) — those keep whatever behavior they already
/// have, since Rust's <c>run_fallback</c> is never reached for them either. Getting this boundary
/// wrong in either direction is a real, oracle-visible divergence: too eager, and a legitimate
/// runtime error incorrectly triggers a PATH-exec attempt; too conservative, and a genuine
/// clap-equivalent parse failure keeps the wrong (bespoke, non-127) exit shape.
/// </para>
/// </remarks>
public sealed class CommandArgumentParseException : Exception
{
    /// <summary>Creates the exception. The message is diagnostic only — never shown to the user,
    /// since the fallback path replaces it with either a TOML-filtered result or the raw
    /// PATH-exec attempt's own output.</summary>
    /// <param name="message">A diagnostic description of what failed to parse.</param>
    public CommandArgumentParseException(string message) : base(message)
    {
    }
}
