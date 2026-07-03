using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtkSharp.Commands.Git;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Gh;

/// <summary>
/// Compact <c>gh</c> (GitHub CLI) proxy. Dispatches on the <c>gh</c> subcommand exactly as Rust's
/// <c>gh_cmd::run</c> (gh_cmd.rs:193) does, filtering JSON-backed output into terse agent-friendly
/// summaries. Ported from <c>src/cmds/git/gh_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope (Phase 7b, Task 1).</b> The <c>pr</c> family (<c>list</c>/<c>view</c>/<c>checks</c>/
/// <c>status</c>/<c>diff</c>) and the <c>issue</c> family (<c>list</c>/<c>view</c>) are filtered here.
/// The top-level <c>--json</c> structured-output guard (gh_cmd.rs:195) and the per-subcommand
/// <c>--jq</c>/<c>--template</c>/<c>--web</c>/<c>--comments</c> guards are ported in full: when the
/// user asks gh for structured or web output, RTK passes through raw so it is never corrupted.
/// </para>
/// <para>
/// <b>Deferred to Task 2 (documented divergence).</b> The <c>run</c>, <c>repo</c>, and <c>api</c>
/// subcommand families are routed to raw passthrough until Task 2 ports their filters. Passthrough
/// never corrupts output, so this is a safe intermediate. The <c>pr</c> write subcommands
/// (<c>create</c>/<c>merge</c>/<c>comment</c>/<c>edit</c>) are likewise routed to passthrough: Rust's
/// <c>pr merge</c> already passes through (gh_cmd.rs:908), and the confirmation-formatting arms for
/// create/comment/edit are out of Task 1's read-only scope.
/// </para>
/// <para>
/// <b>Omitted side effects.</b> Following the <see cref="GitCommand"/> precedent, Rust's token-savings
/// tracking (<c>TimedExecution</c>) and <c>verbose</c> diagnostics are omitted — metrics/diagnostic
/// side effects that do not change filtered output.
/// </para>
/// </remarks>
public static partial class GhCommand
{
    /// <summary>
    /// Maximum list entries shown before an overflow marker. Rust binds this to <c>CAP_LIST</c> (= 20)
    /// from <c>src/core/truncate.rs</c> (gh_cmd.rs:274, 633).
    /// </summary>
    private const int MaxList = 20;

