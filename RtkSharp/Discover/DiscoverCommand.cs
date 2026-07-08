using System.Globalization;

namespace RtkSharp.Discover;

/// <summary>
/// Implements the <c>rtk discover</c> CLI verb: scans Claude Code session history to find shell
/// commands that could have been rewritten to use <c>rtk</c> but weren't, aggregating them into a
/// report of missed token savings. Faithful port of Rust <c>src/discover/mod.rs</c>'s <c>run</c>
/// function (<c>mod.rs</c>:43-297).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>RTK_META_COMMANDS</c> parity.</b> Rust's <c>main.rs</c> lists <c>"discover"</c> in
/// <c>RTK_META_COMMANDS</c> (<c>main.rs</c>:1170-1173), so a Clap parse failure shows Clap's own error
/// instead of falling back to raw shell execution. As with <c>GainCommand</c>/<c>SessionCommand</c>,
/// RtkSharp achieves the same never-falls-back-to-raw-execution guarantee by registering
/// <c>"discover"</c> in <see cref="RtkSharp.Cli.CommandRegistry"/> (done by whichever caller wires this
/// class up — this port does not itself touch <c>CommandRegistry.cs</c>), and by having
/// <see cref="DiscoverArgs.Parse"/> throw on any malformed flag rather than silently ignoring it.
/// </para>
/// <para>
/// <b>Disclosed simplification: flag-parse error text.</b> Clap renders a multi-line usage banner for
/// parse errors; this port emits a short, single-purpose <c>error: ...</c> message to stderr and
/// exits 2 (Clap's own exit code for usage errors) — matching the established
/// <see cref="RtkSharp.Commands.Analytics.GainCommand"/> convention (same disclosed simplification,
/// same rationale).
/// </para>
/// </remarks>
public static class DiscoverCommand
{
    /// <summary>
    /// Runs <c>rtk discover</c> with the given arguments (the remainder after the <c>discover</c>
    /// verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>discover</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        DiscoverArgs parsed;
        try
        {
            parsed = DiscoverArgs.Parse(args);
        }
        catch (DiscoverArgsException ex)
        {
            Console.Error.Write(ex.Message + "\n");
            return 2;
        }

