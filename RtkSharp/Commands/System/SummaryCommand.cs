using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk summary</c> CLI verb: runs an arbitrary shell command and prints a
/// heuristic summary of its output (auto-detecting test results, build output, logs, JSON, plain
/// lists, or falling back to a generic head/tail view). Faithful port of Rust
/// <c>src/cmds/system/summary.rs</c> (including its inline test module).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a meta-command (deliberate Rust-source asymmetry, same as <c>err</c>/<c>test</c>/<c>diff</c>).</b>
/// Rust's <c>RTK_META_COMMANDS</c> list (<c>main.rs</c>:1170-1191) does not include <c>"summary"</c> —
/// it is classified <c>PASSTHROUGH</c> instead (<c>main.rs</c>:2945). A clap-layer parse failure would
/// fall back to a raw PATH-exec of the original argv rather than a clean clap usage error. In
/// practice, Rust's <c>Summary { command: Vec&lt;String&gt; }</c> clap variant (<c>main.rs</c>:303-307)
/// uses <c>trailing_var_arg = true, allow_hyphen_values = true</c>, so essentially any argument
/// vector (including zero args, or args starting with <c>-</c>) parses successfully — there is no
/// realistic parse-failure path for this command, so <see cref="ParseArgs"/> never throws.
/// </para>
/// <para>
/// <b>Shell invocation mirrors <c>err</c>/<c>test</c>.</b> Rust's <c>run</c> (<c>summary.rs</c>:15-39)
/// spawns <c>cmd /C &lt;command&gt;</c> on Windows or <c>sh -c &lt;command&gt;</c> elsewhere via
/// <c>exec_capture</c> (captured, not streamed — unlike <c>err</c>/<c>test</c>'s streaming filters).
/// This port uses <see cref="ProcessExecutor"/> with <see cref="ExecutionCaptureMode.Separate"/> to
/// match, then combines stdout/stderr with a single <c>"\n"</c> join exactly as Rust's
/// <c>format!("{}\n{}", result.stdout, result.stderr)</c> does.
/// </para>
/// </remarks>
public static class SummaryCommand
{
    // src/core/truncate.rs: CAP_WARNINGS = 10, reused for both the list-item cap and the JSON-object
    // key cap (summary.rs:11-12).
    private const int MaxSummaryList = 10;
    private const int MaxSummaryKeys = 10;

    /// <summary>
    /// Registry entry point. Runs <c>rtk summary</c> with the given arguments (the remainder after
    /// the <c>summary</c> verb) — the raw command to run and summarize.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>summary</c>.</param>
    /// <returns>The wrapped command's exit code (or 1 on a top-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, executor: null);

    /// <summary>
    /// Runs <c>rtk summary</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>summary</c>.</param>
    /// <param name="executor">The process executor to use, or null for the default.</param>
    /// <returns>The wrapped command's exit code (or 1 on a top-level failure).</returns>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        var command = ParseArgs(args);

        try
        {
            return await RunCoreAsync(command, RuntimeOptions.Verbosity, executor, Console.Out, Console.Error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Parses <c>rtk summary</c>'s arguments: <c>trailing_var_arg</c> collects everything following
    /// the verb, joined with spaces into a single shell command string. Faithful port of the clap
    /// <c>Commands::Summary</c> variant (<c>main.rs</c>:303-307) and its dispatch
    /// (<c>let cmd = command.join(" ");</c>, <c>main.rs</c>:1857).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>summary</c>.</param>
    /// <returns>The joined shell command string (empty when no arguments were given).</returns>
    internal static string ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return string.Join(' ', args);
    }

    /// <summary>
    /// The testable core: runs <paramref name="command"/> through a shell, captures its combined
    /// output, and prints the heuristic summary. Faithful port of <c>summary::run</c>
    /// (<c>summary.rs</c>:15-39).
    /// </summary>
    /// <param name="command">The shell command to run.</param>
    /// <param name="verbose">The global verbosity level (mirrors Rust's <c>cli.verbose: u8</c>).</param>
    /// <param name="executor">The process executor to use, or null for the default.</param>
    /// <param name="stdout">The destination for the summary.</param>
    /// <param name="stderr">The destination for the verbose diagnostic line.</param>
    /// <returns>The wrapped command's exit code.</returns>
    internal static async Task<int> RunCoreAsync(
        string command, int verbose, IProcessExecutor? executor, TextWriter stdout, TextWriter stderr)
    {
        if (verbose > 0)
        {
            stderr.Write($"Running and summarizing: {command}\n");
        }

        var timer = TimedExecution.Start();

        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var flag = OperatingSystem.IsWindows() ? "/C" : "-c";
        var request = new ExecutionRequest(shell, new[] { flag, command });

        var exec = executor ?? new ProcessExecutor();
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to execute command{detail}");
        }

        var raw = $"{result.Stdout}\n{result.Stderr}";
        var summary = SummarizeOutput(raw, command, result.ExitCode == 0);

        stdout.Write(summary + "\n");
        timer.Track(command, "rtk summary", raw, summary);

        return result.ExitCode;
    }

