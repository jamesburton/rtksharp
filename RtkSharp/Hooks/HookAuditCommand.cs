using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RtkSharp.Core;

namespace RtkSharp.Hooks;

/// <summary>
/// Implements the <c>rtk hook-audit</c> CLI verb: summarizes the hook-rewrite audit log (rewrite vs.
/// skip counts, skip-reason breakdown, top rewritten commands). Faithful port of Rust
/// <c>src/hooks/hook_audit_cmd.rs</c> (<c>run</c> and every private helper it calls).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure reader; the writer is a separate, not-yet-ported concern.</b> The audit log itself is
/// written by <c>hooks::hook_cmd::audit_log</c> (<c>hook_cmd.rs:289-321</c>), gated on
/// <c>RTK_HOOK_AUDIT=1</c>, which this command never touches — it only reads whatever log file already
/// exists. When no log is present (the common case, since audit mode is opt-in), Rust prints a fixed
/// "no log found" message and returns success; this is faithfully reproduced below and does not depend
/// on <see cref="HookCommand"/> having an audit-writing path.
/// </para>
/// <para>
/// <b>Log path resolution deliberately does NOT use <see cref="TrustCommand.ResolveDataDir"/>.</b>
/// Rust's <c>default_log_path()</c> (<c>hook_audit_cmd.rs:7-16</c>) checks <c>RTK_AUDIT_DIR</c> first,
/// then falls back to <c>dirs::home_dir()/.local/share/rtk/hook-audit.log</c> — i.e. the user's raw
/// home directory (<c>%USERPROFILE%</c> on Windows), never <c>dirs::data_local_dir()</c>
/// (<c>%LOCALAPPDATA%</c>). <see cref="TrustCommand.ResolveDataDir"/> ports the latter for the
/// config/trust/tracking subsystem and would resolve to the wrong directory here, so
/// <see cref="ResolveLogPath"/> reimplements the home-dir-based path directly.
/// </para>
/// <para>
/// <b>Preserved Rust-source quirk: the reader's UTC cutoff vs. the writer's local-time timestamps.</b>
/// <c>filter_since_days</c> (<c>hook_audit_cmd.rs:56-69</c>) compares each entry's timestamp string
/// against a UTC-formatted cutoff (<c>%Y-%m-%dT%H:%M:%SZ</c>), while <c>audit_log</c>
/// (<c>hook_cmd.rs:314</c>) writes entries using <c>chrono::Local::now()</c> with NO trailing
/// <c>Z</c>/offset. On a non-UTC system this means the reader's cutoff comparison is comparing
/// against the wrong wall-clock reference — a genuine, disclosed inconsistency in the oracle, ported
/// faithfully here rather than "fixed" toward internally-consistent behavior. See
/// <c>docs/parity/compatibility-ledger.md</c>.
/// </para>
/// <para>
/// <b>Disclosed simplification: flag-parse error text.</b> Like <see cref="Commands.Analytics.GainCommand"/>,
/// Clap's multi-line usage banner for a bad <c>--since</c> value is not reproduced byte-for-byte; this
/// port emits a short <c>error: ...</c> message and exits 2 (Clap's usage-error exit code), preserving
/// the fail-fast behavior without chasing Clap's exact prose.
/// </para>
/// </remarks>
public static class HookAuditCommand
{
    /// <summary>Rust's default <c>--since</c> value when the flag is omitted (<c>hook_audit_cmd.rs</c> clap default, <c>main.rs</c>:750).</summary>
    private const ulong DefaultSinceDays = 7;

    /// <summary>The 30-dash (light horizontal, U+2500) rule printed under the "Hook Audit (...)" header (<c>hook_audit_cmd.rs:132</c>).</summary>
    private const string Rule = "──────────────────────────────";

    /// <summary>
    /// Runs <c>rtk hook-audit</c> with the given arguments (the remainder after the <c>hook-audit</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>hook-audit</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        ulong since;
        try
        {
            since = ParseArgs(args);
        }
        catch (HookAuditArgsException ex)
        {
            Console.Error.Write(ex.Message + "\n");
            return 2;
        }

