using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RtkSharp.Core;
using RtkSharp.Discover;

namespace RtkSharp.Commands.Analytics;

/// <summary>
/// Implements the <c>rtk session</c> CLI verb: compares RTK-routed vs. raw commands across the 10
/// most recently modified Claude Code sessions in the last 30 days. Faithful port of Rust
/// <c>src/analytics/session_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <b>No flags, and <c>cli.verbose</c> is unused.</b> Rust's <c>Session {}</c> clap variant
/// (<c>main.rs</c>:579) takes zero fields, and <c>run(cli.verbose: u8)</c> (<c>main.rs</c>:2133) never
/// reads its parameter — matching that, <see cref="Run"/> takes no arguments at all.
/// </remarks>
public static class SessionCommand
{
    private const int MaxSessions = 10;
    private const ulong SinceDays = 30;
    private const string Rule = "----------------------------------------------------------------------";

    /// <summary>A summarized session for display. Faithful port of Rust <c>SessionSummary</c> (<c>session_cmd.rs</c>:11-26).</summary>
    private sealed record SessionSummary(string Id, string Date, int TotalCmds, int RtkCmds, int OutputTokens)
    {
        public double AdoptionPct() => TotalCmds == 0 ? 0.0 : RtkCmds / (double)TotalCmds * 100.0;
    }

