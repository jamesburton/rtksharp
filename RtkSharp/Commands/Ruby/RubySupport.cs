namespace RtkSharp.Commands.Ruby;

/// <summary>
/// The resolved executable and base arguments for invoking a Ruby tool, auto-detecting
/// <c>bundle exec</c>. Callers append the tool's own arguments after <see cref="BaseArguments"/>,
/// mirroring how Rust's <c>ruby_exec</c> returns a bare <c>Command</c> ready to have tool-specific
/// args appended (<c>src/core/utils.rs:243-253</c>).
/// </summary>
/// <param name="FileName">The executable name to run (<c>"bundle"</c> when a <c>Gemfile</c> is present, otherwise the tool itself).</param>
/// <param name="BaseArguments">Arguments that must precede the tool's own arguments (<c>["exec", tool]</c> under bundler, empty otherwise).</param>
internal readonly record struct RubyCommand(string FileName, IReadOnlyList<string> BaseArguments);

/// <summary>
/// Shared helpers for the Ruby-ecosystem command filters (<c>rake</c>, <c>rubocop</c>, <c>rspec</c>).
/// </summary>
internal static class RubySupport
{
    /// <summary>
    /// Builds a <see cref="RubyCommand"/> for <paramref name="tool"/>, auto-detecting <c>bundle
    /// exec</c> when a <c>Gemfile</c> exists in the current directory (transitive dependencies like
    /// <c>rake</c> won't appear in the <c>Gemfile</c> directly but still need bundler for version
    /// isolation). Faithful port of Rust <c>utils::ruby_exec</c> (<c>src/core/utils.rs:246-253</c>).
    /// </summary>
    /// <param name="tool">The Ruby tool binary name (e.g. <c>"rake"</c>, <c>"rubocop"</c>, <c>"rspec"</c>, <c>"rails"</c>).</param>
    /// <returns>The resolved <see cref="RubyCommand"/>.</returns>
    public static RubyCommand RubyExec(string tool) => RubyExec(tool, Environment.CurrentDirectory);

    /// <summary>
    /// Overload of <see cref="RubyExec(string)"/> taking an explicit directory, for deterministic
    /// testing without mutating the process's current directory.
    /// </summary>
    /// <param name="tool">The Ruby tool binary name.</param>
    /// <param name="directory">The directory to check for a <c>Gemfile</c>.</param>
    /// <returns>The resolved <see cref="RubyCommand"/>.</returns>
    internal static RubyCommand RubyExec(string tool, string directory)
    {
        if (File.Exists(Path.Combine(directory, "Gemfile")))
        {
            return new RubyCommand("bundle", ["exec", tool]);
        }

        return new RubyCommand(tool, []);
    }

    /// <summary>
    /// Last-resort fallback: emits a diagnostic to stderr and returns the last <paramref name="n"/>
    /// lines of <paramref name="output"/> unchanged. Faithful port of Rust <c>utils::fallback_tail</c>
    /// (<c>src/core/utils.rs:233-241</c>).
    /// </summary>
    /// <param name="output">The raw output to tail.</param>
    /// <param name="label">A short label identifying the command, used in the diagnostic message.</param>
    /// <param name="n">The number of trailing lines to keep.</param>
    /// <returns>The last <paramref name="n"/> lines of <paramref name="output"/>, joined with <c>\n</c>.</returns>
    public static string FallbackTail(string output, string label, int n)
    {
        Console.Error.Write($"[rtk] {label}: output format not recognized, showing last {n} lines\n");

        var lines = Core.SourceFilterLineSplitter.SplitLines(output);
        var start = Math.Max(0, lines.Count - n);
        return string.Join('\n', lines.Skip(start));
    }
}
