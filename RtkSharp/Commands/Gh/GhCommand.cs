using System.Text.Json;
using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Gh;
using RtkSharp.Filters.Commands.Git;

namespace RtkSharp.Commands.Gh;

/// <summary>
/// Compact <c>gh</c> (GitHub CLI) proxy. Dispatches on the <c>gh</c> subcommand exactly as Rust's
/// <c>gh_cmd::run</c> (gh_cmd.rs:193) does, filtering JSON-backed output into terse agent-friendly
/// summaries. Ported from <c>src/cmds/git/gh_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The <c>pr</c> family (<c>list</c>/<c>view</c>/<c>checks</c>/<c>status</c>/<c>diff</c>),
/// the <c>issue</c> family (<c>list</c>/<c>view</c>), the <c>run</c> family (<c>list</c>/<c>view</c>),
/// and <c>repo view</c> are filtered here. The top-level <c>--json</c> structured-output guard
/// (gh_cmd.rs:195) and the per-subcommand <c>--jq</c>/<c>--template</c>/<c>--web</c>/<c>--comments</c>/
/// <c>--log</c>/<c>--log-failed</c> guards are ported in full: when the user asks gh for structured,
/// web, or log output, RTK passes through raw so it is never corrupted.
/// </para>
/// <para>
/// <b>Passthrough by design (faithful to Rust).</b> <c>gh api</c> passes through unchanged
/// (gh_cmd.rs:982): converting its JSON to a schema would destroy every value and force a re-fetch, so
/// Rust never filters it. <c>gh run watch</c> is not a Rust <c>run</c> arm (gh_cmd.rs:712) so it falls
/// through to passthrough — this preserves gh's live streaming output, which RTK has no filtered mode
/// for. <c>gh release</c> has no Rust filter at all (no <c>release</c> arm in <c>run</c>,
/// gh_cmd.rs:199-209) so it too passes through. The <c>pr</c> write subcommands
/// (<c>create</c>/<c>merge</c>/<c>comment</c>/<c>edit</c>) route to passthrough: Rust's <c>pr merge</c>
/// already passes through (gh_cmd.rs:908), and the confirmation-formatting arms are out of the
/// read-only scope.
/// </para>
/// <para>
/// <b>Omitted side effects.</b> Following the <see cref="GitCommand"/> precedent, Rust's token-savings
/// tracking (<c>TimedExecution</c>) and <c>verbose</c> diagnostics are omitted — metrics/diagnostic
/// side effects that do not change filtered output.
/// </para>
/// </remarks>
public static partial class GhCommand
{
    // gh flags that take a value — skipped along with their value when isolating the positional
    // identifier from extra flags (gh_cmd.rs:125-134).
    private static readonly string[] FlagsWithValue =
    {
        "-R", "--repo", "-q", "--jq", "-t", "--template", "--job", "--attempt",
    };

    /// <summary>
    /// Registry entry point for the <c>gh</c> verb. Reads the ambient <c>--ultra-compact</c> flag
    /// (see <see cref="RuntimeOptions"/>) and dispatches with the real process executor.
    /// </summary>
    /// <param name="args">The arguments following the <c>gh</c> verb (subcommand first).</param>
    /// <returns>The child <c>gh</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, RuntimeOptions.UltraCompact, new ProcessExecutor(), Console.Out, Console.Error);

    /// <summary>
    /// Test-friendly overload accepting an explicit ultra-compact flag, executor, and writers.
    /// </summary>
    /// <param name="args">The arguments following the <c>gh</c> verb (subcommand first).</param>
    /// <param name="ultraCompact">Whether to prefer ultra-compact summaries.</param>
    /// <param name="executor">The process executor used to run <c>gh</c>.</param>
    /// <param name="stdout">The destination for filtered output.</param>
    /// <param name="stderr">The destination for warnings and errors.</param>
    /// <returns>The child <c>gh</c> process's exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0)
        {
            // No subcommand: run raw gh (prints usage, exits nonzero), matching clap requiring one.
            return await PassthroughAsync(executor, stdout, stderr, Array.Empty<string>()).ConfigureAwait(false);
        }

        var subcommand = args[0];
        var subArgs = args.Skip(1).ToArray();

        // When the user explicitly passes --json they want raw gh JSON, not RTK filtering (gh_cmd.rs:195).
        if (HasJsonFlag(subArgs))
        {
            return await PassthroughAsync(executor, stdout, stderr, Prepend(subcommand, subArgs)).ConfigureAwait(false);
        }

