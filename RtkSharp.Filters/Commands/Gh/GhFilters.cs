using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Gh;

/// <summary>
/// Pure filtering logic for the <c>gh</c> (GitHub CLI) proxy: condensing PR/issue/run/repo JSON and
/// text output into terse agent-friendly summaries. Extracted verbatim from
/// <c>RtkSharp.Commands.Gh.GhCommand</c> — signatures and logic are unchanged, only visibility moved
/// from <c>internal</c> to <c>public</c> and the containing type from <c>GhCommand</c> to
/// <c>GhFilters</c>. See <c>RtkSharp.Commands.Gh.GhCommand</c> for the process-execution/dispatch code
/// that calls these methods, and for the original Rust source pointers (<c>src/cmds/git/gh_cmd.rs</c>)
/// preserved in each method's XML doc.
/// </summary>
/// <remarks>
/// Every private helper referenced here (<c>StateIcon</c>, <c>RunIcon</c>, <c>Truncate</c>, the JSON
/// accessors, <c>SplitLinesForBody</c>, <c>FilterMarkdownSegment</c>, <c>CheckSucceeded</c>/
/// <c>CheckFailed</c>, and the compiled regexes used only by the moved methods) is used exclusively by
/// the eleven methods moved here, so each moved wholesale as a member of this class rather than staying
/// duplicated in <c>GhCommand.cs</c> — confirmed by grep against the original file before the move.
/// <c>StateIcon</c> and <c>Truncate</c> stay <c>internal</c> (rather than <c>private</c>) because the
/// original test suite exercised them directly by qualified name.
/// </remarks>
public static partial class GhFilters
{
    /// <summary>
    /// Maximum list entries shown before an overflow marker. Rust binds this to <c>CAP_LIST</c> (= 20)
    /// from <c>src/core/truncate.rs</c> (gh_cmd.rs:274, 633).
    /// </summary>
    private const int MaxList = 20;

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

    // ===================== pr =====================

    /// <summary>Formats <c>gh pr list --json</c> output into RTK's compact list. Ports <c>format_pr_list</c> (gh_cmd.rs:245).</summary>
    public static string FormatPrList(JsonElement json, bool ultraCompact)
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

    /// <summary>Formats <c>gh pr view --json</c> output. Ports <c>format_pr_view</c> (gh_cmd.rs:360).</summary>
    public static string FormatPrView(JsonElement json, bool ultraCompact)
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

    /// <summary>Summarizes <c>gh pr checks</c> table output. Ports <c>format_pr_checks</c> (gh_cmd.rs:472).</summary>
    public static string FormatPrChecks(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);

