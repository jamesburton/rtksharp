using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic for the <c>rtk lint</c> CLI proxy: the <c>eslint</c>/<c>pylint</c>/
/// generic-fallback grouping paths. Extracted from <c>RtkSharp.Commands.Js.LintCommand</c> (Task 7 of
/// the filters-library extraction) — everything here is a pure function of already-captured text, with
/// no process execution or file I/O.
/// </summary>
public static class LintFilters
{
    /// <summary>Rust's <c>CAP_ERRORS</c> (<c>core/truncate.rs:5</c>) — the generic-fallback issue cap.</summary>
    private const int CapErrors = 20;

    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs:7</c>) — the eslint/pylint per-report file cap.</summary>
    private const int CapWarnings = 10;

    /// <summary>
    /// The default passthrough character limit, matching <c>Config</c>'s <c>Limits.PassthroughMaxChars</c>
    /// default. <c>RtkSharp.Filters</c> is a pure library with no knowledge of - and no dependency on - a
    /// caller's <c>~/.config/rtk/config.toml</c>, so the JSON-parse-failure fallbacks below always use
    /// this hardcoded default rather than loading <c>Config</c> (same precedent as
    /// <c>OutputParserSupport.TruncatePassthrough</c>'s <c>DefaultPassthroughMaxChars</c>, Task 6).
    /// </summary>
    private const int DefaultPassthroughMaxChars = 2000;

    /// <summary>
    /// Groups ESLint JSON output (<c>-f json</c>) by rule and by file. Faithful port of
    /// <c>filter_eslint_json</c> (<c>lint_cmd.rs</c>:223-319).
    /// </summary>
    /// <param name="output">The raw ESLint JSON stdout.</param>
    /// <returns>The grouped summary text.</returns>
    public static string FilterEslintJson(string output)
    {
        List<EslintResult>? results;
        try
        {
            results = JsonSerializer.Deserialize(output, LintJsonContext.Default.ListEslintResult);
        }
        catch (JsonException e)
        {
            return $"ESLint output (JSON parse failed: {e.Message})\n{Utils.Truncate(output, DefaultPassthroughMaxChars)}";
        }

        results ??= [];

        var totalErrors = results.Sum(r => r.ErrorCount);
        var totalWarnings = results.Sum(r => r.WarningCount);
        var totalFiles = results.Count(r => r.Messages.Count > 0);

        if (totalErrors == 0 && totalWarnings == 0)
        {
            return "ESLint: No issues found";
        }

        var byRule = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var msg in results.SelectMany(r => r.Messages))
        {
            if (msg.RuleId is { } rule)
            {
                byRule[rule] = byRule.GetValueOrDefault(rule) + 1;
            }
        }

        var byFile = results
            .Where(r => r.Messages.Count > 0)
            .Select(r => (Result: r, Count: r.Messages.Count))
            .OrderByDescending(x => x.Count)
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"ESLint: {totalErrors} errors, {totalWarnings} warnings in {totalFiles} files\n");

        var ruleCounts = byRule.OrderByDescending(kv => kv.Value).ToList();
        if (ruleCounts.Count > 0)
        {
            sb.Append("Top rules:\n");
            foreach (var (rule, count) in ruleCounts.Take(10))
            {
                sb.Append($"  {rule} ({count}x)\n");
            }

            sb.Append('\n');
        }

        sb.Append("Top files:\n");
        foreach (var (fileResult, count) in byFile.Take(CapWarnings))
        {
            sb.Append($"  {CompactPath(fileResult.FilePath)} ({count} issues)\n");

            var fileRules = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var msg in fileResult.Messages)
            {
                if (msg.RuleId is { } rule)
                {
                    fileRules[rule] = fileRules.GetValueOrDefault(rule) + 1;
                }
            }

            foreach (var (rule, count2) in fileRules.OrderByDescending(kv => kv.Value).Take(3))
            {
                sb.Append($"    {rule} ({count2})\n");
            }
        }

        if (byFile.Count > CapWarnings)
        {
            sb.Append($"\n… +{byFile.Count - CapWarnings} more files\n");
            var allFileLines = string.Join('\n', byFile.Select(x => $"{CompactPath(x.Result.FilePath)} ({x.Count} issues)"));
            if (Tee.ForceTeeTailHint(allFileLines, "eslint-files", CapWarnings + 1) is { } hint)
            {
                sb.Append($"  {hint}\n");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Groups Pylint JSON2 output by symbol and by file. Faithful port of <c>filter_pylint_json</c>
    /// (<c>lint_cmd.rs</c>:322-446).
    /// </summary>
    /// <param name="output">The raw Pylint JSON2 stdout.</param>
    /// <returns>The grouped summary text.</returns>
    public static string FilterPylintJson(string output)
    {
        List<PylintDiagnostic>? diagnostics;
        try
        {
            diagnostics = JsonSerializer.Deserialize(output, LintJsonContext.Default.ListPylintDiagnostic);
        }
        catch (JsonException e)
        {
            return $"Pylint output (JSON parse failed: {e.Message})\n{Utils.Truncate(output, DefaultPassthroughMaxChars)}";
        }

        diagnostics ??= [];

        if (diagnostics.Count == 0)
        {
            return "Pylint: No issues found";
        }

        var errors = diagnostics.Count(d => d.MsgType == "error");
        var warnings = diagnostics.Count(d => d.MsgType == "warning");
        var conventions = diagnostics.Count(d => d.MsgType == "convention");
        var refactors = diagnostics.Count(d => d.MsgType == "refactor");

        var totalFiles = diagnostics.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count();

        var bySymbol = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            var key = $"{diag.Symbol} ({diag.MessageId})";
            bySymbol[key] = bySymbol.GetValueOrDefault(key) + 1;
        }

        var byFile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            byFile[diag.Path] = byFile.GetValueOrDefault(diag.Path) + 1;
        }

        var fileCounts = byFile.OrderByDescending(kv => kv.Value).ToList();

        var sb = new StringBuilder();
        sb.Append($"Pylint: {diagnostics.Count} issues in {totalFiles} files\n");

        if (errors > 0 || warnings > 0)
        {
            sb.Append($"  {errors} errors, {warnings} warnings");
            if (conventions > 0 || refactors > 0)
            {
                sb.Append($", {conventions} conventions, {refactors} refactors");
            }

            sb.Append('\n');
        }

        var symbolCounts = bySymbol.OrderByDescending(kv => kv.Value).ToList();
        if (symbolCounts.Count > 0)
        {
            sb.Append("Top rules:\n");
            foreach (var (symbol, count) in symbolCounts.Take(10))
            {
                sb.Append($"  {symbol} ({count}x)\n");
            }

            sb.Append('\n');
        }

        sb.Append("Top files:\n");
        foreach (var (file, count) in fileCounts.Take(CapWarnings))
        {
            sb.Append($"  {CompactPath(file)} ({count} issues)\n");

            var fileSymbols = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var diag in diagnostics.Where(d => d.Path == file))
            {
                var key = $"{diag.Symbol} ({diag.MessageId})";
                fileSymbols[key] = fileSymbols.GetValueOrDefault(key) + 1;
            }

            foreach (var (symbol, count2) in fileSymbols.OrderByDescending(kv => kv.Value).Take(3))
            {
                sb.Append($"    {symbol} ({count2})\n");
            }
        }

        if (fileCounts.Count > CapWarnings)
        {
            sb.Append($"\n… +{fileCounts.Count - CapWarnings} more files\n");
            var allFileLines = string.Join('\n', fileCounts.Select(x => $"{CompactPath(x.Key)} ({x.Value} issues)"));
            if (Tee.ForceTeeTailHint(allFileLines, "pylint-files", CapWarnings + 1) is { } hint)
            {
                sb.Append($"  {hint}\n");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Fallback for non-JSON linters: a naive line-scan counting "warning"/"error" substrings.
    /// Faithful port of <c>filter_generic_lint</c> (<c>lint_cmd.rs</c>:449-489).
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr.</param>
    /// <returns>The summarized issue count and (capped) issue lines.</returns>
    public static string FilterGenericLint(string output)
    {
        var warnings = 0;
        var errors = 0;
        var issues = new List<string>();

        // Rust's `output.lines()` (lint_cmd.rs:454) strips a trailing '\r' per line; a naive
        // Split('\n') on CRLF child-process output would leave it in, corrupting both the emitted
        // issue text and the 100-char Truncate budget below.
        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            var lineLower = line.ToLowerInvariant();
            if (lineLower.Contains("warning", StringComparison.Ordinal))
            {
                warnings++;
                issues.Add(line);
            }

            if (lineLower.Contains("error", StringComparison.Ordinal) && !lineLower.Contains("0 error", StringComparison.Ordinal))
            {
                errors++;
                issues.Add(line);
            }
        }

        if (errors == 0 && warnings == 0)
        {
            return "Lint: No issues found";
        }

        var sb = new StringBuilder();
        sb.Append($"Lint: {errors} errors, {warnings} warnings\n");

        foreach (var issue in issues.Take(CapErrors))
        {
            sb.Append($"{Utils.Truncate(issue, 100)}\n");
        }

        if (issues.Count > CapErrors)
        {
            sb.Append($"\n… +{issues.Count - CapErrors} more issues\n");
            var allIssues = string.Join('\n', issues);
            if (Tee.ForceTeeTailHint(allIssues, "lint-issues", CapErrors + 1) is { } hint)
            {
                sb.Append($"  {hint}\n");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Shortens a file path by keeping only the portion from the last <c>src/</c>/<c>lib/</c> segment
    /// onward, or just the file name if neither is present. Faithful port of <c>compact_path</c>
    /// (<c>lint_cmd.rs</c>:492-505).
    /// </summary>
    /// <param name="path">The file path to shorten.</param>
    /// <returns>The shortened path.</returns>
    public static string CompactPath(string path)
    {
        var normalized = path.Replace('\\', '/');

        var srcPos = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
        if (srcPos >= 0)
        {
            return "src/" + normalized[(srcPos + 5)..];
        }

        var libPos = normalized.LastIndexOf("/lib/", StringComparison.Ordinal);
        if (libPos >= 0)
        {
            return "lib/" + normalized[(libPos + 5)..];
        }

        var slashPos = normalized.LastIndexOf('/');
        return slashPos >= 0 ? normalized[(slashPos + 1)..] : normalized;
    }
}

/// <summary>ESLint's <c>-f json</c> per-message shape. Port of Rust <c>EslintMessage</c> (<c>lint_cmd.rs</c>:14-22).</summary>
internal sealed class EslintMessage
{
    [JsonPropertyName("ruleId")]
    public string? RuleId { get; set; }

    [JsonPropertyName("severity")]
    public byte Severity { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>ESLint's <c>-f json</c> per-file shape. Port of Rust <c>EslintResult</c> (<c>lint_cmd.rs</c>:24-33).</summary>
internal sealed class EslintResult
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<EslintMessage> Messages { get; set; } = [];

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }
}

/// <summary>Pylint's <c>--output-format=json2</c> per-diagnostic shape. Port of Rust <c>PylintDiagnostic</c> (<c>lint_cmd.rs</c>:35-53).</summary>
internal sealed class PylintDiagnostic
{
    [JsonPropertyName("type")]
    public string MsgType { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("message-id")]
    public string MessageId { get; set; } = "";
}

/// <summary>Source-generated JSON context for <see cref="LintFilters"/>'s ESLint/Pylint DTOs, avoiding reflection-based (de)serialization under <c>PublishAot</c>.</summary>
[JsonSerializable(typeof(List<EslintResult>))]
[JsonSerializable(typeof(List<PylintDiagnostic>))]
internal sealed partial class LintJsonContext : JsonSerializerContext;
