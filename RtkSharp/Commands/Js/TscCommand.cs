using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Implements the <c>rtk tsc</c> CLI verb: groups TypeScript compiler diagnostics by file (most
/// errors first, no per-file cap) and prints a one-line summary plus a top-error-codes breakdown.
/// Faithful port of <c>src/cmds/js/tsc_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffered capture, not streaming — an explicit architecture decision.</b> Rust ships two
/// parallel, behaviorally-equivalent implementations: a real streaming <c>TscHandler</c>
/// (<c>tsc_cmd.rs</c>:45-105, a <c>BlockHandler</c> driven by <c>core::stream::BlockStreamFilter</c>
/// over <c>runner::run_streamed</c> — this is what actually executes when a user runs <c>rtk tsc</c>),
/// and a buffered <c>filter_tsc_output</c> (<c>tsc_cmd.rs</c>:107-214) that Rust's own <c>run()</c>
/// never calls — it exists solely for <c>#[cfg(test)]</c> to exercise directly, and Rust's own test
/// suite exercises this buffered path exclusively (all six <c>#[cfg(test)]</c> functions call
/// <c>filter_tsc_output</c> or run the streaming handler through a synthetic single-shot harness, never
/// against a live streaming child process). This port implements <see cref="FilterTscOutput"/> as the
/// buffered equivalent of <c>filter_tsc_output</c> and drives it through <see cref="CommandRunner"/>'s
/// existing captured-filter pipeline (the same one <c>npm</c>/<c>npx</c> use) rather than introducing a
/// new tsc-specific streaming primitive into RtkSharp's execution layer. This is judged behaviorally
/// sufficient: Rust's own tests never observe a difference between the two paths (that is the whole
/// point of shipping both), and Phase 6's buffered-capture infrastructure already handles this shape of
/// command well. Flagged explicitly here for Phase 8's compatibility ledger (Task 8): <b>tsc runs via
/// buffered capture, not true line-by-line streaming</b> — a user piping enormous compiler output
/// (many thousands of diagnostic lines) will see output only after the process exits, rather than
/// incrementally. No RTK behavior a user can observe from the printed result differs; only the timing
/// of when output appears could, for an extreme input size.
/// </para>
/// <para>
/// <b>Tool resolution: <c>tool_exists("tsc")</c>, not <see cref="PackageManagerDetection"/>.</b> Rust's
/// <c>run()</c> (<c>tsc_cmd.rs</c>:17-25) calls the plain <c>tool_exists("tsc")</c> helper and, on
/// failure, hardcodes a fallback to <c>npx tsc</c> — it does NOT call
/// <c>detect_package_manager()</c>/<c>package_manager_exec()</c> (the pnpm/yarn/npx-exec ladder Task 1
/// ported as <see cref="PackageManagerDetection"/>, used by other future filters like playwright). This
/// port matches that exactly: <see cref="ToolExists"/> mirrors <c>tool_exists</c> via
/// <see cref="PathResolver.Resolve(string)"/> (the same convention <c>TreeCommand</c> established for
/// its own <c>tool_exists("tree")</c> port), and the fallback is unconditionally <c>npx tsc</c> — never
/// pnpm/yarn.
/// </para>
/// <para>
/// <b>Implicit tracking via <see cref="CommandRunner"/>.</b> Rust's <c>run()</c> delegates straight to
/// <c>runner::run_streamed(...)</c> (<c>tsc_cmd.rs</c>:36-42) — tracking happens implicitly inside that
/// shared runner skeleton, with no manual <c>TimedExecution</c> call in <c>tsc_cmd.rs</c> itself. This
/// matches <c>npm</c>/<c>npx</c>'s wiring (Phase 8 Task 2), not <c>pnpm</c>'s manual-<c>TimedExecution</c>
/// pattern (Task 3): <see cref="ExecuteAsync"/> routes through <see cref="CommandRunner.RunFilteredAsync"/>
/// with no separate tracking call.
/// </para>
/// <para>
/// <b>No cap on errors shown per file — deliberate divergence from most RTK truncation conventions.</b>
/// <c>filter_tsc_output</c> (<c>tsc_cmd.rs</c>:196-211) shows every single error for every file with no
/// <c>CAP_LIST</c>/<c>MAX_LISTING</c>-style limit anywhere in the loop; Rust's own
/// <c>test_no_file_limit</c> proves this with 15 files, asserting every one appears. Only individual
/// error messages and continuation/context lines are truncated, to 120 Unicode scalar values via
/// <c>utils::truncate</c> (ported here as <see cref="Utils.Truncate"/>).
/// </para>
/// </remarks>
public static partial class TscCommand
{
    /// <summary>
    /// Matches a single tsc diagnostic line: <c>file(line,col): error|warning TSxxxx: message</c>.
    /// Faithful port of <c>TSC_ERROR</c> (<c>tsc_cmd.rs</c>:11-14).
    /// </summary>
    [GeneratedRegex(@"^(.+?)\((\d+),(\d+)\):\s+(error|warning)\s+(TS\d+):\s+(.+)$")]
    internal static partial Regex TscErrorRegex();

    /// <summary>
    /// Registry entry point for the <c>tsc</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/> (the
    /// registry delegate cannot receive it as an argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>tsc</c> verb.</param>
    /// <returns><c>tsc</c>'s exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunTscSafeAsync(args, RuntimeOptions.Verbosity);

    /// <summary>
    /// Test-friendly overload of the <c>tsc</c> entry point taking an explicit verbosity value instead
    /// of reading <see cref="RuntimeOptions"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>tsc</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <returns><c>tsc</c>'s exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunTscSafeAsync(string[] args, int verbose)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await ExecuteAsync(args, verbose).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail-loud, same convention as NpmCommand/PnpmCommand: an rtk-level failure surfaces as
            // `rtk: {message}`.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Resolves <c>tsc</c> (directly, or via <c>npx tsc</c>), runs it through
    /// <see cref="CommandRunner.RunFilteredAsync"/> with <see cref="FilterTscOutput"/> and tee-to-disk
    /// enabled, and returns the child's exit code. Ports the body of Rust's <c>run()</c>
    /// (<c>tsc_cmd.rs</c>:16-43).
    /// </summary>
    /// <param name="args">The arguments following the <c>tsc</c> verb.</param>
    /// <param name="verbose">The verbosity level; a nonzero value logs the resolved command being run.</param>
    /// <returns><c>tsc</c>'s exit code.</returns>
    internal static Task<int> ExecuteAsync(string[] args, int verbose)
    {
        var tscExists = ToolExists("tsc");

        var cmdArgs = new List<string>();
        string fileName;
        string toolLabel;

        if (tscExists)
        {
            fileName = "tsc";
            toolLabel = "tsc";
        }
        else
        {
            fileName = "npx";
            cmdArgs.Add("tsc");
            toolLabel = "npx tsc";
        }

        cmdArgs.AddRange(args);

        var argsDisplay = string.Join(' ', args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {toolLabel} {argsDisplay}\n");
        }

        return CommandRunner.RunFilteredAsync(
            fileName,
            cmdArgs,
            "tsc",
            argsDisplay,
            FilterTscOutput,
            new RunOptions(TeeLabel: "tsc")
        );
    }

    /// <summary>
    /// Reports whether <paramref name="name"/> is directly resolvable on <c>PATH</c>. Mirrors Rust's
    /// <c>tool_exists</c> (<c>which::which(name).is_ok()</c>, <c>src/core/utils.rs</c>:361-363) via
    /// <see cref="PathResolver.Resolve(string)"/>, the same convention <c>TreeCommand</c> uses for its
    /// own <c>tool_exists("tree")</c> port.
    /// </summary>
    /// <param name="name">The tool binary name to check.</param>
    /// <returns>True if <paramref name="name"/> resolves to a real path on <c>PATH</c>.</returns>
    internal static bool ToolExists(string name) => PathResolver.Resolve(name) != name;

    /// <summary>
    /// A single parsed tsc diagnostic: the matched file/line/code/message plus any indented
    /// continuation lines tsc printed immediately after it. Faithful port of the private <c>TsError</c>
    /// struct nested inside <c>filter_tsc_output</c> (<c>tsc_cmd.rs</c>:108-114).
    /// </summary>
    private sealed class TsError
    {
        public required string File { get; init; }

        public required int Line { get; init; }

        public required string Code { get; init; }

        public required string Message { get; init; }

        public List<string> ContextLines { get; } = [];
    }

    /// <summary>
    /// Buffered filter for tsc's raw diagnostic output: parses every <see cref="TscErrorRegex"/> match
    /// (plus its indented continuation lines) into a <see cref="TsError"/>, groups by file (sorted by
    /// per-file error count descending, every error shown, no cap), and prepends a summary line plus
    /// (when more than one distinct error code appears) a top-5-by-frequency codes line. Faithful port
    /// of <c>filter_tsc_output</c> (<c>tsc_cmd.rs</c>:107-214).
    /// </summary>
    /// <param name="output">The raw <c>tsc</c> output (stdout+stderr) to filter.</param>
    /// <returns>The grouped, summarized output.</returns>
    internal static string FilterTscOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var errors = new List<TsError>();
        var lines = ReadCommand.SplitLines(output);
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];
            var match = TscErrorRegex().Match(line);

            if (match.Success)
            {
                var groups = match.Groups;
                var lineNumber = int.TryParse(groups[2].Value, out var parsed) ? parsed : 0;

                var err = new TsError
                {
                    File = groups[1].Value,
                    Line = lineNumber,
                    Code = groups[5].Value,
                    Message = groups[6].Value,
                };

                // Capture continuation lines (indented context from tsc).
                i++;
                while (i < lines.Count)
                {
                    var next = lines[i];
                    if (next.Length != 0
                        && (next.StartsWith("  ", StringComparison.Ordinal) || next.StartsWith('\t'))
                        && !TscErrorRegex().IsMatch(next))
                    {
                        err.ContextLines.Add(next.Trim());
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                errors.Add(err);
            }
            else
            {
                i++;
            }
        }

        if (errors.Count == 0)
        {
            return output.Contains("Found 0 errors", StringComparison.Ordinal)
                ? "TypeScript: No errors found"
                : "TypeScript compilation completed";
        }

        // Group by file.
        var byFile = new Dictionary<string, List<TsError>>(StringComparer.Ordinal);
        foreach (var err in errors)
        {
            if (!byFile.TryGetValue(err.File, out var list))
            {
                list = [];
                byFile[err.File] = list;
            }

            list.Add(err);
        }

        // Count by error code for the summary.
        var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var err in errors)
        {
            byCode[err.Code] = byCode.GetValueOrDefault(err.Code) + 1;
        }

        var result = new StringBuilder();
        result.Append($"TypeScript: {errors.Count} errors in {byFile.Count} files\n");

        // Top error codes summary (compact, one line) - only when more than one distinct code appears.
        if (byCode.Count > 1)
        {
            var codesStr = byCode
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key} ({kv.Value}x)");
            result.Append($"Top codes: {string.Join(", ", codesStr)}\n\n");
        }

        // Files sorted by error count (most errors first). Show every error per file - no limits.
        var filesSorted = byFile.OrderByDescending(kv => kv.Value.Count);

        foreach (var (file, fileErrors) in filesSorted)
        {
            result.Append($"{file} ({fileErrors.Count} errors)\n");

            foreach (var err in fileErrors)
            {
                result.Append($"  L{err.Line}: {err.Code} {Utils.Truncate(err.Message, 120)}\n");
                foreach (var ctx in err.ContextLines)
                {
                    result.Append($"    {Utils.Truncate(ctx, 120)}\n");
                }
            }

            result.Append('\n');
        }

        return result.ToString().Trim();
    }
}