        return subcommand switch
        {
            "pr" => await RunPrAsync(subArgs, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "issue" => await RunIssueAsync(subArgs, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "run" => await RunWorkflowAsync(subArgs, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "repo" => await RunRepoAsync(subArgs, executor, stdout, stderr).ConfigureAwait(false),

            // gh api passes through unchanged (gh_cmd.rs:982): filtering its JSON would destroy every value.
            "api" => await PassthroughAsync(executor, stdout, stderr, Prepend("api", subArgs)).ConfigureAwait(false),

            // release, and every other subcommand, has no Rust filter — passthrough never corrupts output.
            _ => await PassthroughAsync(executor, stdout, stderr, Prepend(subcommand, subArgs)).ConfigureAwait(false),
        };
    }

    // ===================== pr =====================

    /// <summary>Dispatches <c>gh pr</c> subcommands. Ports <c>run_pr</c> (gh_cmd.rs:212).</summary>
    private static async Task<int> RunPrAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            return await PassthroughAsync(executor, stdout, stderr, Prepend("pr", args)).ConfigureAwait(false);
        }

        var rest = args.Skip(1).ToArray();
        return args[0] switch
        {
            "list" => await ListPrsAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "view" => await ViewPrAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "checks" => await PrChecksAsync(rest, executor, stdout, stderr).ConfigureAwait(false),
            "status" => await PrStatusAsync(rest, executor, stdout, stderr).ConfigureAwait(false),
            "diff" => await PrDiffAsync(rest, executor, stdout, stderr).ConfigureAwait(false),

            // create/merge/comment/edit are write operations (Rust's pr merge already passes through);
            // out of Task 1's read-only scope, so route them raw.
            _ => await PassthroughAsync(executor, stdout, stderr, Prepend("pr", args)).ConfigureAwait(false),
        };
    }

