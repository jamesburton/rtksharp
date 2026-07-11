namespace RtkSharp.Filters;

/// <summary>
/// Public entry point for calling RtkSharp's command filters in-process, on output the caller
/// already captured — no process execution happens in this library. See
/// <c>docs/superpowers/specs/2026-07-10-rtksharp-filters-library-design.md</c> (Revision 2) for the
/// design this implements.
/// </summary>
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
}
