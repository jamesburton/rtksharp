using System;
using System.Linq;
using RtkSharp.Core;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk config [--create]</c> CLI verb: shows the resolved config file's content
/// (or the compiled-in defaults if none exists yet), or creates the default file on disk. Faithful
/// port of Rust <c>Commands::Config</c>'s dispatch arm (<c>main.rs</c>:2037-2044).
/// </summary>
/// <remarks>
/// <b>Fail-loud, not never-block.</b> <c>rtk config</c> is a user-invoked one-shot command, not a
/// runtime hook — the RTK-wide "never block the user" fallback pattern does not apply here. Any
/// exception from <see cref="Config.Load"/>/<see cref="Config.Save"/> (e.g. an unreadable or
/// unparsable existing <c>config.toml</c>) is caught once, at the top of <see cref="Run"/>, and
/// reported as <c>rtk: {message}</c> on stderr with exit code 1 — mirroring
/// <see cref="RtkSharp.Hooks.InitCommand"/>'s and <see cref="RtkSharp.Hooks.VerifyCommand"/>'s
/// identical top-level exception handling for the same class of user command.
/// </remarks>
public static class ConfigCommand
{
    /// <summary>
    /// Runs <c>rtk config</c> with the given arguments (the remainder after the <c>config</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>config</c>.</param>
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
        if (args.Contains("--create", StringComparer.Ordinal))
        {
            var path = Config.CreateDefault();
            Console.Out.Write($"Created: {path}\n");
            return 0;
        }

        Config.ShowConfig();
        return 0;
    }
}
