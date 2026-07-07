using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Git;

/// <summary>
/// Compact <c>glab</c> (GitLab CLI) proxy. Dispatches on the <c>glab</c> subcommand exactly as
/// Rust's <c>glab_cmd::run</c> (glab_cmd.rs:261) does, filtering JSON-backed output into terse
/// agent-friendly summaries. Ported from <c>src/cmds/git/glab_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The <c>mr</c> family (<c>list</c>/<c>view</c>/<c>create</c>/<c>merge</c>/
/// <c>approve</c>/<c>diff</c>/<c>note</c>/<c>update</c>), the <c>issue</c> family
/// (<c>list</c>/<c>view</c>), the <c>ci</c>/<c>pipeline</c> family (<c>list</c>/<c>status</c>/
/// <c>trace</c>), and the <c>release</c> family (<c>list</c>/<c>view</c>) are filtered here. The
/// top-level <c>--output</c>/<c>-F</c>/<c>--json</c> structured-output guard (glab_cmd.rs:263) and
/// the per-view <c>--web</c>/<c>--comments</c>/<c>--output</c>/<c>-F</c> guard are ported in full:
/// when the user asks glab for structured or web output, RTK passes through raw so it is never
/// corrupted.
/// </para>
/// <para>
/// <b>Passthrough by design (faithful to Rust).</b> <c>glab api</c> always passes through
/// (glab_cmd.rs:985-990): converting its JSON to a schema would destroy every value and force a
/// re-fetch. <c>glab ci view</c> is an interactive TUI (tcell) and is not a matched arm of
/// <c>run_ci</c>, so it falls through to passthrough — this preserves glab's live terminal UI,
/// which RTK has no filtered mode for. Every unmatched subcommand at any dispatch level passes
/// through unchanged.
/// </para>
/// <para>
/// <b>The "ci"/"pipeline" passthrough quirk.</b> <c>run_ci</c>'s unmatched-arm passthrough hardcodes
/// the literal prefix <c>"ci"</c> (glab_cmd.rs:663), even when the user invoked <c>glab pipeline</c>
/// — so <c>rtk glab pipeline foo</c> executes <c>glab ci foo</c>, not <c>glab pipeline foo</c>. This
/// is harmless because <c>ci</c> and <c>pipeline</c> are genuine aliases in real <c>glab</c>, but the
/// quirk is ported byte-for-byte per the project's fidelity mandate: see <see cref="RunCiAsync"/>.
/// </para>
/// <para>
/// <b>Omitted side effects.</b> Following the <see cref="GitCommand"/>/<see cref="Gh.GhCommand"/>
/// precedent, Rust's token-savings tracking (<c>TimedExecution</c>) and <c>verbose</c> diagnostics
/// are omitted — metrics/diagnostic side effects that do not change filtered output. Rust's
/// <c>verbose: u8</c> parameter is threaded through every helper only to be ignored
/// (<c>_verbose</c>), so it has no port surface here either.
/// </para>
/// <para>
/// <b>No <c>--</c> restoration needed.</b> Unlike <c>git diff</c>/<c>show</c> (see
/// <see cref="GitCommand.RestoreDoubleDashWithRaw"/>), glab_cmd.rs never inspects or restores a
/// <c>--</c> token: none of its argument-parsing helpers (<c>extract_identifier_and_extra_args</c>,
/// <c>parse_optional_identifier</c>, <c>has_output_flag</c>, <c>should_passthrough_view</c>) treat
/// <c>--</c> specially, and clap's <c>trailing_var_arg</c> on the <c>Glab</c> command's <c>args</c>
/// field passes every token (including a literal <c>--</c>) straight through untouched. So this port
/// has no double-dash restoration surface to mirror.
/// </para>
/// </remarks>
public static partial class GlabCommand
{
    /// <summary>
    /// Maximum MR/issue list entries shown before an overflow marker. Rust binds this to
    /// <c>CAP_LIST</c> (= 20) from <c>src/core/truncate.rs</c> (glab_cmd.rs:329, 562).
    /// </summary>
    private const int MaxList = 20;

    /// <summary>
    /// Maximum CI pipeline list entries shown before an overflow marker. Rust binds this to
    /// <c>CAP_WARNINGS</c> (= 10) from <c>src/core/truncate.rs</c> (glab_cmd.rs:689).
    /// </summary>
    private const int MaxCiList = 10;

    // glab flags that take a value — skipped along with their value when isolating the positional
    // identifier from extra flags (glab_cmd.rs:175-184).
    private static readonly string[] FlagsWithValue =
    {
        "-R", "--repo", "-g", "--group", "-F", "--output", "-m", "--message",
    };

    // Runner boilerplate prefixes stripped from `glab ci trace` output (glab_cmd.rs:803-817).
    private static readonly string[] CiTraceRunnerNoisePrefixes =
    {
        "Running with gitlab-runner",
        "Using Docker executor",
        "Using Shell",
        "Running on runner-",
        "Running on ",
        "Preparing the",
        "Preparing environment",
        "Getting source from",
        "Resolving secrets",
        "Cleaning up",
        "Uploading artifacts",
        "Downloading artifacts",
        "Runtime platform",
    };

