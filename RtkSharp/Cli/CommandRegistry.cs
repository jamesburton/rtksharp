using RtkSharp.Commands.System;
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

        Register("ls", LsCommand.RunAsync);
        Register("read", ReadCommand.RunAsync);
        Register("wc", WcCommand.RunAsync);
        Register("tree", TreeCommand.RunAsync);
        Register("find", FindCommand.RunAsync);
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
