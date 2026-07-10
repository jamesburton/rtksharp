using RtkSharp.Commands.System;
using RtkSharp.Execution;
using static RtkSharp.Filters.Commands.Git.GitFilters;

namespace RtkSharp.Commands.Git;

/// <summary>
/// Compact <c>git</c> proxy. This verb parses the global git options that may precede a subcommand
/// (via <see cref="GitGlobalArgs"/>), then dispatches on the subcommand exactly as Rust's
/// <c>Commands::Git</c> routing in <c>main.rs</c> does. Ported from <c>src/cmds/git/git.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope (Phase 7a, Tasks 1-3).</b> All twelve filtered subcommands are ported here:
/// <c>status</c>, <c>log</c>, <c>diff</c>, and <c>show</c> (Tasks 1-2), plus <c>add</c>,
/// <c>commit</c>, <c>push</c>, <c>pull</c>, <c>branch</c>, <c>fetch</c>, <c>stash</c> (with its
/// optional subcommand argument), and <c>worktree</c> (Task 3). Only a genuinely unknown subcommand
/// is routed to <see cref="RunPassthroughAsync"/>, which runs raw <c>git</c> with the global args
/// threaded through and propagates the exit code. This mirrors Rust's <c>GitCommands::Other</c>
/// external-subcommand passthrough (git.rs:1757): unfiltered output, never blocked.
/// </para>
/// <para>
/// <b>Omitted side effects.</b> Following the <see cref="GrepCommand"/>/<see cref="DotnetCommand"/>
/// precedent, the Rust token-savings tracking (<c>TimedExecution</c>) and the <c>verbose</c>
/// <c>eprintln!</c> diagnostics are omitted — they are metrics/diagnostic side effects that do not
/// change filtered output. main.rs always passes <c>max_lines = None</c> for git, and
/// <c>run_log</c>'s parameter is unused (<c>_max_lines</c>), so no max-lines surface is ported.
/// </para>
/// </remarks>
public static class GitCommand
{
    /// <summary>The <c>--pretty=format:</c> RTK injects for <c>log</c> when the user gave no format flag.</summary>
    private const string RtkLogFormat = "--pretty=format:%h %s (%ar) <%an>%n%b%n---END---";

    /// <summary>
    /// Default per-diff line budget for the compact filter. Rust threads <c>max_lines</c> from
    /// main.rs, which always passes <c>None</c> for git, so <c>compact_diff</c> resolves to
    /// <c>unwrap_or(500)</c> (git.rs:197, 310). RtkSharp inlines the same default.
    /// </summary>
    private const int CompactDiffDefaultMaxLines = 500;

    /// <summary>
    /// The in-progress states git prints a prose header for but porcelain <c>-b</c> omits. Each maps
    /// to the compact one-line summary RTK surfaces instead. Ported from <c>GitStatusState</c>
    /// (git.rs:666-691).
    /// </summary>
    private enum GitStatusState
    {
        /// <summary>An interactive or plain rebase is in progress.</summary>
        Rebase,

        /// <summary>A merge is in progress with unresolved conflicts.</summary>
        MergeConflicts,

        /// <summary>A merge is in progress with all conflicts resolved, ready to commit.</summary>
        MergeReadyToCommit,

        /// <summary>A cherry-pick is in progress.</summary>
        CherryPick,

        /// <summary>A revert is in progress.</summary>
        Revert,

        /// <summary>A bisect session is in progress.</summary>
        Bisect,

        /// <summary>An <c>am</c> (apply-mailbox) session is in progress.</summary>
        Am,

        /// <summary>A sparse checkout is enabled.</summary>
        SparseCheckout,
    }

    /// <summary>
    /// Prose fragments in plain <c>git status</c> output that indicate a rebase is in progress.
    /// Ported from <c>REBASE_INDICATORS</c> (git.rs:693-701).
    /// </summary>
    private static readonly string[] RebaseIndicators =
    {
        "rebase in progress",
        "You are currently rebasing",
        "You are currently editing",
        "You are currently splitting",
        "Last command done",
        "Next command to do",
        "No commands remaining",
    };

    /// <summary>
    /// File-change block headers in plain <c>git status</c> output. <see cref="ExtractStateHeader"/>
    /// stops scanning at the first of these because every state-header line appears above them.
    /// Ported from the <c>STOPPERS</c> constant (git.rs:739-747).
    /// </summary>
    private static readonly string[] StateStoppers =
    {
        "Changes to be committed:",
        "Changes not staged for commit:",
        "Untracked files:",
        "Unmerged paths:",
        "no changes added to commit",
        "nothing to commit",
        "nothing added to commit",
    };

    /// <summary>
    /// Registry entry point for the <c>git</c> verb. Parses global git options, dispatches to the
    /// filtered subcommand or raw passthrough, and returns the child <c>git</c> process's exit code.
    /// </summary>
    /// <param name="args">The arguments following the <c>git</c> verb.</param>
    /// <returns>The child <c>git</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, new ProcessExecutor(), Console.Out, Console.Error);

    /// <summary>
    /// Test-friendly overload accepting an explicit executor and output writers.
    /// </summary>
    /// <param name="args">The arguments following the <c>git</c> verb.</param>
    /// <param name="executor">The process executor used to run <c>git</c>.</param>
    /// <param name="stdout">The destination for filtered output.</param>
    /// <param name="stderr">The destination for warnings and errors.</param>
    /// <returns>The child <c>git</c> process's exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var (globals, rest) = GitGlobalArgs.Parse(args);
        var globalArgs = globals.ToArgs();

        if (rest.Count == 0)
        {
            // No subcommand: clap would require one. Run raw `git` (prints usage, exits nonzero).
            return await RunPassthroughAsync(Array.Empty<string>(), globalArgs, executor, stdout, stderr)
                .ConfigureAwait(false);
        }

        var subcommand = rest[0];
        var subArgs = rest.Skip(1).ToArray();

