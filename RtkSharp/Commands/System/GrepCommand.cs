using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Core;
using RtkSharp.Execution;

namespace RtkSharp.Commands.System;

/// <summary>
/// Compact <c>grep</c> that wraps ripgrep (<c>rg</c>): it translates grep-isms into rg flags,
/// groups matches by file, trims whitespace, truncates long lines around the match, and caps the
/// result set with a <c>[+N more]</c> overflow marker. Files-with-matches, count, and other
/// shape/format modes are passed through natively (unfiltered) so their output is never mangled.
/// When <c>rg</c> cannot run or rejects the invocation, it falls back to system <c>grep</c>.
/// </summary>
/// <remarks>
/// Ported faithfully from <c>src/cmds/system/grep_cmd.rs</c>. Unlike ls/wc/tree this command does
/// not go through the shared <see cref="CommandRunner"/> — grep_cmd.rs has its own multi-stage
/// execution (rg → grep fallback → bare-grep retry) and print structure, which is reproduced here.
/// Notable fidelity notes:
/// <list type="bullet">
///   <item>The rtk-level options (<c>-l</c>/<c>--max-len</c>, <c>-m</c>/<c>--max</c>,
///   <c>--context-only</c>, <c>-t</c>/<c>--file-type</c>) are parsed off the front of the argument
///   vector by <see cref="RunAsync(string[])"/>, mirroring what clap does in <c>main.rs</c> before
///   calling <c>grep_cmd::run</c>. Consistent with the oracle, <c>-l</c>/<c>-m</c> only consume the
///   following token as their value when it parses as a non-negative integer; otherwise the flag is
///   treated as a ripgrep flag and routed into the trailing args (e.g. <c>rtk grep -l PATTERN</c>
///   runs rg's files-with-matches mode, while <c>rtk grep -l 5 PATTERN</c> sets a 5-char cap).</item>
///   <item>clap's <c>restore_double_dash</c> step is a no-op here: RtkSharp's top-level argument
///   parser preserves <c>--</c> in the command args, so no re-insertion is needed.</item>
///   <item>Token-savings tracking (grep_cmd.rs's <c>TimedExecution</c>) and the verbose
///   <c>eprintln!</c> diagnostic are omitted, matching the <see cref="FindCommand"/>/
///   <see cref="ReadCommand"/> precedent — they are metrics/diagnostic side effects that do not
///   change filtered output.</item>
/// </list>
/// </remarks>
public static class GrepCommand
{
    /// <summary>Default maximum displayed line length (clap <c>--max-len</c> default).</summary>
    private const int DefaultMaxLen = 80;

    /// <summary>Default maximum number of displayed results (clap <c>--max</c> default).</summary>
    private const int DefaultMaxResults = 200;

    /// <summary>
    /// Maximum lines displayed per file before the remainder is suppressed
    /// (<c>config::limits().grep_max_per_file</c> default).
    /// </summary>
    private const int GrepMaxPerFile = 25;

    /// <summary>
    /// Short single-char flags that consume one following token (or inline remainder) as their
    /// value. <c>-e</c> is handled separately (its value goes to <c>patterns</c>). Includes all rg
    /// short flags that take a value argument except <c>-e</c> and <c>-r</c> (stripped) and
    /// <c>-E</c>. Mirrors grep_cmd.rs's <c>VALUE_FLAGS_SHORT</c>.
    /// </summary>
    private const string ValueFlagsShort = "ABCMTdfgjmt";

    /// <summary>
    /// Long flags that consume the NEXT token as their value (space-separated form). The inline
    /// <c>=</c> form (<c>--flag=value</c>) is one token and passes through unchanged. <c>--regexp</c>
    /// is handled separately. Mirrors grep_cmd.rs's <c>VALUE_FLAGS_LONG</c>.
    /// </summary>
    private static readonly HashSet<string> ValueFlagsLong = new(StringComparer.Ordinal)
    {
        "--after-context", "--before-context", "--color", "--colors", "--context",
        "--context-separator", "--encoding", "--engine", "--field-context-separator",
        "--field-match-separator", "--file", "--glob", "--iglob", "--ignore-file", "--max-columns",
        "--max-count", "--max-depth", "--max-filesize", "--path-separator", "--pre", "--pre-glob",
        "--replace", "--sort", "--sortr", "--threads", "--type", "--type-add", "--type-clear",
        "--type-not"
    };