    // gh flags that take a value — skipped along with their value when isolating the positional
    // identifier from extra flags (gh_cmd.rs:125-134).
    private static readonly string[] FlagsWithValue =
    {
        "-R", "--repo", "-q", "--jq", "-t", "--template", "--job", "--attempt",
    };

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"(?m)^\s*\[!\[[^\]]*\]\([^)]*\)\]\([^)]*\)\s*$")]
    private static partial Regex BadgeLineRegex();

    [GeneratedRegex(@"(?m)^\s*!\[[^\]]*\]\([^)]*\)\s*$")]
    private static partial Regex ImageOnlyLineRegex();

    [GeneratedRegex(@"(?m)^\s*(?:---+|\*\*\*+|___+)\s*$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiBlankRegex();

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

            // run/repo/api: filtered in Task 2; passthrough until then (never corrupts output).
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
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, root => FormatPrList(root, ultraCompact));
    }

    /// <summary>Formats <c>gh pr list --json</c> output into RTK's compact list. Ports <c>format_pr_list</c> (gh_cmd.rs:245).</summary>
    internal static string FormatPrList(JsonElement json, bool ultraCompact)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var prs = json.EnumerateArray().ToList();
        if (prs.Count == 0)
        {
            return ultraCompact ? "No PRs\n" : "No Pull Requests\n";
        }

        var out_ = new StringBuilder(ultraCompact ? "PRs\n" : "Pull Requests\n");
        var allLines = prs.Select(pr =>
        {
            var number = JInt(pr, "number", 0);
            var title = JStr(pr, "title", "???");
            var state = JStr(pr, "state", "???");
            var author = JNestedStr(pr, "author", "login", "???");
            var icon = StateIcon(state, ultraCompact);
            return $"  {icon} #{number} {Truncate(title, 60)} ({author})";
        }).ToList();

        foreach (var line in allLines.Take(MaxList))
        {
            out_.Append(line).Append('\n');
        }

        if (allLines.Count > MaxList)
        {
            out_.Append($"  … +{allLines.Count - MaxList} more\n");
            var allText = string.Join("\n", allLines);
            var hint = Tee.ForceTeeTailHint(allText, "gh-prs", MaxList + 1);
            if (hint is not null)
            {
                out_.Append($"  {hint}\n");
            }
        }

        return out_.ToString();
    }

    /// <summary>Maps a PR/issue state to its icon. Ports <c>state_icon</c> (gh_cmd.rs:288).</summary>
    internal static string StateIcon(string state, bool ultraCompact) => ultraCompact
        ? state switch { "OPEN" => "O", "MERGED" => "M", "CLOSED" => "C", _ => "?" }
        : state switch { "OPEN" => "[open]", "MERGED" => "[merged]", "CLOSED" => "[closed]", _ => "[unknown]" };

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
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, root => FormatPrView(root, ultraCompact));
    }

    /// <summary>Formats <c>gh pr view --json</c> output. Ports <c>format_pr_view</c> (gh_cmd.rs:360).</summary>
    internal static string FormatPrView(JsonElement json, bool ultraCompact)
    {
        var out_ = new StringBuilder();
        var number = JInt(json, "number", 0);
        var title = JStr(json, "title", "???");
        var state = JStr(json, "state", "???");
        var author = JNestedStr(json, "author", "login", "???");
        var url = JStr(json, "url", string.Empty);
        var mergeable = JStr(json, "mergeable", "UNKNOWN");

        var icon = StateIcon(state, ultraCompact);
        out_.Append($"{icon} PR #{number}: {title}\n");
        out_.Append($"  {author}\n");

        var mergeableStr = mergeable switch
        {
            "MERGEABLE" => "[ok]",
            "CONFLICTING" => "[x]",
            _ => "?",
        };
        out_.Append($"  {state} | {mergeableStr}\n");

        // gh's --json reviews returns a plain array, so this nested reviews.nodes path (GraphQL shape)
        // is absent on real gh CLI output and the reviews line is skipped — faithful to gh_cmd.rs:380.
        if (TryGetProp(json, "reviews", out var reviews)
            && reviews.ValueKind == JsonValueKind.Object
            && TryGetProp(reviews, "nodes", out var nodes)
            && nodes.ValueKind == JsonValueKind.Array)
        {
            var reviewNodes = nodes.EnumerateArray().ToList();
            var approved = reviewNodes.Count(r => JStr(r, "state", string.Empty) == "APPROVED");
            var changes = reviewNodes.Count(r => JStr(r, "state", string.Empty) == "CHANGES_REQUESTED");
            if (approved > 0 || changes > 0)
            {
                out_.Append($"  Reviews: {approved} approved, {changes} changes requested\n");
            }
        }

        if (TryGetProp(json, "statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array)
        {
            var checks = rollup.EnumerateArray().ToList();
            var total = checks.Count;
            var passed = checks.Count(CheckSucceeded);
            var failed = checks.Count(CheckFailed);
            if (ultraCompact)
            {
                out_.Append(failed > 0
                    ? $"  [x]{passed}/{total}  {failed} fail\n"
                    : $"  {passed}/{total}\n");
            }
            else
            {
                out_.Append($"  Checks: {passed}/{total} passed\n");
                if (failed > 0)
                {
                    out_.Append($"  [warn] {failed} checks failed\n");
                }
            }
        }

        out_.Append($"  {url}\n");

        var body = JStr(json, "body", string.Empty);
        if (body.Length != 0)
        {
            var bodyFiltered = FilterMarkdownBody(body);
            if (bodyFiltered.Length != 0)
            {
                out_.Append('\n');
                foreach (var line in SplitLinesForBody(bodyFiltered))
                {
                    out_.Append($"  {line}\n");
                }
            }
            else
            {
                out_.Append("\n  (body contained only badges/images/comments)\n");
            }
        }

        return out_.ToString();
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
        return RunGhFilteredAsync(executor, stdout, stderr, ghArgs, FormatPrChecks, noTrailingNewline: true);
    }

    /// <summary>Summarizes <c>gh pr checks</c> table output. Ports <c>format_pr_checks</c> (gh_cmd.rs:472).</summary>
    internal static string FormatPrChecks(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);

        var passed = 0;
        var failed = 0;
        var pending = 0;
        var failedChecks = new List<string>();

        foreach (var line in ReadCommand.SplitLines(stdout))
        {
            if (line.Contains("[ok]", StringComparison.Ordinal) || line.Contains("pass", StringComparison.Ordinal))
            {
                passed++;
            }
            else if (line.Contains("[x]", StringComparison.Ordinal) || line.Contains("fail", StringComparison.Ordinal))
            {
                failed++;
                failedChecks.Add(line.Trim());
            }
            else if (line.Contains('*', StringComparison.Ordinal) || line.Contains("pending", StringComparison.Ordinal))
            {
                pending++;
            }
        }

        var out_ = new StringBuilder("CI Checks Summary:\n");
        out_.Append($"  [ok] Passed: {passed}\n");
        out_.Append($"  [FAIL] Failed: {failed}\n");
        if (pending > 0)
        {
            out_.Append($"  [pending] Pending: {pending}\n");
        }

        if (failedChecks.Count != 0)
        {
            out_.Append("\n  Failed checks:\n");
            foreach (var check in failedChecks)
            {
                out_.Append($"    {check}\n");
            }
        }

        return out_.ToString();
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
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, FormatPrStatus);
    }

    /// <summary>Formats <c>gh pr status --json</c> output. Ports <c>format_pr_status</c> (gh_cmd.rs:521).</summary>
    internal static string FormatPrStatus(JsonElement json)
    {
        var out_ = new StringBuilder();

        if (TryGetProp(json, "currentBranch", out var currentBranch) && currentBranch.ValueKind != JsonValueKind.Null)
        {
            var entry = FormatPrStatusEntry(currentBranch);
            if (entry.Length != 0)
            {
                out_.Append("Current Branch\n");
                out_.Append(entry);
                out_.Append('\n');
            }
        }

        if (TryGetProp(json, "createdBy", out var createdBy) && createdBy.ValueKind == JsonValueKind.Array)
        {
            var prs = createdBy.EnumerateArray().ToList();
            out_.Append($"Your PRs ({prs.Count}):\n");
            foreach (var pr in prs.Take(5))
            {
                var entry = FormatPrStatusEntry(pr);
                if (entry.Length != 0)
                {
                    out_.Append(entry);
                }
            }
        }

        return out_.ToString();
    }

    /// <summary>Formats a single <c>gh pr status</c> entry. Ports <c>format_pr_status_entry</c> (gh_cmd.rs:545).</summary>
    internal static string FormatPrStatusEntry(JsonElement pr)
    {
        if (pr.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        var number = JInt(pr, "number", 0);
        var title = JStr(pr, "title", "???");
        var reviews = JStr(pr, "reviewDecision", "PENDING");
        var out_ = new StringBuilder($"  #{number} {Truncate(title, 50)} [{reviews}]");

        if (TryGetProp(pr, "statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array)
        {
            var checks = rollup.EnumerateArray().ToList();
            var total = checks.Count;
            if (total > 0)
            {
                var passed = checks.Count(CheckSucceeded);
                var failed = checks.Count(CheckFailed);
                out_.Append($" checks {passed}/{total}");
                if (failed > 0)
                {
                    out_.Append($" fail {failed}");
                }
            }
        }

        out_.Append('\n');
        return out_.ToString();
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
            raw => raw.Trim().Length == 0 ? "No diff" : GitCommand.CompactDiff(raw, 500),
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
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, root => FormatIssueList(root, ultraCompact));
    }

    /// <summary>Formats <c>gh issue list --json</c> output. Ports <c>format_issue_list</c> (gh_cmd.rs:607).</summary>
    internal static string FormatIssueList(JsonElement json, bool ultraCompact)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var issues = json.EnumerateArray().ToList();
        if (issues.Count == 0)
        {
            return "No Issues\n";
        }

        var out_ = new StringBuilder("Issues\n");
        var allLines = issues.Select(issue =>
        {
            var number = JInt(issue, "number", 0);
            var title = JStr(issue, "title", "???");
            var state = JStr(issue, "state", "???");
            var icon = ultraCompact
                ? (state == "OPEN" ? "O" : "C")
                : (state == "OPEN" ? "[open]" : "[closed]");
            return $"  {icon} #{number} {Truncate(title, 60)}";
        }).ToList();

        foreach (var line in allLines.Take(MaxList))
        {
            out_.Append(line).Append('\n');
        }

        if (allLines.Count > MaxList)
        {
            out_.Append($"  … +{allLines.Count - MaxList} more\n");
            var allText = string.Join("\n", allLines);
            var hint = Tee.ForceTeeTailHint(allText, "gh-issues", MaxList + 1);
            if (hint is not null)
            {
                out_.Append($"  {hint}\n");
            }
        }

        return out_.ToString();
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
        return RunGhJsonAsync(executor, stdout, stderr, ghArgs, FormatIssueView);
    }

    /// <summary>Formats <c>gh issue view --json</c> output. Ports <c>format_issue_view</c> (gh_cmd.rs:673).</summary>
    internal static string FormatIssueView(JsonElement json)
    {
        var out_ = new StringBuilder();
        var number = JInt(json, "number", 0);
        var title = JStr(json, "title", "???");
        var state = JStr(json, "state", "???");
        var author = JNestedStr(json, "author", "login", "???");
        var url = JStr(json, "url", string.Empty);

        var icon = state == "OPEN" ? "[open]" : "[closed]";
        out_.Append($"{icon} Issue #{number}: {title}\n");
        out_.Append($"  Author: @{author}\n");
        out_.Append($"  Status: {state}\n");
        out_.Append($"  URL: {url}\n");

        var body = JStr(json, "body", string.Empty);
        if (body.Length != 0)
        {
            var bodyFiltered = FilterMarkdownBody(body);
            if (bodyFiltered.Length != 0)
            {
                out_.Append("\n  Description:\n");
                foreach (var line in SplitLinesForBody(bodyFiltered))
                {
                    out_.Append($"    {line}\n");
                }
            }
            else
            {
                out_.Append("\n  Description: (body contained only badges/images/comments)\n");
            }
        }

        return out_.ToString();
    }

    // ===================== markdown body filter =====================

    /// <summary>
    /// Filters a PR/issue markdown body to remove noise (HTML comments, badge lines, image-only lines,
    /// horizontal rules) and collapse excessive blank lines, preserving fenced code blocks untouched.
    /// Ports <c>filter_markdown_body</c> (gh_cmd.rs:29).
    /// </summary>
    /// <param name="body">The raw markdown body.</param>
    /// <returns>The filtered body, trimmed (no leading/trailing whitespace).</returns>
    internal static string FilterMarkdownBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.Length == 0)
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        var remaining = body;

        while (true)
        {
            // Find next code-block opening, preferring ``` anywhere over ~~~ (gh_cmd.rs:40-50).
            var pos = remaining.IndexOf("```", StringComparison.Ordinal);
            if (pos < 0)
            {
                pos = remaining.IndexOf("~~~", StringComparison.Ordinal);
            }

            if (pos < 0)
            {
                result.Append(FilterMarkdownSegment(remaining));
                break;
            }

            var fence = remaining.AsSpan(pos).StartsWith("```") ? "```" : "~~~";

            // Filter the text before the code block.
            result.Append(FilterMarkdownSegment(remaining[..pos]));

            var afterOpen = pos + fence.Length;
            var nlAfterOpen = remaining.IndexOf('\n', afterOpen);
            var codeStart = nlAfterOpen >= 0 ? nlAfterOpen + 1 : remaining.Length;

            var fenceInBody = codeStart < remaining.Length
                ? remaining.IndexOf(fence, codeStart, StringComparison.Ordinal)
                : -1;
            if (fenceInBody >= 0)
            {
                var end = fenceInBody + fence.Length;

                // Preserve the entire code block as-is.
                result.Append(remaining, pos, end - pos);

                // Include the rest of the closing fence line.
                var nlAfterClose = remaining.IndexOf('\n', end);
                var afterClose = nlAfterClose >= 0 ? nlAfterClose + 1 : remaining.Length;
                result.Append(remaining, end, afterClose - end);
                remaining = remaining[afterClose..];
            }
            else
            {
                // Unclosed code block — preserve everything.
                result.Append(remaining.AsSpan(pos));
                break;
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>Filters a markdown segment outside any code block. Ports <c>filter_markdown_segment</c> (gh_cmd.rs:102).</summary>
    private static string FilterMarkdownSegment(string text)
    {
        var s = HtmlCommentRegex().Replace(text, string.Empty);
        s = BadgeLineRegex().Replace(s, string.Empty);
        s = ImageOnlyLineRegex().Replace(s, string.Empty);
        s = HorizontalRuleRegex().Replace(s, string.Empty);
        s = MultiBlankRegex().Replace(s, "\n\n");
        return s;
    }

    /// <summary>
    /// Splits a filtered body into lines the way Rust's <c>str::lines()</c> does (used to prefix each
    /// line with indentation): split on <c>\n</c>, dropping a trailing <c>\r</c>, with no trailing
    /// empty entry when the text ends in a newline.
    /// </summary>
    private static IEnumerable<string> SplitLinesForBody(string text)
    {
        // FilterMarkdownBody trims trailing newlines, so a simple split matches Rust's lines() here.
        return text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l);
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

    /// <summary>
    /// Truncates a string to <paramref name="maxLen"/> Unicode scalar values, appending <c>...</c> when
    /// longer (or returning <c>...</c> when <paramref name="maxLen"/> &lt; 3). Ports <c>utils::truncate</c>
    /// (utils.rs:25), counting runes so multibyte input is never split mid-character.
    /// </summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="maxLen">The maximum width in Unicode scalar values.</param>
    /// <returns>The (possibly truncated) string.</returns>
    internal static string Truncate(string s, int maxLen)
    {
        ArgumentNullException.ThrowIfNull(s);

        var runes = s.EnumerateRunes().ToList();
        if (runes.Count <= maxLen)
        {
            return s;
        }

        if (maxLen < 3)
        {
            return "...";
        }

        return string.Concat(runes.Take(maxLen - 3).Select(r => r.ToString())) + "...";
    }

    // ===================== JSON accessors =====================

    private static bool TryGetProp(JsonElement el, string prop, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Mirrors serde_json's <c>json[prop].as_str().unwrap_or(dflt)</c>: the value only when it is a JSON string.</summary>
    private static string JStr(JsonElement el, string prop, string dflt) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt : dflt;

    /// <summary>Mirrors <c>json[prop][inner].as_str().unwrap_or(dflt)</c> for a nested string (e.g. <c>author.login</c>).</summary>
    private static string JNestedStr(JsonElement el, string prop, string inner, string dflt) =>
        TryGetProp(el, prop, out var v) ? JStr(v, inner, dflt) : dflt;

    /// <summary>Mirrors <c>json[prop].as_i64().unwrap_or(dflt)</c>: the value only when it is a JSON integer.</summary>
    private static long JInt(JsonElement el, string prop, long dflt) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : dflt;

    /// <summary>A status check counts as passed when its <c>conclusion</c> or <c>state</c> is <c>SUCCESS</c> (gh_cmd.rs:400).</summary>
    private static bool CheckSucceeded(JsonElement check) =>
        JStr(check, "conclusion", string.Empty) == "SUCCESS" || JStr(check, "state", string.Empty) == "SUCCESS";

    /// <summary>A status check counts as failed when its <c>conclusion</c> or <c>state</c> is <c>FAILURE</c> (gh_cmd.rs:407).</summary>
    private static bool CheckFailed(JsonElement check) =>
        JStr(check, "conclusion", string.Empty) == "FAILURE" || JStr(check, "state", string.Empty) == "FAILURE";

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