    /// <summary>
    /// Runs <c>rtk session</c> with the given arguments (the remainder after the <c>session</c> verb).
    /// Rust's <c>Session {}</c> clap variant (<c>main.rs</c>:579) takes zero fields, so any argument
    /// here is a usage error — matching the established <c>GainCommand</c>/<c>HookAuditCommand</c>
    /// convention (short <c>error: ...</c> message, exit 2, rather than reproducing Clap's banner).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>session</c> (must be empty).</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.Write($"error: unexpected argument '{args[0]}' found\n");
            return 2;
        }

        try
        {
            return RunCore(Console.Out);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// The testable core of <see cref="Run"/>. Faithful port of <c>run</c> (<c>session_cmd.rs</c>:59-189).
    /// </summary>
    /// <param name="stdout">The destination for all output.</param>
    /// <returns>0 (the only failure path is an exception from <see cref="ClaudeProvider.DiscoverSessions"/>, which propagates).</returns>
    internal static int RunCore(TextWriter stdout)
    {
        List<string> sessions;
        try
        {
            sessions = ClaudeProvider.DiscoverSessions(null, SinceDays);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to discover Claude Code sessions: {ex.Message}", ex);
        }

        if (sessions.Count == 0)
        {
            stdout.Write("No Claude Code sessions found in the last 30 days.\n");
            stdout.Write("Make sure Claude Code has been used at least once.\n");
            return 0;
        }

        // Skip subagent files — only top-level session JSONL — then sort by mtime desc (stable, matching
        // Rust's Vec::sort_by) and take the top 10.
        var sessionFiles = sessions
            .Where(p => !p.Contains("subagents", StringComparison.Ordinal))
            .OrderByDescending(SafeLastWriteTimeUtc)
            .Take(MaxSessions)
            .ToList();

        var summaries = new List<SessionSummary>();

        foreach (var path in sessionFiles)
        {
            List<ExtractedCommand> cmds;
            try
            {
                cmds = ClaudeProvider.ExtractCommands(path);
            }
            catch (Exception)
            {
                continue;
            }

            if (cmds.Count == 0)
            {
                continue;
            }

            var (totalCmds, rtkCmds, outputTokens) = CountRtkCommands(cmds);

            var id = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(id))
            {
                id = "unknown";
            }

            var shortId = id.Length > 8 ? id[..8] : id;
            var date = FormatRelativeDate(path);

            summaries.Add(new SessionSummary(shortId, date, totalCmds, rtkCmds, outputTokens));
        }

        if (summaries.Count == 0)
        {
            stdout.Write("No sessions with Bash commands found.\n");
            return 0;
        }

        stdout.Write("RTK Session Overview (last 10)\n");
        stdout.Write(Rule + "\n");
        stdout.Write(FormatRow("Session", "Date", "Cmds", "RTK", "Adoption", "", "Output") + "\n");
        stdout.Write(Rule + "\n");

        var totalCmdsSum = 0;
        var totalRtkSum = 0;

        foreach (var s in summaries)
        {
            var pct = s.AdoptionPct();
            var bar = ProgressBar(pct, 5);
            totalCmdsSum += s.TotalCmds;
            totalRtkSum += s.RtkCmds;

            stdout.Write(FormatDataRow(s, pct, bar) + "\n");
        }

        stdout.Write(Rule + "\n");

        var avgAdoption = totalCmdsSum > 0 ? totalRtkSum / (double)totalCmdsSum * 100.0 : 0.0;
        stdout.Write($"Average adoption: {avgAdoption.ToString("F0", CultureInfo.InvariantCulture)}%\n");
        stdout.Write("Tip: Run `rtk discover` to find missed RTK opportunities\n");

        return 0;
    }

    /// <summary>
    /// Counts RTK-covered commands from extracted commands. A command is "covered" if it either
    /// starts with <c>"rtk "</c> (explicit invocation) or would be rewritten by the hook
    /// (<see cref="CommandClassifier.IsSupported"/> returns <see langword="true"/>). Chained commands
    /// (e.g. <c>"cd ./path &amp;&amp; rtk ls"</c>) are split so each part is classified independently.
    /// Faithful port of <c>count_rtk_commands</c> (<c>session_cmd.rs</c>:35-51).
    /// </summary>
    /// <param name="cmds">The extracted commands to count.</param>
    /// <returns>A tuple of (total logical command count, RTK-covered count, summed output length).</returns>
    internal static (int Total, int Rtk, int Output) CountRtkCommands(IReadOnlyList<ExtractedCommand> cmds)
    {
        var total = 0;
        var rtk = 0;

        foreach (var c in cmds)
        {
            var parts = CommandClassifier.SplitCommandChain(c.Command);
            foreach (var part in parts)
            {
                total++;
                if (part.StartsWith("rtk ", StringComparison.Ordinal) || CommandClassifier.IsSupported(part))
                {
                    rtk++;
                }
            }
        }

        var output = cmds.Where(c => c.OutputLen.HasValue).Sum(c => c.OutputLen!.Value);
        return (total, rtk, output);
    }

    /// <summary>
    /// Renders an ASCII progress bar: <c>@</c> for filled cells (rounded), <c>.</c> for the rest.
    /// Faithful port of <c>progress_bar</c> (<c>session_cmd.rs</c>:53-57).
    /// </summary>
    /// <param name="pct">The percentage (0-100) to render.</param>
    /// <param name="width">The bar's total width in characters.</param>
    /// <returns>The rendered progress bar.</returns>
    internal static string ProgressBar(double pct, int width)
    {
        var filled = (int)Math.Round(pct / 100.0 * width, MidpointRounding.AwayFromZero);
        var empty = Math.Max(width - filled, 0);
        return new string('@', filled) + new string('.', empty);
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private static string FormatRelativeDate(string path)
    {
        DateTime mtime;
        try
        {
            mtime = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return "?";
        }

        var elapsed = DateTime.UtcNow - mtime;
        var days = (long)Math.Max(elapsed.TotalSeconds, 0) / 86400;
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            _ => $"{days}d ago",
        };
    }

    private static string FormatRow(string session, string date, string cmds, string rtk, string adoption, string bar, string output) =>
        $"{PadRight(session, 12)} {PadRight(date, 12)} {PadLeft(cmds, 5)} {PadLeft(rtk, 5)} {PadLeft(adoption, 9)} {PadRight(bar, 7)} {PadLeft(output, 8)}";

    private static string FormatDataRow(SessionSummary s, double pct, string bar) =>
        $"{PadRight(s.Id, 12)} {PadRight(s.Date, 12)} {PadLeft(s.TotalCmds.ToString(CultureInfo.InvariantCulture), 5)} " +
        $"{PadLeft(s.RtkCmds.ToString(CultureInfo.InvariantCulture), 5)} {PadLeft(pct.ToString("F0", CultureInfo.InvariantCulture) + "%", 9)} " +
        $"{PadRight(bar, 7)} {PadLeft(Utils.FormatTokens(s.OutputTokens), 8)}";

    private static string PadRight(string s, int width) => s.Length >= width ? s : s + new string(' ', width - s.Length);

    private static string PadLeft(string s, int width) => s.Length >= width ? s : new string(' ', width - s.Length) + s;
}