    /// <summary>
    /// Ripgrep-only flags the grep fallback drops so grep doesn't abort (issue #2167). Mirrors
    /// grep_cmd.rs's <c>RG_ONLY_LONG</c>.
    /// </summary>
    private static readonly HashSet<string> RgOnlyLong = new(StringComparer.Ordinal)
    {
        "--glob", "--iglob", "--type", "--type-not", "--type-add", "--type-clear", "--hidden",
        "--no-ignore", "--pcre2", "--json", "--stats", "--sort", "--sortr", "--engine", "--mmap",
        "--no-mmap", "--trim", "--one-file-system", "--max-columns", "--max-depth", "--max-filesize",
        "--path-separator", "--field-context-separator", "--field-match-separator", "--pre",
        "--pre-glob"
    };

    /// <summary>
    /// Parses a single rg/grep match or context line of the form <c>file\0line[:-]content</c>.
    /// The underlying command is invoked with <c>-0</c> (rg) / <c>--null</c> (grep) so the filename
    /// is NUL-separated from <c>line[:-]content</c>; NUL cannot appear in file paths, so content or
    /// path colons never confuse the parser (issue #1436). This pattern is built from arbitrary
    /// captured output, so it is compiled once and reused.
    /// </summary>
    private static readonly Regex MatchLineRegex =
        new(@"^([^\x00]+)\x00(\d+)([:-])(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Which arm of a short flag cluster was parsed.</summary>
    internal enum ClusterKind
    {
        /// <summary>All chars were boolean flags or <c>r</c>/<c>R</c> (stripped).</summary>
        Boolean,

        /// <summary>A value-taking flag was encountered; scanning stopped there.</summary>
        ValueTaking
    }

    /// <summary>
    /// Result of parsing the content of a short flag cluster (the part after <c>-</c>). Mirrors
    /// grep_cmd.rs's <c>ClusterResult</c> enum.
    /// </summary>
    /// <param name="Kind">Whether the cluster reduced to boolean flags or hit a value-taking flag.</param>
    /// <param name="Prefix">
    /// Boolean flags accumulated before any value-taking char, with <c>r</c>/<c>R</c> stripped;
    /// <c>null</c> when nothing remains.
    /// </param>
    /// <param name="Flag">The value-taking flag char (only meaningful for <see cref="ClusterKind.ValueTaking"/>).</param>
    /// <param name="Inline">
    /// Bytes after <paramref name="Flag"/> in the cluster — its inline value, returned verbatim.
    /// Empty means "consume the next token instead."
    /// </param>
    internal readonly record struct ClusterResult(ClusterKind Kind, string? Prefix, char Flag, string Inline)
    {
        /// <summary>Creates a boolean-cluster result.</summary>
        /// <param name="prefix">The stripped boolean flag letters, or null.</param>
        /// <returns>A <see cref="ClusterKind.Boolean"/> result.</returns>
        public static ClusterResult Boolean(string? prefix) => new(ClusterKind.Boolean, prefix, '\0', string.Empty);

        /// <summary>Creates a value-taking cluster result.</summary>
        /// <param name="prefix">The stripped boolean prefix, or null.</param>
        /// <param name="flag">The value-taking flag char.</param>
        /// <param name="inline">The inline value (empty to consume the next token).</param>
        /// <returns>A <see cref="ClusterKind.ValueTaking"/> result.</returns>
        public static ClusterResult ValueTaking(string? prefix, char flag, string inline) =>
            new(ClusterKind.ValueTaking, prefix, flag, inline);
    }

    /// <summary>Extracted <c>(patterns, paths, flags)</c> from the trailing argument vector.</summary>
    /// <param name="Patterns">The positional pattern plus all <c>-e</c>/<c>--regexp</c> values.</param>
    /// <param name="Paths">The subsequent non-flag positionals (empty → caller defaults to <c>.</c>).</param>
    /// <param name="Flags">Other flags forwarded to rg (<c>-r</c>/<c>-R</c>/<c>--recursive</c> stripped).</param>
    internal sealed record ExtractResult(List<string> Patterns, List<string> Paths, List<string> Flags);

    /// <summary>
    /// Registry entry point. Parses the rtk-level options off the front of <paramref name="args"/>
    /// (mirroring clap in <c>main.rs</c>), then runs the ripgrep-backed filter against the remaining
    /// trailing arguments, printing to the console and returning the underlying tool's exit code.
    /// </summary>
    /// <param name="args">The arguments following the <c>grep</c> verb.</param>
    /// <returns>The child process's exit code (0 = matches, 1 = no match, ≥2 = error).</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var maxLen = DefaultMaxLen;
        var maxResults = DefaultMaxResults;
        var contextOnly = false;
        string? fileType = null;

        var i = 0;
        while (i < args.Length)
        {
            var a = args[i];

            if (a == "--context-only")
            {
                contextOnly = true;
                i++;
                continue;
            }

            if (TryTakeUsizeOption(args, ref i, a, "-l", "--max-len", out var lenVal))
            {
                maxLen = lenVal;
                continue;
            }

            if (TryTakeUsizeOption(args, ref i, a, "-m", "--max", out var maxVal))
            {
                maxResults = maxVal;
                continue;
            }

            if (TryTakeStringOption(args, ref i, a, "-t", "--file-type", out var ftVal))
            {
                fileType = ftVal;
                continue;
            }

            // First token that is not an rtk-level option: everything from here is trailing args.
            break;
        }

        var extraArgs = args.Skip(i).ToList();

        return RunAsync(maxLen, maxResults, contextOnly, fileType, extraArgs, Console.Out, Console.Error);
    }