    /// <summary>
    /// Builds the heuristic summary: a status header, line count, then a type-specific body chosen
    /// by <see cref="DetectOutputType"/>. Faithful port of <c>summarize_output</c> (<c>summary.rs</c>:41-68).
    /// </summary>
    /// <param name="output">The combined stdout+stderr text.</param>
    /// <param name="command">The command that was run (shown truncated in the header).</param>
    /// <param name="success">Whether the command exited successfully.</param>
    /// <returns>The rendered summary.</returns>
    internal static string SummarizeOutput(string output, string command, bool success)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(command);

        var lines = ReadCommand.SplitLines(output);
        var result = new List<string>();

        var statusIcon = success ? "[ok]" : "[FAIL]";
        result.Add($"{statusIcon} Command: {Utils.Truncate(command, 60)}");
        result.Add($"   {lines.Count} lines of output");
        result.Add(string.Empty);

        var outputType = DetectOutputType(output, command);

        switch (outputType)
        {
            case OutputType.TestResults:
                SummarizeTests(output, result);
                break;
            case OutputType.BuildOutput:
                SummarizeBuild(output, result);
                break;
            case OutputType.LogOutput:
                SummarizeLogsQuick(output, result);
                break;
            case OutputType.ListOutput:
                SummarizeList(output, result);
                break;
            case OutputType.JsonOutput:
                SummarizeJson(output, result);
                break;
            default:
                SummarizeGeneric(output, result);
                break;
        }

