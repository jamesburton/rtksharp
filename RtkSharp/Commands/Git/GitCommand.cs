using System.Text;
using RtkSharp.Commands.System;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Git;

/// <summary>
/// Compact <c>git</c> proxy. This verb parses the global git options that may precede a subcommand
/// (via <see cref="GitGlobalArgs"/>), then dispatches on the subcommand exactly as Rust's
/// <c>Commands::Git</c> routing in <c>main.rs</c> does. Ported from <c>src/cmds/git/git.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope (Phase 7a, Tasks 1-2).</b> <c>status</c>, <c>log</c>, <c>diff</c>, and <c>show</c> are
/// filtered here. Every other subcommand — the yet-to-be-ported filters (<c>add</c>, <c>commit</c>,
/// <c>push</c>, <c>pull</c>, <c>branch</c>, <c>fetch</c>, <c>stash</c>, <c>worktree</c>) and any
/// genuinely unknown subcommand — is routed to <see cref="RunPassthroughAsync"/>, which runs raw
/// <c>git</c> with the global args threaded through and propagates the exit code. This mirrors Rust's
/// <c>GitCommands::Other</c> external-subcommand passthrough (git.rs:1757) and is the correct interim
/// behavior for the not-yet-filtered verbs: unfiltered output, never blocked.
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

    /// <summary>Commit-block separator RTK injects via <see cref="RtkLogFormat"/> (git.rs:447).</summary>
    private const string LogBlockSeparator = "---END---";

    /// <summary>Truncation width for log headers/bodies when RTK set the default limit (git.rs:555).</summary>
    private const int LogTruncateDefault = 80;

    /// <summary>Wider truncation width when the user set an explicit <c>-N</c> limit (git.rs:555).</summary>
    private const int LogTruncateUserLimit = 120;

    /// <summary>Maximum commit-body lines kept per commit before an omission marker (git.rs:596).</summary>
    private const int LogMaxBodyLines = 3;

    /// <summary>
    /// Default per-diff line budget for the compact filter. Rust threads <c>max_lines</c> from
    /// main.rs, which always passes <c>None</c> for git, so <c>compact_diff</c> resolves to
    /// <c>unwrap_or(500)</c> (git.rs:197, 310). RtkSharp inlines the same default.
    /// </summary>
    private const int CompactDiffDefaultMaxLines = 500;

    /// <summary>Maximum +/- lines shown per hunk before the remainder is counted as truncated (git.rs:338).</summary>
    private const int CompactDiffMaxHunkLines = 100;

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

    /// <summary>
    /// Filters git log output: truncates long lines and, for RTK's injected format, keeps a commit
    /// header plus up to three body lines with an omission marker. When <paramref name="userSetLimit"/>
    /// is true the caller passed an explicit <c>-N</c>, so line-capping is skipped (git already
    /// returned exactly N commits) and a wider truncation width preserves rebase/squash context.
    /// Ports <c>filter_log_output</c> (git.rs:549).
    /// </summary>
    /// <param name="output">The raw <c>git log</c> stdout.</param>
    /// <param name="limit">The commit/line cap RTK applies when it set the default limit.</param>
    /// <param name="userSetLimit">Whether the user passed an explicit <c>-N</c> limit.</param>
    /// <param name="userFormat">Whether the user supplied their own <c>--oneline</c>/<c>--pretty</c>/<c>--format</c>.</param>
    /// <returns>The filtered log text (no trailing newline).</returns>
    internal static string FilterLogOutput(string output, int limit, bool userSetLimit, bool userFormat)
    {
        ArgumentNullException.ThrowIfNull(output);

        var truncateWidth = userSetLimit ? LogTruncateUserLimit : LogTruncateDefault;

        // User-supplied format: RTK injected no ---END--- markers, so truncate line-by-line.
        if (userFormat)
        {
            var lines = ReadCommand.SplitLines(output);
            var maxLines = userSetLimit ? lines.Count : limit;
            return string.Join("\n", lines.Take(maxLines).Select(l => TruncateLine(l, truncateWidth)));
        }

        // RTK-injected format: split into commit blocks separated by ---END---.
        var commits = output.Split(LogBlockSeparator);
        var maxCommits = userSetLimit ? commits.Length : limit;

        var result = new List<string>();
        foreach (var rawBlock in commits.Take(maxCommits))
        {
            var block = rawBlock.Trim();
            if (block.Length == 0)
            {
                continue;
            }

            var blockLines = ReadCommand.SplitLines(block);
            if (blockLines.Count == 0)
            {
                continue;
            }

            // First line is the header: hash subject (date) <author>.
            var header = TruncateLine(blockLines[0].Trim(), truncateWidth);

            // Remaining lines: keep up to 3 non-empty, non-trailer lines.
            var allBodyLines = blockLines
                .Skip(1)
                .Select(l => l.Trim())
                .Where(l =>
                    l.Length != 0
                    && !l.StartsWith("Signed-off-by:", StringComparison.Ordinal)
                    && !l.StartsWith("Co-authored-by:", StringComparison.Ordinal))
                .ToList();

            var bodyOmitted = Math.Max(0, allBodyLines.Count - LogMaxBodyLines);
            var bodyLines = allBodyLines.Take(LogMaxBodyLines).ToList();

            if (bodyLines.Count == 0)
            {
                result.Add(header);
            }
            else
            {
                var entry = new StringBuilder(header);
                foreach (var body in bodyLines)
                {
                    entry.Append($"\n  {TruncateLine(body, truncateWidth)}");
                }

                if (bodyOmitted > 0)
                {
                    entry.Append($"\n  [+{bodyOmitted} lines omitted]");
                }

                result.Add(entry.ToString());
            }
        }

        return string.Join("\n", result).Trim();
    }

    /// <summary>
    /// Truncates a single line to <paramref name="width"/> Unicode scalar values, appending
    /// <c>...</c> when it was longer. Ports <c>truncate_line</c> (git.rs:616), counting <c>char</c>s
    /// the way Rust counts <c>chars()</c> so multibyte input is never split mid-character.
    /// </summary>
    /// <param name="line">The line to truncate.</param>
    /// <param name="width">The maximum width in Unicode scalar values.</param>
    /// <returns>The (possibly truncated) line.</returns>
    internal static string TruncateLine(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);

        var runes = line.EnumerateRunes().ToList();
        if (runes.Count <= width)
        {
            return line;
        }

        var kept = string.Concat(runes.Take(width - 3).Select(r => r.ToString()));
        return $"{kept}...";
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
    /// Condenses a unified diff to the changed lines, keeping hunk headers and per-file <c>+A -R</c>
    /// counts while dropping index/mode metadata and unchanged context that precedes the first change
    /// in a hunk. Caps each hunk at <see cref="CompactDiffMaxHunkLines"/> shown lines and the whole
    /// output at <paramref name="maxLines"/> entries, appending truncation markers when either limit
    /// is hit. Ports <c>compact_diff</c> (git.rs:330).
    /// </summary>
    /// <param name="diff">The raw unified diff text.</param>
    /// <param name="maxLines">The overall entry budget before the diff is truncated.</param>
    /// <returns>The compacted diff (no trailing newline).</returns>
    internal static string CompactDiff(string diff, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var result = new List<string>();
        var currentFile = string.Empty;
        var added = 0;
        var removed = 0;
        var inHunk = false;
        var hunkShown = 0;
        var hunkSkipped = 0;
        var wasTruncated = false;

        foreach (var line in ReadCommand.SplitLines(diff))
        {
            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                // Flush hunk truncation before starting a new file.
                if (hunkSkipped > 0)
                {
                    result.Add($"  ... ({hunkSkipped} lines truncated)");
                    wasTruncated = true;
                    hunkSkipped = 0;
                }

                if (currentFile.Length != 0 && (added > 0 || removed > 0))
                {
                    result.Add($"  +{added} -{removed}");
                }

                currentFile = ExtractDiffFileName(line);
                result.Add($"\n{currentFile}");
                added = 0;
                removed = 0;
                inHunk = false;
                hunkShown = 0;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                // Flush hunk truncation before starting a new hunk.
                if (hunkSkipped > 0)
                {
                    result.Add($"  ... ({hunkSkipped} lines truncated)");
                    wasTruncated = true;
                    hunkSkipped = 0;
                }

                inHunk = true;
                hunkShown = 0;

                // Preserve the full unified diff hunk header, including trailing function/symbol context.
                result.Add($"  {line}");
            }
            else if (inHunk)
            {
                if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    added++;
                    if (hunkShown < CompactDiffMaxHunkLines)
                    {
                        result.Add($"  {line}");
                        hunkShown++;
                    }
                    else
                    {
                        hunkSkipped++;
                    }
                }
                else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
                {
                    removed++;
                    if (hunkShown < CompactDiffMaxHunkLines)
                    {
                        result.Add($"  {line}");
                        hunkShown++;
                    }
                    else
                    {
                        hunkSkipped++;
                    }
                }
                else if (hunkShown < CompactDiffMaxHunkLines && !line.StartsWith('\\'))
                {
                    // Context line: only kept once at least one +/- line has been shown in this hunk.
                    if (hunkShown > 0)
                    {
                        result.Add($"  {line}");
                        hunkShown++;
                    }
                }
            }

            if (result.Count >= maxLines)
            {
                result.Add("\n... (more changes truncated)");
                wasTruncated = true;
                break;
            }
        }

        // Flush the final hunk.
        if (hunkSkipped > 0)
        {
            result.Add($"  ... ({hunkSkipped} lines truncated)");
            wasTruncated = true;
        }

        if (currentFile.Length != 0 && (added > 0 || removed > 0))
        {
            result.Add($"  +{added} -{removed}");
        }

        if (wasTruncated)
        {
            result.Add("[full diff: rtk git diff --no-compact]");
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Extracts the post-image path from a <c>diff --git a/… b/…</c> header, matching Rust's
    /// <c>line.split(" b/").nth(1).unwrap_or("unknown")</c> (git.rs:352).
    /// </summary>
    private static string ExtractDiffFileName(string line)
    {
        var parts = line.Split(" b/");
        return parts.Length > 1 ? parts[1] : "unknown";
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
    /// Formats porcelain <c>-b</c> status output into RTK's compact view. Ports
    /// <c>format_status_output</c> (git.rs:625).
    /// </summary>
    /// <param name="porcelain">The <c>git status --porcelain -b</c> stdout.</param>
    /// <returns>The formatted status text.</returns>
    internal static string FormatStatusOutput(string porcelain) => FormatStatusInner(porcelain, null);

    /// <summary>
    /// Formats porcelain <c>-b</c> status output, substituting an explicit detached-HEAD reference
    /// for the opaque <c>## HEAD (no branch)</c> branch line. Ports
    /// <c>format_status_output_detached</c> (git.rs:629).
    /// </summary>
    /// <param name="porcelain">The <c>git status --porcelain -b</c> stdout.</param>
    /// <param name="detachedRef">The explicit "HEAD detached at/from &lt;ref&gt;" line to display.</param>
    /// <returns>The formatted status text.</returns>
    internal static string FormatStatusOutputDetached(string porcelain, string detachedRef) =>
        FormatStatusInner(porcelain, detachedRef);

    /// <summary>
    /// Shared status formatter. Ports <c>format_status_inner</c> (git.rs:633): rewrites the porcelain
    /// branch header <c>## &lt;branch&gt;</c> to <c>* &lt;branch&gt;</c> (or the detached ref), keeps the
    /// remaining change lines verbatim, and appends a clean marker when nothing but the branch line
    /// is present.
    /// </summary>
    private static string FormatStatusInner(string porcelain, string? detached)
    {
        ArgumentNullException.ThrowIfNull(porcelain);

        var lines = ReadCommand.SplitLines(porcelain).Where(l => l.Trim().Length != 0).ToList();

        if (lines.Count == 0)
        {
            return "Clean working tree";
        }

        var output = new List<string>();

        var branchLine = lines[0];
        if (branchLine.StartsWith("##", StringComparison.Ordinal))
        {
            var branch = TrimStartMatches(branchLine, "## ");
            output.Add($"* {detached ?? branch}");
        }
        else
        {
            output.Add(branchLine);
        }

        for (var i = 1; i < lines.Count; i++)
        {
            output.Add(lines[i]);
        }

        if (lines.Count == 1 && lines[0].StartsWith("##", StringComparison.Ordinal))
        {
            output.Add("clean — nothing to commit");
        }

        return string.Join("\n", output);
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

    /// <summary>
    /// Minimal filtering for <c>git status</c> with user-provided args: drops blank lines and git
    /// hints, short-circuits on a clean working tree, and returns <c>ok</c> when nothing remains.
    /// Ports <c>filter_status_with_args</c> (git.rs:779).
    /// </summary>
    /// <param name="output">The raw <c>git status</c> stdout.</param>
    /// <returns>The filtered status text (no trailing newline).</returns>
    internal static string FilterStatusWithArgs(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new List<string>();

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            // Skip git hints — can appear at the start of or within a line.
            if (trimmed.StartsWith("(use \"git", StringComparison.Ordinal)
                || trimmed.StartsWith("(create/copy files", StringComparison.Ordinal)
                || trimmed.Contains("(use \"git add", StringComparison.Ordinal)
                || trimmed.Contains("(use \"git restore", StringComparison.Ordinal))
            {
                continue;
            }

            // Special case: clean working tree.
            if (trimmed.Contains("nothing to commit", StringComparison.Ordinal)
                && trimmed.Contains("working tree clean", StringComparison.Ordinal))
            {
                result.Add(trimmed);
                break;
            }

            result.Add(line);
        }

        return result.Count == 0 ? "ok" : string.Join("\n", result);
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

    /// <summary>
    /// Removes every leading repetition of <paramref name="prefix"/> from <paramref name="value"/>,
    /// matching Rust's <c>str::trim_start_matches</c> (which strips repeated whole-pattern prefixes).
    /// </summary>
    private static string TrimStartMatches(string value, string prefix)
    {
        var span = value.AsSpan();
        while (span.StartsWith(prefix, StringComparison.Ordinal))
        {
            span = span[prefix.Length..];
        }

        return span.ToString();
    }
}