    /// <summary>
    /// Executes the ripgrep-backed grep filter. Direct port of grep_cmd.rs's <c>run</c> function,
    /// writing to <paramref name="stdout"/>/<paramref name="stderr"/> (injectable for testing) and
    /// returning the underlying tool's exit code.
    /// </summary>
    /// <param name="maxLen">Maximum displayed line length.</param>
    /// <param name="maxResults">Maximum number of displayed result lines.</param>
    /// <param name="contextOnly">When true, show only the match context, not the full line.</param>
    /// <param name="fileType">Optional rg file-type filter (e.g. <c>rust</c>), or null.</param>
    /// <param name="args">The trailing arguments (pattern, paths, and grep/rg flags).</param>
    /// <param name="stdout">The destination for filtered output.</param>
    /// <param name="stderr">The destination for warnings and errors.</param>
    /// <param name="executor">The process executor to run rg/grep with, or null for the default.</param>
    /// <returns>The child process's exit code.</returns>
    internal static async Task<int> RunAsync(
        int maxLen,
        int maxResults,
        bool contextOnly,
        string? fileType,
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        IProcessExecutor? executor = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var exec = executor ?? new ProcessExecutor();

        // --version / --help: pass through to rg (then grep) without filtering.
        if (args.Any(a => a is "--version" or "--help" or "-h"))
        {
            var vResult = await ExecCaptureAsync(exec, "rg", args).ConfigureAwait(false);
            if (!vResult.WasStarted)
            {
                vResult = await ExecCaptureAsync(exec, "grep", args).ConfigureAwait(false);
            }

            stdout.Write(vResult.Stdout);
            if (vResult.Stderr.Length > 0)
            {
                stderr.Write(vResult.Stderr);
            }

            return vResult.ExitCode;
        }

        var extracted = ExtractPatternPath(args);
        var patterns = extracted.Patterns;

        if (patterns.Count == 0)
        {
            stderr.Write("rtk grep: pattern required (positional or -e)\n");
            return 1;
        }

        var patternDisplay = patterns.Count == 1 ? patterns[0] : string.Join("|", patterns);

        var paths = extracted.Paths.Count == 0 ? new List<string> { "." } : extracted.Paths;
        var pathDisplay = string.Join(" ", paths);
        var extraFlags = extracted.Flags;

        // format/shape flags: native passthrough, no -0 NUL leak (#2333).
        if (HasFormatFlag(extraFlags) || HasShapeFlag(extraFlags))
        {
            return await PassthroughAsync(
                exec,
                new[] { "--no-heading", "--no-ignore-vcs" },
                new[] { "-r" },
                fileType,
                extraFlags,
                patterns,
                paths,
                stdout,
                stderr).ConfigureAwait(false);
        }

        // GROUP path: -0 NUL-disambiguates file:line for the reparse (#1436).
        var result = await GrepCaptureAsync(
            exec,
            new[] { "-nH0", "--no-heading", "--no-ignore-vcs" },
            new[] { "-rnH", "--null" },
            fileType,
            extraFlags,
            patterns,
            paths).ConfigureAwait(false);

        var exitCode = result.ExitCode;
        var rawOutput = result.Stdout;

        if (rawOutput.Trim().Length == 0)
        {
            if (IsGrepErrorExit(exitCode))
            {
                var stderrTrimmed = result.Stderr.Trim();
                if (stderrTrimmed.Length > 0)
                {
                    stderr.Write(stderrTrimmed + "\n");
                }

                stderr.Write($"grep failed with exit code {exitCode}\n");
                return exitCode;
            }

            stdout.Write($"0 matches for '{patternDisplay}'\n");
            return exitCode;
        }

        // Safety net: unparseable shape → passthrough verbatim, never silently drop (#2333).
        if (UnparsedSignal(rawOutput) > 0)
        {
            return await PassthroughAsync(
                exec,
                new[] { "-nH", "--no-heading", "--no-ignore-vcs" },
                new[] { "-rnH" },
                fileType,
                extraFlags,
                patterns,
                paths,
                stdout,
                stderr).ConfigureAwait(false);
        }

        // Mandatory fallback: grouping/formatting must never crash or hide output from the user.
        string output;
        try
        {
            output = BuildGroupedOutput(rawOutput, patternDisplay, maxLen, maxResults, contextOnly);
        }
        catch (Exception ex)
        {
            stderr.Write($"rtk: filter warning: {ex.Message}\n");
            output = rawOutput.Replace('\0', ':');
        }

        stdout.Write(output);
        return exitCode;
    }

