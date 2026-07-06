using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Parser;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Which pnpm subcommand an invocation resolved to. Mirrors Rust's <c>PnpmCommands</c> subcommand
/// enum (<c>main.rs</c>:877-908).
/// </summary>
public enum PnpmVerb
{
    /// <summary><c>pnpm list</c> — ultra-dense dependency listing.</summary>
    List,

    /// <summary><c>pnpm outdated</c> — condensed current/latest comparison.</summary>
    Outdated,

    /// <summary><c>pnpm install</c> — progress-bar-stripped install log.</summary>
    Install,

    /// <summary><c>pnpm typecheck</c> — pure alias delegating to the tsc filter.</summary>
    Typecheck,

    /// <summary>Any unrecognized pnpm subcommand — raw passthrough.</summary>
    Other,
}

/// <summary>
/// The resolved parse of a <c>rtk pnpm ...</c> invocation: the global <c>--filter</c>/<c>-F</c>
/// values (which precede the subcommand token, exactly like Rust's <c>Pnpm {{ filter: Vec&lt;String&gt;,
/// command: PnpmCommands }}</c> clap shape, <c>main.rs</c>:205-212), which subcommand was resolved, the
/// resolved <c>--depth</c> for <c>list</c>, and the subcommand's own trailing arguments.
/// </summary>
/// <param name="Filters">The <c>--filter</c>/<c>-F</c> values, in the order they appeared.</param>
/// <param name="Verb">The resolved subcommand.</param>
/// <param name="Depth">
/// The <c>--depth</c>/<c>-d</c> value for <see cref="PnpmVerb.List"/> (default <c>0</c>, matching
/// clap's <c>default_value = "0"</c>); unused for other verbs.
/// </param>
/// <param name="Args">
/// The subcommand's own trailing arguments (for <see cref="PnpmVerb.List"/>, with any
/// <c>--depth</c>/<c>-d</c> flag already extracted); empty for <see cref="PnpmVerb.Other"/>.
/// </param>
/// <param name="PassthroughArgs">
/// For <see cref="PnpmVerb.Other"/>, the full remaining argument vector (including the unrecognized
/// subcommand token itself) to hand to <c>pnpm</c> unchanged; empty for all other verbs.
/// </param>
internal readonly record struct PnpmInvocation(
    string[] Filters, PnpmVerb Verb, int Depth, string[] Args, string[] PassthroughArgs);

