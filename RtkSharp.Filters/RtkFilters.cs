using System.Linq;
using RtkSharp.Rewrite;

namespace RtkSharp.Filters;

/// <summary>
/// Public entry point for calling RtkSharp's command filters in-process, on output the caller
/// already captured — no process execution happens in this library. See
/// <c>docs/superpowers/specs/2026-07-10-rtksharp-filters-library-design.md</c> (Revision 2) for the
/// design this implements.
/// </summary>
/// <remarks>
/// While no filter here executes a process, <c>Filter("curl", ...)</c> is a disclosed exception to
/// "no side effects": for large, non-JSON, TTY-bound responses it unconditionally writes a copy of
/// the output to disk via <see cref="RtkSharp.Core.Tee.ForceTeeHint"/>, inherited as-is from
/// curl's original filter behavior. Callers relying on this facade being purely in-memory should be
/// aware of that one exception before invoking <c>Filter("curl", ...)</c>.
/// </remarks>
public static class RtkFilters
{
    /// <summary>True if a filter is registered for <paramref name="command"/>.</summary>
    /// <param name="command">The command name (e.g. <c>"git"</c>).</param>
    /// <returns>True if <paramref name="command"/> has a registered filter.</returns>
    public static bool IsRegistered(string command) => FilterRegistry.IsRegistered(command);

    /// <summary>
    /// Filters already-captured output for <paramref name="command"/>.
    /// </summary>
    /// <param name="command">The command name (e.g. <c>"git"</c>).</param>
    /// <param name="args">The command's arguments (e.g. <c>["status"]</c>).</param>
    /// <param name="rawStdout">The command's captured stdout.</param>
    /// <param name="rawStderr">The command's captured stderr.</param>
    /// <param name="exitCode">The command's exit code.</param>
    /// <returns>The filtered output.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no filter is registered for <paramref name="command"/>. Check
    /// <see cref="IsRegistered"/> first, or catch this and fall back to raw output.
    /// </exception>
    public static string Filter(string command, string[] args, string rawStdout, string rawStderr, int exitCode)
    {
        if (!FilterRegistry.TryGet(command, out var handler))
        {
            throw new InvalidOperationException($"No filter registered for command '{command}'.");
        }

        return handler(args, rawStdout, rawStderr, exitCode);
    }

    /// <summary>
    /// Tokenizes <paramref name="commandLine"/> and splits it into a
    /// command name and argv, but only if it's a single, non-compound
    /// command — no pipes, <c>&amp;&amp;</c>/<c>||</c>/<c>;</c> operators,
    /// or redirects.
    /// </summary>
    /// <param name="commandLine">The raw shell command line to parse.</param>
    /// <param name="command">The parsed command name, or <c>""</c> if parsing failed.</param>
    /// <param name="args">The parsed argument list, or an empty array if parsing failed.</param>
    /// <returns>True if <paramref name="commandLine"/> is a single non-compound command.</returns>
    public static bool TryParseSingleCommand(string commandLine, out string command, out string[] args)
    {
        command = "";
        args = [];

        var tokens = ShellLexer.Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            return false;
        }

        if (tokens.Any(t => t.Kind is TokenKind.Operator or TokenKind.Pipe or TokenKind.Redirect or TokenKind.Shellism))
        {
            return false;
        }

        var argTokens = tokens.Where(t => t.Kind == TokenKind.Arg).ToList();
        if (argTokens.Count == 0)
        {
            return false;
        }

        command = ShellLexer.StripQuotes(argTokens[0].Value);
        args = argTokens.Skip(1).Select(t => ShellLexer.StripQuotes(t.Value)).ToArray();
        return true;
    }
}