    // git fetch / checkout boilerplate stripped from `glab ci trace` output (glab_cmd.rs:822-828).
    private static readonly string[] CiTraceGitNoisePrefixes =
    {
        "Fetching changes with git",
        "Initialized empty Git",
        "Created fresh repository",
        "Checking out ",
        "Skipping Git submodules",
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

    [GeneratedRegex(@"/-/merge_requests/(\d+)")]
    private static partial Regex MrUrlRegex();

    [GeneratedRegex(@"section_(?:start|end):\d+:[a-z0-9_]+(?:\x1b\[0K|\[0K)*")]
    private static partial Regex SectionMarkerRegex();

    [GeneratedRegex(@"\[[\d;]+[A-Za-z]")]
    private static partial Regex BareAnsiRegex();

    /// <summary>
    /// Registry entry point for the <c>glab</c> verb. Reads the ambient <c>--ultra-compact</c> flag
    /// (see <see cref="RuntimeOptions"/>) and dispatches with the real process executor.
    /// </summary>
    /// <param name="args">The arguments following the <c>glab</c> verb (subcommand first).</param>
    /// <returns>The child <c>glab</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, RuntimeOptions.UltraCompact, new ProcessExecutor(), Console.Out, Console.Error);

    /// <summary>
    /// Test-friendly overload accepting an explicit ultra-compact flag, executor, and writers.
    /// </summary>
    /// <param name="args">The arguments following the <c>glab</c> verb (subcommand first).</param>
    /// <param name="ultraCompact">Whether to prefer ultra-compact summaries.</param>
    /// <param name="executor">The process executor used to run <c>glab</c>.</param>
    /// <param name="stdout">The destination for filtered output.</param>
    /// <param name="stderr">The destination for warnings and errors.</param>
    /// <returns>The child <c>glab</c> process's exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0)
        {
            // No subcommand: run raw glab (prints usage, exits nonzero), matching clap requiring one.
            return await PassthroughAsync(executor, stdout, stderr, Array.Empty<string>()).ConfigureAwait(false);
        }

        var subcommand = args[0];
        var subArgs = args.Skip(1).ToArray();

        // If the user explicitly requests a specific output format, passthrough unchanged (glab_cmd.rs:263).
        if (HasOutputFlag(subArgs))
        {
            return await PassthroughAsync(executor, stdout, stderr, Prepend(subcommand, subArgs)).ConfigureAwait(false);
        }

        return subcommand switch
        {
            "mr" => await RunMrAsync(subArgs, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "issue" => await RunIssueAsync(subArgs, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "ci" or "pipeline" => await RunCiAsync(subArgs, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "release" => await RunReleaseAsync(subArgs, executor, stdout, stderr).ConfigureAwait(false),

            // glab api passes through unchanged (glab_cmd.rs:985-990): filtering its JSON would destroy every value.
            "api" => await PassthroughAsync(executor, stdout, stderr, Prepend("api", subArgs)).ConfigureAwait(false),

            _ => await PassthroughAsync(executor, stdout, stderr, Prepend(subcommand, subArgs)).ConfigureAwait(false),
        };
    }

    // ===================== mr =====================

    /// <summary>Dispatches <c>glab mr</c> subcommands. Ports <c>run_mr</c> (glab_cmd.rs:279).</summary>
    private static async Task<int> RunMrAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            return await PassthroughAsync(executor, stdout, stderr, Prepend("mr", args)).ConfigureAwait(false);
        }

        var rest = args.Skip(1).ToArray();
        return args[0] switch
        {
            "list" => await MrListAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "view" => await MrViewAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "create" => await MrCreateAsync(rest, executor, stdout, stderr).ConfigureAwait(false),
            "merge" => await MrActionAsync("merge", "merged", rest, executor, stdout, stderr).ConfigureAwait(false),
            "approve" => await MrActionAsync("approve", "approved", rest, executor, stdout, stderr).ConfigureAwait(false),
            "diff" => await MrDiffAsync(rest, executor, stdout, stderr).ConfigureAwait(false),
            "note" => await MrActionAsync("note", "noted", rest, executor, stdout, stderr).ConfigureAwait(false),
            "update" => await MrActionAsync("update", "updated", rest, executor, stdout, stderr).ConfigureAwait(false),
            _ => await PassthroughAsync(executor, stdout, stderr, Prepend("mr", args)).ConfigureAwait(false),
        };
    }

    /// <summary>Runs <c>glab mr list</c> and prints a compact list. Ports <c>mr_list</c> (glab_cmd.rs:344).</summary>
    private static Task<int> MrListAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "mr", "list", "-F", "json" };
        glabArgs.AddRange(args);
        return RunGlabJsonAsync(executor, stdout, stderr, glabArgs, root => FormatMrList(root, ultraCompact));
    }

    /// <summary>Formats <c>glab mr list -F json</c> output into RTK's compact list. Ports <c>format_mr_list</c> (glab_cmd.rs:298).</summary>
    internal static string FormatMrList(JsonElement json, bool ultraCompact)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var mrs = json.EnumerateArray().ToList();
        if (mrs.Count == 0)
        {
            return ultraCompact ? "No MRs\n" : "No Merge Requests\n";
        }

        var out_ = new StringBuilder(ultraCompact ? "MRs\n" : "Merge Requests\n");
        var allLines = mrs.Select(mr =>
        {
            var iid = JInt(mr, "iid", 0);
            var title = JStr(mr, "title", "???");
            var state = JStr(mr, "state", "???");
            var author = JNestedStr(mr, "author", "username", "???");
            var icon = StateIcon(state, ultraCompact);
            return $"  {icon} !{iid} {Truncate(title, 60)} ({author})";
        }).ToList();

        foreach (var line in allLines.Take(MaxList))
        {
            out_.Append(line).Append('\n');
        }

        if (allLines.Count > MaxList)
        {
            out_.Append($"  … +{allLines.Count - MaxList} more\n");
            var allText = string.Join("\n", allLines);
            var hint = Tee.ForceTeeTailHint(allText, "glab-mrs", MaxList + 1);
            if (hint is not null)
            {
                out_.Append($"  {hint}\n");
            }
        }

        return out_.ToString();
    }

    /// <summary>Maps an MR/issue state to its icon (glab uses lowercase states). Ports <c>state_icon</c> (glab_cmd.rs:115).</summary>
    internal static string StateIcon(string state, bool ultraCompact) => ultraCompact
        ? state switch { "opened" => "O", "merged" => "M", "closed" => "C", _ => "?" }
        : state switch { "opened" => "[open]", "merged" => "[merged]", "closed" => "[closed]", _ => "?" };

    /// <summary>
    /// Pipeline status icon. Non-compact mode uses text tags for parity with <c>gh_cmd.rs</c>.
    /// Ports <c>pipeline_icon</c> (glab_cmd.rs:136). Note the asymmetry preserved from Rust: compact
    /// mode groups <c>running</c>/<c>pending</c> into a single <c>~</c>, but non-compact mode gives
    /// them distinct <c>[run]</c>/<c>[pend]</c> tags.
    /// </summary>
    internal static string PipelineIcon(string status, bool ultraCompact) => ultraCompact
        ? status switch
        {
            "success" => "+",
            "failed" => "x",
            "canceled" or "cancelled" => "X",
            "running" or "pending" => "~",
            "skipped" => "-",
            _ => "?",
        }
        : status switch
        {
            "success" => "[ok]",
            "failed" => "[fail]",
            "canceled" or "cancelled" => "[cancel]",
            "running" => "[run]",
            "pending" => "[pend]",
            "skipped" => "[skip]",
            _ => "?",
        };

    /// <summary>Extracts the MR number from a glab MR URL. Ports <c>extract_mr_number</c> (glab_cmd.rs:160).</summary>
    internal static string? ExtractMrNumber(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = MrUrlRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Formats <c>glab mr view -F json</c> output. Ports <c>format_mr_view</c> (glab_cmd.rs:354).</summary>
    internal static string FormatMrView(JsonElement json, bool ultraCompact)
    {
        var iid = JInt(json, "iid", 0);
        var title = JStr(json, "title", "???");
        var state = JStr(json, "state", "???");
        var author = JNestedStr(json, "author", "username", "???");
        var webUrl = JStr(json, "web_url", string.Empty);
        var mergeStatus = JStr(json, "merge_status", "unknown");
        var sourceBranch = JStr(json, "source_branch", "???");
        var targetBranch = JStr(json, "target_branch", "???");

        var icon = StateIcon(state, ultraCompact);

        var out_ = new StringBuilder();
        out_.Append($"{icon} MR !{iid}: {title}\n");
        out_.Append($"  {author}\n");

        var mergeableStr = mergeStatus switch
        {
            "can_be_merged" => "[ok]",
            "cannot_be_merged" => "[conflict]",
            _ => "[?]",
        };
        out_.Append($"  {state} | {mergeableStr}\n");
        out_.Append($"  {sourceBranch} -> {targetBranch}\n");

        if (TryGetProp(json, "labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            var joined = labels.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .ToList();
            if (joined.Count != 0)
            {
                out_.Append($"  Labels: {string.Join(", ", joined)}\n");
            }
        }

        if (TryGetProp(json, "reviewers", out var reviewers) && reviewers.ValueKind == JsonValueKind.Array)
        {
            var names = reviewers.EnumerateArray()
                .Select(r => JStr(r, "username", string.Empty))
                .Where(u => u.Length != 0)
                .Select(u => $"@{u}")
                .ToList();
            if (names.Count != 0)
            {
                out_.Append($"  Reviewers: {string.Join(", ", names)}\n");
            }
        }

        if (TryGetProp(json, "head_pipeline", out var pipeline) && pipeline.ValueKind != JsonValueKind.Null)
        {
            var pipelineStatus = JStr(pipeline, "status", "unknown");
            var pIcon = PipelineIcon(pipelineStatus, ultraCompact);
            out_.Append($"  Pipeline: {pIcon} {pipelineStatus}\n");
        }

        out_.Append($"  {webUrl}\n");

        var desc = JStrOrNull(json, "description");
        if (!string.IsNullOrEmpty(desc))
        {
            var descFiltered = FilterMarkdownBody(desc);
            if (descFiltered.Length != 0)
            {
                out_.Append('\n');
                foreach (var line in SplitLinesForBody(descFiltered))
                {
                    out_.Append($"  {line}\n");
                }
            }
        }

        return out_.ToString();
    }

    /// <summary>Runs <c>glab mr view</c> and prints a compact summary. Ports <c>mr_view</c> (glab_cmd.rs:421).</summary>
    private static Task<int> MrViewAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        // `glab mr view` without an identifier defaults to the MR for the current branch.
        var (mrNumberOpt, extra) = ParseOptionalIdentifier(args);

        if (ShouldPassthroughView(extra))
        {
            var baseArgs = new List<string> { "mr", "view" };
            if (mrNumberOpt is not null)
            {
                baseArgs.Add(mrNumberOpt);
            }

            baseArgs.AddRange(extra);
            return PassthroughAsync(executor, stdout, stderr, baseArgs);
        }

        var glabArgs = new List<string> { "mr", "view" };
        if (mrNumberOpt is not null)
        {
            glabArgs.Add(mrNumberOpt);
        }

        glabArgs.Add("-F");
        glabArgs.Add("json");
        glabArgs.AddRange(extra);
        return RunGlabJsonAsync(executor, stdout, stderr, glabArgs, root => FormatMrView(root, ultraCompact));
    }

    /// <summary>Runs <c>glab mr create</c> and prints an <c>ok created</c> confirmation. Ports <c>mr_create</c> (glab_cmd.rs:450).</summary>
    private static Task<int> MrCreateAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "mr", "create" };
        glabArgs.AddRange(args);
        return RunGlabFilteredAsync(
            executor, stdout, stderr, glabArgs,
            raw =>
            {
                // glab mr create outputs the URL on success.
                var url = raw.Trim();
                var mrNum = ExtractMrNumber(url) ?? string.Empty;
                var detail = mrNum.Length != 0 ? $"!{mrNum} {url}" : url;
                return OkConfirmation("created", detail);
            },
            noTrailingNewline: false);
    }

    /// <summary>Runs <c>glab mr diff</c> and prints a compacted diff. Ports <c>mr_diff</c> (glab_cmd.rs:475).</summary>
    private static Task<int> MrDiffAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "mr", "diff" };
        glabArgs.AddRange(args);
        return RunGlabFilteredAsync(
            executor, stdout, stderr, glabArgs,
            raw => raw.Trim().Length == 0 ? "No diff\n" : GitCommand.CompactDiff(raw, 500),
            noTrailingNewline: false);
    }

    /// <summary>
    /// Generic MR action handler for merge/approve/note/update. Uses
    /// <see cref="ExtractIdentifierAndExtraArgs"/> to correctly find the MR number even when it
    /// appears after flags (e.g. <c>glab mr note -m "msg" 42</c>). Ports <c>mr_action</c>
    /// (glab_cmd.rs:499).
    /// </summary>
    private static Task<int> MrActionAsync(
        string subcmd, string label, string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "mr", subcmd };
        glabArgs.AddRange(args);

        var extracted = ExtractIdentifierAndExtraArgs(args);
        var mrNum = extracted is { } e ? $"!{e.Identifier}" : string.Empty;

        return RunGlabFilteredAsync(
            executor, stdout, stderr, glabArgs, _ => OkConfirmation(label, mrNum), noTrailingNewline: false);
    }

    // ===================== issue =====================

    /// <summary>Dispatches <c>glab issue</c> subcommands. Ports <c>run_issue</c> (glab_cmd.rs:521).</summary>
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
            "list" => await IssueListAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "view" => await IssueViewAsync(rest, executor, stdout, stderr).ConfigureAwait(false),
            _ => await PassthroughAsync(executor, stdout, stderr, Prepend("issue", args)).ConfigureAwait(false),
        };
    }

    /// <summary>Runs <c>glab issue list</c> and prints a compact list. Ports <c>issue_list</c> (glab_cmd.rs:577).</summary>
    private static Task<int> IssueListAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "issue", "list", "-F", "json" };
        glabArgs.AddRange(args);
        return RunGlabJsonAsync(executor, stdout, stderr, glabArgs, root => FormatIssueList(root, ultraCompact));
    }

    /// <summary>Formats <c>glab issue list -F json</c> output. Ports <c>format_issue_list</c> (glab_cmd.rs:534).</summary>
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
            var iid = JInt(issue, "iid", 0);
            var title = JStr(issue, "title", "???");
            var state = JStr(issue, "state", "???");
            var icon = ultraCompact
                ? (state == "opened" ? "O" : "C")
                : (state == "opened" ? "[open]" : "[closed]");
            return $"  {icon} #{iid} {Truncate(title, 60)}";
        }).ToList();

        foreach (var line in allLines.Take(MaxList))
        {
            out_.Append(line).Append('\n');
        }

        if (allLines.Count > MaxList)
        {
            out_.Append($"  … +{allLines.Count - MaxList} more\n");
            var allText = string.Join("\n", allLines);
            var hint = Tee.ForceTeeTailHint(allText, "glab-issues", MaxList + 1);
            if (hint is not null)
            {
                out_.Append($"  {hint}\n");
            }
        }

        return out_.ToString();
    }

    /// <summary>Formats <c>glab issue view -F json</c> output. Ports <c>format_issue_view</c> (glab_cmd.rs:589).</summary>
    internal static string FormatIssueView(JsonElement json)
    {
        var iid = JInt(json, "iid", 0);
        var title = JStr(json, "title", "???");
        var state = JStr(json, "state", "???");
        var author = JNestedStr(json, "author", "username", "???");
        var webUrl = JStr(json, "web_url", string.Empty);

        var icon = state == "opened" ? "[open]" : "[closed]";

        var out_ = new StringBuilder();
        out_.Append($"{icon} Issue #{iid}: {title}\n");
        out_.Append($"  Author: @{author}\n");
        out_.Append($"  Status: {state}\n");
        out_.Append($"  URL: {webUrl}\n");

        var desc = JStrOrNull(json, "description");
        if (!string.IsNullOrEmpty(desc))
        {
            var descFiltered = FilterMarkdownBody(desc);
            if (descFiltered.Length != 0)
            {
                out_.Append("\n  Description:\n");
                foreach (var line in SplitLinesForBody(descFiltered))
                {
                    out_.Append($"    {line}\n");
                }
            }
        }

        return out_.ToString();
    }

    /// <summary>Runs <c>glab issue view</c> and prints a compact summary. Ports <c>issue_view</c> (glab_cmd.rs:623).</summary>
    private static Task<int> IssueViewAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        // Let glab emit its own error message when the identifier is missing rather than pre-rejecting.
        var (issueNumberOpt, extra) = ParseOptionalIdentifier(args);

        if (ShouldPassthroughView(extra))
        {
            var baseArgs = new List<string> { "issue", "view" };
            if (issueNumberOpt is not null)
            {
                baseArgs.Add(issueNumberOpt);
            }

            baseArgs.AddRange(extra);
            return PassthroughAsync(executor, stdout, stderr, baseArgs);
        }

        var glabArgs = new List<string> { "issue", "view" };
        if (issueNumberOpt is not null)
        {
            glabArgs.Add(issueNumberOpt);
        }

        glabArgs.Add("-F");
        glabArgs.Add("json");
        glabArgs.AddRange(extra);
        return RunGlabJsonAsync(executor, stdout, stderr, glabArgs, FormatIssueView);
    }

    // ===================== ci / pipeline =====================

    /// <summary>
    /// Dispatches <c>glab ci</c>/<c>glab pipeline</c> subcommands. Ports <c>run_ci</c>
    /// (glab_cmd.rs:653). <c>ci view</c> is an interactive TUI (tcell) and has no matched arm, so it
    /// falls to the default passthrough case. The unmatched-arm passthrough hardcodes the literal
    /// prefix <c>"ci"</c> — a byte-for-byte port of a Rust quirk where <c>rtk glab pipeline foo</c>
    /// executes <c>glab ci foo</c> (harmless because they are genuine glab aliases).
    /// </summary>
    private static async Task<int> RunCiAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            return await PassthroughAsync(executor, stdout, stderr, Prepend("ci", args)).ConfigureAwait(false);
        }

        var rest = args.Skip(1).ToArray();
        return args[0] switch
        {
            "list" => await CiListAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "status" => await CiStatusAsync(rest, ultraCompact, executor, stdout, stderr).ConfigureAwait(false),
            "trace" => await CiTraceAsync(rest, executor, stdout, stderr).ConfigureAwait(false),

            // "ci view" is an interactive TUI (tcell) — must run with inherited stdio.
            _ => await PassthroughAsync(executor, stdout, stderr, Prepend("ci", args)).ConfigureAwait(false),
        };
    }

    /// <summary>Runs <c>glab ci list</c> and prints a compact list. Ports <c>ci_list</c> (glab_cmd.rs:705).</summary>
    private static Task<int> CiListAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "ci", "list", "-F", "json" };
        glabArgs.AddRange(args);
        return RunGlabJsonAsync(executor, stdout, stderr, glabArgs, root => FormatCiList(root, ultraCompact));
    }

    /// <summary>Formats <c>glab ci list -F json</c> output. Ports <c>format_ci_list</c> (glab_cmd.rs:668).</summary>
    internal static string FormatCiList(JsonElement json, bool ultraCompact)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var pipelines = json.EnumerateArray().ToList();
        if (pipelines.Count == 0)
        {
            return "No Pipelines\n";
        }

        var out_ = new StringBuilder("Pipelines\n");
        var allLines = pipelines.Select(pipeline =>
        {
            var id = JInt(pipeline, "id", 0);
            var status = JStr(pipeline, "status", "???");
            var refName = JStr(pipeline, "ref", "???");
            var icon = PipelineIcon(status, ultraCompact);
            return $"  {icon} #{id} {status} ({refName})";
        }).ToList();

        foreach (var line in allLines.Take(MaxCiList))
        {
            out_.Append(line).Append('\n');
        }

        if (allLines.Count > MaxCiList)
        {
            out_.Append($"  … +{allLines.Count - MaxCiList} more\n");
            var allText = string.Join("\n", allLines);
            var hint = Tee.ForceTeeTailHint(allText, "glab-pipelines", MaxCiList + 1);
            if (hint is not null)
            {
                out_.Append($"  {hint}\n");
            }
        }

        return out_.ToString();
    }

    /// <summary>
    /// Runs <c>glab ci status</c> (no <c>-F json</c> support — text parsing with raw fallback). Ports
    /// <c>ci_status</c> (glab_cmd.rs:755).
    /// </summary>
    private static Task<int> CiStatusAsync(
        string[] args, bool ultraCompact, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "ci", "status" };
        glabArgs.AddRange(args);
        return RunGlabFilteredAsync(
            executor, stdout, stderr, glabArgs, raw => FormatCiStatus(raw, ultraCompact), noTrailingNewline: false);
    }

    /// <summary>
    /// Formats <c>glab ci status</c> text output (English keyword parsing, raw fallback). Returns
    /// the raw input verbatim when no status keyword is recognized on any line (e.g. non-English
    /// locale). Ports <c>format_ci_status</c> (glab_cmd.rs:717).
    /// </summary>
    internal static string FormatCiStatus(string raw, bool ultraCompact)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var out_ = new StringBuilder();
        var anyKeywordMatched = false;

        foreach (var line in ReadCommand.SplitLines(raw))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var icon = trimmed.Contains("passed", StringComparison.Ordinal) || trimmed.Contains("success", StringComparison.Ordinal)
                ? PipelineIcon("success", ultraCompact)
                : trimmed.Contains("failed", StringComparison.Ordinal)
                    ? PipelineIcon("failed", ultraCompact)
                    : trimmed.Contains("running", StringComparison.Ordinal)
                        ? PipelineIcon("running", ultraCompact)
                        : trimmed.Contains("pending", StringComparison.Ordinal)
                            ? PipelineIcon("pending", ultraCompact)
                            : trimmed.Contains("canceled", StringComparison.Ordinal) || trimmed.Contains("cancelled", StringComparison.Ordinal)
                                ? PipelineIcon("canceled", ultraCompact)
                                : string.Empty;

            if (icon.Length != 0)
            {
                anyKeywordMatched = true;
                out_.Append($"{icon} {trimmed}\n");
            }
            else
            {
                out_.Append($"  {trimmed}\n");
            }
        }

        // Non-English locale or unrecognized format — preserve raw output verbatim.
        return anyKeywordMatched ? out_.ToString() : raw;
    }

    /// <summary>Runs <c>glab ci trace</c> and prints the filtered job log. Ports <c>ci_trace</c> (glab_cmd.rs:771).</summary>
    private static Task<int> CiTraceAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "ci", "trace" };
        glabArgs.AddRange(args);
        return RunGlabFilteredAsync(executor, stdout, stderr, glabArgs, FilterCiTrace, noTrailingNewline: false);
    }

    /// <summary>
    /// Filters CI job trace output: strips ANSI codes, section markers, and runner boilerplate.
    /// Keeps warnings, errors, and build output. Ports <c>filter_ci_trace</c> (glab_cmd.rs:788).
    /// </summary>
    internal static string FilterCiTrace(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var cleaned = Utils.StripAnsi(raw);
        cleaned = BareAnsiRegex().Replace(cleaned, string.Empty);
        cleaned = SectionMarkerRegex().Replace(cleaned, string.Empty);

        var out_ = new StringBuilder();

        foreach (var line in ReadCommand.SplitLines(cleaned))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (CiTraceRunnerNoisePrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal))
                || (trimmed.StartsWith("on ", StringComparison.Ordinal) && trimmed.Contains("system ID:", StringComparison.Ordinal)))
            {
                continue;
            }

            if (CiTraceGitNoisePrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            out_.Append(trimmed).Append('\n');
        }

        return out_.ToString();
    }

    // ===================== release =====================

    /// <summary>Dispatches <c>glab release</c> subcommands. Ports <c>run_release</c> (glab_cmd.rs:840).</summary>
    private static async Task<int> RunReleaseAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            return await PassthroughAsync(executor, stdout, stderr, Prepend("release", args)).ConfigureAwait(false);
        }

        var rest = args.Skip(1).ToArray();
        return args[0] switch
        {
            "list" => await ReleaseListAsync(rest, executor, stdout, stderr).ConfigureAwait(false),
            "view" => await ReleaseViewAsync(rest, executor, stdout, stderr).ConfigureAwait(false),
            _ => await PassthroughAsync(executor, stdout, stderr, Prepend("release", args)).ConfigureAwait(false),
        };
    }

    /// <summary>Runs <c>glab release list</c> and prints a compact list. Ports <c>release_list</c> (glab_cmd.rs:905).</summary>
    private static Task<int> ReleaseListAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "release", "list" };
        glabArgs.AddRange(args);
        return RunGlabFilteredAsync(
            executor, stdout, stderr, glabArgs, raw => FormatReleaseList(raw) ?? raw, noTrailingNewline: false);
    }

    /// <summary>
    /// Formats <c>glab release list</c> tab-separated output into compact form. Input format:
    /// <c>"Name\tTag\tCreated\n"</c> header + data rows. Returns null when no data rows were parsed,
    /// so the caller can fall back to the raw stdout. Ports <c>format_release_list</c> (glab_cmd.rs:854).
    /// </summary>
    internal static string? FormatReleaseList(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var lines = new Queue<string>(ReadCommand.SplitLines(raw));

        // Skip "Showing N releases..." preamble and blank lines up to (and consuming) the header row.
        while (lines.Count > 0)
        {
            var trimmed = lines.Peek().Trim();
            if (trimmed.StartsWith("Name\t", StringComparison.Ordinal) || trimmed.StartsWith("NAME\t", StringComparison.Ordinal))
            {
                lines.Dequeue(); // consume header
                break;
            }

            lines.Dequeue();
        }

        var out_ = new StringBuilder("Releases\n");
        var count = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var name = parts[0].Trim();
            var tag = parts[1].Trim();
            var created = parts[2].Trim();

            out_.Append(name == tag ? $"  {name} ({created})\n" : $"  {name} [{tag}] ({created})\n");

            count++;
            if (count >= 20)
            {
                break;
            }
        }

        return count == 0 ? null : out_.ToString();
    }

    /// <summary>Runs <c>glab release view</c> and prints the filtered release notes. Ports <c>release_view</c> (glab_cmd.rs:920).</summary>
    private static Task<int> ReleaseViewAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "release", "view" };
        glabArgs.AddRange(args);
        return RunGlabFilteredAsync(executor, stdout, stderr, glabArgs, FilterReleaseView, noTrailingNewline: false);
    }

    /// <summary>
    /// Filters release view output: strips the SOURCES block, image lines, HTML comments,
    /// horizontal rules, and collapses blank lines. Ports <c>filter_release_view</c> (glab_cmd.rs:937).
    /// </summary>
    internal static string FilterReleaseView(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var out_ = new StringBuilder();
        var inSources = false;

        foreach (var line in ReadCommand.SplitLines(raw))
        {
            var trimmed = line.Trim();

            // Skip SOURCES section (archive download URLs).
            if (trimmed == "SOURCES")
            {
                inSources = true;
                continue;
            }

            if (inSources)
            {
                if (trimmed.StartsWith("http://", StringComparison.Ordinal) || trimmed.StartsWith("https://", StringComparison.Ordinal))
                {
                    continue;
                }

                inSources = false;
            }

            // Strip image-only lines.
            if (trimmed.StartsWith("![", StringComparison.Ordinal) && trimmed.EndsWith(')') && trimmed.Contains("](", StringComparison.Ordinal))
            {
                continue;
            }

            // Strip glab's "Image: name → url" rendering.
            if (trimmed.StartsWith("Image:", StringComparison.Ordinal) && trimmed.Contains('→'))
            {
                continue;
            }

            // Strip HTML comments.
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal) && trimmed.EndsWith("-->", StringComparison.Ordinal))
            {
                continue;
            }

            // Strip horizontal rules (--- rendered as --------).
            if (trimmed.Length >= 3 && trimmed.All(c => c == '-'))
            {
                continue;
            }

            out_.Append(line).Append('\n');
        }

        // Collapse multiple blank lines.
        return MultiBlankRegex().Replace(out_.ToString(), "\n\n");
    }

    // ===================== markdown body filter =====================

    /// <summary>
    /// Filters an MR/issue markdown body to remove noise (HTML comments, badge lines, image-only
    /// lines, horizontal rules) and collapse excessive blank lines, preserving fenced code blocks
    /// untouched. Ports <c>filter_markdown_body</c> (glab_cmd.rs:42).
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
            // Find next code-block opening, preferring ``` anywhere over ~~~ (glab_cmd.rs:51-53).
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

    /// <summary>Filters a markdown segment outside any code block. Ports <c>filter_markdown_segment</c> (glab_cmd.rs:105).</summary>
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
    /// Splits a filtered body into lines the way Rust's <c>str::lines()</c> does (used to prefix
    /// each line with indentation): split on <c>\n</c>, dropping a trailing <c>\r</c>, with no
    /// trailing empty entry when the text ends in a newline.
    /// </summary>
    private static IEnumerable<string> SplitLinesForBody(string text)
    {
        // FilterMarkdownBody trims trailing newlines, so a simple split matches Rust's lines() here.
        return text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l);
    }

    // ===================== identifier parsing =====================

    /// <summary>
    /// Extracts the first positional identifier (MR/issue number or URL) from args, skipping glab
    /// flags that take a value. Returns the identifier and remaining args. Ports
    /// <c>extract_identifier_and_extra_args</c> (glab_cmd.rs:169).
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

            // First non-flag arg is the identifier (number/URL).
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
    /// Like <see cref="ExtractIdentifierAndExtraArgs"/> but yields <c>(null, args)</c> when no
    /// positional identifier is present, so callers can defer the "id required" decision to
    /// <c>glab</c> itself (e.g. <c>glab mr view</c> defaults to the current branch's MR). Ports
    /// <c>parse_optional_identifier</c> (glab_cmd.rs:218).
    /// </summary>
    /// <param name="args">The subcommand arguments.</param>
    /// <returns>The optional identifier and the extra args.</returns>
    internal static (string? Identifier, List<string> Extra) ParseOptionalIdentifier(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var extracted = ExtractIdentifierAndExtraArgs(args);
        return extracted is { } e ? (e.Identifier, e.Extra) : (null, args.ToList());
    }

    /// <summary>
    /// Reports whether the user explicitly requested JSON/custom output format. When present, the
    /// caller passes through to avoid double JSON injection. Ports <c>has_output_flag</c>
    /// (glab_cmd.rs:227).
    /// </summary>
    internal static bool HasOutputFlag(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a is "--output" or "-F" or "--json");
    }

    /// <summary>
    /// Reports whether a <c>view</c> subcommand should passthrough (<c>--web</c>, <c>--comments</c>,
    /// etc.) rather than be filtered. Shared by both <c>mr view</c> and <c>issue view</c>. Ports
    /// <c>should_passthrough_view</c> (glab_cmd.rs:233).
    /// </summary>
    internal static bool ShouldPassthroughView(IReadOnlyList<string> extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return extraArgs.Any(a => a is "--web" or "--comments" or "--output" or "-F");
    }

    /// <summary>
    /// Truncates a string to <paramref name="maxLen"/> Unicode scalar values, appending <c>...</c>
    /// when longer (or returning <c>...</c> when <paramref name="maxLen"/> &lt; 3). Ports
    /// <c>utils::truncate</c> (utils.rs:25), counting runes so multibyte input is never split
    /// mid-character.
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

    /// <summary>Ports <c>ok_confirmation</c> (utils.rs:181): <c>"ok {action}"</c>, or <c>"ok {action} {detail}"</c> when non-empty.</summary>
    internal static string OkConfirmation(string action, string detail) =>
        detail.Length == 0 ? $"ok {action}" : $"ok {action} {detail}";

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

    /// <summary>Mirrors <c>json[prop].as_str()</c> (no default — returns null when absent, non-string, or JSON null).</summary>
    private static string? JStrOrNull(JsonElement el, string prop) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Mirrors <c>json[prop][inner].as_str().unwrap_or(dflt)</c> for a nested string (e.g. <c>author.username</c>).</summary>
    private static string JNestedStr(JsonElement el, string prop, string inner, string dflt) =>
        TryGetProp(el, prop, out var v) ? JStr(v, inner, dflt) : dflt;

    /// <summary>Mirrors <c>json[prop].as_i64().unwrap_or(dflt)</c>: the value only when it is a JSON integer.</summary>
    private static long JInt(JsonElement el, string prop, long dflt) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : dflt;

    // ===================== runners =====================

    /// <summary>
    /// Runs a <c>glab</c> command, parses its stdout as JSON, and prints the result of
    /// <paramref name="formatFn"/>. On a JSON parse error the raw stdout is printed unchanged
    /// (glab returns plain text for empty results). Ports <c>run_glab_json</c> (glab_cmd.rs:242)
    /// with the <c>stdout_only().early_exit_on_failure().no_trailing_newline()</c> envelope.
    /// </summary>
    private static Task<int> RunGlabJsonAsync(
        IProcessExecutor executor, TextWriter stdout, TextWriter stderr,
        IReadOnlyList<string> glabArgs, Func<JsonElement, string> formatFn)
    {
        return RunGlabFilteredAsync(executor, stdout, stderr, glabArgs, raw =>
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
    /// Runs a <c>glab</c> command capturing stdout/stderr separately, then filters stdout with
    /// <paramref name="filter"/>. On child failure the raw streams are forwarded and the exit code
    /// propagated without filtering (Rust's <c>skip_filter_on_failure</c>, runner.rs:98). The
    /// mandatory fallback contract applies: a throwing filter falls back to raw stdout.
    /// </summary>
    private static async Task<int> RunGlabFilteredAsync(
        IProcessExecutor executor, TextWriter stdout, TextWriter stderr,
        IReadOnlyList<string> glabArgs, Func<string, string> filter, bool noTrailingNewline)
    {
        var result = await executor.ExecuteAsync(
            new ExecutionRequest("glab", glabArgs, CaptureMode: ExecutionCaptureMode.Separate)).ConfigureAwait(false);

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
    /// Runs <c>glab</c> with the given argv (after the executable name) as a raw passthrough,
    /// inheriting the child's stdio so output reaches the terminal unchanged, and propagates the
    /// exit code. Ports <c>run_passthrough</c>/<c>run_passthrough_with_extra</c> (glab_cmd.rs:994-1005).
    /// </summary>
    private static async Task<int> PassthroughAsync(
        IProcessExecutor executor, TextWriter stdout, TextWriter stderr, IReadOnlyList<string> glabArgs)
    {
        var result = await executor.ExecuteAsync(
            new ExecutionRequest("glab", glabArgs, CaptureMode: ExecutionCaptureMode.Inherit)).ConfigureAwait(false);

        // Inherited stdio: glab wrote directly to the terminal. If capture happened anyway (e.g. a
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
