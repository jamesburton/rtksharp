using System;

namespace RtkSharp.Hooks;

/// <summary>
/// Implements the <c>rtk verify</c> CLI verb. Faithful port of the hook-integrity subset of Rust
/// <c>Commands::Verify</c>'s dispatch arm (<c>main.rs</c>:2521-2535): when invoked with no
/// <c>--filter</c>, it runs <see cref="Integrity.RunVerify"/>.
/// </summary>
/// <remarks>
/// <para>
/// Rust's <c>Commands::Verify</c> also accepts <c>--filter &lt;name&gt;</c> and <c>--require-all</c>,
/// which run TOML filter inline tests via <c>hooks::verify_cmd::run</c> — a distinct feature with no
/// counterpart in this port yet. Those flags are recognized (so the CLI surface matches Rust's
/// <c>clap</c> definition) but rejected with a clear "not yet implemented" diagnostic, consistent
/// with <see cref="InitCommand"/>'s handling of not-yet-ported <c>init</c> modes.
/// </para>
/// <para>
/// <b>Known parity gap (deferred, out of Phase 9b Task 3 scope):</b> this port implements
/// <c>hooks::integrity::run_verify</c> only. The oracle's <c>Commands::Verify</c> handler
/// (<c>main.rs</c>:2523-2536) additionally and unconditionally runs
/// <c>hooks::verify_cmd::run(None, require_all)</c> (TOML inline-filter self-tests) for every
/// no-<c>--filter</c> invocation — that call is NOT yet ported, so bare <c>rtk verify</c> output
/// diverges from the oracle (missing the trailing "N/M tests passed" or "No inline tests found."
/// block). Tracked as a deferred gap, out of Phase 9b Task 3 scope (belongs with the Phase 4 TOML
/// filter system). See <c>docs/parity/compatibility-ledger.md</c> for the ledgered entry.
/// </para>
/// </remarks>
public static class VerifyCommand
{
    /// <summary>
    /// Runs <c>rtk verify</c> with the given arguments (the remainder after the <c>verify</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>verify</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        try
        {
            return RunCore(args);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    private static int RunCore(string[] args)
    {
        var verbose = 0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-v":
                case "--verbose":
                    verbose++;
                    break;
                case "--filter":
                    throw Deferred("--filter (TOML filter inline tests)");
                case "--require-all":
                    throw Deferred("--require-all (TOML filter inline tests)");
                default:
                    throw new InitAbortException($"unrecognized verify argument: {args[i]}");
            }
        }

        return Integrity.RunVerify(verbose);
    }

    /// <summary>Builds the "not yet implemented" abort for a mode deferred to a follow-up task.</summary>
    /// <param name="feature">The flag/mode name, e.g. <c>"--filter"</c>.</param>
    /// <returns>An <see cref="InitAbortException"/> carrying the deferred-mode diagnostic.</returns>
    private static InitAbortException Deferred(string feature) =>
        new($"{feature} is not yet implemented in this port (tracked for a follow-up task)");
}
