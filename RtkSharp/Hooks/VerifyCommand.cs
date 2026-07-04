using System;

namespace RtkSharp.Hooks;

/// <summary>
/// Implements the <c>rtk verify</c> CLI verb. Faithful port of the hook-integrity subset of Rust
/// <c>Commands::Verify</c>'s dispatch arm (<c>main.rs</c>:2521-2535): when invoked with no
/// <c>--filter</c>, it runs <see cref="Integrity.RunVerify"/>.
/// </summary>
/// <remarks>
/// Rust's <c>Commands::Verify</c> also accepts <c>--filter &lt;name&gt;</c> and <c>--require-all</c>,
/// which run TOML filter inline tests via <c>hooks::verify_cmd::run</c> — a distinct feature with no
/// counterpart in this port yet. Those flags are recognized (so the CLI surface matches Rust's
/// <c>clap</c> definition) but rejected with a clear "not yet implemented" diagnostic, consistent
/// with <see cref="InitCommand"/>'s handling of not-yet-ported <c>init</c> modes.
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
