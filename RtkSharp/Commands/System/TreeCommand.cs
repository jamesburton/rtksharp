using RtkSharp.Execution;

namespace RtkSharp.Commands.System;

/// <summary>
/// Proxies to the native <c>tree</c> command and compacts its output. To reduce token usage
/// the noise directories in <see cref="SystemConstants.NoiseDirs"/> are excluded by default
/// via <c>tree -I &lt;pattern&gt;</c>, unless the user asked to see everything (<c>-a</c>/
/// <c>--all</c>) or supplied their own ignore pattern (<c>-I</c>/<c>--ignore=</c>). The final
/// summary line (e.g. <c>5 directories, 23 files</c>) and trailing blank lines are stripped
/// from the output.
/// </summary>
/// <remarks>
/// Ported faithfully from <c>src/cmds/system/tree.rs</c>. Execution goes through
/// <see cref="CommandRunner.RunFilteredAsync"/> with the same <c>RunOptions</c> as tree.rs
/// (<c>stdout_only().early_exit_on_failure().no_trailing_newline()</c> →
/// <c>FilterStdoutOnly</c>, <c>SkipFilterOnFailure</c>, <c>NoTrailingNewline</c>). The
/// verbose reduction diagnostic (tree.rs's <c>eprintln!("Lines: … reduction")</c>) is omitted:
/// the registered handler signature is <c>RunAsync(string[])</c> with no verbosity level, and
/// that line is a stderr diagnostic that never affects filtered output.
/// <para>
/// The tool-existence precheck mirrors tree.rs's <c>tool_exists("tree")</c> (which resolves via
/// <c>which</c>, honoring <c>PATHEXT</c> on Windows). Here it uses <see cref="PathResolver"/>,
/// which returns its input unchanged when the name cannot be resolved. On Windows the built-in
/// <c>tree.com</c> in <c>System32</c> is found, so the missing-tool branch does not trigger on a
/// stock Windows install; the branch and its exact install-hint text are preserved for platforms
/// where <c>tree</c> is genuinely absent.
/// </para>
/// </remarks>
public static class TreeCommand
{
    /// <summary>
    /// The exact error text emitted when <c>tree</c> is not installed. Transcribed from
    /// tree.rs's <c>anyhow::bail!</c>: the Rust source uses string-continuation escapes
    /// (<c>\n\</c>) which collapse the following line's indentation, so each install hint has no
    /// leading whitespace before the dash. Surfaced to the user as <c>rtk: {message}</c>,
    /// matching main.rs's <c>eprintln!("rtk: {:#}", e)</c> error handler.
    /// </summary>
    internal const string ToolNotFoundMessage =
        "tree command not found. Install it first:\n" +
        "- macOS: brew install tree\n" +
        "- Ubuntu/Debian: sudo apt install tree\n" +
        "- Fedora/RHEL: sudo dnf install tree\n" +
        "- Arch: sudo pacman -S tree";

    /// <summary>
    /// Runs the underlying <c>tree</c> with noise-directory exclusion applied by default and
    /// prints the compacted tree. If <c>tree</c> is not installed, prints the install hint to
    /// stderr and returns 1.
    /// </summary>
    /// <param name="args">The arguments following the <c>tree</c> verb (flags and paths).</param>
    /// <returns>The underlying <c>tree</c> process's exit code, or 1 if <c>tree</c> is missing.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, static name => PathResolver.Resolve(name) != name);

    /// <summary>
    /// Testable core of <see cref="RunAsync(string[])"/>: the tool-existence check is injected so
    /// the missing-tool branch can be exercised deterministically regardless of the host's
    /// installed tools.
    /// </summary>
    /// <param name="args">The arguments following the <c>tree</c> verb.</param>
    /// <param name="toolExists">Predicate reporting whether a named tool is resolvable on the host.</param>
    /// <returns>The underlying <c>tree</c> process's exit code, or 1 if <c>tree</c> is missing.</returns>
    internal static Task<int> RunAsync(string[] args, Func<string, bool> toolExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(toolExists);

        if (!toolExists("tree"))
        {
            Console.Error.WriteLine($"rtk: {ToolNotFoundMessage}");
            return Task.FromResult(1);
        }

        var showAll = args.Any(a => a is "-a" or "--all");
        var hasIgnore = args.Any(a => a == "-I" || a.StartsWith("--ignore=", StringComparison.Ordinal));

        var treeArgs = new List<string>();
        if (!showAll && !hasIgnore)
        {
            treeArgs.Add("-I");
            treeArgs.Add(string.Join("|", SystemConstants.NoiseDirs));
        }

        treeArgs.AddRange(args);

        return CommandRunner.RunFilteredAsync(
            "tree",
            treeArgs,
            "tree",
            string.Join(' ', args),
            FilterTreeOutput,
            new RunOptions(FilterStdoutOnly: true, SkipFilterOnFailure: true, NoTrailingNewline: true)
        );
    }

    /// <summary>
    /// Removes the trailing summary line (e.g. <c>5 directories, 23 files</c>) and trailing blank
    /// lines from raw <c>tree</c> output, preserving the tree structure. An empty input yields a
    /// single newline. Mirrors tree.rs's <c>filter_tree_output</c>.
    /// </summary>
    /// <param name="raw">Raw stdout from <c>tree</c>.</param>
    /// <returns>The compacted output, always terminated by a single newline.</returns>
    public static string FilterTreeOutput(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var lines = ReadCommand.SplitLines(raw);
        if (lines.Count == 0)
        {
            return "\n";
        }

        var filtered = new List<string>();
        foreach (var line in lines)
        {
            // Skip the final summary line (e.g., "5 directories, 23 files").
            if (line.Contains("director", StringComparison.Ordinal) &&
                line.Contains("file", StringComparison.Ordinal))
            {
                continue;
            }

            // Skip leading blank lines.
            if (line.Trim().Length == 0 && filtered.Count == 0)
            {
                continue;
            }

            filtered.Add(line);
        }

        // Remove trailing blank lines.
        while (filtered.Count > 0 && filtered[^1].Trim().Length == 0)
        {
            filtered.RemoveAt(filtered.Count - 1);
        }

        return string.Join("\n", filtered) + "\n";
    }
}