        try
        {
            return RunCore(parsed, Console.Out, Console.Error, RtkSharp.Core.RuntimeOptions.Verbosity);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// The testable core of <see cref="Run"/>. Faithful port of Rust <c>run</c> (<c>mod.rs</c>:43-275).
    /// </summary>
    /// <param name="args">The parsed <c>rtk discover</c> arguments.</param>
    /// <param name="stdout">The destination for the rendered report.</param>
    /// <param name="stderr">The destination for verbose/warning diagnostics.</param>
    /// <param name="verbosity">The global verbosity level (mirrors Rust's <c>cli.verbose: u8</c>).</param>
    /// <returns>0 on success (a session-discovery I/O failure propagates as an exception).</returns>
    internal static int RunCore(DiscoverArgs args, TextWriter stdout, TextWriter stderr, int verbosity)
    {
        var projectFilter = ResolveProjectFilter(args);

        List<string> sessions;
        try
        {
            sessions = ClaudeProvider.DiscoverSessions(projectFilter, args.SinceDays);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to discover Claude Code sessions: {ex.Message}", ex);
        }

        if (verbosity > 0)
        {
            stderr.Write($"Scanning {sessions.Count} session files...\n");
            foreach (var s in sessions)
            {
                stderr.Write($"  {s}\n");
            }
        }

        var totalCommands = 0;
        var alreadyRtk = 0;
        var parseErrors = 0;
        var rtkDisabledCount = 0;
        var rtkDisabledCmds = new Dictionary<string, int>(StringComparer.Ordinal);
        var supportedMap = new Dictionary<string, SupportedBucket>(StringComparer.Ordinal);
        var unsupportedMap = new Dictionary<string, UnsupportedBucket>(StringComparer.Ordinal);

        foreach (var sessionPath in sessions)
        {
            List<ExtractedCommand> extracted;
            try
            {
                extracted = ClaudeProvider.ExtractCommands(sessionPath);
            }
            catch (Exception ex)
            {
                if (verbosity > 0)
                {
                    stderr.Write($"Warning: skipping {sessionPath}: {ex.Message}\n");
                }

                parseErrors++;
                continue;
            }

            foreach (var extCmd in extracted)
            {
                var parts = DiscoverRegistry.SplitCommandChain(extCmd.Command);
                foreach (var part in parts)
                {
                    totalCommands++;

                    // Detect RTK_DISABLED= bypass before classification.
                    var (envPrefix, actualCmd) = DiscoverRegistry.StripDisabledPrefix(part);
                    if (DiscoverRegistry.PrefixContainsRtkDisabled(envPrefix))
                    {
                        // Only count if the underlying command is one RTK supports.
                        if (DiscoverRegistry.ClassifyCommand(actualCmd) is Classification.Supported)
                        {
                            rtkDisabledCount++;
                            var display = TruncateCommand(actualCmd);
                            rtkDisabledCmds[display] = rtkDisabledCmds.GetValueOrDefault(display) + 1;
                        }

                        continue;
                    }

                    switch (DiscoverRegistry.ClassifyCommand(part))
                    {
                        case Classification.Supported sup:
                            {
                                if (!supportedMap.TryGetValue(sup.RtkEquivalent, out var bucket))
                                {
                                    bucket = new SupportedBucket(sup.RtkEquivalent, sup.Category);
                                    supportedMap[sup.RtkEquivalent] = bucket;
                                }

                                bucket.Count++;

                                // Estimate tokens for this command: real tool_result length when known,
                                // else a category/subcommand average.
                                var outputTokens = extCmd.OutputLen is { } len
                                    ? len / 4
                                    : DiscoverRegistry.CategoryAvgTokens(sup.Category, ExtractSubcmd(part));

                                var savings = (int)(outputTokens * sup.EstimatedSavingsPct / 100.0);
                                bucket.TotalOutputTokens += savings;
                                // Accumulate pre-savings tokens too, so the bucket's effective savings
                                // rate can be derived as a weighted average across all sub-commands later.
                                bucket.TotalRawOutputTokens += outputTokens;

                                var displayName = TruncateCommand(part);
                                var key = $"{displayName}:{sup.Status}";
                                bucket.CommandCounts[key] = bucket.CommandCounts.GetValueOrDefault(key) + 1;
                                break;
                            }

                        case Classification.Unsupported uns:
                            {
                                if (!unsupportedMap.TryGetValue(uns.BaseCommand, out var ubucket))
                                {
                                    ubucket = new UnsupportedBucket { Example = part };
                                    unsupportedMap[uns.BaseCommand] = ubucket;
                                }

                                ubucket.Count++;
                                break;
                            }

                        case Classification.Ignored:
                            if (part.Trim().StartsWith("rtk ", StringComparison.Ordinal))
                            {
                                alreadyRtk++;
                            }

                            break;
                    }
                }
            }
        }

        var supported = supportedMap.Values
            .Select(BuildSupportedEntry)
            .OrderByDescending(s => s.EstimatedSavingsTokens)
            .ToList();

        var unsupported = unsupportedMap
            .Select(kv => new UnsupportedEntry(kv.Key, kv.Value.Count, kv.Value.Example))
            .OrderByDescending(u => u.Count)
            .ToList();

        var rtkDisabledExamples = rtkDisabledCmds
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(5)
            .Select(kv => $"{kv.Key} ({kv.Value}x)")
            .ToList();

        var report = new DiscoverReport
        {
            SessionsScanned = sessions.Count,
            TotalCommands = totalCommands,
            AlreadyRtk = alreadyRtk,
            SinceDays = args.SinceDays,
            Supported = supported,
            Unsupported = unsupported,
            ParseErrors = parseErrors,
            RtkDisabledCount = rtkDisabledCount,
            RtkDisabledExamples = rtkDisabledExamples,
            AgentStatus = AgentIntegrationStatus.Detect(),
        };

        if (args.Format == "json")
        {
            stdout.Write(DiscoverReportFormatter.FormatJson(report) + "\n");
        }
        else
        {
            stdout.Write(DiscoverReportFormatter.FormatText(report, args.Limit, verbosity > 0));
        }

        return 0;
    }

    /// <summary>
    /// Resolves the project filter: <c>--all</c> means no filter; an explicit <c>--project</c> value
    /// wins next; otherwise the current working directory's encoded slug is used. Faithful port of
    /// the project-filter resolution in <c>run</c> (<c>mod.rs</c>:53-64).
    /// </summary>
    private static string? ResolveProjectFilter(DiscoverArgs args)
    {
        if (args.All)
        {
            return null;
        }

        if (args.Project is not null)
        {
            return args.Project;
        }

        string cwd;
        try
        {
            cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        }
        catch (Exception)
        {
            cwd = Directory.GetCurrentDirectory();
        }

        return ClaudeProvider.EncodeProjectPath(cwd);
    }

    private static SupportedEntry BuildSupportedEntry(SupportedBucket bucket)
    {
        // Pick the most common command as the display name. Rust's `.max_by_key(|(_, c)| *c)`
        // (mod.rs:191) returns the LAST element on a count tie (per Iterator::max_by_key's
        // documented tie-break); this loop mirrors that by using `>=` rather than `>`.
        var commandWithStatus = string.Empty;
        var status = RtkStatus.Existing;
        var bestCount = -1;

        foreach (var (name, count) in bucket.CommandCounts)
        {
            if (count < bestCount)
            {
                continue;
            }

            bestCount = count;
            var colonPos = name.LastIndexOf(':');
            if (colonPos >= 0)
            {
                commandWithStatus = name[..colonPos];
                status = name[(colonPos + 1)..] switch
                {
                    "Passthrough" => RtkStatus.Passthrough,
                    "NotSupported" => RtkStatus.NotSupported,
                    _ => RtkStatus.Existing,
                };
            }
            else
            {
                commandWithStatus = name;
                status = RtkStatus.Existing;
            }
        }

        // Derive the effective savings rate from accumulated totals rather than the first-seen
        // sub-command's rate — a weighted average across all sub-commands in this bucket.
        var effectiveSavingsPct = bucket.TotalRawOutputTokens > 0
            ? bucket.TotalOutputTokens * 100.0 / bucket.TotalRawOutputTokens
            : 0.0;

        return new SupportedEntry(
            commandWithStatus,
            bucket.Count,
            bucket.RtkEquivalent,
            bucket.Category,
            bucket.TotalOutputTokens,
            effectiveSavingsPct,
            status);
    }

    /// <summary>
    /// Extracts the subcommand from a command string (second whitespace-delimited word). Faithful
    /// port of Rust <c>extract_subcmd</c> (<c>mod.rs</c>:278-285).
    /// </summary>
    internal static string ExtractSubcmd(string cmd)
    {
        var parts = cmd.Trim().Split((char[]?)null, 3, StringSplitOptions.None);
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    /// <summary>
    /// Truncates a command for display, keeping its first two whitespace-delimited words. Faithful
    /// port of Rust <c>truncate_command</c> (<c>mod.rs</c>:288-297).
    /// </summary>
    internal static string TruncateCommand(string cmd)
    {
        var trimmed = cmd.Trim();
        var parts = trimmed.Split((char[]?)null, 3, StringSplitOptions.None);
        return parts.Length switch
        {
            0 => string.Empty,
            1 => parts[0],
            _ => $"{parts[0]} {parts[1]}",
        };
    }

    /// <summary>Aggregation bucket for a supported rtk-equivalent command. Mirrors Rust <c>SupportedBucket</c> (<c>mod.rs</c>:22-35).</summary>
    private sealed class SupportedBucket(string rtkEquivalent, string category)
    {
        public string RtkEquivalent { get; } = rtkEquivalent;

        public string Category { get; } = category;

        public int Count { get; set; }

        public int TotalOutputTokens { get; set; }

        public int TotalRawOutputTokens { get; set; }

        public Dictionary<string, int> CommandCounts { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Aggregation bucket for an unsupported command. Mirrors Rust <c>UnsupportedBucket</c> (<c>mod.rs</c>:37-41).</summary>
    private sealed class UnsupportedBucket
    {
        public int Count { get; set; }

        public required string Example { get; init; }
    }
}

/// <summary>
/// Parsed <c>rtk discover</c> flags. Hand-rolled port of the Clap-derived <c>Commands::Discover</c>
/// arg struct (<c>main.rs</c>:560-576) — see <see cref="DiscoverCommand"/>'s remarks regarding
/// parse-error text fidelity.
/// </summary>
public sealed class DiscoverArgs
{
    /// <summary>Filter by project path (substring match), or <see langword="null"/> for the current-directory default.</summary>
    public string? Project { get; private set; }

    /// <summary>Max commands per report section.</summary>
    public int Limit { get; private set; } = 15;

    /// <summary>Scan all projects instead of just the current one.</summary>
    public bool All { get; private set; }

    /// <summary>Limit to sessions from the last N days.</summary>
    public ulong SinceDays { get; private set; } = 30;

    /// <summary>Output format: <c>"text"</c> or <c>"json"</c>.</summary>
    public string Format { get; private set; } = "text";

    /// <summary>
    /// Parses <c>rtk discover</c>'s CLI flags.
    /// </summary>
    /// <param name="args">The raw CLI arguments following the <c>discover</c> verb.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="DiscoverArgsException">An unrecognized flag, a stray positional, a missing value, or an unparsable numeric value.</exception>
    public static DiscoverArgs Parse(string[] args)
    {
        var result = new DiscoverArgs();

        var i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-p" or "--project":
                    result.Project = RequireValue(args, ref i, arg);
                    break;
                case "-l" or "--limit":
                    result.Limit = RequireIntValue(args, ref i, arg);
                    break;
                case "-a" or "--all":
                    result.All = true;
                    i++;
                    break;
                case "-s" or "--since":
                    result.SinceDays = RequireUlongValue(args, ref i, arg);
                    break;
                case "-f" or "--format":
                    result.Format = RequireValue(args, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith("--project=", StringComparison.Ordinal))
                    {
                        result.Project = arg["--project=".Length..];
                        i++;
                    }
                    else if (arg.StartsWith("--limit=", StringComparison.Ordinal))
                    {
                        result.Limit = ParseInt(arg["--limit=".Length..], "--limit");
                        i++;
                    }
                    else if (arg.StartsWith("--since=", StringComparison.Ordinal))
                    {
                        result.SinceDays = ParseUlong(arg["--since=".Length..], "--since");
                        i++;
                    }
                    else if (arg.StartsWith("--format=", StringComparison.Ordinal))
                    {
                        result.Format = arg["--format=".Length..];
                        i++;
                    }
                    else
                    {
                        throw new DiscoverArgsException($"error: unexpected argument '{arg}' found");
                    }

                    break;
            }
        }

        return result;
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new DiscoverArgsException($"error: a value is required for '{flag}' but none was supplied");
        }

        var value = args[i + 1];
        i += 2;
        return value;
    }

    private static int RequireIntValue(string[] args, ref int i, string flag) =>
        ParseInt(RequireValue(args, ref i, flag), flag);

    private static ulong RequireUlongValue(string[] args, ref int i, string flag) =>
        ParseUlong(RequireValue(args, ref i, flag), flag);

    private static int ParseInt(string value, string flag)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new DiscoverArgsException($"error: invalid value '{value}' for '{flag}': not a valid number");
        }

        return parsed;
    }

    private static ulong ParseUlong(string value, string flag)
    {
        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new DiscoverArgsException($"error: invalid value '{value}' for '{flag}': not a valid number");
        }

        return parsed;
    }
}

/// <summary>Thrown by <see cref="DiscoverArgs.Parse"/> on any malformed <c>rtk discover</c> invocation.</summary>
public sealed class DiscoverArgsException(string message) : Exception(message);
