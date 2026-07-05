using RtkSharp.Commands.Analytics;
using RtkSharp.Commands.Dotnet;
using RtkSharp.Commands.Gh;
using RtkSharp.Commands.Git;
using RtkSharp.Commands.Js;
using RtkSharp.Commands.System;
using RtkSharp.Hooks;
using RtkSharp.Rewrite;

namespace RtkSharp.Cli;

/// <summary>
/// Maps CLI verbs (e.g. <c>rewrite</c>, <c>ls</c>) to their handlers. <c>Program.cs</c>
/// consults this registry before falling back to generic passthrough execution, so each
/// filter module registers itself here rather than Program.cs growing a chain of
/// hand-written if-blocks.
/// </summary>
public static class CommandRegistry
{
    private static readonly Dictionary<string, Func<string[], Task<int>>> Handlers = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the built-in command handlers at static initialization time.
    /// </summary>
    static CommandRegistry()
    {
        Register("rewrite", args =>
        {
            var (exitCode, output) = RewriteCommand.Evaluate(string.Join(' ', args));
            Console.Out.Write(output);
            return Task.FromResult(exitCode);
        });

        Register("hook", args => Task.FromResult(HookCommand.Run(args)));
        Register("init", args => Task.FromResult(InitCommand.Run(args)));
        Register("verify", args => Task.FromResult(VerifyCommand.Run(args)));
        Register("config", args => Task.FromResult(ConfigCommand.Run(args)));
        Register("trust", args => Task.FromResult(TrustCommand.RunTrust(args)));
        Register("untrust", args => Task.FromResult(TrustCommand.RunUntrust(args)));

        // Registering "gain" here (rather than raw shell passthrough) is what gives it Rust's
        // RTK_META_COMMANDS guarantee (main.rs:1170-1205): a lookup hit in this registry always wins
        // over RtkProgram.RunAsync's TOML-fallback/raw-passthrough path, so a bad `rtk gain` flag can
        // never fall back to executing a literal `gain` binary from $PATH.
        Register("gain", args => Task.FromResult(GainCommand.Run(args)));

        Register("ls", LsCommand.RunAsync);
        Register("read", ReadCommand.RunAsync);
        Register("wc", WcCommand.RunAsync);
        Register("tree", TreeCommand.RunAsync);
        Register("find", FindCommand.RunAsync);
        Register("grep", GrepCommand.RunAsync);
        Register("dotnet", DotnetCommand.RunAsync);
        Register("git", GitCommand.RunAsync);
        Register("gh", GhCommand.RunAsync);
        Register("npm", NpmCommand.RunAsync);
        Register("npx", NpmCommand.ExecAsync);
        Register("pnpm", PnpmCommand.RunAsync);
        Register("tsc", TscCommand.RunAsync);
        Register("vitest", VitestCommand.RunVitestAsync);
        Register("jest", VitestCommand.RunJestAsync);
        Register("playwright", PlaywrightCommand.RunAsync);
        Register("prisma", PrismaCommand.RunAsync);

        // Registering "run" here gives it the same RTK_META_COMMANDS-equivalent guarantee as "gain"
        // above: a registry hit always wins over RtkProgram.RunAsync's TOML-fallback/raw-passthrough
        // path, so `rtk run` can never fall back to executing a literal `run` binary from $PATH.
        Register("run", RunCommand.RunAsync);

        // Registering "proxy" here gives it the same RTK_META_COMMANDS-equivalent guarantee as "run"/
        // "gain" above: a registry hit always wins over RtkProgram.RunAsync's TOML-fallback/raw-
        // passthrough path, so `rtk proxy` can never fall back to executing a literal `proxy` binary
        // from $PATH.
        Register("proxy", args => ProxyCommand.RunAsync(args));

        // Registering "pipe" here gives it the same RTK_META_COMMANDS-equivalent guarantee as
        // "run"/"proxy"/"gain" above: a registry hit always wins over RtkProgram.RunAsync's
        // TOML-fallback/raw-passthrough path, so `rtk pipe` can never fall back to executing a
        // literal `pipe` binary from $PATH.
        Register("pipe", PipeCommand.RunAsync);

        // "err"/"test" are registered here the same way "git"/"gh"/"dotnet" are: a normal dispatch
        // entry, NOT the meta-command-style guarantee "run"/"proxy"/"pipe"/"gain" get above. This is
        // deliberate: Rust's own RTK_META_COMMANDS list does not include "err"/"test" either (they're
        // classified under Rust's PASSTHROUGH set instead) - a Rust-source asymmetry preserved as-is,
        // not "fixed" for consistency with the meta-commands above.
        Register("err", ErrCommand.RunAsync);
        Register("test", TestCommand.RunAsync);

        // "env" is registered the same way "err"/"test" are: a normal dispatch entry, not the
        // meta-command-style guarantee "run"/"proxy"/"pipe"/"gain" get above - Rust's own
        // RTK_META_COMMANDS list doesn't include "env" either, but the registry-hit-always-wins
        // behavior already gives it the sufficient substitute established in Phase 6.
        Register("env", EnvCommand.RunAsync);
    }

    /// <summary>
    /// Registers <paramref name="handler"/> as the implementation for the <paramref name="commandName"/> verb.
    /// </summary>
    /// <param name="commandName">The CLI verb to register, e.g. <c>"ls"</c>.</param>
    /// <param name="handler">The handler invoked with the command's remaining arguments.</param>
    public static void Register(string commandName, Func<string[], Task<int>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(handler);
        Handlers[commandName] = handler;
    }

    /// <summary>
    /// Attempts to look up the handler registered for <paramref name="commandName"/>.
    /// </summary>
    /// <param name="commandName">The CLI verb to look up.</param>
    /// <param name="handler">The registered handler, if found.</param>
    /// <returns>True if a handler is registered for <paramref name="commandName"/>; otherwise false.</returns>
    public static bool TryGet(string commandName, out Func<string[], Task<int>> handler) =>
        Handlers.TryGetValue(commandName, out handler!);
}