/// <summary>
/// Implements the <c>rtk pnpm</c> CLI verb: <c>list</c>/<c>outdated</c>/<c>install</c>/
/// <c>typecheck</c>/passthrough. Faithful port of <c>src/cmds/js/pnpm_cmd.rs</c> plus the
/// <c>Commands::Pnpm</c> dispatch arm and <c>validate_pnpm_filters</c>/<c>merge_pnpm_args</c> helpers
/// living inline in <c>main.rs</c> (<c>main.rs</c>:1383-1422, 1699-1726).
/// </summary>
/// <remarks>
/// <para>
/// <b>Manual <see cref="TimedExecution"/> calls, not the <see cref="CommandRunner"/> skeleton.</b>
/// Unlike <c>npm</c>/<c>npx</c>/the future <c>tsc</c> port (Phase 8 Tasks 2/4), Rust's own
/// <c>pnpm_cmd.rs</c> calls <c>tracking::TimedExecution::start()</c>/<c>.track(...)</c> directly inside
/// each of <c>run_list</c>/<c>run_outdated</c>/<c>run_install</c>, rather than routing through the
/// shared <c>core::runner</c> skeleton. This port matches that per-function manual-tracking pattern
/// exactly, per the phase plan's explicit note that both wiring patterns coexist in the Rust source and
/// must be preserved per-module rather than unified.
/// </para>
/// <para>
/// <b><c>pnpm list</c> bypasses the shared <see cref="DependencyState"/> formatter.</b>
/// <see cref="FormatDependencyListing"/> is a bespoke prod/dev-grouped renderer
/// (<c>format_dependency_listing</c>, <c>pnpm_cmd.rs</c>:292-347) used only by <c>list</c>; <c>outdated</c>
/// uses the shared <see cref="ITokenFormatter"/>-based <see cref="DependencyState.Format"/> instead. This
/// Rust-source inconsistency is preserved deliberately, not "fixed" toward uniformity (phase plan Global
/// Constraints).
/// </para>
/// <para>
/// <b>The cap on <c>list</c> only applies when unfiltered.</b> <c>run_list</c>
/// (<c>pnpm_cmd.rs</c>:383-385) computes <c>is_filtered</c> by checking whether any of the raw args is
/// exactly <c>--prod</c>/<c>-P</c>/<c>--dev</c>/<c>-D</c>, then passes <c>cap = !is_filtered</c> to
/// <see cref="FormatDependencyListing"/> — an explicitly-scoped invocation (the user already narrowed
/// the listing themselves) is never capped, unlike a plain <c>pnpm list</c>.
/// </para>
/// <para>
/// <b><c>pnpm outdated</c> always returns exit code 0.</b> <c>run_outdated</c> (<c>pnpm_cmd.rs</c>:420-467)
/// never inspects <c>result.success()</c> and unconditionally returns <c>Ok(0)</c> — pnpm's own
/// <c>outdated</c> subcommand exits non-zero whenever outdated packages exist, which Rust deliberately
/// papers over so the presence of outdated packages never looks like an rtk-level failure. Ported
/// exactly: <see cref="RunOutdatedAsync"/> ignores the child's exit code entirely.
/// </para>
/// <para>
/// <b>Typecheck: a pure alias, now delegating to <see cref="TscCommand"/>.</b> Rust's
/// <c>PnpmCommands::Typecheck</c> arm (<c>main.rs</c>:1721) is zero pnpm-specific logic — it calls
/// straight into <c>tsc_cmd::run(&amp;args, cli.verbose)</c> with the subcommand's own (unmerged) args,
/// deliberately NOT the <c>--filter</c>-merged args every other verb here uses (<see cref="MergeFilters"/>
/// is only applied to <see cref="PnpmVerb.List"/>/<see cref="PnpmVerb.Outdated"/>/
/// <see cref="PnpmVerb.Install"/>/<see cref="PnpmVerb.Other"/>). Now that Phase 8 Task 4 has landed
/// <see cref="TscCommand"/>, this delegates to <see cref="TscCommand.RunTscSafeAsync"/> with
/// <c>invocation.Args</c> unmerged, exactly matching Rust's call site. The <c>--filter</c>+<c>typecheck</c>
/// warning (<see cref="ValidatePnpmFilters"/>) still fires unconditionally before dispatch, exactly as
/// Rust's <c>validate_pnpm_filters</c> call happens before the subcommand match regardless of whether
/// that subcommand's own execution succeeds (<c>main.rs</c>:1701-1703) — filters preceding
/// <c>typecheck</c> are warned about and ignored, never silently applied.
/// </para>
/// </remarks>
public static partial class PnpmCommand
{
    /// <summary>
    /// Maximum dependencies shown per <c>[prod]</c>/<c>[dev]</c> section in
    /// <see cref="FormatDependencyListing"/> before a truncation marker + tee hint. Bound to Rust's
    /// <c>CAP_LIST</c> (<c>src/core/truncate.rs</c>:9, = 20), matching <c>MAX_LISTING</c>
    /// (<c>pnpm_cmd.rs</c>:17).
    /// </summary>
    internal const int MaxListing = 20;

    /// <summary>
    /// Registry entry point for the <c>pnpm</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/>
    /// (the registry delegate cannot receive it as an argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>pnpm</c> verb.</param>
    /// <returns>The resolved subcommand's exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunPnpmSafeAsync(args, RuntimeOptions.Verbosity, executor: null);

    /// <summary>
    /// Test-friendly overload of the <c>pnpm</c> entry point taking an explicit verbosity value and an
    /// injectable <see cref="IProcessExecutor"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>pnpm</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <param name="executor">The process executor to run the child <c>pnpm</c> process with, or null for the default.</param>
    /// <returns>The resolved subcommand's exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunPnpmSafeAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await DispatchAsync(args, verbose, executor).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Fail-loud, same convention as NpmCommand: an rtk-level failure surfaces as `rtk: {message}`.
            // Guarded proactively, not thrown from this command today — see DockerCommand/
            // PrismaCommand's remarks for the swallowed-exception bug this prevents.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Dispatches a parsed <c>pnpm</c> invocation to its resolved subcommand. Ports the body of Rust's
    /// <c>Commands::Pnpm</c> arm (<c>main.rs</c>:1699-1726): emits the <c>validate_pnpm_filters</c>
    /// warning if applicable, merges <c>--filter</c> values in front of each subcommand's own args
    /// (<c>merge_pnpm_args</c>, <c>main.rs</c>:1384-1390), then executes.
    /// </summary>
    /// <param name="args">The arguments following the <c>pnpm</c> verb.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor for the resolved subcommand, or null for the default.</param>
    /// <returns>The resolved subcommand's exit code.</returns>
    internal static Task<int> DispatchAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        var invocation = ParseInvocation(args);

        var warning = ValidatePnpmFilters(invocation.Filters, invocation.Verb);
        if (warning is not null)
        {
            Console.Error.Write(warning + "\n");
        }

