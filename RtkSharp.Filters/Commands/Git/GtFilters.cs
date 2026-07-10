using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Git;

/// <summary>
/// Pure filtering logic for the <c>rtk gt</c> (Graphite) proxy: condensing <c>gt log</c>/<c>submit</c>/
/// <c>sync</c>/<c>restack</c>/<c>create</c> output. Extracted verbatim from
/// <c>RtkSharp.Commands.Git.GtCommand</c> — signatures and logic are unchanged, only visibility moved
/// from <c>internal</c> to <c>public</c> and the containing type from <c>GtCommand</c> to
/// <c>GtFilters</c>. See <c>RtkSharp.Commands.Git.GtCommand</c> for the process-execution/dispatch code
/// that calls these methods, and for the original Rust source pointers (<c>src/cmds/git/gt_cmd.rs</c>)
/// preserved in each method's XML doc.
/// </summary>
public static class GtFilters
{
    private static readonly Regex EmailRegex = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);

    private static readonly Regex BranchNameRegex = new(
        @"(?:Created|Pushed|pushed|Deleted|deleted)\s+branch\s+[`""']?([a-zA-Z0-9/_.\-+@]+)",
        RegexOptions.Compiled);

    private static readonly Regex PrLineRegex = new(
        @"(Created|Updated)\s+pull\s+request\s+#(\d+)\s+for\s+([^\s:]+)(?::\s*(\S+))?",
        RegexOptions.Compiled);

    // gt log entries are multi-line — trim the list cap to keep token savings above 60%.
    // Rust binds this to reduced(CAP_LIST, 5) = reduced(20, 5) = 15 (src/core/truncate.rs).
    private const int MaxLogEntries = 15;

    /// <summary>The identity filter used for <c>gt</c> subcommands whose raw output is already compact enough. Ports the trivial <c>|s| s</c> filter passed for <c>gt branch</c>/<c>gt log short</c>.</summary>
    /// <param name="input">The input text.</param>
    /// <returns><paramref name="input"/> unchanged.</returns>
    public static string FilterIdentity(string input) => input;

    /// <summary>
    /// Compacts <c>gt log</c>'s ASCII-graph output: strips author emails, truncates long lines, and
    /// caps the number of graph-node entries shown. Faithful port of Rust's
    /// <c>filter_gt_log_entries</c> (<c>gt_cmd.rs</c>:175-204).
    /// </summary>
    /// <param name="input">The ANSI-stripped, trimmed <c>gt log</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGtLogEntries(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var lines = trimmed.Split('\n');
        var result = new List<string>();
        var entryCount = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (IsGraphNode(line))
            {
                entryCount++;
            }

            var replaced = EmailRegex.Replace(line, "");
            var processed = Utils.Truncate(replaced.TrimEnd(), 120);
            result.Add(processed);

            if (entryCount >= MaxLogEntries)
            {
                var remaining = 0;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (IsGraphNode(lines[j]))
                    {
                        remaining++;
                    }
                }

                if (remaining > 0)
                {
                    result.Add($"... +{remaining} more entries");
                }

                break;
            }
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Summarizes <c>gt submit</c> output: which branches were pushed, and which PRs were
    /// created/updated (with URL, when present). Faithful port of Rust's <c>filter_gt_submit</c>
    /// (<c>gt_cmd.rs</c>:206-263).
    /// </summary>
    /// <param name="input">The ANSI-stripped, trimmed <c>gt submit</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGtSubmit(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var pushed = new List<string>();
        var prs = new List<string>();

        foreach (var raw in trimmed.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Contains("pushed", StringComparison.Ordinal) || line.Contains("Pushed", StringComparison.Ordinal))
            {
                pushed.Add(ExtractBranchName(line));
            }
            else
            {
                var caps = PrLineRegex.Match(line);
                if (caps.Success)
                {
                    var action = caps.Groups[1].Value.ToLowerInvariant();
                    var num = caps.Groups[2].Value;
                    var branch = caps.Groups[3].Value;
                    if (caps.Groups[4].Success)
                    {
                        prs.Add($"{action} PR #{num} {branch} {caps.Groups[4].Value}");
                    }
                    else
                    {
                        prs.Add($"{action} PR #{num} {branch}");
                    }
                }
            }
        }

        var summary = new List<string>();

        if (pushed.Count > 0)
        {
            var branchNames = pushed.Where(s => s.Length != 0).ToList();
            summary.Add(branchNames.Count > 0
                ? $"pushed {string.Join(", ", branchNames)}"
                : $"pushed {pushed.Count} branches");
        }

        summary.AddRange(prs);

        return summary.Count == 0 ? Utils.Truncate(trimmed, 200) : string.Join('\n', summary);
    }

    /// <summary>
    /// Summarizes <c>gt sync</c> output: how many branches were synced and which were deleted.
    /// Faithful port of Rust's <c>filter_gt_sync</c> (<c>gt_cmd.rs</c>:265-318).
    /// </summary>
    /// <param name="input">The ANSI-stripped, trimmed <c>gt sync</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGtSync(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var synced = 0;
        var deleted = 0;
        var deletedNames = new List<string>();

        foreach (var raw in trimmed.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if ((line.Contains("Synced", StringComparison.Ordinal) && line.Contains("branch", StringComparison.Ordinal))
                || line.StartsWith("Synced with remote", StringComparison.Ordinal))
            {
                synced++;
            }

            if (line.Contains("deleted", StringComparison.Ordinal) || line.Contains("Deleted", StringComparison.Ordinal))
            {
                deleted++;
                var name = ExtractBranchName(line);
                if (name.Length != 0)
                {
                    deletedNames.Add(name);
                }
            }
        }

        var parts = new List<string>();

        if (synced > 0)
        {
            parts.Add($"{synced} synced");
        }

        if (deleted > 0)
        {
            parts.Add(deletedNames.Count == 0
                ? $"{deleted} deleted"
                : $"{deleted} deleted ({string.Join(", ", deletedNames)})");
        }

        return parts.Count == 0 ? OkConfirmation("synced", "") : $"ok sync: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Summarizes <c>gt restack</c> output: how many branches were restacked. Faithful port of Rust's
    /// <c>filter_gt_restack</c> (<c>gt_cmd.rs</c>:320-339).
    /// </summary>
    /// <param name="input">The ANSI-stripped, trimmed <c>gt restack</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGtRestack(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var restacked = 0;
        foreach (var raw in trimmed.Split('\n'))
        {
            var line = raw.Trim();
            if ((line.Contains("Restacked", StringComparison.Ordinal) || line.Contains("Rebased", StringComparison.Ordinal))
                && line.Contains("branch", StringComparison.Ordinal))
            {
                restacked++;
            }
        }

        return restacked > 0
            ? OkConfirmation("restacked", $"{restacked} branches")
            : OkConfirmation("restacked", "");
    }

    /// <summary>
    /// Summarizes <c>gt create</c> output: the name of the branch that was created. Faithful port of
    /// Rust's <c>filter_gt_create</c> (<c>gt_cmd.rs</c>:341-365).
    /// </summary>
    /// <param name="input">The ANSI-stripped, trimmed <c>gt create</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGtCreate(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var branchName = string.Empty;
        foreach (var raw in trimmed.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("Created", StringComparison.Ordinal) || line.Contains("created", StringComparison.Ordinal))
            {
                branchName = ExtractBranchName(line);
                break;
            }
        }

        if (branchName.Length == 0)
        {
            var firstLine = trimmed.Split('\n').FirstOrDefault() ?? "";
            return OkConfirmation("created", firstLine.Trim());
        }

        return OkConfirmation("created", branchName);
    }

    /// <summary>Formats a write-operation confirmation: <c>"ok {action}"</c> or <c>"ok {action} {detail}"</c>. Faithful port of Rust's <c>ok_confirmation</c> (<c>src/core/utils.rs</c>:181-187).</summary>
    private static string OkConfirmation(string action, string detail) =>
        detail.Length == 0 ? $"ok {action}" : $"ok {action} {detail}";

    /// <summary>
    /// Detects whether a <c>gt log</c> line is a graph-node entry (as opposed to a message/continuation
    /// line). Faithful port of Rust's <c>is_graph_node</c> (<c>gt_cmd.rs</c>:367-377). <c>internal</c>
    /// (not <c>public</c>, matching pre-move visibility) because it is directly unit-tested in
    /// <c>RtkSharp.Filters.Tests</c> (granted access via <c>InternalsVisibleTo</c>) even though it isn't
    /// part of this extraction's public interface list.
    /// </summary>
    internal static bool IsGraphNode(string line)
    {
        var stripped = line.TrimStart('│').TrimStart('|').TrimStart();
        return stripped.StartsWith('◉')
            || stripped.StartsWith('○')
            || stripped.StartsWith('◯')
            || stripped.StartsWith('◆')
            || stripped.StartsWith('●')
            || stripped.StartsWith('@')
            || stripped.StartsWith('*');
    }

    /// <summary>
    /// Extracts the branch name from a <c>Created branch ...</c>/<c>Pushed branch ...</c>/
    /// <c>Deleted branch ...</c> line. Faithful port of Rust's <c>extract_branch_name</c>
    /// (<c>gt_cmd.rs</c>:379-386). <c>internal</c> for the same reason as <see cref="IsGraphNode"/>.
    /// </summary>
    internal static string ExtractBranchName(string line)
    {
        var match = BranchNameRegex.Match(line);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
