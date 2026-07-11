using RtkSharp.Execution;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Filters directory listings into a compact tree format. Always executes the underlying
/// <c>ls -la</c> (with <c>LC_ALL=C</c> so the date column is parseable), then groups
/// directories first (with a trailing <c>/</c>), renders human-readable file sizes, and —
/// when a long listing was requested — prefixes each entry with its octal permissions.
/// </summary>
/// <remarks>
/// Ported faithfully from <c>src/cmds/system/ls.rs</c>. The <c>verbose</c> diagnostic
/// (<c>eprintln!("Chars: … reduction")</c>) is intentionally omitted: the registered
/// handler signature is <c>RunAsync(string[])</c> and receives no verbosity level, and the
/// line is a stderr diagnostic that does not affect filtered output. The pure filtering logic
/// lives in <see cref="LsFilters"/> (<c>RtkSharp.Filters</c>); this class retains only the
/// process-execution entry point.
/// </remarks>
public static class LsCommand
{
    /// <summary>
    /// Executes the underlying <c>ls -la</c> for the requested paths/flags and prints the
    /// compacted listing.
    /// </summary>
    /// <param name="args">The arguments following the <c>ls</c> verb (flags and paths).</param>
    /// <returns>The underlying <c>ls</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var showAll = LsFilters.DetectShowAll(args);
        var showLong = LsFilters.DetectShowLong(args);

        var flags = args.Where(a => a.StartsWith('-')).ToList();
        var paths = args.Where(a => !a.StartsWith('-')).ToList();

        // Always run `ls -la`; forward extra flags with -l/-a/-h stripped (we already
        // request the long+all listing and render sizes ourselves), and drop --all since
        // the noise filter handles hidden-dir visibility instead.
        var lsArgs = new List<string> { "-la" };
        foreach (var flag in flags)
        {
            if (flag.StartsWith("--"))
            {
                if (flag != "--all")
                {
                    lsArgs.Add(flag);
                }
            }
            else
            {
                var stripped = flag.TrimStart('-');
                var extra = new string(stripped.Where(c => c is not ('l' or 'a' or 'h')).ToArray());
                if (extra.Length > 0)
                {
                    lsArgs.Add($"-{extra}");
                }
            }
        }

        if (paths.Count == 0)
        {
            lsArgs.Add(".");
        }
        else
        {
            lsArgs.AddRange(paths);
        }

        var targetDisplay = paths.Count == 0 ? "." : string.Join(' ', paths);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C"
        };

        // Only show the summary line in interactive mode (not when piped), matching ls.rs's
        // `std::io::stdout().is_terminal()` gate.
        var isTty = !Console.IsOutputRedirected;

        return CommandRunner.RunFilteredAsync(
            "ls",
            lsArgs,
            "ls",
            $"-la {targetDisplay}",
            raw => LsFilters.ApplyFilter(raw, showAll, showLong, isTty),
            new RunOptions(FilterStdoutOnly: true, SkipFilterOnFailure: true, NoTrailingNewline: true),
            environment
        );
    }
}