        return invocation.Verb switch
        {
            PnpmVerb.List => RunListAsync(invocation.Depth, MergeFilters(invocation.Filters, invocation.Args), verbose, executor),
            PnpmVerb.Outdated => RunOutdatedAsync(MergeFilters(invocation.Filters, invocation.Args), verbose, executor),
            PnpmVerb.Install => RunInstallAsync(MergeFilters(invocation.Filters, invocation.Args), verbose, executor),
            PnpmVerb.Typecheck => TscCommand.RunTscSafeAsync(invocation.Args, verbose),
            PnpmVerb.Other => RunPassthroughAsync(MergeFilters(invocation.Filters, invocation.PassthroughArgs), verbose, executor),
            _ => throw new ArgumentOutOfRangeException(nameof(args), invocation.Verb, "Unknown pnpm verb."),
        };
    }

    /// <summary>
    /// Parses the raw arguments following the <c>pnpm</c> verb into a <see cref="PnpmInvocation"/>:
    /// leading <c>--filter</c>/<c>-F</c> flags (each consuming a value, in either <c>--filter X</c> or
    /// <c>--filter=X</c> form) are collected until the first non-filter token, which is then resolved
    /// as the subcommand name. Mirrors clap's global-option-then-subcommand parsing for
    /// <c>Pnpm {{ filter, command }}</c> (<c>main.rs</c>:205-212).
    /// </summary>
    /// <param name="args">The raw arguments following the <c>pnpm</c> verb.</param>
    /// <returns>The parsed invocation.</returns>
    internal static PnpmInvocation ParseInvocation(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var filters = new List<string>();
        var i = 0;

        while (i < args.Length)
        {
            var token = args[i];

            if (token is "--filter" or "-F")
            {
                if (i + 1 >= args.Length)
                {
                    break;
                }

                filters.Add(args[i + 1]);
                i += 2;
                continue;
            }

            if (token.StartsWith("--filter=", StringComparison.Ordinal))
            {
                filters.Add(token["--filter=".Length..]);
                i++;
                continue;
            }

            break;
        }

        var rest = args[i..];
        var filtersArray = filters.ToArray();

        if (rest.Length == 0)
        {
            return new PnpmInvocation(filtersArray, PnpmVerb.Other, 0, [], []);
        }

        var verbToken = rest[0];
        var subArgs = rest[1..];

        return verbToken switch
        {
            "list" => BuildListInvocation(filtersArray, subArgs),
            "outdated" => new PnpmInvocation(filtersArray, PnpmVerb.Outdated, 0, subArgs, []),
            "install" => new PnpmInvocation(filtersArray, PnpmVerb.Install, 0, subArgs, []),
            "typecheck" => new PnpmInvocation(filtersArray, PnpmVerb.Typecheck, 0, subArgs, []),
            _ => new PnpmInvocation(filtersArray, PnpmVerb.Other, 0, [], rest),
        };
    }

    private static PnpmInvocation BuildListInvocation(string[] filters, string[] subArgs)
    {
        var (depth, listArgs) = ExtractDepth(subArgs);
        return new PnpmInvocation(filters, PnpmVerb.List, depth, listArgs, []);
    }

    /// <summary>
    /// Extracts an optional <c>--depth</c>/<c>-d</c> flag (in <c>--depth N</c>, <c>--depth=N</c>,
    /// <c>-d N</c>, or <c>-d=N</c> form) from <paramref name="args"/>, defaulting to <c>0</c> when
    /// absent (matching clap's <c>default_value = "0"</c>, <c>main.rs</c>:881). Any unparseable
    /// <c>--depth</c> value is dropped without affecting the default, rather than throwing — a filter
    /// must never crash rtk over a malformed flag.
    /// </summary>
    /// <param name="args">The arguments following <c>list</c>.</param>
    /// <returns>The resolved depth and the remaining arguments with the depth flag removed.</returns>
    internal static (int Depth, string[] Args) ExtractDepth(string[] args)
    {
        var depth = 0;
        var rest = new List<string>();
        var i = 0;

        while (i < args.Length)
        {
            var token = args[i];

            if (token is "--depth" or "-d")
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed))
                {
                    depth = parsed;
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (token.StartsWith("--depth=", StringComparison.Ordinal) && int.TryParse(token["--depth=".Length..], out var eqDepth))
            {
                depth = eqDepth;
                i++;
                continue;
            }

            if (token.StartsWith("-d=", StringComparison.Ordinal) && int.TryParse(token[3..], out var shortEqDepth))
            {
                depth = shortEqDepth;
                i++;
                continue;
            }

            rest.Add(token);
            i++;
        }

        return (depth, rest.ToArray());
    }

    /// <summary>
    /// Validates that <c>--filter</c> is not combined with <c>typecheck</c>. Faithful port of
    /// <c>validate_pnpm_filters</c> (<c>main.rs</c>:1402-1422): only <see cref="PnpmVerb.Typecheck"/>
    /// with at least one filter produces a warning (every other verb, and a filterless typecheck,
    /// return <see langword="null"/>).
    /// </summary>
    /// <param name="filters">The <c>--filter</c>/<c>-F</c> values collected for this invocation.</param>
    /// <param name="verb">The resolved subcommand.</param>
    /// <returns>The warning text to print to stderr, or <see langword="null"/> if none applies.</returns>
    internal static string? ValidatePnpmFilters(IReadOnlyList<string> filters, PnpmVerb verb)
    {
        ArgumentNullException.ThrowIfNull(filters);

        if (verb != PnpmVerb.Typecheck || filters.Count == 0)
        {
            return null;
        }

        return "[rtk] warning: --filter is not yet supported for pnpm tsc, filters preceding the subcommand will be ignored";
    }

    /// <summary>
    /// Merges <c>--filter</c> values in front of a subcommand's own arguments, each rendered as
    /// <c>--filter=value</c>. Faithful port of <c>merge_pnpm_args</c> (<c>main.rs</c>:1384-1390).
    /// </summary>
    /// <param name="filters">The <c>--filter</c>/<c>-F</c> values, in order.</param>
    /// <param name="args">The subcommand's own trailing arguments.</param>
    /// <returns>The merged argument vector.</returns>
    internal static string[] MergeFilters(IReadOnlyList<string> filters, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(args);

        var merged = new string[filters.Count + args.Count];
        var idx = 0;

        foreach (var filter in filters)
        {
            merged[idx++] = $"--filter={filter}";
        }

        foreach (var arg in args)
        {
            merged[idx++] = arg;
        }

        return merged;
    }

    // -----------------------------------------------------------------------
    // run_list (pnpm_cmd.rs:364-418)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs <c>pnpm list --depth=N --json [args]</c>: on a non-zero exit, echoes stderr and returns the
    /// exit code directly with no filtering (<c>pnpm_cmd.rs</c>:378-381); on success, parses the output
    /// with <see cref="PnpmListParser"/> and renders it via the bespoke
    /// <see cref="FormatDependencyListing"/>, capped only when the invocation is unfiltered.
    /// </summary>
    /// <param name="depth">The <c>--depth</c> value.</param>
    /// <param name="args">The merged (filters + user) arguments to append.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run <c>pnpm</c> with, or null for the default.</param>
    /// <returns><c>0</c> on success, or the child's exit code on failure.</returns>
    internal static async Task<int> RunListAsync(int depth, IReadOnlyList<string> args, int verbose, IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();
        var exec = executor ?? new ProcessExecutor();

        var cmdArgs = new List<string> { "list", $"--depth={depth}", "--json" };
        cmdArgs.AddRange(args);

        var result = await ExecuteAsync(exec, cmdArgs, "pnpm list").ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            Console.Error.Write(result.Stderr);
            return result.ExitCode;
        }

        var isFiltered = args.Any(a => a is "--prod" or "-P" or "--dev" or "-D");

        var parseResult = new PnpmListParser().Parse(result.Stdout);

        string filtered = parseResult switch
        {
            ParseResult<DependencyState>.Full full => LogAndFormatList(full.Data, isFiltered, verbose, tier1: true, warnings: null),
            ParseResult<DependencyState>.Degraded degraded => LogAndFormatList(degraded.Data, isFiltered, verbose, tier1: false, degraded.Warnings),
            ParseResult<DependencyState>.Passthrough passthrough => PassthroughList(passthrough.Raw),
            _ => throw new InvalidOperationException("Unreachable: unknown ParseResult variant."),
        };

        Console.Out.Write(filtered + "\n");

        timer.Track($"pnpm list --depth={depth}", $"rtk pnpm list --depth={depth}", result.Stdout, filtered);

        return 0;
    }

    private static string LogAndFormatList(DependencyState data, bool isFiltered, int verbose, bool tier1, IReadOnlyList<string>? warnings)
    {
        if (verbose > 0)
        {
            if (tier1)
            {
                Console.Error.Write("pnpm list (Tier 1: Full JSON parse)\n");
            }
            else
            {
                OutputParserSupport.EmitDegradationWarning("pnpm list", string.Join(", ", warnings ?? []));
            }
        }

        return FormatDependencyListing(data, cap: !isFiltered);
    }

    private static string PassthroughList(string raw)
    {
        OutputParserSupport.EmitPassthroughWarning("pnpm list", "All parsing tiers failed");
        return raw;
    }

    // -----------------------------------------------------------------------
    // run_outdated (pnpm_cmd.rs:420-467)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs <c>pnpm outdated --format json [args]</c> and renders it via the SHARED
    /// <see cref="DependencyState"/>/<see cref="ITokenFormatter"/> formatter (unlike <c>list</c>).
    /// Ignores the child's exit code entirely and always returns <c>0</c> — pnpm's own <c>outdated</c>
    /// subcommand exits non-zero whenever outdated packages exist, which Rust's <c>run_outdated</c>
    /// deliberately never inspects (<c>pnpm_cmd.rs</c>:432-467, no <c>result.success()</c> check
    /// anywhere in this function).
    /// </summary>
    /// <param name="args">The merged (filters + user) arguments to append.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run <c>pnpm</c> with, or null for the default.</param>
    /// <returns>Always <c>0</c>.</returns>
    internal static async Task<int> RunOutdatedAsync(IReadOnlyList<string> args, int verbose, IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();
        var exec = executor ?? new ProcessExecutor();

        var cmdArgs = new List<string> { "outdated", "--format", "json" };
        cmdArgs.AddRange(args);

        var result = await ExecuteAsync(exec, cmdArgs, "pnpm outdated").ConfigureAwait(false);
        var combined = result.Stdout + result.Stderr;

        var parseResult = new PnpmOutdatedParser().Parse(result.Stdout);
        var mode = FormatModeExtensions.FromVerbosity((byte)Math.Clamp(verbose, 0, byte.MaxValue));

        string filtered = parseResult switch
        {
            ParseResult<DependencyState>.Full full => LogAndFormatOutdated(full.Data, mode, verbose, tier1: true, warnings: null),
            ParseResult<DependencyState>.Degraded degraded => LogAndFormatOutdated(degraded.Data, mode, verbose, tier1: false, degraded.Warnings),
            ParseResult<DependencyState>.Passthrough passthrough => PassthroughOutdated(passthrough.Raw),
            _ => throw new InvalidOperationException("Unreachable: unknown ParseResult variant."),
        };

        Console.Out.Write((string.IsNullOrWhiteSpace(filtered) ? "All packages up-to-date" : filtered) + "\n");

        timer.Track("pnpm outdated", "rtk pnpm outdated", combined, filtered);

        return 0;
    }

    private static string LogAndFormatOutdated(DependencyState data, FormatMode mode, int verbose, bool tier1, IReadOnlyList<string>? warnings)
    {
        if (verbose > 0)
        {
            if (tier1)
            {
                Console.Error.Write("pnpm outdated (Tier 1: Full JSON parse)\n");
            }
            else
            {
                OutputParserSupport.EmitDegradationWarning("pnpm outdated", string.Join(", ", warnings ?? []));
            }
        }

        return ((ITokenFormatter)data).Format(mode);
    }

    private static string PassthroughOutdated(string raw)
    {
        OutputParserSupport.EmitPassthroughWarning("pnpm outdated", "All parsing tiers failed");
        return raw;
    }

    // -----------------------------------------------------------------------
    // run_install (pnpm_cmd.rs:469-537)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs <c>pnpm install [args]</c>: on a non-zero exit, echoes stderr and returns the exit code
    /// directly with no filtering; on success, strips progress-bar noise via
    /// <see cref="FilterPnpmInstall"/>.
    /// </summary>
    /// <param name="args">The merged (filters + user) arguments to append.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run <c>pnpm</c> with, or null for the default.</param>
    /// <returns><c>0</c> on success, or the child's exit code on failure.</returns>
    internal static async Task<int> RunInstallAsync(IReadOnlyList<string> args, int verbose, IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();
        var exec = executor ?? new ProcessExecutor();

        var cmdArgs = new List<string> { "install" };
        cmdArgs.AddRange(args);

        if (verbose > 0)
        {
            Console.Error.Write("pnpm install running...\n");
        }

        var result = await ExecuteAsync(exec, cmdArgs, "pnpm install").ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            Console.Error.Write(result.Stderr);
            return result.ExitCode;
        }

        var combined = result.Stdout + result.Stderr;
        var filtered = FilterPnpmInstall(combined);

        Console.Out.Write(filtered + "\n");

        timer.Track("pnpm install", "rtk pnpm install", combined, filtered);

        return 0;
    }

    /// <summary>
    /// Filters <c>pnpm install</c> output: strips progress-bar lines (containing <c>Progress</c>,
    /// <c>│</c>, or <c>%</c>) and any blank line immediately following one, while keeping
    /// error/<c>ERR</c>/<c>ERROR</c> lines and summary lines (containing <c>packages in</c> or
    /// <c>dependencies</c>, or starting with <c>+</c>/<c>-</c>). Faithful port of
    /// <c>filter_pnpm_install</c> (<c>pnpm_cmd.rs</c>:501-537).
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>pnpm install</c>.</param>
    /// <returns>The filtered output, or the literal <c>"ok"</c> if nothing survived.</returns>
    internal static string FilterPnpmInstall(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new List<string>();
        var sawProgress = false;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            if (line.Contains("Progress", StringComparison.Ordinal) || line.Contains('│') || line.Contains('%'))
            {
                sawProgress = true;
                continue;
            }

            if (sawProgress && line.Trim().Length == 0)
            {
                continue;
            }

            if (line.Contains("ERR", StringComparison.Ordinal)
                || line.Contains("error", StringComparison.Ordinal)
                || line.Contains("ERROR", StringComparison.Ordinal))
            {
                result.Add(line);
                continue;
            }

            if (line.Contains("packages in", StringComparison.Ordinal)
                || line.Contains("dependencies", StringComparison.Ordinal)
                || line.StartsWith('+')
                || line.StartsWith('-'))
            {
                result.Add(line.Trim());
            }
        }

        return result.Count == 0 ? "ok" : string.Join('\n', result);
    }

    // -----------------------------------------------------------------------
    // Other/passthrough (pnpm_cmd.rs:539-541 -> core::runner::run_passthrough)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs <c>pnpm &lt;args&gt;</c> as an unfiltered, inherited-stdio passthrough for an unrecognized
    /// subcommand, tracking it manually via <see cref="TimedExecution.TrackPassthrough"/>. Faithful
    /// port of <c>run_passthrough</c> (<c>pnpm_cmd.rs</c>:539-541), which delegates to
    /// <c>core::runner::run_passthrough("pnpm", args, verbose)</c> — that shared runner path inherits
    /// stdio (<c>RunMode::Passthrough</c>, <c>runner.rs</c>:186-193) and uses the literal error context
    /// <c>"Failed to run pnpm"</c> (not including the args), which this method matches exactly.
    /// </summary>
    /// <param name="args">The full argument vector to pass to <c>pnpm</c> (including the unrecognized subcommand token).</param>
    /// <param name="verbose">The verbosity level; a nonzero value logs the passthrough args.</param>
    /// <param name="executor">The process executor to spawn <c>pnpm</c> with, or null for the default.</param>
    /// <returns><c>pnpm</c>'s exit code.</returns>
    internal static async Task<int> RunPassthroughAsync(IReadOnlyList<string> args, int verbose, IProcessExecutor? executor)
    {
        if (verbose > 0)
        {
            Console.Error.Write($"pnpm passthrough: [{string.Join(", ", args.Select(a => $"\"{a}\""))}]\n");
        }

        // Timer starts before spawning, mirroring Rust's `TimedExecution::start()` placement.
        var timer = TimedExecution.Start();

        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest("pnpm", args, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run pnpm{detail}");
        }

        var argsStr = string.Join(' ', args);
        timer.TrackPassthrough($"pnpm {argsStr}", $"rtk pnpm {argsStr} (passthrough)");

        return Utils.ExitCodeFromStatus(result.ExitCode, "pnpm");
    }

    // -----------------------------------------------------------------------
    // Shared exec helper
    // -----------------------------------------------------------------------

    private static async Task<ExecutionResult> ExecuteAsync(IProcessExecutor exec, IReadOnlyList<string> args, string failureLabel)
    {
        var request = new ExecutionRequest("pnpm", args, CaptureMode: ExecutionCaptureMode.Separate);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run {failureLabel}{detail}");
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // format_dependency_listing (pnpm_cmd.rs:292-347) - bespoke, NOT the shared TokenFormatter
    // -----------------------------------------------------------------------

    /// <summary>
    /// Formats a dependency listing with grouped <c>[prod]</c>/<c>[dev]</c> sections. Faithful port of
    /// <c>format_dependency_listing</c> (<c>pnpm_cmd.rs</c>:292-347): each section is capped and
    /// tee-hinted independently, only when <paramref name="cap"/> is true.
    /// </summary>
    /// <param name="state">The parsed dependency state.</param>
    /// <param name="cap">
    /// True for a plain <c>pnpm list</c> (both categories present, may truncate); false for
    /// <c>pnpm list --prod</c>/<c>--dev</c> (the user already narrowed the listing, so every package
    /// must be shown).
    /// </param>
    /// <returns>The formatted listing.</returns>
    internal static string FormatDependencyListing(DependencyState state, bool cap)
    {
        ArgumentNullException.ThrowIfNull(state);

        var prod = state.Dependencies.Where(d => !d.DevDependency).ToList();
        var dev = state.Dependencies.Where(d => d.DevDependency).ToList();
        var total = Math.Max(state.TotalPackages, state.Dependencies.Count);

        var lines = new List<string> { $"{total} packages ({prod.Count} prod / {dev.Count} dev)" };

        AppendListingSection(lines, "[prod]", prod, cap, "pnpm-prod");
        AppendListingSection(lines, "[dev]", dev, cap, "pnpm-dev");

        return string.Join('\n', lines);
    }

    private static void AppendListingSection(List<string> lines, string header, List<Dependency> deps, bool cap, string teeSlug)
    {
        if (deps.Count == 0)
        {
            return;
        }

        lines.Add(header);

        var shown = cap ? Math.Min(deps.Count, MaxListing) : deps.Count;
        foreach (var dep in deps.Take(shown))
        {
            lines.Add($"  {dep.Name} {dep.CurrentVersion}");
        }

        if (cap && deps.Count > MaxListing)
        {
            lines.Add($"  … +{deps.Count - MaxListing} more");

            var allEntries = string.Join('\n', deps.Select(d => $"  {d.Name} {d.CurrentVersion}"));
            var hint = Tee.ForceTeeTailHint(allEntries, teeSlug, MaxListing + 1);
            if (hint is not null)
            {
                lines.Add($"  {hint}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Parsers (Task 1's OutputParser<T> abstraction over DependencyState)
    // -----------------------------------------------------------------------

    /// <summary>
    /// JSON shape of a single dependency-tree package node in <c>pnpm list --json</c> output,
    /// recursive through <c>dependencies</c>/<c>devDependencies</c>. Faithful port of
    /// <c>PackageJsonListItem</c> (<c>pnpm_cmd.rs</c>:27-34).
    /// </summary>
    private sealed class PnpmListPackage
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("dependencies")]
        public Dictionary<string, PnpmListPackage>? Dependencies { get; set; }

        [JsonPropertyName("devDependencies")]
        public Dictionary<string, PnpmListPackage>? DevDependencies { get; set; }
    }

    /// <summary>
    /// JSON shape of a top-level workspace entry in <c>pnpm list --json</c> output. Faithful port of
    /// <c>PnpmListOutput</c> (<c>pnpm_cmd.rs</c>:20-25) — unlike <see cref="PnpmListPackage"/>, this
    /// carries a required <c>name</c> field (matching Rust's non-<c>Option</c> <c>name: String</c>,
    /// which fails deserialization of the whole array when absent).
    /// </summary>
    private sealed class PnpmListEntry
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("dependencies")]
        public Dictionary<string, PnpmListPackage>? Dependencies { get; set; }

        [JsonPropertyName("devDependencies")]
        public Dictionary<string, PnpmListPackage>? DevDependencies { get; set; }
    }

    /// <summary>
    /// Parser for <c>pnpm list --json</c> output. Faithful port of <c>PnpmListParser</c>
    /// (<c>pnpm_cmd.rs</c>:52-97): tier 1 recursively walks the JSON dependency tree; tier 2
    /// (<see cref="ExtractListText"/>) regex/text-scans the human-readable tree output.
    /// </summary>
    internal sealed class PnpmListParser : OutputParser<DependencyState>
    {
        /// <inheritdoc/>
        protected override DependencyState? TryFull(string input)
        {
            List<PnpmListEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize(input, PnpmJsonContext.Default.ListPnpmListEntry);
            }
            catch (JsonException)
            {
                return null;
            }

            if (entries is null)
            {
                return null;
            }

            var dependencies = new List<Dependency>();
            var totalCount = 0;

            foreach (var entry in entries)
            {
                CollectDependencies(entry.Name, entry.Version, entry.Dependencies, entry.DevDependencies, isDev: false, dependencies, ref totalCount);
            }

            return new DependencyState { TotalPackages = totalCount, OutdatedCount = 0, Dependencies = dependencies };
        }

        /// <inheritdoc/>
        protected override (DependencyState Data, IReadOnlyList<string> Warnings)? TryDegraded(string input)
        {
            var extracted = ExtractListText(input);
            return extracted is null ? null : (extracted, new[] { "JSON parse failed" });
        }

        /// <summary>
        /// Recursively collects dependencies from a pnpm package tree node. Faithful port of
        /// <c>collect_dependencies</c> (<c>pnpm_cmd.rs</c>:100-125): entries reached through
        /// <c>devDependencies</c> are always marked dev, regardless of the parent's own
        /// <paramref name="isDev"/> flag.
        /// </summary>
        private static void CollectDependencies(
            string name,
            string? version,
            Dictionary<string, PnpmListPackage>? dependencies,
            Dictionary<string, PnpmListPackage>? devDependencies,
            bool isDev,
            List<Dependency> output,
            ref int count)
        {
            if (version is not null)
            {
                output.Add(new Dependency { Name = name, CurrentVersion = version, DevDependency = isDev });
                count++;
            }

            if (dependencies is not null)
            {
                foreach (var (depName, depPkg) in dependencies)
                {
                    CollectDependencies(depName, depPkg.Version, depPkg.Dependencies, depPkg.DevDependencies, isDev, output, ref count);
                }
            }

            if (devDependencies is not null)
            {
                foreach (var (depName, depPkg) in devDependencies)
                {
                    CollectDependencies(depName, depPkg.Version, depPkg.Dependencies, depPkg.DevDependencies, true, output, ref count);
                }
            }
        }

        /// <summary>
        /// Tier 2: extracts a dependency listing from pnpm's human-readable tree output. Faithful port
        /// of <c>extract_list_text</c> (<c>pnpm_cmd.rs</c>:127-185).
        /// </summary>
        private static DependencyState? ExtractListText(string output)
        {
            var dependencies = new List<Dependency>();
            var count = 0;
            var isDev = false;

            foreach (var line in ReadCommand.SplitLines(output))
            {
                var trimmed = line.Trim();

                if (trimmed == "devDependencies:")
                {
                    isDev = true;
                    continue;
                }

                if (trimmed == "dependencies:")
                {
                    isDev = false;
                    continue;
                }

                if (line.Contains('│') || line.Contains('├') || line.Contains('└')
                    || line.Contains("Legend:", StringComparison.Ordinal) || trimmed.Length == 0)
                {
                    continue;
                }

                var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                var pkgStr = parts[0];
                var atPos = pkgStr.LastIndexOf('@');
                if (atPos < 0)
                {
                    continue;
                }

                var name = pkgStr[..atPos];
                var version = pkgStr[(atPos + 1)..];
                if (name.Length == 0 || version.Length == 0)
                {
                    continue;
                }

                dependencies.Add(new Dependency { Name = name, CurrentVersion = version, DevDependency = isDev });
                count++;
            }

            return count > 0
                ? new DependencyState { TotalPackages = count, OutdatedCount = 0, Dependencies = dependencies }
                : null;
        }
    }

    /// <summary>
    /// JSON shape of a single package entry in <c>pnpm outdated --format json</c> output. Faithful port
    /// of <c>PnpmOutdatedPackage</c> (<c>pnpm_cmd.rs</c>:43-50).
    /// </summary>
    private sealed class PnpmOutdatedPackage
    {
        [JsonPropertyName("current")]
        public required string Current { get; set; }

        [JsonPropertyName("latest")]
        public required string Latest { get; set; }

        [JsonPropertyName("wanted")]
        public string? Wanted { get; set; }

        [JsonPropertyName("dependencyType")]
        public string DependencyType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Parser for <c>pnpm outdated --format json</c> output. Faithful port of <c>PnpmOutdatedParser</c>
    /// (<c>pnpm_cmd.rs</c>:187-236): tier 1 parses the current/latest/wanted JSON map; tier 2
    /// (<see cref="ExtractOutdatedText"/>) regex/text-scans the human-readable table output.
    /// </summary>
    internal sealed class PnpmOutdatedParser : OutputParser<DependencyState>
    {
        /// <inheritdoc/>
        protected override DependencyState? TryFull(string input)
        {
            Dictionary<string, PnpmOutdatedPackage>? packages;
            try
            {
                packages = JsonSerializer.Deserialize(input, PnpmJsonContext.Default.DictionaryStringPnpmOutdatedPackage);
            }
            catch (JsonException)
            {
                return null;
            }

            if (packages is null)
            {
                return null;
            }

            var dependencies = new List<Dependency>();
            var outdatedCount = 0;

            foreach (var (name, pkg) in packages)
            {
                if (pkg.Current != pkg.Latest)
                {
                    outdatedCount++;
                }

                dependencies.Add(new Dependency
                {
                    Name = name,
                    CurrentVersion = pkg.Current,
                    LatestVersion = pkg.Latest,
                    WantedVersion = pkg.Wanted,
                    DevDependency = pkg.DependencyType == "devDependencies",
                });
            }

            return new DependencyState { TotalPackages = dependencies.Count, OutdatedCount = outdatedCount, Dependencies = dependencies };
        }

        /// <inheritdoc/>
        protected override (DependencyState Data, IReadOnlyList<string> Warnings)? TryDegraded(string input)
        {
            var extracted = ExtractOutdatedText(input);
            return extracted is null ? null : (extracted, new[] { "JSON parse failed" });
        }

        /// <summary>
        /// Tier 2: extracts current/wanted/latest columns from pnpm's human-readable outdated table.
        /// Faithful port of <c>extract_outdated_text</c> (<c>pnpm_cmd.rs</c>:238-286).
        /// </summary>
        private static DependencyState? ExtractOutdatedText(string output)
        {
            var dependencies = new List<Dependency>();
            var outdatedCount = 0;

            foreach (var line in ReadCommand.SplitLines(output))
            {
                if (line.Contains('│') || line.Contains('├') || line.Contains('└') || line.Contains('─')
                    || line.StartsWith("Legend:", StringComparison.Ordinal)
                    || line.StartsWith("Package", StringComparison.Ordinal)
                    || line.Trim().Length == 0)
                {
                    continue;
                }

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                {
                    continue;
                }

                var name = parts[0];
                var current = parts[1];
                var latest = parts[3];

                if (current != latest)
                {
                    outdatedCount++;
                }

                dependencies.Add(new Dependency
                {
                    Name = name,
                    CurrentVersion = current,
                    LatestVersion = latest,
                    WantedVersion = parts[2],
                    DevDependency = false,
                });
            }

            return dependencies.Count > 0
                ? new DependencyState { TotalPackages = dependencies.Count, OutdatedCount = outdatedCount, Dependencies = dependencies }
                : null;
        }
    }

    /// <summary>
    /// Source-generated JSON metadata for pnpm's <c>list</c>/<c>outdated</c> JSON shapes, required
    /// because <c>RtkSharp.csproj</c> publishes with <c>PublishAot=true</c> — reflection-based
    /// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming, matching the
    /// convention <c>TrustCommand</c>'s <c>TrustStoreJsonContext</c> established.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
    [JsonSerializable(typeof(List<PnpmListEntry>))]
    [JsonSerializable(typeof(Dictionary<string, PnpmOutdatedPackage>))]
    private sealed partial class PnpmJsonContext : JsonSerializerContext;
}
