using System.Linq;
using System.Text.RegularExpressions;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Git;

/// <summary>
/// Implements the <c>rtk gt</c> CLI verb: Graphite (gt) stacked-PR commands with compact output.
/// <c>log</c>/<c>submit</c>/<c>sync</c>/<c>restack</c>/<c>create</c>/<c>branch</c> each capture the
/// real <c>gt</c> invocation and run it through a dedicated compact filter; any other subcommand is
/// either routed to an existing <see cref="GitCommand"/> filter (since <c>gt</c> itself falls back to
/// <c>git</c> for unrecognized subcommands) or passed straight through. Faithful port of
/// <c>src/cmds/git/gt_cmd.rs</c> plus its dispatch arm in <c>src/main.rs</c> (<c>GtCommands</c> enum,
/// <c>Commands::Gt</c> match arm).
/// </summary>
/// <remarks>
/// <para>
/// <b>New sibling file, not a <see cref="GitCommand"/> modification.</b> <c>gt</c> is git-adjacent
/// (its Rust module lives under <c>cmds::git::gt_cmd</c>), so this port lives alongside
/// <see cref="GitCommand"/> in the same folder, but is a wholly separate static class — no shared
/// file was touched.
/// </para>
/// <para>
/// <b><c>gt status</c>/<c>diff</c>/<c>show</c>/<c>add</c>/<c>push</c>/<c>pull</c>/<c>fetch</c>/
/// <c>stash</c>/<c>worktree</c> route to <see cref="GitCommand"/>.</b> Rust's <c>run_other</c>
/// (<c>gt_cmd.rs</c>:127-164) reflects that <c>gt</c> itself passes any subcommand it doesn't
/// recognize straight through to <c>git</c> — so RTK routes those specific git-shaped subcommands to
/// its own git filters (for the token savings), rather than treating them as opaque passthrough.
/// <see cref="GitCommand.RunAsync(string[])"/>'s public overload already dispatches on
/// <c>args[0]</c> as the git subcommand exactly like <c>crate::git::run(GitCommand::Status, ...)</c>
/// does in Rust, so prepending the subcommand token and delegating to it (rather than re-implementing
/// per-subcommand plumbing here) is a faithful, minimal-surface port of that arm — including the
/// <c>stash</c> case, where Rust separately extracts <c>rest.first()</c> as the stash subcommand:
/// <see cref="GitCommand"/>'s own <c>stash</c> arm already does that same extraction internally from
/// the args it's given, so no special-casing is needed here.
/// </para>
/// <para>
/// <b>Any other subcommand is a genuine raw passthrough</b> (<see cref="PassthroughGtAsync"/>) —
/// inherited stdio, exit code propagated, tracked as passthrough (0 tokens). Faithful port of Rust's
/// <c>passthrough_gt</c> (<c>gt_cmd.rs</c>:166-170), which itself calls
/// <c>core::runner::run_passthrough</c>.
/// </para>
/// </remarks>
public static class GtCommand
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

    /// <summary>
    /// Registry entry point. Dispatches on the first argument (the gt subcommand) exactly as Rust's
    /// <c>GtCommands</c> routing does.
    /// </summary>
    /// <param name="args">The arguments following the <c>gt</c> verb (subcommand first).</param>
    /// <returns>The wrapped <c>gt</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity, null);

    /// <summary>Test-friendly overload accepting an explicit executor.</summary>
    /// <param name="args">The arguments following the <c>gt</c> verb (subcommand first).</param>
    /// <param name="verbose">The rtk-level verbosity count (<c>-v</c>/<c>-vv</c>/...).</param>
    /// <param name="executor">The process executor to use, or null for the default.</param>
    /// <returns>The wrapped <c>gt</c> process's exit code.</returns>
    internal static Task<int> RunAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            Console.Error.Write("gt: no subcommand specified\n");
            return Task.FromResult(1);
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "log" => RunLogAsync(rest, verbose, executor),
            "submit" => RunGtFilteredAsync(["submit"], rest, verbose, "gt_submit", FilterGtSubmit, executor),
            "sync" => RunGtFilteredAsync(["sync"], rest, verbose, "gt_sync", FilterGtSync, executor),
            "restack" => RunGtFilteredAsync(["restack"], rest, verbose, "gt_restack", FilterGtRestack, executor),
            "create" => RunGtFilteredAsync(["create"], rest, verbose, "gt_create", FilterGtCreate, executor),
            "branch" => RunGtFilteredAsync(["branch"], rest, verbose, "gt_branch", FilterIdentity, executor),
            _ => RunOtherAsync(args, verbose, executor),
        };
    }

    /// <summary>
    /// Runs <c>gt log</c>/<c>gt log short</c>/<c>gt log long</c>, choosing the filter per subcommand.
    /// Faithful port of Rust's <c>run_log</c> (<c>gt_cmd.rs</c>:87-105).
    /// </summary>
    private static Task<int> RunLogAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        if (args.Length > 0 && args[0] == "short")
        {
            return RunGtFilteredAsync(["log", "short"], args[1..], verbose, "gt_log_short", FilterIdentity, executor);
        }

        if (args.Length > 0 && args[0] == "long")
        {
            return RunGtFilteredAsync(["log", "long"], args[1..], verbose, "gt_log_long", FilterGtLogEntries, executor);
        }

        return RunGtFilteredAsync(["log"], args, verbose, "gt_log", FilterGtLogEntries, executor);
    }

    /// <summary>
    /// Shared capture/filter/tee/track skeleton for every <c>gt</c> subcommand that gets a compact
    /// filter. Faithful port of Rust's <c>run_gt_filtered</c> (<c>gt_cmd.rs</c>:24-81).
    /// </summary>
    private static async Task<int> RunGtFilteredAsync(
        string[] subcmd, string[] args, int verbose, string teeLabel, Func<string, string> filter,
        IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();

        var invocation = new List<string>(subcmd);
        invocation.AddRange(args);

        var subcmdStr = string.Join(' ', subcmd);
        if (RuntimeOptions.Verbosity > 0 || verbose > 0)
        {
            Console.Error.Write($"Running: gt {subcmdStr} {string.Join(' ', args)}\n");
        }

        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest("gt", invocation, CaptureMode: ExecutionCaptureMode.Separate);
        ExecutionResult cmdOutput;
        try
        {
            cmdOutput = await exec.ExecuteAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to run gt {subcmdStr}. Is gt (Graphite) installed?", ex);
        }

        var raw = $"{cmdOutput.Stdout}\n{cmdOutput.Stderr}";

        var clean = Utils.StripAnsi(cmdOutput.Stdout.Trim());
        var output = (RuntimeOptions.Verbosity > 0 || verbose > 0) ? clean : filter(clean);

        var hint = Tee.TeeAndHint(raw, teeLabel, cmdOutput.ExitCode);
        Console.Out.Write(hint is not null ? $"{output}\n{hint}\n" : $"{output}\n");

        if (!string.IsNullOrWhiteSpace(cmdOutput.Stderr))
        {
            Console.Error.Write(cmdOutput.Stderr.Trim() + "\n");
        }

        var label = args.Length == 0 ? $"gt {subcmdStr}" : $"gt {subcmdStr} {string.Join(' ', args)}";
        timer.Track(label, $"rtk {label}", raw, output);

        return cmdOutput.ExitCode;
    }

    private static string FilterIdentity(string input) => input;

    /// <summary>
    /// Routes <c>gt</c> subcommands that <c>gt</c> itself falls back to <c>git</c> for, to RTK's own
    /// git filters, and passes anything else straight through. Faithful port of Rust's <c>run_other</c>
    /// (<c>gt_cmd.rs</c>:127-164).
    /// </summary>
    private static Task<int> RunOtherAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        if (args.Length == 0)
        {
            throw new InvalidOperationException("gt: no subcommand specified");
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "status" or "diff" or "show" or "add" or "push" or "pull" or "fetch" or "stash" or "worktree" =>
                GitCommand.RunAsync(args),
            _ => PassthroughGtAsync(subcommand, rest, verbose, executor),
        };
    }

    /// <summary>
    /// Runs an unrecognized <c>gt</c> subcommand unfiltered, propagating both streams and the exit
    /// code. Faithful port of Rust's <c>passthrough_gt</c> (<c>gt_cmd.rs</c>:166-170), which delegates
    /// to <c>core::runner::run_passthrough</c> (inherited stdio, tracked with 0 tokens).
    /// </summary>
    private static async Task<int> PassthroughGtAsync(
        string subcommand, string[] args, int verbose, IProcessExecutor? executor)
    {
        var osArgs = new List<string> { subcommand };
        osArgs.AddRange(args);

        if (RuntimeOptions.Verbosity > 0 || verbose > 0)
        {
            Console.Error.Write($"gt passthrough: {string.Join(", ", osArgs.Select(a => $"\"{a}\""))}\n");
        }

        var timer = TimedExecution.Start();
        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest("gt", osArgs, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        var cmdLabel = $"gt {string.Join(' ', osArgs)}".Trim();
        timer.TrackPassthrough(cmdLabel, $"rtk {cmdLabel} (passthrough)");
        return result.ExitCode;
    }

    /// <summary>Formats a write-operation confirmation: <c>"ok {action}"</c> or <c>"ok {action} {detail}"</c>. Faithful port of Rust's <c>ok_confirmation</c> (<c>src/core/utils.rs</c>:181-187).</summary>
    private static string OkConfirmation(string action, string detail) =>
        detail.Length == 0 ? $"ok {action}" : $"ok {action} {detail}";

    /// <summary>
    /// Compacts <c>gt log</c>'s ASCII-graph output: strips author emails, truncates long lines, and
    /// caps the number of graph-node entries shown. Faithful port of Rust's
    /// <c>filter_gt_log_entries</c> (<c>gt_cmd.rs</c>:175-204).
    /// </summary>
    /// <param name="input">The ANSI-stripped, trimmed <c>gt log</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterGtLogEntries(string input)
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
    internal static string FilterGtSubmit(string input)
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
    internal static string FilterGtSync(string input)
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
    internal static string FilterGtRestack(string input)
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
    internal static string FilterGtCreate(string input)
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

    private static bool IsGraphNode(string line)
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

    private static string ExtractBranchName(string line)
    {
        var match = BranchNameRegex.Match(line);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
