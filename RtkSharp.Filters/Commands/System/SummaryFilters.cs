using System.Text.Json;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure heuristic-summary logic for the <c>rtk summary</c> CLI verb: auto-detects test results,
/// build output, logs, JSON, plain lists, or falls back to a generic head/tail view. Ported from
/// Rust <c>src/cmds/system/summary.rs</c>. The shell invocation and tracking logic lives in
/// <see cref="RtkSharp.Commands.System.SummaryCommand"/> (<c>RtkSharp</c>).
/// </summary>
public static class SummaryFilters
{
    // src/core/truncate.rs: CAP_WARNINGS = 10, reused for both the list-item cap and the JSON-object
    // key cap (summary.rs:11-12).
    private const int MaxSummaryList = 10;
    private const int MaxSummaryKeys = 10;

    /// <summary>
    /// Builds the heuristic summary: a status header, line count, then a type-specific body chosen
    /// by <see cref="DetectOutputType"/>. Faithful port of <c>summarize_output</c> (<c>summary.rs</c>:41-68).
    /// </summary>
    /// <param name="output">The combined stdout+stderr text.</param>
    /// <param name="command">The command that was run (shown truncated in the header).</param>
    /// <param name="success">Whether the command exited successfully.</param>
    /// <returns>The rendered summary.</returns>
    public static string SummarizeOutput(string output, string command, bool success)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(command);

        var lines = ReadFilters.SplitLines(output);
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

    /// <summary>The heuristically-detected shape of a summarized command's output.</summary>
    public enum OutputType
    {
        /// <summary>Test-runner-shaped output (pass/fail/skip counts).</summary>
        TestResults,

        /// <summary>Compiler-shaped output (compiled/error/warning counts).</summary>
        BuildOutput,

        /// <summary>Log-shaped output (error/warning/info counts).</summary>
        LogOutput,

        /// <summary>Plain-list-shaped output (one short item per line).</summary>
        ListOutput,

        /// <summary>JSON-shaped output.</summary>
        JsonOutput,

        /// <summary>Unrecognized output, summarized generically.</summary>
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
    public static OutputType DetectOutputType(string output, string command)
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

        foreach (var line in ReadFilters.SplitLines(output))
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

        foreach (var line in ReadFilters.SplitLines(output))
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

        foreach (var line in ReadFilters.SplitLines(output))
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
        var lines = ReadFilters.SplitLines(output).Where(l => l.Trim().Length != 0).ToList();
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
        var lines = ReadFilters.SplitLines(output);

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
    public static int? ExtractNumber(string text, string after)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(after);

        var match = Regex.Match(text, $@"(\d+)\s*{Regex.Escape(after)}", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }
}
