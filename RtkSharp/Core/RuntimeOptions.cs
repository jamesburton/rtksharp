namespace RtkSharp.Core;

/// <summary>
/// Ambient holder for rtk's process-global options that the command registry's
/// <c>Func&lt;string[], Task&lt;int&gt;&gt;</c> handler signature cannot carry.
/// </summary>
/// <remarks>
/// <para>
/// Rust threads <c>cli.ultra_compact</c> as an explicit parameter into
/// <c>gh_cmd::run(subcommand, args, verbose, ultra_compact)</c> (main.rs:1673). RtkSharp's
/// <see cref="Cli.CommandRegistry"/> dispatches on a fixed <c>Func&lt;string[], Task&lt;int&gt;&gt;</c>
/// delegate that only receives the wrapped command's args — the global <c>--ultra-compact</c>
/// flag is parsed off the front by <see cref="RtkArguments"/> and never appears in
/// <see cref="RtkArguments.CommandArgs"/>, so it cannot reach a handler through the args array.
/// </para>
/// <para>
/// Rather than widen every handler signature (8 verbs) for a flag only <c>gh</c>/<c>glab</c>
/// consume, <c>Program</c> publishes the parsed flag here once, immediately before dispatch, and the
/// <c>gh</c> handler's public entry point reads it. Handlers that take the flag as an explicit
/// parameter (their test-friendly overloads) never touch this ambient state, so unit tests stay
/// deterministic and independent of process-global mutation.
/// </para>
/// </remarks>
public static class RuntimeOptions
{
    /// <summary>
    /// Whether the user requested ultra-compact summaries via the global <c>-u</c>/<c>--ultra-compact</c>
    /// flag. Set once by <c>Program</c> before command dispatch; read by handlers whose registry
    /// delegate cannot receive it as an argument.
    /// </summary>
    public static bool UltraCompact { get; set; }

    /// <summary>
    /// The verbosity level requested via the global <c>-v</c>/<c>-vv</c>/<c>-vvv</c>/<c>--verbose</c>
    /// flag(s) (mirrors Rust's <c>cli.verbose: u8</c>, main.rs:67 — "only recognized before the
    /// subcommand"). Set once by <c>Program</c> before command dispatch; read by
    /// <c>RtkSharp.Hooks.InitCommand</c>, whose registry delegate cannot receive it as an argument.
    /// </summary>
    public static int Verbosity { get; set; }
}