    /// <summary>
    /// Builds the grouped, capped, truncated output block from raw NUL-separated rg/grep output.
    /// Mirrors the grouping half of grep_cmd.rs's <c>run</c>, including the "never-worse" guard
    /// that falls back to the plain <c>file:line:content</c> rendering when grouping did not shrink
    /// the output.
    /// </summary>
    /// <param name="rawOutput">Raw NUL-separated output from rg/grep.</param>
    /// <param name="patternDisplay">The display form of the pattern(s).</param>
    /// <param name="maxLen">Maximum displayed line length.</param>
    /// <param name="maxResults">Maximum number of displayed result lines.</param>
    /// <param name="contextOnly">When true, show only the match context.</param>
    /// <returns>The rendered output block (ending in a newline).</returns>
    internal static string BuildGroupedOutput(
        string rawOutput, string patternDisplay, int maxLen, int maxResults, bool contextOnly)
    {
        Regex? contextRe = null;
        if (contextOnly)
        {
            try
            {
                contextRe = new Regex(
                    $".{{0,20}}{Regex.Escape(patternDisplay)}.*",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                contextRe = null;
            }
        }

        // Insertion order preserved to mirror Rust's HashMap.len() (file count) and per-file order.
        var byFile = new Dictionary<string, List<(int LineNum, bool IsMatch, string Content)>>(StringComparer.Ordinal);
        foreach (var line in ReadCommand.SplitLines(rawOutput))
        {
            var parsed = ParseMatchLine(line);
            if (parsed is not { } entry)
            {
                continue;
            }

            var cleaned = CleanLine(entry.Content, maxLen, contextRe, patternDisplay);
            if (!byFile.TryGetValue(entry.File, out var bucket))
            {
                bucket = [];
                byFile[entry.File] = bucket;
            }

            bucket.Add((entry.LineNum, entry.IsMatch, cleaned));
        }

        var totalMatches = byFile.Values.SelectMany(v => v).Count(e => e.IsMatch);

        var sb = new StringBuilder();
        sb.Append($"{totalMatches} matches in {byFile.Count} files:\n\n");

        var shown = 0;
        var files = byFile.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        foreach (var (file, entries) in files)
        {
            if (shown >= maxResults)
            {
                break;
            }

            var fileDisplay = CompactPath(file);
            foreach (var (lineNum, isMatch, content) in entries.Take(GrepMaxPerFile))
            {
                if (shown >= maxResults)
                {
                    break;
                }

                sb.Append(isMatch
                    ? $"{fileDisplay}:{lineNum}:{content}\n"
                    : $"{fileDisplay}-{lineNum}-{content}\n");
                shown++;
            }
        }

        var totalLines = byFile.Values.Sum(v => v.Count);
        if (totalLines > shown)
        {
            sb.Append($"[+{totalLines - shown} more]\n");
        }

        var rtkOutput = sb.ToString();

        // Never-worse: show plain `file:line:content` (NUL -> `:`) if grouping didn't shrink it.
        var plain = rawOutput.Replace('\0', ':');
        return Utf8Len(rtkOutput) < Utf8Len(plain) ? rtkOutput : plain;
    }

    /// <summary>
    /// Parse the content of a short flag cluster (everything after the leading <c>-</c>). Scans
    /// left-to-right: strips <c>r</c>/<c>R</c>, accumulates boolean flag letters, and stops at the
    /// first value-taking flag (from <see cref="ValueFlagsShort"/> or <c>e</c>). Everything after
    /// that flag char is its inline value, returned verbatim (no <c>r</c>/<c>R</c> stripping).
    /// Mirrors grep_cmd.rs's <c>parse_cluster</c>.
    /// </summary>
    /// <param name="rest">The cluster content (the part after <c>-</c>).</param>
    /// <returns>The parsed <see cref="ClusterResult"/>.</returns>
    internal static ClusterResult ParseCluster(string rest)
    {
        ArgumentNullException.ThrowIfNull(rest);

        var rawPrefix = new StringBuilder();
        for (var j = 0; j < rest.Length; j++)
        {
            var ch = rest[j];
            if (ch == 'e' || ValueFlagsShort.IndexOf(ch) >= 0)
            {
                var prefix = StripR(rawPrefix.ToString());
                var inline = rest[(j + 1)..];
                return ClusterResult.ValueTaking(prefix, ch, inline);
            }

            rawPrefix.Append(ch);
        }

        return ClusterResult.Boolean(StripR(rawPrefix.ToString()));
    }

    /// <summary>
    /// Strips <c>r</c>/<c>R</c> from a string of flag letters, returning null when nothing remains.
    /// Only ever called on accumulated flag letters (never on inline values). Mirrors
    /// grep_cmd.rs's <c>strip_r</c>.
    /// </summary>
    /// <param name="flagLetters">The accumulated flag letters.</param>
    /// <returns>The stripped letters, or null when empty.</returns>
    internal static string? StripR(string flagLetters)
    {
        ArgumentNullException.ThrowIfNull(flagLetters);
        var s = new string(flagLetters.Where(c => c != 'r' && c != 'R').ToArray());
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Drops <c>--recursive</c> (a grep-ism); passes all other long flags through unchanged.
    /// Mirrors grep_cmd.rs's <c>strip_recursive</c>.
    /// </summary>
    /// <param name="arg">The long flag.</param>
    /// <returns>The flag, or null when it is <c>--recursive</c>.</returns>
    internal static string? StripRecursive(string arg) => arg == "--recursive" ? null : arg;

    /// <summary>
    /// Removes ripgrep-only flags (and their values) so the grep fallback does not abort on an
    /// unknown option (issue #2167). Mirrors grep_cmd.rs's <c>strip_rg_only</c>.
    /// </summary>
    /// <param name="extraArgs">The forwarded flags.</param>
    /// <returns>The flags with rg-only entries removed.</returns>
    internal static List<string> StripRgOnly(IReadOnlyList<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        var outArgs = new List<string>();
        var skipNext = false;
        foreach (var a in extraArgs)
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            var name = a.Split('=', 2)[0];
            var rgOnly = RgOnlyLong.Contains(name) || a == "-g" || a == "-T";
            if (rgOnly)
            {
                var takesValue = a == "-g" || a == "-T" || ValueFlagsLong.Contains(name);
                if (takesValue && !a.Contains('='))
                {
                    skipNext = true;
                }

                continue;
            }

            outArgs.Add(a);
        }

        return outArgs;
    }

    /// <summary>
    /// Reports whether any argument is a shape flag (<c>--column</c>, <c>--vimgrep</c>, <c>-b</c>,
    /// <c>--byte-offset</c>, <c>--null-data</c>) that must bypass grouping. Mirrors grep_cmd.rs's
    /// <c>has_shape_flag</c>.
    /// </summary>
    /// <param name="extraArgs">The forwarded flags.</param>
    /// <returns>True when a shape flag is present.</returns>
    internal static bool HasShapeFlag(IReadOnlyList<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return extraArgs.Any(a =>
        {
            var name = a.Split('=', 2)[0];
            return name is "--column" or "--vimgrep" or "-b" or "--byte-offset" or "--null-data";
        });
    }

    /// <summary>
    /// Reports whether any argument is a format flag (count, files-with/without-matches,
    /// only-matching, null, json, passthru, files) that must bypass grouping. Mirrors
    /// grep_cmd.rs's <c>has_format_flag</c>.
    /// </summary>
    /// <param name="extraArgs">The forwarded flags.</param>
    /// <returns>True when a format flag is present.</returns>
    internal static bool HasFormatFlag(IReadOnlyList<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return extraArgs.Any(a => a is
            "-c" or "--count" or "--count-matches" or "-l" or "--files-with-matches" or "-L"
            or "--files-without-match" or "-o" or "--only-matching" or "-Z" or "--null" or "--json"
            or "--passthru" or "--files");
    }

    /// <summary>
    /// Extracts <c>(patterns, paths, flags)</c> from the raw trailing args. Short clusters are
    /// scanned left-to-right; the first value-taking letter terminates the cluster (everything
    /// after it is its inline value). Long value-taking flags consume the next token. <c>--</c>
    /// marks everything after it as positional. Mirrors grep_cmd.rs's <c>extract_pattern_path</c>.
    /// </summary>
    /// <param name="args">The trailing argument vector.</param>
    /// <returns>The extracted patterns, paths, and flags.</returns>
    internal static ExtractResult ExtractPatternPath(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var ePatterns = new List<string>();
        var positionals = new List<string>();
        var flags = new List<string>();
        var pastDashDash = false;
        var i = 0;

        while (i < args.Count)
        {
            var arg = args[i];

            if (pastDashDash)
            {
                positionals.Add(arg);
                i++;
                continue;
            }

            if (arg == "--")
            {
                pastDashDash = true;
                i++;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                // --regexp is the long form of -e: value goes to patterns.
                if (arg == "--regexp")
                {
                    if (i + 1 < args.Count)
                    {
                        ePatterns.Add(args[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    continue;
                }

                // Other long value-taking flags: consume next token as value.
                if (ValueFlagsLong.Contains(arg))
                {
                    flags.Add(arg);
                    if (i + 1 < args.Count)
                    {
                        flags.Add(args[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    continue;
                }

                // Drop --recursive; pass everything else through.
                var cleaned = StripRecursive(arg);
                if (cleaned is not null)
                {
                    flags.Add(cleaned);
                }

                i++;
                continue;
            }

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                var cluster = ParseCluster(arg[1..]);
                if (cluster.Kind == ClusterKind.Boolean)
                {
                    if (cluster.Prefix is { } boolPrefix)
                    {
                        flags.Add($"-{boolPrefix}");
                    }

                    i++;
                }
                else
                {
                    if (cluster.Prefix is { } vtPrefix)
                    {
                        flags.Add($"-{vtPrefix}");
                    }

                    if (cluster.Flag == 'e')
                    {
                        if (cluster.Inline.Length > 0)
                        {
                            ePatterns.Add(cluster.Inline);
                            i++;
                        }
                        else if (i + 1 < args.Count)
                        {
                            ePatterns.Add(args[i + 1]);
                            i += 2;
                        }
                        else
                        {
                            flags.Add("-e");
                            i++;
                        }
                    }
                    else
                    {
                        flags.Add($"-{cluster.Flag}");
                        if (cluster.Inline.Length > 0)
                        {
                            flags.Add(cluster.Inline);
                            i++;
                        }
                        else if (i + 1 < args.Count)
                        {
                            flags.Add(args[i + 1]);
                            i += 2;
                        }
                        else
                        {
                            i++;
                        }
                    }
                }

                continue;
            }

            // Bare positional (including a lone "-").
            positionals.Add(arg);
            i++;
        }

        // If -e/--regexp was used: all positionals are paths.
        // Otherwise: first positional is the pattern, rest are paths.
        List<string> resultPatterns;
        List<string> resultPaths;
        if (ePatterns.Count > 0)
        {
            resultPatterns = ePatterns;
            resultPaths = positionals;
        }
        else
        {
            resultPatterns = positionals.Take(1).ToList();
            resultPaths = positionals.Skip(1).ToList();
        }

        return new ExtractResult(resultPatterns, resultPaths, flags);
    }

    /// <summary>
    /// Counts lines that do not parse as NUL-separated match/context lines (and are not blank or a
    /// bare <c>--</c> context separator) — a nonzero count signals an output shape grouping cannot
    /// handle. Mirrors grep_cmd.rs's <c>unparsed_signal</c>.
    /// </summary>
    /// <param name="stdout">The raw output to inspect.</param>
    /// <returns>The number of unparseable lines.</returns>
    internal static int UnparsedSignal(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        return ReadCommand.SplitLines(stdout).Count(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length != 0 && trimmed != "--" && ParseMatchLine(line) is null;
        });
    }

    /// <summary>
    /// Parses a single rg/grep match or context line of the form <c>file\0line[:-]content</c>.
    /// Returns null for lines that do not match the expected shape. The bool is true for match
    /// lines (<c>:</c> separator) and false for context lines (<c>-</c> separator). Mirrors
    /// grep_cmd.rs's <c>parse_match_line</c>.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <returns>The parsed components, or null.</returns>
    internal static (string File, int LineNum, bool IsMatch, string Content)? ParseMatchLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var m = MatchLineRegex.Match(line);
        if (!m.Success)
        {
            return null;
        }

        if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var lineNum))
        {
            return null;
        }

        var file = m.Groups[1].Value;
        var isMatch = m.Groups[3].Value == ":";
        var content = m.Groups[4].Value;
        return (file, lineNum, isMatch, content);
    }

    /// <summary>
    /// Trims and, when necessary, truncates a match line around the pattern so it fits within
    /// <paramref name="maxLen"/>. When <paramref name="contextRe"/> is provided and matches, a
    /// short context window is returned instead. Mirrors grep_cmd.rs's <c>clean_line</c>. Slicing
    /// operates on Unicode scalar values (matching Rust's <c>chars()</c>) so multibyte input is
    /// never split mid-character.
    /// </summary>
    /// <param name="line">The raw line content.</param>
    /// <param name="maxLen">The maximum length budget.</param>
    /// <param name="contextRe">Optional context-window regex, or null.</param>
    /// <param name="pattern">The search pattern (used to center truncation).</param>
    /// <returns>The cleaned line.</returns>
    internal static string CleanLine(string line, int maxLen, Regex? contextRe, string pattern)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.Trim();

        if (contextRe is not null)
        {
            var m = contextRe.Match(trimmed);
            if (m.Success && Utf8Len(m.Value) <= maxLen)
            {
                return m.Value;
            }
        }

        if (Utf8Len(trimmed) <= maxLen)
        {
            return trimmed;
        }

        var lower = trimmed.ToLowerInvariant();
        var patternLower = pattern.ToLowerInvariant();
        var pos = lower.IndexOf(patternLower, StringComparison.Ordinal);

        var chars = trimmed.EnumerateRunes().ToList();
        var charLen = chars.Count;

        if (pos >= 0)
        {
            var charPos = lower[..pos].EnumerateRunes().Count();

            var start = Math.Max(0, charPos - maxLen / 3);
            var end = Math.Min(start + maxLen, charLen);
            start = end == charLen ? Math.Max(0, end - maxLen) : start;

            var slice = RunesToString(chars, start, end);
            if (start > 0 && end < charLen)
            {
                return $"...{slice}...";
            }

            return start > 0 ? $"...{slice}" : $"{slice}...";
        }

        var truncated = RunesToString(chars, 0, Math.Min(maxLen - 3, charLen));
        return $"{truncated}...";
    }

    /// <summary>
    /// Compacts a long <c>/</c>-separated path to <c>first/.../parent/name</c>. Paths at or under
    /// 50 bytes, or with three or fewer segments, are returned unchanged. Mirrors grep_cmd.rs's
    /// <c>compact_path</c>.
    /// </summary>
    /// <param name="path">The path to compact.</param>
    /// <returns>The compacted (or original) path.</returns>
    internal static string CompactPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Utf8Len(path) <= 50)
        {
            return path;
        }