    /// <summary>Runs <c>gh pr list</c> with RTK's field projection and prints a compact list. Ports <c>list_prs</c> (gh_cmd.rs:231).</summary>
    private static Task<int> ListPrsAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var ghArgs = new List<string> { "pr", "list", "--json", "number,title,state,author,updatedAt" };
        ghArgs.AddRange(args);
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, root => GhFilters.FormatPrList(root, ultraCompact));
    }

    /// <summary>Ports <c>should_passthrough_pr_view</c> (gh_cmd.rs:306).</summary>
    internal static bool ShouldPassthroughPrView(IReadOnlyList<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return extraArgs.Any(a => a is "--json" or "--jq" or "--web" or "--comments");
    }

    /// <summary>Ports <c>should_passthrough_issue_view</c> (gh_cmd.rs:312).</summary>
    internal static bool ShouldPassthroughIssueView(IReadOnlyList<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return extraArgs.Any(a => a is "--json" or "--jq" or "--web" or "--comments");
    }

    /// <summary>Ports <c>should_passthrough_pr_status</c> (gh_cmd.rs:318).</summary>
    internal static bool ShouldPassthroughPrStatus(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a is "--help" or "-h" or "--web" or "--jq" or "--template");
    }

    /// <summary>The <c>--json</c> field projection for <c>gh pr status</c>. Ports <c>pr_status_json_fields</c> (gh_cmd.rs:327).</summary>
    internal static string PrStatusJsonFields() => "number,title,reviewDecision,statusCheckRollup";

    /// <summary>Runs <c>gh pr view</c> and prints a compact summary. Ports <c>view_pr</c> (gh_cmd.rs:331).</summary>
    private static Task<int> ViewPrAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var (idOpt, extra) = ParseOptionalIdentifier(args);
        if (ShouldPassthroughPrView(extra))
        {
            var baseArgs = new List<string> { "pr", "view" };
            if (idOpt is not null)
            {
                baseArgs.Add(idOpt);
            }

            baseArgs.AddRange(extra);
            return PassthroughAsync(executor, stdout, stderr, baseArgs);
        }

        var ghArgs = new List<string> { "pr", "view" };
        if (idOpt is not null)
        {
            ghArgs.Add(idOpt);
        }

        ghArgs.Add("--json");
        ghArgs.Add("number,title,state,author,body,url,mergeable,reviews,statusCheckRollup");
        ghArgs.AddRange(extra);
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, root => GhFilters.FormatPrView(root, ultraCompact));
    }

    /// <summary>Runs <c>gh pr checks</c> and prints a CI summary. Ports <c>pr_checks</c> (gh_cmd.rs:446).</summary>
    private static Task<int> PrChecksAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var (idOpt, extra) = ParseOptionalIdentifier(args);
        var ghArgs = new List<string> { "pr", "checks" };
        if (idOpt is not null)
        {
            ghArgs.Add(idOpt);
        }

        ghArgs.AddRange(extra);
        return RunGhFilteredAsync(executor, stdout, stderr, ghArgs, GhFilters.FormatPrChecks, noTrailingNewline: true);
    }

    /// <summary>Runs <c>gh pr status</c> and prints a compact summary. Ports <c>pr_status</c> (gh_cmd.rs:505).</summary>
    private static Task<int> PrStatusAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        if (ShouldPassthroughPrStatus(args))
        {
            return PassthroughAsync(executor, stdout, stderr, Prepend("pr", Prepend("status", args)));
        }

        var ghArgs = new List<string> { "pr", "status", "--json", PrStatusJsonFields() };
        ghArgs.AddRange(args);
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, GhFilters.FormatPrStatus);
    }

    /// <summary>Flags that change <c>gh pr diff</c> away from a unified diff. Ports <c>has_non_diff_format_flag</c> (gh_cmd.rs:920).</summary>
    internal static bool HasNonDiffFormatFlag(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a is "--name-only" or "--name-status" or "--stat" or "--numstat" or "--shortstat");
    }

    /// <summary>Runs <c>gh pr diff</c> and prints a compacted diff. Ports <c>pr_diff</c> (gh_cmd.rs:930).</summary>
    private static Task<int> PrDiffAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var noCompact = args.Any(a => a == "--no-compact");
        var ghArgs = args.Where(a => a != "--no-compact").ToList();
        if (noCompact || HasNonDiffFormatFlag(ghArgs))
        {
            return PassthroughAsync(executor, stdout, stderr, Prepend("pr", Prepend("diff", ghArgs)));
        }

        var cmdArgs = new List<string> { "pr", "diff" };
        cmdArgs.AddRange(ghArgs);

        // gh_cmd.rs:945 uses stdout_only().early_exit_on_failure() WITHOUT no_trailing_newline,
        // so the compacted diff is printed with a trailing newline (println!).
        return RunGhFilteredAsync(
            executor, stdout, stderr, cmdArgs,
            raw => raw.Trim().Length == 0 ? "No diff" : GitFilters.CompactDiff(raw, 500),
            noTrailingNewline: false);
    }

    // ===================== issue =====================

    /// <summary>Dispatches <c>gh issue</c> subcommands. Ports <c>run_issue</c> (gh_cmd.rs:584).</summary>
    private static async Task<int> RunIssueAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            return await PassthroughAsync(executor, stdout, stderr, Prepend("issue", args)).ConfigureAwait(false);
        }

        var rest = args.Skip(1).ToArray();
        return args[0] switch
        {
            "list" => await ListIssuesAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "view" => await ViewIssueAsync(rest, executor, stdout, stderr).ConfigureAwait(false),
            _ => await PassthroughAsync(executor, stdout, stderr, Prepend("issue", args)).ConfigureAwait(false),
        };
    }

    /// <summary>Runs <c>gh issue list</c> and prints a compact list. Ports <c>list_issues</c> (gh_cmd.rs:596).</summary>
    private static Task<int> ListIssuesAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var ghArgs = new List<string> { "issue", "list", "--json", "number,title,state,author" };
        ghArgs.AddRange(args);
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, root => GhFilters.FormatIssueList(root, ultraCompact));
    }

    /// <summary>Runs <c>gh issue view</c> and prints a compact summary. Ports <c>view_issue</c> (gh_cmd.rs:647).</summary>
    private static Task<int> ViewIssueAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var (idOpt, extra) = ParseOptionalIdentifier(args);
        if (ShouldPassthroughIssueView(extra))
        {
            var baseArgs = new List<string> { "issue", "view" };
            if (idOpt is not null)
            {
                baseArgs.Add(idOpt);
            }

            baseArgs.AddRange(extra);
            return PassthroughAsync(executor, stdout, stderr, baseArgs);
        }

        var ghArgs = new List<string> { "issue", "view" };
        if (idOpt is not null)
        {
            ghArgs.Add(idOpt);
        }

        ghArgs.Add("--json");
        ghArgs.Add("number,title,state,author,body,url");
        ghArgs.AddRange(extra);
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, GhFilters.FormatIssueView);
    }

    // ===================== run (workflow) =====================

    /// <summary>Dispatches <c>gh run</c> subcommands. Ports <c>run_workflow</c> (gh_cmd.rs:707).</summary>
    private static async Task<int> RunWorkflowAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            return await PassthroughAsync(executor, stdout, stderr, Prepend("run", args)).ConfigureAwait(false);
        }

        var rest = args.Skip(1).ToArray();
        return args[0] switch
        {
            "list" => await ListRunsAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "view" => await ViewRunAsync(rest, executor, stdout, stderr).ConfigureAwait(false),

            // watch (streaming) and every other run subcommand fall through to passthrough (gh_cmd.rs:715).
            _ => await PassthroughAsync(executor, stdout, stderr, Prepend("run", args)).ConfigureAwait(false),
        };
    }

    /// <summary>Runs <c>gh run list</c> with RTK's field projection and a fixed <c>--limit 10</c>. Ports <c>list_runs</c> (gh_cmd.rs:719).</summary>
    private static Task<int> ListRunsAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var ghArgs = new List<string>
        {
            "run", "list", "--json", "databaseId,name,status,conclusion,createdAt", "--limit", "10",
        };
        ghArgs.AddRange(args);
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, root => GhFilters.FormatRunList(root, ultraCompact));
    }

    /// <summary>
    /// Reports whether <c>gh run view</c> args should bypass filtering (<c>--log-failed</c>/<c>--log</c>/
    /// <c>--json</c> produce output the filter would wrongly strip). Ports <c>should_passthrough_run_view</c>
    /// (gh_cmd.rs:775).
    /// </summary>
    internal static bool ShouldPassthroughRunView(IReadOnlyList<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return extraArgs.Any(a => a is "--log-failed" or "--log" or "--json");
    }

    /// <summary>Runs <c>gh run view</c> and prints a compact summary. Ports <c>view_run</c> (gh_cmd.rs:781).</summary>
    private static Task<int> ViewRunAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var (idOpt, extra) = ParseOptionalIdentifier(args);
        if (ShouldPassthroughRunView(extra))
        {
            var baseArgs = new List<string> { "run", "view" };
            if (idOpt is not null)
            {
                baseArgs.Add(idOpt);
            }

            baseArgs.AddRange(extra);
            return PassthroughAsync(executor, stdout, stderr, baseArgs);
        }

        var ghArgs = new List<string> { "run", "view" };
        if (idOpt is not null)
        {
            ghArgs.Add(idOpt);
        }

        ghArgs.AddRange(extra);
        var runId = idOpt ?? string.Empty;

        // gh run view emits a text report (not --json); filter the plain text like Rust does.
        return RunGhFilteredAsync(
            executor, stdout, stderr, ghArgs, raw => GhFilters.FormatRunView(raw, runId), noTrailingNewline: true);
    }

    // ===================== repo =====================

    /// <summary>
    /// Dispatches <c>gh repo</c>: only <c>view</c> (the default when no subcommand is given) is filtered;
    /// anything else passes through. Ports <c>run_repo</c> (gh_cmd.rs:842).
    /// </summary>
    private static Task<int> RunRepoAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var (subcommand, restArgs) = args.Length == 0
            ? ("view", Array.Empty<string>())
            : (args[0], args.Skip(1).ToArray());

        if (subcommand != "view")
        {
            return PassthroughAsync(executor, stdout, stderr, Prepend("repo", args));
        }

        // Rust threads the user's rest args BEFORE the --json projection (gh_cmd.rs:852-859).
        var ghArgs = new List<string> { "repo", "view" };
        ghArgs.AddRange(restArgs);
        ghArgs.Add("--json");
        ghArgs.Add("name,owner,description,url,stargazerCount,forkCount,isPrivate");
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, GhFilters.FormatRepoView);
    }

    // ===================== identifier parsing =====================

    /// <summary>
    /// Extracts a positional identifier (PR/issue/run number) from args, separating it from remaining
    /// extra flags (e.g. <c>-R owner/repo</c>). Handles the flag either side of the identifier. Ports
    /// <c>extract_identifier_and_extra_args</c> (gh_cmd.rs:119).
    /// </summary>
    /// <param name="args">The subcommand arguments.</param>
    /// <returns>The identifier and extra args, or null when no positional identifier is present.</returns>
    internal static (string Identifier, List<string> Extra)? ExtractIdentifierAndExtraArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return null;
        }

        string? identifier = null;
        var extra = new List<string>();
        var skipNext = false;

        foreach (var arg in args)
        {
            if (skipNext)
            {
                extra.Add(arg);
                skipNext = false;
                continue;
            }

            if (FlagsWithValue.Contains(arg))
            {
                extra.Add(arg);
                skipNext = true;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                extra.Add(arg);
                continue;
            }

            if (identifier is null)
            {
                identifier = arg;
            }
            else
            {
                extra.Add(arg);
            }
        }

        return identifier is null ? null : (identifier, extra);
    }

    /// <summary>
    /// Like <see cref="ExtractIdentifierAndExtraArgs"/> but yields <c>(null, args)</c> when no positional
    /// identifier is present, so callers can defer the "id required" decision to gh. Ports
    /// <c>parse_optional_identifier</c> (gh_cmd.rs:168).
    /// </summary>
    /// <param name="args">The subcommand arguments.</param>
    /// <returns>The optional identifier and the extra args.</returns>
    internal static (string? Identifier, List<string> Extra) ParseOptionalIdentifier(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var extracted = ExtractIdentifierAndExtraArgs(args);
        return extracted is { } e ? (e.Identifier, e.Extra) : (null, args.ToList());
    }

    /// <summary>Reports whether the args contain a bare <c>--json</c> flag. Ports <c>has_json_flag</c> (gh_cmd.rs:112).</summary>
    internal static bool HasJsonFlag(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a == "--json");
    }

    // ===================== runners =====================

    /// <summary>
    /// Runs a <c>gh</c> command, parses its stdout as JSON, and prints the result of
    /// <paramref name="formatFn"/>. On a JSON parse error the raw stdout is printed unchanged. Ports
    /// <c>run_gh_json</c> (gh_cmd.rs:175) with the <c>stdout_only().early_exit_on_failure().no_trailing_newline()</c> envelope.
    /// </summary>
    private static Task<int> RunGhJsonAsync(
        IProcessExecutor executor, TextWriter stdout, TextWriter stderr,
        IReadOnlyList<string> ghArgs, Func<JsonElement, string> formatFn)
    {
        return RunGhFilteredAsync(executor, stdout, stderr, ghArgs, raw =>
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return formatFn(doc.RootElement);
            }
            catch (JsonException)
            {
                return raw;
            }
        }, noTrailingNewline: true);
    }

    /// <summary>
    /// Runs a <c>gh</c> command capturing stdout/stderr separately, then filters stdout with
    /// <paramref name="filter"/>. On child failure the raw streams are forwarded and the exit code
    /// propagated without filtering (Rust's <c>skip_filter_on_failure</c>, runner.rs:98). The mandatory
    /// fallback contract applies: a throwing filter falls back to raw stdout.
    /// </summary>
    private static async Task<int> RunGhFilteredAsync(
        IProcessExecutor executor, TextWriter stdout, TextWriter stderr,
        IReadOnlyList<string> ghArgs, Func<string, string> filter, bool noTrailingNewline)
    {
        var result = await executor.ExecuteAsync(
            new ExecutionRequest("gh", ghArgs, CaptureMode: ExecutionCaptureMode.Separate)).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            if (!string.IsNullOrWhiteSpace(result.Failure))
            {
                stderr.Write(result.Failure + "\n");
            }

            return result.ExitCode;
        }

        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                stdout.Write(result.Stdout);
            }

            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                stderr.Write(result.Stderr);
            }

            return result.ExitCode;
        }

        string filtered;
        try
        {
            filtered = filter(result.Stdout);
        }
        catch (Exception ex)
        {
            // Mandatory fallback contract: a filter must never crash or hide output.
            stderr.Write($"rtk: filter warning: {ex.Message}\n");
            stdout.Write(result.Stdout);
            return result.ExitCode;
        }

        stdout.Write(noTrailingNewline ? filtered : filtered + "\n");
        return result.ExitCode;
    }

    /// <summary>
    /// Runs <c>gh</c> with the given argv (after the executable name) as a raw passthrough, inheriting
    /// the child's stdio so output reaches the terminal unchanged, and propagates the exit code. Ports
    /// <c>run_passthrough</c>/<c>run_passthrough_with_extra</c> (gh_cmd.rs:990-1001).
    /// </summary>
    private static async Task<int> PassthroughAsync(
        IProcessExecutor executor, TextWriter stdout, TextWriter stderr, IReadOnlyList<string> ghArgs)
    {
        var result = await executor.ExecuteAsync(
            new ExecutionRequest("gh", ghArgs, CaptureMode: ExecutionCaptureMode.Inherit)).ConfigureAwait(false);

        // Inherited stdio: gh wrote directly to the terminal. If capture happened anyway (e.g. a
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

    /// <summary>Returns a new list with <paramref name="head"/> prepended to <paramref name="tail"/>.</summary>
    private static List<string> Prepend(string head, IReadOnlyList<string> tail)
    {
        var list = new List<string>(tail.Count + 1) { head };
        list.AddRange(tail);
        return list;
    }
}
