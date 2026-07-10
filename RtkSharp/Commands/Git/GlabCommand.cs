using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Git;
using static RtkSharp.Filters.Commands.Git.GlabFilters;

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
    // glab flags that take a value — skipped along with their value when isolating the positional
    // identifier from extra flags (glab_cmd.rs:175-184).
    private static readonly string[] FlagsWithValue =
    {
        "-R", "--repo", "-g", "--group", "-F", "--output", "-m", "--message",
    };

    [GeneratedRegex(@"/-/merge_requests/(\d+)")]
    private static partial Regex MrUrlRegex();

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
        return RunGlabJsonAsync(executor, stdout, stderr, glabArgs, root => GlabFilters.FormatMrList(root, ultraCompact));
    }

    /// <summary>Extracts the MR number from a glab MR URL. Ports <c>extract_mr_number</c> (glab_cmd.rs:160).</summary>
    internal static string? ExtractMrNumber(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = MrUrlRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
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
            raw => raw.Trim().Length == 0 ? "No diff\n" : GitFilters.CompactDiff(raw, 500),
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

    /// <summary>Runs <c>glab ci trace</c> and prints the filtered job log. Ports <c>ci_trace</c> (glab_cmd.rs:771).</summary>
    private static Task<int> CiTraceAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "ci", "trace" };
        glabArgs.AddRange(args);
        return RunGlabFilteredAsync(executor, stdout, stderr, glabArgs, FilterCiTrace, noTrailingNewline: false);
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

    /// <summary>Runs <c>glab release view</c> and prints the filtered release notes. Ports <c>release_view</c> (glab_cmd.rs:920).</summary>
    private static Task<int> ReleaseViewAsync(
        string[] args, IProcessExecutor executor, TextWriter stdout, TextWriter stderr)
    {
        var glabArgs = new List<string> { "release", "view" };
        glabArgs.AddRange(args);
        return RunGlabFilteredAsync(executor, stdout, stderr, glabArgs, FilterReleaseView, noTrailingNewline: false);
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

    /// <summary>Ports <c>ok_confirmation</c> (utils.rs:181): <c>"ok {action}"</c>, or <c>"ok {action} {detail}"</c> when non-empty.</summary>
    internal static string OkConfirmation(string action, string detail) =>
        detail.Length == 0 ? $"ok {action}" : $"ok {action} {detail}";

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