        var passed = 0;
        var failed = 0;
        var pending = 0;
        var failedChecks = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(stdout))
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

    /// <summary>Formats <c>gh pr status --json</c> output. Ports <c>format_pr_status</c> (gh_cmd.rs:521).</summary>
    public static string FormatPrStatus(JsonElement json)
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
    public static string FormatPrStatusEntry(JsonElement pr)
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

    // ===================== issue =====================

    /// <summary>Formats <c>gh issue list --json</c> output. Ports <c>format_issue_list</c> (gh_cmd.rs:607).</summary>
    public static string FormatIssueList(JsonElement json, bool ultraCompact)
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

    /// <summary>Formats <c>gh issue view --json</c> output. Ports <c>format_issue_view</c> (gh_cmd.rs:673).</summary>
    public static string FormatIssueView(JsonElement json)
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

    // ===================== run (workflow) =====================

    /// <summary>Formats <c>gh run list --json</c> output. Ports <c>format_run_list</c> (gh_cmd.rs:734).</summary>
    public static string FormatRunList(JsonElement json, bool ultraCompact)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var out_ = new StringBuilder(ultraCompact ? "Runs\n" : "Workflow Runs\n");
        foreach (var run in json.EnumerateArray())
        {
            var id = JInt(run, "databaseId", 0);
            var name = JStr(run, "name", "???");
            var status = JStr(run, "status", "???");
            var conclusion = JStr(run, "conclusion", string.Empty);
            var icon = RunIcon(status, conclusion, ultraCompact);
            out_.Append($"  {icon} {Truncate(name, 50)} [{id}]\n");
        }

        return out_.ToString();
    }

    /// <summary>Maps a workflow run's status/conclusion to its icon. Ports the match in <c>format_run_list</c> (gh_cmd.rs:750).</summary>
    private static string RunIcon(string status, string conclusion, bool ultraCompact) => ultraCompact
        ? conclusion switch
        {
            "success" => "[ok]",
            "failure" => "[x]",
            "cancelled" => "X",
            _ => status == "in_progress" ? "~" : "?",
        }
        : conclusion switch
        {
            "success" => "[ok]",
            "failure" => "[FAIL]",
            "cancelled" => "[X]",
            _ => status == "in_progress" ? "[time]" : "[pending]",
        };

    /// <summary>Summarizes <c>gh run view</c> text output. Ports <c>format_run_view</c> (gh_cmd.rs:815).</summary>
    public static string FormatRunView(string stdout, string runId)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(runId);

        var out_ = new StringBuilder(runId.Length == 0 ? "Workflow Run\n" : $"Workflow Run #{runId}\n");
        var inJobs = false;

        foreach (var line in SourceFilterLineSplitter.SplitLines(stdout))
        {
            if (line.Contains("JOBS", StringComparison.Ordinal))
            {
                inJobs = true;
            }

            if (inJobs)
            {
                if (line.Contains('✓', StringComparison.Ordinal) || line.Contains("success", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains("[x]", StringComparison.Ordinal) || line.Contains("fail", StringComparison.Ordinal))
                {
                    out_.Append($"  [FAIL] {line.Trim()}\n");
                }
            }
            else if (line.Contains("Status:", StringComparison.Ordinal) || line.Contains("Conclusion:", StringComparison.Ordinal))
            {
                out_.Append($"  {line.Trim()}\n");
            }
        }

        return out_.ToString();
    }

    // ===================== repo =====================

    /// <summary>Formats <c>gh repo view --json</c> output. Ports <c>format_repo_view</c> (gh_cmd.rs:863).</summary>
    public static string FormatRepoView(JsonElement json)
    {
        var out_ = new StringBuilder();
        var name = JStr(json, "name", "???");
        var owner = JNestedStr(json, "owner", "login", "???");
        var description = JStr(json, "description", string.Empty);
        var url = JStr(json, "url", string.Empty);
        var stars = JInt(json, "stargazerCount", 0);
        var forks = JInt(json, "forkCount", 0);
        var isPrivate = JBool(json, "isPrivate", false);
        var visibility = isPrivate ? "[private]" : "[public]";

        out_.Append($"{owner}/{name}\n");
        out_.Append($"  {visibility}\n");
        if (description.Length != 0)
        {
            out_.Append($"  {Truncate(description, 80)}\n");
        }

        out_.Append($"  {stars} stars | {forks} forks\n");
        out_.Append($"  {url}\n");
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
    public static string FilterMarkdownBody(string body)
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

    // ===================== shared helpers =====================

    /// <summary>
    /// Truncates a string to <paramref name="maxLen"/> Unicode scalar values, appending <c>...</c> when
    /// longer (or returning <c>...</c> when <paramref name="maxLen"/> &lt; 3). Ports <c>utils::truncate</c>
    /// (utils.rs:25), counting runes so multibyte input is never split mid-character.
    /// </summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="maxLen">The maximum width in Unicode scalar values.</param>
    /// <returns>The (possibly truncated) string.</returns>
    internal static string Truncate(string s, int maxLen) => Utils.Truncate(s, maxLen);

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

    /// <summary>Mirrors <c>json[prop].as_bool().unwrap_or(dflt)</c>: the value only when it is a JSON boolean.</summary>
    private static bool JBool(JsonElement el, string prop, bool dflt) =>
        TryGetProp(el, prop, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => true,
            _ => false,
        } ? v.GetBoolean() : dflt;

    /// <summary>A status check counts as passed when its <c>conclusion</c> or <c>state</c> is <c>SUCCESS</c> (gh_cmd.rs:400).</summary>
    private static bool CheckSucceeded(JsonElement check) =>
        JStr(check, "conclusion", string.Empty) == "SUCCESS" || JStr(check, "state", string.Empty) == "SUCCESS";

    /// <summary>A status check counts as failed when its <c>conclusion</c> or <c>state</c> is <c>FAILURE</c> (gh_cmd.rs:407).</summary>
    private static bool CheckFailed(JsonElement check) =>
        JStr(check, "conclusion", string.Empty) == "FAILURE" || JStr(check, "state", string.Empty) == "FAILURE";
}