        return string.Join("\n", result);
    }

    internal enum OutputType
    {
        TestResults,
        BuildOutput,
        LogOutput,
        ListOutput,
        JsonOutput,
        Generic,
    }

    /// <summary>
    /// Classifies <paramref name="output"/>/<paramref name="command"/> into an <see cref="OutputType"/>
    /// by cheap keyword heuristics, checked in a fixed priority order. Faithful port of
    /// <c>detect_output_type</c> (<c>summary.rs</c>:80-110).
    /// </summary>
    /// <param name="output">The combined stdout+stderr text.</param>
    /// <param name="command">The command that was run.</param>
    /// <returns>The detected output type.</returns>
    internal static OutputType DetectOutputType(string output, string command)
    {
        var cmdLower = command.ToLowerInvariant();
        var outLower = output.ToLowerInvariant();

        if (cmdLower.Contains("test", StringComparison.Ordinal)
            || (outLower.Contains("passed", StringComparison.Ordinal) && outLower.Contains("failed", StringComparison.Ordinal)))
        {
            return OutputType.TestResults;
        }

        if (cmdLower.Contains("build", StringComparison.Ordinal)
            || cmdLower.Contains("compile", StringComparison.Ordinal)
            || outLower.Contains("compiling", StringComparison.Ordinal))
        {
            return OutputType.BuildOutput;
        }

        if (outLower.Contains("error:", StringComparison.Ordinal)
            || outLower.Contains("warn:", StringComparison.Ordinal)
            || outLower.Contains("[info]", StringComparison.Ordinal))
        {
            return OutputType.LogOutput;
        }

        var trimmedStart = output.TrimStart();
        if (trimmedStart.StartsWith('{') || trimmedStart.StartsWith('['))
        {
            return OutputType.JsonOutput;
        }

        var looksLikeList = output.Split('\n').All(l =>
            l.Length < 200 && (l.Contains('\t', StringComparison.Ordinal)
                ? false
                : l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 10));

        return looksLikeList ? OutputType.ListOutput : OutputType.Generic;
    }

    /// <summary>Summarizes test-runner-shaped output: pass/fail/skip counts and up to 5 failure lines. Faithful port of <c>summarize_tests</c> (<c>summary.rs</c>:112-161).</summary>
    private static void SummarizeTests(string output, List<string> result)
    {
        result.Add("Test Results:");

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<string>();

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var lower = line.ToLowerInvariant();

            if (lower.Contains("passed", StringComparison.Ordinal)
                || lower.Contains('✓', StringComparison.Ordinal)
                || lower.Contains("ok", StringComparison.Ordinal))
            {
                var n = ExtractNumber(lower, "passed");
                passed = n ?? passed + 1;
            }

            if (lower.Contains("failed", StringComparison.Ordinal)
                || lower.Contains("[x]", StringComparison.Ordinal)
                || lower.Contains("fail", StringComparison.Ordinal))
            {
                var n = ExtractNumber(lower, "failed");
                if (n is not null)
                {
                    failed = n.Value;
                }

                if (!line.Contains("0 failed", StringComparison.Ordinal))
                {
                    failures.Add(line);
                }
            }

            if (lower.Contains("skipped", StringComparison.Ordinal) || lower.Contains("ignored", StringComparison.Ordinal))
            {
                var n = ExtractNumber(lower, "skipped") ?? ExtractNumber(lower, "ignored");
                if (n is not null)
                {
                    skipped = n.Value;
                }
            }
        }

        result.Add($"   [ok] {passed} passed");
        if (failed > 0)
        {
            result.Add($"   [FAIL] {failed} failed");
        }

        if (skipped > 0)
        {
            result.Add($"   skip {skipped} skipped");
        }

        if (failures.Count > 0)
        {
            result.Add(string.Empty);
            result.Add("   Failures:");
            foreach (var f in failures.Take(5))
            {
                result.Add($"   • {Utils.Truncate(f, 70)}");
            }
        }
    }

    /// <summary>Summarizes compiler-shaped output: compiled/error/warning counts and up to 5 error lines. Faithful port of <c>summarize_build</c> (<c>summary.rs</c>:163-207).</summary>
    private static void SummarizeBuild(string output, List<string> result)
    {
        result.Add("Build Summary:");

        var errors = 0;
        var warnings = 0;
        var compiled = 0;
        var errorMsgs = new List<string>();

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var lower = line.ToLowerInvariant();

            if (lower.Contains("error", StringComparison.Ordinal) && !lower.Contains("0 error", StringComparison.Ordinal))
            {
                errors++;
                if (errorMsgs.Count < 5)
                {
                    errorMsgs.Add(line);
                }
            }

            if (lower.Contains("warning", StringComparison.Ordinal) && !lower.Contains("0 warning", StringComparison.Ordinal))
            {
                warnings++;
            }

            if (lower.Contains("compiling", StringComparison.Ordinal) || lower.Contains("compiled", StringComparison.Ordinal))
            {
                compiled++;
            }
        }

        if (compiled > 0)
        {
            result.Add($"   {compiled} crates/files compiled");
        }

        if (errors > 0)
        {
            result.Add($"   [error] {errors} errors");
        }

        if (warnings > 0)
        {
            result.Add($"   [warn] {warnings} warnings");
        }

        if (errors == 0 && warnings == 0)
        {
            result.Add("   [ok] Build successful");
        }

        if (errorMsgs.Count > 0)
        {
            result.Add(string.Empty);
            result.Add("   Errors:");
            foreach (var e in errorMsgs)
            {
                result.Add($"   • {Utils.Truncate(e, 70)}");
            }
        }
    }

    /// <summary>Summarizes log-shaped output: error/warning/info counts. Faithful port of <c>summarize_logs_quick</c> (<c>summary.rs</c>:209-230).</summary>
    private static void SummarizeLogsQuick(string output, List<string> result)
    {
        result.Add("Log Summary:");

        var errors = 0;
        var warnings = 0;
        var info = 0;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var lower = line.ToLowerInvariant();
            if (lower.Contains("error", StringComparison.Ordinal) || lower.Contains("fatal", StringComparison.Ordinal))
            {
                errors++;
            }
            else if (lower.Contains("warn", StringComparison.Ordinal))
            {
                warnings++;
            }
            else if (lower.Contains("info", StringComparison.Ordinal))
            {
                info++;
            }
        }

        result.Add($"   [error] {errors} errors");
        result.Add($"   [warn] {warnings} warnings");
        result.Add($"   [info] {info} info");
    }

    /// <summary>Summarizes plain-list-shaped output: item count, up to <see cref="MaxSummaryList"/> items. Faithful port of <c>summarize_list</c> (<c>summary.rs</c>:232-242).</summary>
    private static void SummarizeList(string output, List<string> result)
    {
        var lines = ReadCommand.SplitLines(output).Where(l => l.Trim().Length != 0).ToList();
        result.Add($"List ({lines.Count} items):");

        foreach (var line in lines.Take(MaxSummaryList))
        {
            result.Add($"   • {Utils.Truncate(line, 70)}");
        }

        if (lines.Count > MaxSummaryList)
        {
            result.Add($"   ... +{lines.Count - MaxSummaryList} more");
        }
    }

    /// <summary>Summarizes JSON-shaped output: array length, or object key list (up to <see cref="MaxSummaryKeys"/>). Faithful port of <c>summarize_json</c> (<c>summary.rs</c>:244-269).</summary>
    private static void SummarizeJson(string output, List<string> result)
    {
        result.Add("JSON Output:");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            result.Add("   (Invalid JSON)");
            return;
        }

        using (doc)
        {
            var value = doc.RootElement;
            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                    result.Add($"   Array with {value.GetArrayLength()} items");
                    break;

                case JsonValueKind.Object:
                    {
                        var properties = value.EnumerateObject().ToList();
                        result.Add($"   Object with {properties.Count} keys:");
                        foreach (var prop in properties.Take(MaxSummaryKeys))
                        {
                            result.Add($"   • {prop.Name}");
                        }

                        if (properties.Count > MaxSummaryKeys)
                        {
                            result.Add($"   ... +{properties.Count - MaxSummaryKeys} more keys");
                        }

                        break;
                    }

                default:
                    result.Add($"   {Utils.Truncate(RawValueText(value), 100)}");
                    break;
            }
        }
    }

    /// <summary>Renders a scalar <see cref="JsonElement"/> the way Rust's <c>Value::to_string()</c> would (compact JSON text).</summary>
    private static string RawValueText(JsonElement value) => value.GetRawText();

    /// <summary>Summarizes unrecognized output: first 5 non-blank lines, and (when more than 10 lines total) the last 3. Faithful port of <c>summarize_generic</c> (<c>summary.rs</c>:271-292).</summary>
    private static void SummarizeGeneric(string output, List<string> result)
    {
        var lines = ReadCommand.SplitLines(output);

        result.Add("Output:");

        foreach (var line in lines.Take(5))
        {
            if (line.Trim().Length != 0)
            {
                result.Add($"   {Utils.Truncate(line, 75)}");
            }
        }

        if (lines.Count > 10)
        {
            result.Add("   ...");
            foreach (var line in lines.Skip(lines.Count - 3))
            {
                if (line.Trim().Length != 0)
                {
                    result.Add($"   {Utils.Truncate(line, 75)}");
                }
            }
        }
    }

    /// <summary>
    /// Extracts the first integer immediately followed by (whitespace then) <paramref name="after"/>
    /// in <paramref name="text"/> — e.g. <c>ExtractNumber("12 passed", "passed")</c> returns 12.
    /// Faithful port of <c>extract_number</c> (<c>summary.rs</c>:294-299).
    /// </summary>
    /// <param name="text">The (already-lowercased) text to search.</param>
    /// <param name="after">The literal word the number must precede.</param>
    /// <returns>The parsed number, or null if no match.</returns>
    internal static int? ExtractNumber(string text, string after)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(after);

        var match = Regex.Match(text, $@"(\d+)\s*{Regex.Escape(after)}", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }
}