        return subcommand switch
        {
            "status" => await RunStatusAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "log" => await RunLogAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "diff" => await RunDiffAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "show" => await RunShowAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "add" => await RunAddAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "commit" => await RunCommitAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "push" => await RunPushAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "pull" => await RunPullAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "branch" => await RunBranchAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "fetch" => await RunFetchAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "stash" => await RunStashAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            "worktree" => await RunWorktreeAsync(subArgs, globalArgs, executor, stdout, stderr).ConfigureAwait(false),
            _ => await RunPassthroughAsync(rest.ToArray(), globalArgs, executor, stdout, stderr).ConfigureAwait(false),
        };
    }

    // ===================== log =====================

    /// <summary>
    /// Runs <c>git log</c> with RTK's compact defaults and prints the filtered history. Ports
    /// <c>run_log</c> (git.rs:420).
    /// </summary>
    private static async Task<int> RunLogAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var hasFormatFlag = args.Any(a =>
            a.StartsWith("--oneline", StringComparison.Ordinal)
            || a.StartsWith("--pretty", StringComparison.Ordinal)
            || a.StartsWith("--format", StringComparison.Ordinal));

        var hasLimitFlag = args.Any(a =>
            (a.StartsWith('-') && a.Length > 1 && char.IsAsciiDigit(a[1]))
            || a == "-n"
            || a.StartsWith("--max-count", StringComparison.Ordinal));

        var cmdArgs = new List<string>(globalArgs) { "log" };

        // Use %b (body) to preserve first lines of the commit body for agent context (git.rs:446).
        if (!hasFormatFlag)
        {
            cmdArgs.Add(RtkLogFormat);
        }

        int limit;
        bool userSetLimit;
        if (hasLimitFlag)
        {
            // User explicitly passed -N / -n N / --max-count=N → respect their choice (git.rs:451).
            limit = ParseUserLimit(args) ?? 10;
            userSetLimit = true;
        }
        else if (hasFormatFlag)
        {
            // --oneline / --pretty without -N: compact output, allow more (git.rs:456).
            cmdArgs.Add("-50");
            limit = 50;
            userSetLimit = false;
        }
        else
        {
            // No flags at all: default to 10 (git.rs:460).
            cmdArgs.Add("-10");
            limit = 10;
            userSetLimit = false;
        }

        var wantsMerges = args.Any(a => a == "--merges" || a == "--min-parents=2" || a == "--no-merges");
        if (!wantsMerges && !hasLimitFlag)
        {
            cmdArgs.Add("--no-merges");
        }

        cmdArgs.AddRange(args);

        var result = await ExecAsync(executor, cmdArgs, null).ConfigureAwait(false);

        if (!Succeeded(result))
        {
            stderr.Write(result.Stderr + "\n");
            return result.ExitCode;
        }

        string filtered;
        try
        {
            filtered = FilterLogOutput(result.Stdout, limit, userSetLimit, hasFormatFlag);
        }
        catch (Exception ex)
        {
            // Mandatory fallback contract: a filter must never crash or hide output.
            stderr.Write($"rtk: filter warning: {ex.Message}\n");
            stdout.Write(result.Stdout);
            return result.ExitCode;
        }

        stdout.Write(filtered + "\n");
        return 0;
    }

    /// <summary>
    /// Parses the user-specified limit from <c>git log</c> args: handles <c>-20</c>, <c>-n 20</c>,
    /// <c>--max-count=20</c>, and <c>--max-count 20</c>. Ports <c>parse_user_limit</c> (git.rs:507).
    /// </summary>
    /// <param name="args">The user-supplied <c>git log</c> arguments.</param>
    /// <returns>The parsed limit, or null when none is present.</returns>
    internal static int? ParseUserLimit(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            // -20 (combined digit form)
            if (arg.StartsWith('-') && arg.Length > 1 && char.IsAsciiDigit(arg[1])
                && int.TryParse(arg[1..], out var combined))
            {
                return combined;
            }

            // -n 20 (two-token form)
            if (arg == "-n" && i + 1 < args.Count && int.TryParse(args[i + 1], out var nValue))
            {
                return nValue;
            }

            // --max-count=20
            if (arg.StartsWith("--max-count=", StringComparison.Ordinal)
                && int.TryParse(arg["--max-count=".Length..], out var eqValue))
            {
                return eqValue;
            }

            // --max-count 20 (two-token form)
            if (arg == "--max-count" && i + 1 < args.Count && int.TryParse(args[i + 1], out var spaceValue))
            {
                return spaceValue;
            }
        }

        return null;
    }

    // ===================== diff =====================

    /// <summary>
    /// Runs <c>git diff</c> with RTK's compact-by-default behavior. Ports <c>run_diff</c>
    /// (git.rs:106): <c>--stat</c>/<c>--numstat</c>/<c>--shortstat</c> or an explicit
    /// <c>--no-compact</c> pass straight through (the RTK-only <c>--no-compact</c> flag is stripped
    /// before reaching git); otherwise a <c>--stat</c> summary is printed first, then the full diff
    /// condensed by <see cref="CompactDiff"/>.
    /// </summary>
    private static async Task<int> RunDiffAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        // Issue #1215 (git.rs:115): restore any `--` clap consumed. RtkSharp's top-level parser
        // preserves `--` in the command args (see RestoreDoubleDashWithRaw), so re-insertion is a
        // no-op here and `args` is already the faithful, double-dash-preserving vector.
        var wantsStat = args.Any(a => a == "--stat" || a == "--numstat" || a == "--shortstat");
        var wantsCompact = !args.Any(a => a == "--no-compact");

        if (wantsStat || !wantsCompact)
        {
            // Passthrough: --stat family or explicit --no-compact. Drop the RTK-only --no-compact flag.
            var passArgs = new List<string>(globalArgs) { "diff" };
            foreach (var a in args)
            {
                if (a == "--no-compact")
                {
                    continue;
                }

                passArgs.Add(a);
            }

            var passResult = await ExecAsync(executor, passArgs, null).ConfigureAwait(false);
            if (!Succeeded(passResult))
            {
                stderr.Write(passResult.Stderr + "\n");
                return passResult.ExitCode;
            }

            stdout.Write(passResult.Stdout.Trim() + "\n");
            return 0;
        }

        // Default RTK behavior: --stat summary first, then the compacted diff.
        var statArgs = new List<string>(globalArgs) { "diff", "--stat" };
        statArgs.AddRange(args);
        var statResult = await ExecAsync(executor, statArgs, null).ConfigureAwait(false);
        if (!Succeeded(statResult))
        {
            // Mirror run_diff: only emit stderr when it carries a non-blank message (git.rs:166).
            if (!string.IsNullOrWhiteSpace(statResult.Stderr))
            {
                stderr.Write(statResult.Stderr);
            }

            return statResult.ExitCode;
        }

        stdout.Write(statResult.Stdout.Trim() + "\n");

        var diffArgs = new List<string>(globalArgs) { "diff" };
        diffArgs.AddRange(args);
        var diffResult = await ExecAsync(executor, diffArgs, null).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(diffResult.Stdout))
        {
            string compacted;
            try
            {
                compacted = CompactDiff(diffResult.Stdout, CompactDiffDefaultMaxLines);
            }
            catch (Exception ex)
            {
                // Mandatory fallback contract: a filter must never crash or hide output.
                stderr.Write($"rtk: filter warning: {ex.Message}\n");
                stdout.Write(diffResult.Stdout);
                return 0;
            }

            stdout.Write("\nChanges:\n");
            stdout.Write(compacted + "\n");
        }

        return 0;
    }

    // ===================== show =====================

    /// <summary>
    /// Runs <c>git show</c> with RTK's compact defaults. Ports <c>run_show</c> (git.rs:213):
    /// <c>--stat</c>/<c>--numstat</c>/<c>--shortstat</c>, an explicit <c>--pretty</c>/<c>--format</c>,
    /// or a <c>rev:path</c> blob argument pass straight through; otherwise a one-line commit summary,
    /// a <c>--stat</c> summary, and the compacted patch are emitted in sequence.
    /// </summary>
    private static async Task<int> RunShowAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var wantsStatOnly = args.Any(a => a == "--stat" || a == "--numstat" || a == "--shortstat");
        var wantsFormat = args.Any(a =>
            a.StartsWith("--pretty", StringComparison.Ordinal) || a.StartsWith("--format", StringComparison.Ordinal));

        // `git show rev:path` prints a blob, not a commit diff — pass through to avoid duplicated
        // output from the compact-show steps (git.rs:232).
        var wantsBlobShow = args.Any(IsBlobShowArg);

        if (wantsStatOnly || wantsFormat || wantsBlobShow)
        {
            var passArgs = new List<string>(globalArgs) { "show" };
            passArgs.AddRange(args);
            var passResult = await ExecAsync(executor, passArgs, null).ConfigureAwait(false);
            if (!Succeeded(passResult))
            {
                stderr.Write(passResult.Stderr + "\n");
                return passResult.ExitCode;
            }

            // Blob mode preserves exact bytes (no trailing-newline normalization); commit mode trims.
            stdout.Write(wantsBlobShow ? passResult.Stdout : passResult.Stdout.Trim() + "\n");
            return 0;
        }

        // Step 1: one-line commit summary. (The raw-output capture Rust runs only for token tracking
        // is omitted, matching the Task 1 precedent of dropping metrics-only side effects.)
        var summaryArgs = new List<string>(globalArgs) { "show", "--no-patch", "--pretty=format:%h %s (%ar) <%an>" };
        summaryArgs.AddRange(args);
        var summaryResult = await ExecAsync(executor, summaryArgs, null).ConfigureAwait(false);
        if (!Succeeded(summaryResult))
        {
            stderr.Write(summaryResult.Stderr + "\n");
            return summaryResult.ExitCode;
        }

        stdout.Write(summaryResult.Stdout.Trim() + "\n");

        // Step 2: --stat summary (no success gate — Rust only propagates spawn failures here).
        var statArgs = new List<string>(globalArgs) { "show", "--stat", "--pretty=format:" };
        statArgs.AddRange(args);
        var statResult = await ExecAsync(executor, statArgs, null).ConfigureAwait(false);
        var statText = statResult.Stdout.Trim();
        if (statText.Length != 0)
        {
            stdout.Write(statText + "\n");
        }

        // Step 3: compacted patch.
        var diffArgs = new List<string>(globalArgs) { "show", "--pretty=format:" };
        diffArgs.AddRange(args);
        var diffResult = await ExecAsync(executor, diffArgs, null).ConfigureAwait(false);
        var diffText = diffResult.Stdout.Trim();
        if (diffText.Length != 0)
        {
            string compacted;
            try
            {
                compacted = CompactDiff(diffText, CompactDiffDefaultMaxLines);
            }
            catch (Exception ex)
            {
                // Mandatory fallback contract: a filter must never crash or hide output.
                stderr.Write($"rtk: filter warning: {ex.Message}\n");
                stdout.Write(diffText);
                return 0;
            }

            stdout.Write(compacted + "\n");
        }

        return 0;
    }

    /// <summary>
    /// Reports whether a <c>git show</c> argument is a <c>rev:path</c> blob reference (as opposed to a
    /// flag such as <c>--pretty=format:...</c>): a non-flag token containing a colon. Ports
    /// <c>is_blob_show_arg</c> (git.rs:325).
    /// </summary>
    /// <param name="arg">The <c>git show</c> argument to classify.</param>
    /// <returns>True when the argument selects a blob rather than a commit.</returns>
    internal static bool IsBlobShowArg(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        return !arg.StartsWith('-') && arg.Contains(':', StringComparison.Ordinal);
    }

    /// <summary>
    /// Restores <c>--</c> tokens that clap consumed under <c>trailing_var_arg</c>, ported from
    /// <c>args_utils::restore_double_dash_with_raw</c> (src/core/args_utils.rs) for issue #1215.
    /// Returns <paramref name="parsedArgs"/> unchanged when <paramref name="rawArgs"/> holds no more
    /// <c>--</c> than the parsed vector; otherwise returns the user-args suffix of the raw vector,
    /// which restores every consumed <c>--</c> at its original position.
    /// </summary>
    /// <remarks>
    /// In RtkSharp this reduces to the identity function at runtime: the top-level argument parser
    /// preserves <c>--</c> in a command's args, so <c>run_diff</c> never sees a stripped vector. The
    /// full algorithm is ported (and unit-tested against the Rust vectors) so the parity guarantee
    /// survives any future change to that parser.
    /// </remarks>
    /// <param name="parsedArgs">The (clap-)parsed argument vector, possibly missing <c>--</c> tokens.</param>
    /// <param name="rawArgs">The raw process argument vector to recover stripped <c>--</c> from.</param>
    /// <returns>The argument vector with consumed <c>--</c> tokens restored.</returns>
    internal static IReadOnlyList<string> RestoreDoubleDashWithRaw(
        IReadOnlyList<string> parsedArgs, IReadOnlyList<string> rawArgs)
    {
        ArgumentNullException.ThrowIfNull(parsedArgs);
        ArgumentNullException.ThrowIfNull(rawArgs);

        var rawDashCount = rawArgs.Count(a => a == "--");
        var parsedDashCount = parsedArgs.Count(a => a == "--");

        if (rawDashCount <= parsedDashCount)
        {
            return parsedArgs.ToList();
        }

        var missingDashes = rawDashCount - parsedDashCount;
        var userRegionLen = parsedArgs.Count + missingDashes;

        if (rawArgs.Count <= userRegionLen)
        {
            return parsedArgs.ToList();
        }

        var userRegionStart = rawArgs.Count - userRegionLen;
        return rawArgs.Skip(userRegionStart).ToList();
    }

    // ===================== status =====================

    /// <summary>
    /// Runs <c>git status</c> and prints a compact, grouped view. Ports <c>run_status</c>
    /// (git.rs:815): a narrow compact path (<c>--porcelain -b</c>) for no-arg / branch-or-short-only
    /// invocations, and a minimal-filter path for all other explicit args.
    /// </summary>
    private static async Task<int> RunStatusAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        if (!UsesCompactStatusPath(args))
        {
            return await RunStatusMinimalAsync(args, globalArgs, executor, stdout, stderr).ConfigureAwait(false);
        }

        // Plain status (LC_ALL=C for stable English phrases) feeds detached-HEAD / in-progress-state
        // extraction; porcelain -b omits both. Tolerate its failure (git.rs:855).
        var localeEnv = new Dictionary<string, string?>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };
        var rawStatusArgs = new List<string>(globalArgs) { "status" };
        rawStatusArgs.AddRange(args);
        var rawResult = await ExecAsync(executor, rawStatusArgs, localeEnv).ConfigureAwait(false);
        var rawOutput = rawResult.WasStarted ? rawResult.Stdout : string.Empty;

        var porcelainArgs = new List<string>(globalArgs) { "status", "--porcelain", "-b" };
        var result = await ExecAsync(executor, porcelainArgs, null).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result.Stderr) && result.Stderr.Contains("not a git repository", StringComparison.Ordinal))
        {
            stderr.Write("Not a git repository\n");
            return result.ExitCode;
        }

        string finalOutput;
        try
        {
            var detached = ExtractDetachedHead(rawOutput);
            var formatted = detached is not null
                ? FormatStatusOutputDetached(result.Stdout, detached)
                : FormatStatusOutput(result.Stdout);

            // Surface in-progress state (rebase/merge/cherry-pick/...) that porcelain -b hides.
            var state = ExtractStateHeader(rawOutput);
            finalOutput = state is not null ? $"{state}\n{formatted}" : formatted;
        }
        catch (Exception ex)
        {
            // Mandatory fallback contract: a filter must never crash or hide output.
            stderr.Write($"rtk: filter warning: {ex.Message}\n");
            stdout.Write(result.Stdout);
            return result.ExitCode;
        }

        stdout.Write(finalOutput + "\n");
        return 0;
    }

    /// <summary>
    /// The minimal-filter status path for explicit args that fall outside the compact case: runs
    /// <c>git status &lt;args&gt;</c> and strips hints and blank lines. Ports the non-compact branch of
    /// <c>run_status</c> (git.rs:820-853).
    /// </summary>
    private static async Task<int> RunStatusMinimalAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var statusArgs = new List<string>(globalArgs) { "status" };
        statusArgs.AddRange(args);
        var result = await ExecAsync(executor, statusArgs, null).ConfigureAwait(false);

        if (!Succeeded(result))
        {
            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                stderr.Write(result.Stderr);
            }

            return result.ExitCode;
        }

        if (!string.IsNullOrEmpty(result.Stderr))
        {
            stderr.Write(result.Stderr);
        }

        string filtered;
        try
        {
            filtered = FilterStatusWithArgs(result.Stdout);
        }
        catch (Exception ex)
        {
            // Mandatory fallback contract: a filter must never crash or hide output.
            stderr.Write($"rtk: filter warning: {ex.Message}\n");
            stdout.Write(result.Stdout);
            return result.ExitCode;
        }

        stdout.Write(filtered);
        return 0;
    }

    /// <summary>
    /// Reports whether <c>git status</c> should take the compact <c>--porcelain -b</c> path: true for
    /// no-arg status and for invocations whose only flags are branch/short combinations. Ports
    /// <c>uses_compact_status_path</c> (git.rs:52).
    /// </summary>
    /// <param name="args">The user-supplied <c>git status</c> arguments.</param>
    /// <returns>True when the compact path applies.</returns>
    internal static bool UsesCompactStatusPath(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return true;
        }

        var sawBranch = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-b":
                case "--branch":
                    sawBranch = true;
                    break;
                case "-sb":
                case "-bs":
                    return true;
                case "-s":
                case "--short":
                    break;
                default:
                    return false;
            }
        }

        return sawBranch;
    }

    /// <summary>
    /// Extracts a compact in-progress state summary (rebase/merge/cherry-pick/revert/bisect/am/sparse
    /// checkout) from plain <c>git status</c> output, or null when none is in progress. Porcelain
    /// <c>-b</c> omits git's state header, so hiding it would mislead the user. Ports
    /// <c>extract_state_header</c> (git.rs:736).
    /// </summary>
    /// <param name="raw">The plain <c>git status</c> stdout captured for state extraction.</param>
    /// <returns>The compact state summary, or null.</returns>
    internal static string? ExtractStateHeader(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        foreach (var line in ReadCommand.SplitLines(raw))
        {
            var stripped = line.Trim();

            if (StateStoppers.Any(s => stripped.StartsWith(s, StringComparison.Ordinal)))
            {
                break;
            }

            if (DetectStatusState(stripped) is { } state)
            {
                return StateSummary(state);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the explicit "HEAD detached at/from &lt;ref&gt;" line from plain <c>git status</c>
    /// output, or null when HEAD is on a branch. Ports <c>extract_detached_head</c> (git.rs:771).
    /// </summary>
    /// <param name="raw">The plain <c>git status</c> stdout captured for detached-HEAD extraction.</param>
    /// <returns>The detached-HEAD line, or null.</returns>
    internal static string? ExtractDetachedHead(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return ReadCommand.SplitLines(raw)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("HEAD detached ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Classifies a single plain-status line into a <see cref="GitStatusState"/>, or null. Ports
    /// <c>detect_status_state</c> (git.rs:703).
    /// </summary>
    private static GitStatusState? DetectStatusState(string line)
    {
        if (line.Contains("All conflicts fixed but you are still merging", StringComparison.Ordinal))
        {
            return GitStatusState.MergeReadyToCommit;
        }

        if (line.Contains("You have unmerged paths", StringComparison.Ordinal))
        {
            return GitStatusState.MergeConflicts;
        }

        if (line.Contains("You are currently cherry-picking", StringComparison.Ordinal))
        {
            return GitStatusState.CherryPick;
        }

        if (line.Contains("You are currently reverting", StringComparison.Ordinal))
        {
            return GitStatusState.Revert;
        }

        if (line.Contains("You are currently bisecting", StringComparison.Ordinal))
        {
            return GitStatusState.Bisect;
        }

        if (line.Contains("You are in the middle of an am session", StringComparison.Ordinal))
        {
            return GitStatusState.Am;
        }

        if (line.Contains("You are in a sparse checkout", StringComparison.Ordinal))
        {
            return GitStatusState.SparseCheckout;
        }

        if (RebaseIndicators.Any(i => line.Contains(i, StringComparison.Ordinal)))
        {
            return GitStatusState.Rebase;
        }

        return null;
    }

    /// <summary>Maps a <see cref="GitStatusState"/> to its compact summary. Ports <c>summary</c> (git.rs:679).</summary>
    private static string StateSummary(GitStatusState state) => state switch
    {
        GitStatusState.Rebase => "rebase in progress",
        GitStatusState.MergeConflicts => "merge in progress. unresolved conflicts",
        GitStatusState.MergeReadyToCommit => "merge in progress. no conflicts",
        GitStatusState.CherryPick => "cherry-pick in progress",
        GitStatusState.Revert => "revert in progress",
        GitStatusState.Bisect => "bisect in progress",
        GitStatusState.Am => "am session in progress",
        GitStatusState.SparseCheckout => "sparse checkout enabled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown git status state."),
    };

    // ===================== add =====================

    /// <summary>
    /// Runs <c>git add</c> (defaulting to <c>.</c> when no paths are given) and prints a compact
    /// staged-file summary. Ports <c>run_add</c> (git.rs:913): on success it queries
    /// <c>git diff --cached --stat --shortstat</c> and emits <c>ok &lt;shortstat&gt;</c>, staying
    /// silent for a no-op stage exactly as git itself does.
    /// </summary>
    private static async Task<int> RunAddAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var addArgs = new List<string>(globalArgs) { "add" };
        if (args.Length == 0)
        {
            addArgs.Add(".");
        }
        else
        {
            addArgs.AddRange(args);
        }

        var result = await ExecAsync(executor, addArgs, null).ConfigureAwait(false);

        if (!Succeeded(result))
        {
            stderr.Write("FAILED: git add\n");
            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                stderr.Write(result.Stderr.TrimEnd('\n') + "\n");
            }

            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                stderr.Write(result.Stdout.TrimEnd('\n') + "\n");
            }

            return result.ExitCode;
        }

        var statArgs = new List<string>(globalArgs) { "diff", "--cached", "--stat", "--shortstat" };
        var statResult = await ExecAsync(executor, statArgs, null).ConfigureAwait(false);

        var compact = FormatAddSummary(statResult.Stdout);
        if (compact.Length != 0)
        {
            stdout.Write(compact + "\n");
        }

        return 0;
    }

    // ===================== commit =====================

    /// <summary>
    /// Runs <c>git commit</c> and prints a compact result. Ports <c>run_commit</c> (git.rs:1008):
    /// on success it collapses git's <c>[branch abc1234] message</c> line to <c>ok &lt;short-hash&gt;</c>,
    /// reports <c>ok (nothing to commit)</c> for the no-op case, and otherwise forwards git's own
    /// output and propagates the exit code. Stdin is inherited so an editor/GPG prompt still works.
    /// </summary>
    private static async Task<int> RunCommitAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var commitArgs = new List<string>(globalArgs) { "commit" };
        commitArgs.AddRange(args);

        var result = await ExecAsync(executor, commitArgs, null).ConfigureAwait(false);

        if (Succeeded(result))
        {
            var firstLine = ReadCommand.SplitLines(result.Stdout).FirstOrDefault() ?? string.Empty;
            stdout.Write(ParseCommitOutput(firstLine) + "\n");
            return 0;
        }

        if (result.Stderr.Contains("nothing to commit", StringComparison.Ordinal)
            || result.Stdout.Contains("nothing to commit", StringComparison.Ordinal))
        {
            stdout.Write("ok (nothing to commit)\n");
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            stderr.Write(result.Stderr);
        }

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            stderr.Write(result.Stdout);
        }

        return result.ExitCode;
    }

    // ===================== push =====================

    /// <summary>
    /// Git push progress prefixes (matched after trimming leading whitespace) dropped from the stream.
    /// Ported from <c>GIT_PUSH_NOISE_PREFIXES</c> (git.rs:1063-1070).
    /// </summary>
    private static readonly string[] GitPushNoisePrefixes =
    {
        "Enumerating objects:",
        "Counting objects:",
        "Compressing objects:",
        "Writing objects:",
        "Delta compression using",
        "Total ",
    };

    /// <summary>
    /// Runs <c>git push</c>, stripping progress noise and appending a compact <c>ok</c> summary.
    /// Ports <c>run_push</c> (git.rs:1118) and its <c>GitPushLineHandler</c> streaming filter
    /// (git.rs:1072-1116): non-noise lines pass through on their original stream, then on success a
    /// summary line — <c>ok (up-to-date)</c>, <c>ok &lt;pushed-ref&gt;</c>, or <c>ok</c> — is emitted.
    /// </summary>
    /// <remarks>
    /// The Rust filter streams stdout and stderr interleaved and routes the summary to the file
    /// descriptor of the last line seen. Git push writes its progress and ref updates to stderr and
    /// its trailing tracking-setup message to stdout, so the natural emission order is stderr lines
    /// then the stdout trailer. RtkSharp captures both streams and replays kept stderr lines (to
    /// stderr) before kept stdout lines (to stdout), then emits the summary to the stream of the last
    /// replayed line (stdout when the child wrote any, else stderr) — observably identical to the
    /// oracle's interleaved stream for git push's output.
    /// </remarks>
    private static async Task<int> RunPushAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var pushArgs = new List<string>(globalArgs) { "push" };
        pushArgs.AddRange(args);

        var result = await ExecAsync(executor, pushArgs, null).ConfigureAwait(false);

        var upToDate = false;
        string? pushedRef = null;

        void Observe(string line)
        {
            if (line.Contains("Everything up-to-date", StringComparison.Ordinal))
            {
                upToDate = true;
            }

            pushedRef ??= ExtractPushedRef(line);
        }

        foreach (var line in ReadCommand.SplitLines(result.Stderr))
        {
            if (IsPushNoiseLine(line))
            {
                continue;
            }

            Observe(line);
            stderr.Write(line + "\n");
        }

        foreach (var line in ReadCommand.SplitLines(result.Stdout))
        {
            if (IsPushNoiseLine(line))
            {
                continue;
            }

            Observe(line);
            stdout.Write(line + "\n");
        }

        if (result.ExitCode == 0)
        {
            var summary = upToDate ? "ok (up-to-date)" : pushedRef is not null ? $"ok {pushedRef}" : "ok";
            var summaryWriter = result.Stdout.Length != 0 ? stdout : stderr;
            summaryWriter.Write(summary + "\n");
        }

        return result.ExitCode;
    }

    /// <summary>
    /// Reports whether a <c>git push</c> line is progress noise to drop: a blank line, or a line whose
    /// leading-whitespace-trimmed text starts with one of <see cref="GitPushNoisePrefixes"/>. Ports
    /// <c>GitPushLineHandler::should_skip</c> (git.rs:1079-1087).
    /// </summary>
    private static bool IsPushNoiseLine(string line)
    {
        if (line.Length == 0)
        {
            return true;
        }

        var trimmed = line.TrimStart();
        return GitPushNoisePrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Extracts the destination ref from a <c>git push</c> line containing <c> -&gt; </c> (e.g.
    /// <c> * [new branch]      main -&gt; main</c> yields <c>main</c>), or null when the line has no
    /// such marker. Ports the <c>observe_line</c> ref parse (git.rs:1093-1100).
    /// </summary>
    /// <param name="line">A <c>git push</c> output line.</param>
    /// <returns>The pushed destination ref, or null.</returns>
    internal static string? ExtractPushedRef(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var idx = line.IndexOf(" -> ", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var after = line[(idx + 4)..];
        var dest = after.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(dest) ? null : dest;
    }

    // ===================== pull =====================

    /// <summary>
    /// Runs <c>git pull</c> and prints a compact result. Ports <c>run_pull</c> (git.rs:1150):
    /// <c>ok (up-to-date)</c> when nothing changed, <c>ok N files +A -R</c> when the merge/rebase
    /// touched files, else <c>ok</c>; on failure it forwards git's output and propagates the exit code.
    /// </summary>
    private static async Task<int> RunPullAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var pullArgs = new List<string>(globalArgs) { "pull" };
        pullArgs.AddRange(args);

        var result = await ExecAsync(executor, pullArgs, null).ConfigureAwait(false);

        if (!Succeeded(result))
        {
            stderr.Write("FAILED: git pull\n");
            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                stderr.Write(result.Stderr.TrimEnd('\n') + "\n");
            }

            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                stderr.Write(result.Stdout.TrimEnd('\n') + "\n");
            }

            return result.ExitCode;
        }

        stdout.Write(FormatPullSummary(result.Stdout) + "\n");
        return 0;
    }

    // ===================== branch =====================

    /// <summary>
    /// Branch action flags that indicate a write operation (delete/rename/copy/upstream). Ported from
    /// the <c>has_action_flag</c> predicate (git.rs:1243-1255).
    /// </summary>
    private static readonly string[] BranchActionFlags =
    {
        "-d", "-D", "-m", "-M", "-c", "-C", "--set-upstream-to", "-u", "--unset-upstream", "--edit-description",
    };

    /// <summary>
    /// List-mode branch flags that keep <c>git branch</c> in listing mode (rather than creating a
    /// branch from a positional argument). Ported from the <c>has_list_flag</c> predicate
    /// (git.rs:1261-1277).
    /// </summary>
    private static readonly string[] BranchListFlags =
    {
        "-a", "--all", "-r", "--remotes", "--list", "--merged", "--no-merged",
        "--contains", "--no-contains", "--format", "--sort", "--points-at",
    };

    /// <summary>
    /// Runs <c>git branch</c>. Ports <c>run_branch</c> (git.rs:1235): <c>--show-current</c> passes the
    /// raw ref through; action flags or a positional branch name (branch creation) collapse to
    /// <c>ok</c>; otherwise the branch list is compacted by <see cref="FilterBranchOutput"/>.
    /// </summary>
    private static async Task<int> RunBranchAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var hasActionFlag = args.Any(a =>
            BranchActionFlags.Contains(a) || a.StartsWith("--set-upstream-to=", StringComparison.Ordinal));
        var hasShowFlag = args.Any(a => a == "--show-current");
        var hasListFlag = args.Any(IsBranchListFlag);
        var hasPositionalArg = args.Any(a => !a.StartsWith('-'));

        // --show-current: passthrough with raw stdout (not "ok").
        if (hasShowFlag)
        {
            var showArgs = new List<string>(globalArgs) { "branch" };
            showArgs.AddRange(args);
            var showResult = await ExecAsync(executor, showArgs, null).ConfigureAwait(false);

            if (Succeeded(showResult))
            {
                stdout.Write(showResult.Stdout.Trim() + "\n");
                return 0;
            }

            stderr.Write($"FAILED: git branch {string.Join(' ', args)}\n");
            if (!string.IsNullOrWhiteSpace(showResult.Stderr))
            {
                stderr.Write(showResult.Stderr.TrimEnd('\n') + "\n");
            }

            return showResult.ExitCode;
        }

        // Write operation: action flags, or positional args without list flags (= branch creation).
        if (hasActionFlag || (hasPositionalArg && !hasListFlag))
        {
            var writeArgs = new List<string>(globalArgs) { "branch" };
            writeArgs.AddRange(args);
            var writeResult = await ExecAsync(executor, writeArgs, null).ConfigureAwait(false);

            if (Succeeded(writeResult))
            {
                stdout.Write("ok\n");
                return 0;
            }

            stderr.Write($"FAILED: git branch {string.Join(' ', args)}\n");
            if (!string.IsNullOrWhiteSpace(writeResult.Stderr))
            {
                stderr.Write(writeResult.Stderr.TrimEnd('\n') + "\n");
            }

            if (!string.IsNullOrWhiteSpace(writeResult.Stdout))
            {
                stderr.Write(writeResult.Stdout.TrimEnd('\n') + "\n");
            }

            return writeResult.ExitCode;
        }

        // List mode: compact branch list.
        var listArgs = new List<string>(globalArgs) { "branch" };
        if (!hasListFlag)
        {
            listArgs.Add("-a");
        }

        listArgs.Add("--no-color");
        listArgs.AddRange(args);

        var result = await ExecAsync(executor, listArgs, null).ConfigureAwait(false);

        if (!Succeeded(result))
        {
            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                stderr.Write(result.Stderr);
            }

            return result.ExitCode;
        }

        string filtered;
        try
        {
            filtered = FilterBranchOutput(result.Stdout);
        }
        catch (Exception ex)
        {
            // Mandatory fallback contract: a filter must never crash or hide output.
            stderr.Write($"rtk: filter warning: {ex.Message}\n");
            stdout.Write(result.Stdout);
            return 0;
        }

        stdout.Write(filtered + "\n");
        return 0;
    }

    /// <summary>Whether a branch argument is a list-mode flag (bare or <c>=</c>-valued). Ports the <c>has_list_flag</c> predicate (git.rs:1261-1277).</summary>
    private static bool IsBranchListFlag(string a) =>
        BranchListFlags.Contains(a)
        || a.StartsWith("--format=", StringComparison.Ordinal)
        || a.StartsWith("--sort=", StringComparison.Ordinal)
        || a.StartsWith("--points-at=", StringComparison.Ordinal);

    // ===================== fetch =====================

    /// <summary>
    /// Runs <c>git fetch</c> and prints a compact result. Ports <c>run_fetch</c> (git.rs:1446):
    /// counts new refs from stderr (lines containing <c>-&gt;</c> or <c>[new</c>) and emits
    /// <c>ok fetched (N new refs)</c>, or <c>ok fetched</c> when none; on failure it forwards git's
    /// output and propagates the exit code.
    /// </summary>
    private static async Task<int> RunFetchAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var fetchArgs = new List<string>(globalArgs) { "fetch" };
        fetchArgs.AddRange(args);

        var result = await ExecAsync(executor, fetchArgs, null).ConfigureAwait(false);

        if (!Succeeded(result))
        {
            stderr.Write("FAILED: git fetch\n");
            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                stderr.Write(result.Stderr.TrimEnd('\n') + "\n");
            }

            return result.ExitCode;
        }

        stdout.Write(FormatFetchSummary(result.Stderr) + "\n");
        return 0;
    }

    // ===================== stash =====================

    /// <summary>
    /// Stash subcommands routed to the generic <c>ok stash &lt;sub&gt;</c> action arm (everything but
    /// list/show and the push/save default). Ported from the match arm at git.rs:1568.
    /// </summary>
    private static readonly string[] StashActionSubcommands =
    {
        "apply", "branch", "clear", "create", "drop", "export", "import", "pop", "store",
    };

    /// <summary>
    /// Runs <c>git stash</c> with an optional leading subcommand. Ports <c>run_stash</c>
    /// (git.rs:1509): <c>list</c> and <c>show</c> get bespoke compaction; apply/pop/drop/… collapse to
    /// <c>ok stash &lt;sub&gt;</c>; a bare stash (or push/save, or an unknown token treated as a
    /// pathspec) creates a stash and reports <c>ok stashed</c> (or <c>No local changes to save</c>).
    /// </summary>
    private static async Task<int> RunStashAsync(
        string[] subArgs, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var subcommand = subArgs.Length > 0 ? subArgs[0] : null;
        var args = subArgs.Length > 0 ? subArgs.Skip(1).ToArray() : Array.Empty<string>();

        switch (subcommand)
        {
            case "list":
                {
                    var listArgs = new List<string>(globalArgs) { "stash", "list" };
                    var result = await ExecAsync(executor, listArgs, null).ConfigureAwait(false);

                    if (result.Stdout.Trim().Length == 0)
                    {
                        stdout.Write("No stashes\n");
                        return 0;
                    }

                    stdout.Write(FilterStashList(result.Stdout) + "\n");
                    return 0;
                }

            case "show":
                {
                    var showArgs = new List<string>(globalArgs) { "stash", "show", "-p" };
                    showArgs.AddRange(args);
                    var result = await ExecAsync(executor, showArgs, null).ConfigureAwait(false);

                    if (result.Stdout.Trim().Length == 0)
                    {
                        stdout.Write("Empty stash\n");
                        return 0;
                    }

                    stdout.Write(CompactDiff(result.Stdout, 100) + "\n");
                    return 0;
                }

            case not null when StashActionSubcommands.Contains(subcommand):
                {
                    var actionArgs = new List<string>(globalArgs) { "stash", subcommand };
                    actionArgs.AddRange(args);
                    var result = await ExecAsync(executor, actionArgs, null).ConfigureAwait(false);

                    if (Succeeded(result))
                    {
                        stdout.Write(FormatStashMessage(subcommand, result.Stdout, result.Stderr) + "\n");
                        return 0;
                    }

                    stderr.Write($"FAILED: git stash {subcommand}\n");
                    if (!string.IsNullOrWhiteSpace(result.Stderr))
                    {
                        stderr.Write(result.Stderr.TrimEnd('\n') + "\n");
                    }

                    return result.ExitCode;
                }

            default:
                {
                    // Bare stash, push/save, or an unknown token treated as a pathspec to `git stash push`.
                    var (sub, extra) = subcommand switch
                    {
                        "save" => ("save", (string?)null),
                        "push" => ("push", null),
                        null => ("push", null),
                        _ => ("push", subcommand),
                    };

                    var pushArgs = new List<string>(globalArgs) { "stash", sub };
                    if (extra is not null)
                    {
                        pushArgs.Add(extra);
                    }

                    pushArgs.AddRange(args);
                    var result = await ExecAsync(executor, pushArgs, null).ConfigureAwait(false);

                    if (Succeeded(result))
                    {
                        stdout.Write(FormatStashMessage(subcommand, result.Stdout, result.Stderr) + "\n");
                        return 0;
                    }

                    stderr.Write($"FAILED: git stash {sub}\n");
                    if (!string.IsNullOrWhiteSpace(result.Stderr))
                    {
                        stderr.Write(result.Stderr.TrimEnd('\n') + "\n");
                    }

                    return result.ExitCode;
                }
        }
    }

    // ===================== worktree =====================

    /// <summary>
    /// Worktree subcommands that mutate state and pass through to a compact <c>ok</c>. Ported from the
    /// <c>has_action</c> predicate (git.rs:1678-1680).
    /// </summary>
    private static readonly string[] WorktreeActionSubcommands =
    {
        "add", "remove", "prune", "lock", "unlock", "move",
    };

    /// <summary>
    /// Runs <c>git worktree</c>. Ports <c>run_worktree</c> (git.rs:1670): action subcommands
    /// (add/remove/prune/…) collapse to <c>ok</c>; otherwise <c>git worktree list</c> is compacted by
    /// <see cref="FilterWorktreeList"/>.
    /// </summary>
    private static async Task<int> RunWorktreeAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var hasAction = args.Any(WorktreeActionSubcommands.Contains);

        if (hasAction)
        {
            var actionArgs = new List<string>(globalArgs) { "worktree" };
            actionArgs.AddRange(args);
            var result = await ExecAsync(executor, actionArgs, null).ConfigureAwait(false);

            if (Succeeded(result))
            {
                stdout.Write("ok\n");
                return 0;
            }

            stderr.Write($"FAILED: git worktree {string.Join(' ', args)}\n");
            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                stderr.Write(result.Stderr.TrimEnd('\n') + "\n");
            }

            return result.ExitCode;
        }

        var listArgs = new List<string>(globalArgs) { "worktree", "list" };
        var listResult = await ExecAsync(executor, listArgs, null).ConfigureAwait(false);

        stdout.Write(FilterWorktreeList(listResult.Stdout) + "\n");
        return 0;
    }

    // ===================== passthrough =====================

    /// <summary>
    /// Runs any unfiltered git subcommand raw, threading the global args through and inheriting the
    /// child's stdio so its output reaches the terminal unchanged. Propagates the exit code. Ports
    /// <c>run_passthrough</c> (git.rs:1757).
    /// </summary>
    private static async Task<int> RunPassthroughAsync(
        string[] args, List<string> globalArgs, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var childArgs = new List<string>(globalArgs);
        childArgs.AddRange(args);

        var result = await executor
            .ExecuteAsync(new ExecutionRequest("git", childArgs, CaptureMode: ExecutionCaptureMode.Inherit))
            .ConfigureAwait(false);

        // Inherited stdio: git wrote directly to the terminal. If capture happened anyway (e.g. a
        // recording executor in tests), forward it so nothing is dropped.
        if (!string.IsNullOrEmpty(result.Stdout))
        {
            stdout.Write(result.Stdout);
        }

        if (!string.IsNullOrEmpty(result.Stderr))
        {
            stderr.Write(result.Stderr);
        }

        return result.ExitCode;
    }

    // ===================== helpers =====================

    /// <summary>Runs <c>git</c> with the given argument list and optional environment overrides, capturing both streams.</summary>
    private static async Task<ExecutionResult> ExecAsync(
        IProcessExecutor executor, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?>? environment)
    {
        var request = new ExecutionRequest(
            "git", args, Environment: environment, CaptureMode: ExecutionCaptureMode.Separate);
        return await executor.ExecuteAsync(request).ConfigureAwait(false);
    }

    /// <summary>Whether the child process started and exited zero (Rust's <c>CaptureResult::success</c>).</summary>
    private static bool Succeeded(ExecutionResult result) => result.WasStarted && result.ExitCode == 0;
}