        try
        {
            return RunCore(ResolveLogPath(), since, RuntimeOptions.Verbosity, Console.Out);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Runs the audit-summary logic against an explicit log path, writing to <paramref name="stdout"/>.
    /// Exposed (internal) so behavior can be tested without touching the real filesystem/home
    /// directory. Faithful port of <c>hook_audit_cmd::run</c> (<c>hook_audit_cmd.rs:71-175</c>).
    /// </summary>
    /// <param name="logPath">The resolved audit-log file path.</param>
    /// <param name="sinceDays">The <c>--since</c> value (0 = all time).</param>
    /// <param name="verbose">
    /// The top-level <c>-v</c>/<c>-vv</c>/<c>-vvv</c>/<c>--verbose</c> count (Rust's <c>cli.verbose</c>,
    /// only recognized before the subcommand — mirrors <see cref="RuntimeOptions.Verbosity"/>, the same
    /// ambient-state mechanism <c>VerifyCommand</c>/<c>ErrCommand</c>/<c>TestCommand</c>/<c>ProxyCommand</c>
    /// use). When greater than 0, an extra trailing <c>Log: {path}</c> line is printed
    /// (<c>hook_audit_cmd.rs:170-172</c>).
    /// </param>
    /// <param name="stdout">The destination for all output (Rust uses <c>println!</c> exclusively — no stderr output exists in this command).</param>
    /// <returns>0 (this command never fails once argument parsing succeeds, matching the oracle).</returns>
    internal static int RunCore(string logPath, ulong sinceDays, int verbose, TextWriter stdout)
    {
        if (!File.Exists(logPath))
        {
            stdout.Write($"No audit log found at {logPath}\n");
            stdout.Write("Enable audit mode: export RTK_HOOK_AUDIT=1 in your shell, then use Claude Code.\n");
            return 0;
        }

        var content = File.ReadAllText(logPath);
        var entries = SplitLines(content).Select(ParseLine).Where(e => e is not null).Select(e => e!).ToList();

        if (entries.Count == 0)
        {
            stdout.Write("Audit log is empty.\n");
            return 0;
        }

        var filtered = FilterSinceDays(entries, sinceDays, DateTime.UtcNow);

        if (filtered.Count == 0)
        {
            stdout.Write($"No entries in the last {sinceDays} days.\n");
            return 0;
        }

        var actionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var cmdCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in filtered)
        {
            actionCounts[entry.Action] = actionCounts.GetValueOrDefault(entry.Action) + 1;
            if (entry.Action == "rewrite")
            {
                var baseCmd = BaseCommand(entry.OriginalCmd);
                cmdCounts[baseCmd] = cmdCounts.GetValueOrDefault(baseCmd) + 1;
            }
        }

        var total = filtered.Count;
        var rewrites = actionCounts.GetValueOrDefault("rewrite");
        var skips = total - rewrites;
        var rewritePct = total > 0 ? rewrites / (double)total * 100.0 : 0.0;
        var skipPct = total > 0 ? skips / (double)total * 100.0 : 0.0;

        var period = sinceDays == 0 ? "all time" : $"last {sinceDays} days";

        stdout.Write($"Hook Audit ({period})\n");
        stdout.Write(Rule + "\n");
        stdout.Write($"Total invocations: {total}\n");
        stdout.Write($"Rewrites:          {rewrites} ({rewritePct.ToString("F1", CultureInfo.InvariantCulture)}%)\n");
        stdout.Write($"Skips:             {skips} ({skipPct.ToString("F1", CultureInfo.InvariantCulture)}%)\n");

        var skipActions = actionCounts
            .Where(kv => kv.Key.StartsWith("skip:", StringComparison.Ordinal))
            .ToList();

        if (skipActions.Count > 0)
        {
            var sortedSkips = skipActions.OrderByDescending(kv => kv.Value).ToList();
            foreach (var (action, count) in sortedSkips)
            {
                var reason = action["skip:".Length..];
                var padding = new string(' ', 14 - Math.Min(reason.Length, 13));
                stdout.Write($"  {reason}:{padding}{count}\n");
            }
        }

        if (cmdCounts.Count > 0)
        {
            var top = cmdCounts
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key} ({kv.Value})")
                .ToList();
            stdout.Write($"Top commands: {string.Join(", ", top)}\n");
        }

        if (verbose > 0)
        {
            stdout.Write($"\nLog: {logPath}\n");
        }

