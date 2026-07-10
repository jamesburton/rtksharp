using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Git;

/// <summary>
/// Pure filtering logic for the <c>glab</c> (GitLab CLI) proxy: condensing MR/issue/CI/release JSON
/// and text output into terse agent-friendly summaries. Extracted verbatim from
/// <c>RtkSharp.Commands.Git.GlabCommand</c> — signatures and logic are unchanged, only visibility moved
/// from <c>internal</c> to <c>public</c> and the containing type from <c>GlabCommand</c> to
/// <c>GlabFilters</c>. See <c>RtkSharp.Commands.Git.GlabCommand</c> for the process-execution/dispatch
/// code that calls these methods, and for the original Rust source pointers
/// (<c>src/cmds/git/glab_cmd.rs</c>) preserved in each method's XML doc.
/// </summary>
/// <remarks>
/// Every private helper referenced here (<c>StateIcon</c>, <c>PipelineIcon</c>, <c>Truncate</c>, the
/// JSON accessors, <c>SplitLinesForBody</c>, <c>FilterMarkdownSegment</c>, the noise-prefix arrays, and
/// the compiled regexes used only by the moved methods) is used exclusively by the ten methods moved
/// here, so each moved wholesale as a private member of this class rather than staying duplicated in
/// <c>GlabCommand.cs</c> — confirmed by grep against the original file before the move.
/// </remarks>
public static partial class GlabFilters
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

    [GeneratedRegex(@"section_(?:start|end):\d+:[a-z0-9_]+(?:\x1b\[0K|\[0K)*")]
    private static partial Regex SectionMarkerRegex();

    [GeneratedRegex(@"\[[\d;]+[A-Za-z]")]
    private static partial Regex BareAnsiRegex();

    /// <summary>Formats <c>glab mr list -F json</c> output into RTK's compact list. Ports <c>format_mr_list</c> (glab_cmd.rs:298).</summary>
    public static string FormatMrList(JsonElement json, bool ultraCompact)
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

    /// <summary>
    /// Maps an MR/issue state to its icon (glab uses lowercase states). Ports <c>state_icon</c>
    /// (glab_cmd.rs:115). <c>internal</c> (not <c>public</c>, matching pre-move visibility) because it
    /// is directly unit-tested in <c>RtkSharp.Filters.Tests</c> (granted access via
    /// <c>InternalsVisibleTo</c>) even though it isn't part of this extraction's public interface list.
    /// </summary>
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

    /// <summary>Formats <c>glab mr view -F json</c> output. Ports <c>format_mr_view</c> (glab_cmd.rs:354).</summary>
    public static string FormatMrView(JsonElement json, bool ultraCompact)
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

    /// <summary>Formats <c>glab issue list -F json</c> output. Ports <c>format_issue_list</c> (glab_cmd.rs:534).</summary>
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
    public static string FormatIssueView(JsonElement json)
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

    /// <summary>Formats <c>glab ci list -F json</c> output. Ports <c>format_ci_list</c> (glab_cmd.rs:668).</summary>
    public static string FormatCiList(JsonElement json, bool ultraCompact)
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
    /// Formats <c>glab ci status</c> text output (English keyword parsing, raw fallback). Returns
    /// the raw input verbatim when no status keyword is recognized on any line (e.g. non-English
    /// locale). Ports <c>format_ci_status</c> (glab_cmd.rs:717).
    /// </summary>
    public static string FormatCiStatus(string raw, bool ultraCompact)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var out_ = new StringBuilder();
        var anyKeywordMatched = false;

        foreach (var line in SourceFilterLineSplitter.SplitLines(raw))
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

    /// <summary>
    /// Filters CI job trace output: strips ANSI codes, section markers, and runner boilerplate.
    /// Keeps warnings, errors, and build output. Ports <c>filter_ci_trace</c> (glab_cmd.rs:788).
    /// </summary>
    public static string FilterCiTrace(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var cleaned = Utils.StripAnsi(raw);
        cleaned = BareAnsiRegex().Replace(cleaned, string.Empty);
        cleaned = SectionMarkerRegex().Replace(cleaned, string.Empty);

        var out_ = new StringBuilder();

        foreach (var line in SourceFilterLineSplitter.SplitLines(cleaned))
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

    /// <summary>
    /// Formats <c>glab release list</c> tab-separated output into compact form. Input format:
    /// <c>"Name\tTag\tCreated\n"</c> header + data rows. Returns null when no data rows were parsed,
    /// so the caller can fall back to the raw stdout. Ports <c>format_release_list</c> (glab_cmd.rs:854).
    /// </summary>
    public static string? FormatReleaseList(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var lines = new Queue<string>(SourceFilterLineSplitter.SplitLines(raw));

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

    /// <summary>
    /// Filters release view output: strips the SOURCES block, image lines, HTML comments,
    /// horizontal rules, and collapses blank lines. Ports <c>filter_release_view</c> (glab_cmd.rs:937).
    /// </summary>
    public static string FilterReleaseView(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var out_ = new StringBuilder();
        var inSources = false;

        foreach (var line in SourceFilterLineSplitter.SplitLines(raw))
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

    /// <summary>
    /// Filters an MR/issue markdown body to remove noise (HTML comments, badge lines, image-only
    /// lines, horizontal rules) and collapse excessive blank lines, preserving fenced code blocks
    /// untouched. Ports <c>filter_markdown_body</c> (glab_cmd.rs:42).
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

    /// <summary>
    /// Truncates a string to <paramref name="maxLen"/> Unicode scalar values, appending <c>...</c>
    /// when longer (or returning <c>...</c> when <paramref name="maxLen"/> &lt; 3). Ports
    /// <c>utils::truncate</c> (utils.rs:25), counting runes so multibyte input is never split
    /// mid-character.
    /// </summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="maxLen">The maximum width in Unicode scalar values.</param>
    /// <returns>The (possibly truncated) string.</returns>
    private static string Truncate(string s, int maxLen)
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
}