        var parts = path.Split('/');
        if (parts.Length <= 3)
        {
            return path;
        }

        return $"{parts[0]}/.../{parts[^2]}/{parts[^1]}";
    }

    /// <summary>
    /// grep/rg convention: exit 1 = no match (normal), exit ≥ 2 = a real error (bad regex, tool
    /// crash, missing binary). Mirrors grep_cmd.rs's <c>is_grep_error_exit</c>.
    /// </summary>
    /// <param name="exitCode">The process exit code.</param>
    /// <returns>True when the exit code indicates a real error.</returns>
    internal static bool IsGrepErrorExit(int exitCode) => exitCode >= 2;

    /// <summary>
    /// Runs rg with the given base flags; falls back to system grep when rg cannot run or rejects
    /// the invocation (exit 2, no output), and retries grep bare if it aborts on an rg-only flag
    /// that <see cref="StripRgOnly"/> missed. Mirrors grep_cmd.rs's <c>grep_capture</c>.
    /// </summary>
    private static async Task<ExecutionResult> GrepCaptureAsync(
        IProcessExecutor exec,
        IReadOnlyList<string> rgBase,
        IReadOnlyList<string> grepBase,
        string? fileType,
        IReadOnlyList<string> extraArgs,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> paths)
    {
        var rgArgs = new List<string>(rgBase);
        if (fileType is not null)
        {
            rgArgs.Add("--type");
            rgArgs.Add(fileType);
        }

        rgArgs.AddRange(extraArgs);
        foreach (var p in patterns)
        {
            rgArgs.Add("-e");
            rgArgs.Add(p.Replace(@"\|", "|"));
        }

        rgArgs.Add("--");
        rgArgs.AddRange(paths);

        var rgResult = await ExecCaptureAsync(exec, "rg", rgArgs).ConfigureAwait(false);
        if (rgResult.WasStarted && !(rgResult.ExitCode == 2 && rgResult.Stdout.Length == 0))
        {
            return rgResult;
        }

        // rg unavailable or rejected the invocation: fall back to system grep (#2543).
        var stripped = StripRgOnly(extraArgs);
        var grepResult = await RunGrepFallbackAsync(exec, grepBase, stripped, patterns, paths).ConfigureAwait(false);
        if (grepResult.ExitCode == 2 && grepResult.Stdout.Length == 0 && grepResult.Stderr.Contains("option"))
        {
            // grep aborted on an rg-only flag strip_rg_only missed; retry bare (#2167).
            return await RunGrepFallbackAsync(exec, grepBase, Array.Empty<string>(), patterns, paths)
                .ConfigureAwait(false);
        }

        return grepResult;
    }

    /// <summary>
    /// Runs system grep with the given base flags, forwarded args, patterns, and paths. The rg
    /// file-type filter cannot be translated to grep syntax and is silently skipped. Mirrors
    /// grep_cmd.rs's <c>run_grep_fallback</c>.
    /// </summary>
    private static Task<ExecutionResult> RunGrepFallbackAsync(
        IProcessExecutor exec,
        IReadOnlyList<string> grepBase,
        IReadOnlyList<string> extraArgs,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> paths)
    {
        var grepArgs = new List<string>(grepBase);
        grepArgs.AddRange(extraArgs);
        foreach (var p in patterns)
        {
            grepArgs.Add("-e");
            grepArgs.Add(p);
        }

        grepArgs.Add("--");
        grepArgs.AddRange(paths);

        return ExecCaptureAsync(exec, "grep", grepArgs);
    }

    /// <summary>
    /// Native passthrough for format/shape modes and the unparseable-output safety net: runs
    /// rg/grep and prints its (ANSI-stripped) stdout and any stderr verbatim. Mirrors grep_cmd.rs's
    /// <c>passthrough</c>.
    /// </summary>
    private static async Task<int> PassthroughAsync(
        IProcessExecutor exec,
        IReadOnlyList<string> rgBase,
        IReadOnlyList<string> grepBase,
        string? fileType,
        IReadOnlyList<string> extraArgs,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> paths,
        TextWriter stdout,
        TextWriter stderr)
    {
        var result = await GrepCaptureAsync(exec, rgBase, grepBase, fileType, extraArgs, patterns, paths)
            .ConfigureAwait(false);

        stdout.Write(Utils.StripAnsi(result.Stdout));
        if (result.Stderr.Length > 0)
        {
            stderr.Write(result.Stderr.Trim());
        }

        return result.ExitCode;
    }

    /// <summary>Runs a capture of <paramref name="fileName"/> with <paramref name="args"/>.</summary>
    private static async Task<ExecutionResult> ExecCaptureAsync(
        IProcessExecutor exec, string fileName, IReadOnlyList<string> args)
    {
        var request = new ExecutionRequest(fileName, args, CaptureMode: ExecutionCaptureMode.Separate);
        return await exec.ExecuteAsync(request).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognizes an rtk-level option that consumes a non-negative integer value. Consistent with
    /// the oracle, the following token is only consumed as the value when it parses as a
    /// non-negative integer; otherwise the flag is left for the trailing args (returns false
    /// without advancing).
    /// </summary>
    private static bool TryTakeUsizeOption(
        string[] args, ref int i, string arg, string shortName, string longName, out int value)
    {
        value = 0;

        // Space-separated form: -l 5 / --max-len 5.
        if (arg == shortName || arg == longName)
        {
            if (i + 1 < args.Length &&
                int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                i += 2;
                return true;
            }

            return false;
        }

        // Inline form: --max-len=5 / -l5.
        if (arg.StartsWith(longName + "=", StringComparison.Ordinal))
        {
            var candidate = arg[(longName.Length + 1)..];
            if (int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                i++;
                return true;
            }

            return false;
        }

        if (arg.StartsWith(shortName, StringComparison.Ordinal) && arg.Length > shortName.Length)
        {
            var candidate = arg[shortName.Length..];
            if (int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                i++;
                return true;
            }
        }

        return false;
    }

    /// <summary>Recognizes an rtk-level option that consumes a free-form string value.</summary>
    private static bool TryTakeStringOption(
        string[] args, ref int i, string arg, string shortName, string longName, out string value)
    {
        value = string.Empty;

        if (arg == shortName || arg == longName)
        {
            if (i + 1 < args.Length)
            {
                value = args[i + 1];
                i += 2;
                return true;
            }

            // Flag with no value: consume the flag alone (clap would error; treat as no value).
            i++;
            return true;
        }

        if (arg.StartsWith(longName + "=", StringComparison.Ordinal))
        {
            value = arg[(longName.Length + 1)..];
            i++;
            return true;
        }

        if (arg.StartsWith(shortName, StringComparison.Ordinal) && arg.Length > shortName.Length)
        {
            value = arg[shortName.Length..];
            i++;
            return true;
        }

        return false;
    }

    /// <summary>Concatenates runes <c>[start, end)</c> into a string.</summary>
    private static string RunesToString(IReadOnlyList<Rune> runes, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            sb.Append(runes[i].ToString());
        }

        return sb.ToString();
    }

    /// <summary>Returns the UTF-8 byte length of <paramref name="s"/> (matching Rust's <c>str::len()</c>).</summary>
    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);
}