        return 0;
    }

    /// <summary>
    /// Parses <c>-s</c>/<c>--since &lt;days&gt;</c>/<c>--since=&lt;days&gt;</c> off the <c>hook-audit</c>
    /// argument remainder. Any other token is a usage error.
    /// </summary>
    /// <param name="args">The arguments following the <c>hook-audit</c> verb.</param>
    /// <returns>The parsed <c>--since</c> day count, defaulting to <see cref="DefaultSinceDays"/>.</returns>
    /// <exception cref="HookAuditArgsException">The arguments are malformed or unrecognized.</exception>
    internal static ulong ParseArgs(string[] args)
    {
        var since = DefaultSinceDays;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-s" or "--since")
            {
                if (i + 1 >= args.Length)
                {
                    throw new HookAuditArgsException("error: a value is required for '--since <SINCE>' but none was supplied");
                }

                since = ParseSinceValue(args[++i]);
            }
            else if (arg.StartsWith("--since=", StringComparison.Ordinal))
            {
                since = ParseSinceValue(arg["--since=".Length..]);
            }
            else
            {
                throw new HookAuditArgsException($"error: unexpected argument '{arg}' found");
            }
        }

        return since;
    }

    private static ulong ParseSinceValue(string value)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new HookAuditArgsException($"error: invalid value '{value}' for '--since <SINCE>': invalid digit found in string");
        }

        return parsed;
    }

    /// <summary>
    /// Resolves the audit-log path: <c>RTK_AUDIT_DIR</c> if set, else
    /// <c>{home}/.local/share/rtk/hook-audit.log</c>. Faithful port of <c>default_log_path()</c>
    /// (<c>hook_audit_cmd.rs:7-16</c>) — see the class remarks for why this does not reuse
    /// <see cref="TrustCommand.ResolveDataDir"/>.
    /// </summary>
    /// <returns>The resolved audit-log file path.</returns>
    internal static string ResolveLogPath()
    {
        var overrideDir = Environment.GetEnvironmentVariable("RTK_AUDIT_DIR");
        if (!string.IsNullOrEmpty(overrideDir))
        {
            return Path.Combine(overrideDir, "hook-audit.log");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "rtk", "hook-audit.log");
    }

    /// <summary>
    /// Splits <paramref name="content"/> the way Rust's <c>str::lines()</c> does: split on <c>\n</c>,
    /// each line's trailing <c>\r</c> stripped, and no trailing empty entry for content ending in a
    /// newline.
    /// </summary>
    /// <param name="content">The raw file content.</param>
    /// <returns>The content's lines.</returns>
    internal static IEnumerable<string> SplitLines(string content)
    {
        if (content.Length == 0)
        {
            yield break;
        }

        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
            {
                continue;
            }

            var end = i > start && content[i - 1] == '\r' ? i - 1 : i;
            yield return content[start..end];
            start = i + 1;
        }

        if (start < content.Length)
        {
            yield return content[start..];
        }
    }

    /// <summary>
    /// Parses a single log line: <c>"timestamp | action | original_cmd | rewritten_cmd"</c>. Faithful
    /// port of <c>parse_line</c> (<c>hook_audit_cmd.rs:28-39</c>) — splits on the literal <c>" | "</c>
    /// separator into at most 4 parts; a fourth field is optional (defaults to <c>"-"</c>).
    /// </summary>
    /// <param name="line">A single line from the audit log.</param>
    /// <returns>The parsed entry, or <see langword="null"/> if the line has fewer than 3 fields.</returns>
    internal static AuditEntry? ParseLine(string line)
    {
        var parts = line.Split(" | ", 4, StringSplitOptions.None);
        if (parts.Length < 3)
        {
            return null;
        }

        return new AuditEntry(parts[0], parts[1], parts[2]);
    }

    /// <summary>
    /// Extracts the base command (first 1-2 whitespace-separated tokens, skipping leading
    /// <c>KEY=value</c> env-var-prefix tokens) for grouping. Faithful port of <c>base_command</c>
    /// (<c>hook_audit_cmd.rs:42-54</c>).
    /// </summary>
    /// <param name="cmd">The original (un-rewritten) command string.</param>
    /// <returns>The grouped base-command label.</returns>
    internal static string BaseCommand(string cmd)
    {
        var stripped = cmd
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(w => w.Contains('=', StringComparison.Ordinal))
            .ToList();

        return stripped.Count switch
        {
            0 => cmd,
            1 => stripped[0],
            _ => $"{stripped[0]} {stripped[1]}",
        };
    }

    /// <summary>
    /// Filters <paramref name="entries"/> to those within the last <paramref name="days"/> days,
    /// comparing each entry's raw timestamp string against a UTC-formatted cutoff. Faithful port of
    /// <c>filter_since_days</c> (<c>hook_audit_cmd.rs:56-69</c>), including its lexical
    /// string-vs-string comparison (not a parsed-datetime comparison) — see the class remarks for the
    /// preserved UTC-cutoff-vs-local-time-writer inconsistency this inherits.
    /// </summary>
    /// <param name="entries">The parsed audit-log entries.</param>
    /// <param name="days">The day count (0 = return all entries unfiltered).</param>
    /// <param name="utcNow">The current UTC instant, injectable for deterministic testing.</param>
    /// <returns>The entries at or after the cutoff.</returns>
    internal static List<AuditEntry> FilterSinceDays(IReadOnlyList<AuditEntry> entries, ulong days, DateTime utcNow)
    {
        if (days == 0)
        {
            return entries.ToList();
        }

        var cutoff = utcNow - TimeSpan.FromDays(days);
        var cutoffStr = cutoff.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        return entries.Where(e => string.CompareOrdinal(e.Timestamp, cutoffStr) >= 0).ToList();
    }
}

/// <summary>A single parsed audit-log entry. Mirrors Rust's <c>AuditEntry</c> (<c>hook_audit_cmd.rs:20-25</c>); the rewritten-command field is parsed but never used by any summary output, so it is not retained here.</summary>
/// <param name="Timestamp">The raw timestamp string (compared lexically, never parsed as a date).</param>
/// <param name="Action">The action label (<c>"rewrite"</c> or <c>"skip:&lt;reason&gt;"</c>).</param>
/// <param name="OriginalCmd">The original (pre-rewrite) command string.</param>
internal sealed record AuditEntry(string Timestamp, string Action, string OriginalCmd);

/// <summary>
/// A usage error from <see cref="HookAuditCommand.ParseArgs"/>, carrying the message to print
/// verbatim to stderr before exiting 2 (Clap's usage-error exit code).
/// </summary>
internal sealed class HookAuditArgsException(string message) : Exception(message);
