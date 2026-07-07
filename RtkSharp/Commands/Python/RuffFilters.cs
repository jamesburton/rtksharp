using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Commands.System;
using RtkSharp.Core;

namespace RtkSharp.Commands.Python;

/// <summary>
/// Buffered filters for <c>ruff check --output-format=json</c> and <c>ruff format</c> output.
/// Faithful port of <c>filter_ruff_check_json</c>/<c>filter_ruff_format</c>/<c>compact_path</c>
/// (<c>src/cmds/python/ruff_cmd.rs</c>).
/// </summary>
internal static class RuffFilters
{
    // Rust CAP_WARNINGS from src/core/truncate.rs — used here for both the "top rules" and
    // "top files" listings (MAX_RUFF_RULES/MAX_RUFF_FILES) and the format-mode file listing
    // (MAX_RUFF_FORMAT_FILES), exactly as ruff_cmd.rs reuses the same constant for all three.
    private const int CapWarnings = 10;

    private const int MaxViolations = 50;

    /// <summary>
    /// Filters <c>ruff check --output-format=json</c> output: groups violations by rule code and by
    /// file, shows the top offenders of each, then lists up to <see cref="MaxViolations"/> individual
    /// violations. Faithful port of Rust <c>filter_ruff_check_json</c> (<c>ruff_cmd.rs</c>:94-229).
    /// </summary>
    /// <param name="output">The raw <c>ruff check --output-format=json</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterRuffCheckJson(string output)
    {
        List<RuffDiagnostic>? diagnostics;
        try
        {
            diagnostics = JsonSerializer.Deserialize(output, RuffJsonContext.Default.ListRuffDiagnostic);
        }
        catch (JsonException e)
        {
            return $"Ruff check (JSON parse failed: {e.Message})\n{Utils.Truncate(output, Config.LoadOrDefault().Limits.PassthroughMaxChars)}";
        }

        diagnostics ??= [];

        if (diagnostics.Count == 0)
        {
            return "Ruff: No issues found";
        }

        var totalIssues = diagnostics.Count;
        var fixableCount = diagnostics.Count(d => d.Fix is not null);

        var uniqueFiles = new HashSet<string>(diagnostics.Select(d => d.Filename), StringComparer.Ordinal);
        var totalFiles = uniqueFiles.Count;

        var byRule = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            byRule[diag.Code] = byRule.GetValueOrDefault(diag.Code) + 1;
        }

        var byFile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            byFile[diag.Filename] = byFile.GetValueOrDefault(diag.Filename) + 1;
        }

        var fileCounts = byFile.ToList();
        fileCounts.Sort((a, b) => b.Value.CompareTo(a.Value));

        var result = new StringBuilder();
        result.Append($"Ruff: {totalIssues} issues in {totalFiles} files");
        if (fixableCount > 0)
        {
            result.Append($" ({fixableCount} fixable)");
        }

        result.Append('\n');

        var ruleCounts = byRule.ToList();
        ruleCounts.Sort((a, b) => b.Value.CompareTo(a.Value));

        const int maxRuffRules = CapWarnings;
        const int maxRuffFiles = CapWarnings;
        if (ruleCounts.Count > 0)
        {
            result.Append("Top rules:\n");
            foreach (var (rule, count) in ruleCounts.Take(maxRuffRules))
            {
                result.Append($"  {rule} ({count}x)\n");
            }

            result.Append('\n');
        }

        result.Append("Top files:\n");
        foreach (var (file, count) in fileCounts.Take(maxRuffFiles))
        {
            var shortPath = CompactPath(file);
            result.Append($"  {shortPath} ({count} issues)\n");

            var fileRules = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var diag in diagnostics.Where(d => d.Filename == file))
            {
                fileRules[diag.Code] = fileRules.GetValueOrDefault(diag.Code) + 1;
            }

            var fileRuleCounts = fileRules.ToList();
            fileRuleCounts.Sort((a, b) => b.Value.CompareTo(a.Value));

            foreach (var (rule, ruleCount) in fileRuleCounts.Take(3))
            {
                result.Append($"    {rule} ({ruleCount})\n");
            }
        }

        if (fileCounts.Count > maxRuffFiles)
        {
            result.Append($"\n... +{fileCounts.Count - maxRuffFiles} more files\n");
        }

        var violationLines = diagnostics.Select(diag =>
            $"  {CompactPath(diag.Filename)}:{diag.Location.Row}:{diag.Location.Column} {diag.Code} {Utils.Truncate(diag.Message.Trim(), 100)}\n"
        ).ToList();

        result.Append("\nViolations:\n");
        foreach (var line in violationLines.Take(MaxViolations))
        {
            result.Append(line);
        }

        if (violationLines.Count > MaxViolations)
        {
            result.Append($"  … +{violationLines.Count - MaxViolations} more\n");
            var full = string.Concat(violationLines);
            if (Tee.ForceTeeTailHint(full, "ruff-check", MaxViolations + 1) is { } hint)
            {
                result.Append($"  {hint}\n");
            }
        }

        if (fixableCount > 0)
        {
            result.Append($"\n[hint] Run `ruff check --fix` to auto-fix {fixableCount} issues\n");
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Filters <c>ruff format</c> output: reports how many files still need formatting (check mode)
    /// or passes through the write-mode summary. Faithful port of Rust <c>filter_ruff_format</c>
    /// (<c>ruff_cmd.rs</c>:232-319).
    /// </summary>
    /// <param name="output">The raw <c>ruff format</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterRuffFormat(string output)
    {
        var filesToFormat = new List<string>();
        var filesChecked = 0;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var trimmed = line.Trim();
            var lower = trimmed.ToLowerInvariant();

            if (lower.Contains("would reformat:", StringComparison.Ordinal))
            {
                var parts = trimmed.Split(':');
                if (parts.Length > 1)
                {
                    filesToFormat.Add(parts[1].Trim());
                }
            }

            if (lower.Contains("left unchanged", StringComparison.Ordinal))
            {
                var commaParts = trimmed.Split(',');
                foreach (var part in commaParts)
                {
                    var partLower = part.ToLowerInvariant();
                    if (partLower.Contains("left unchanged", StringComparison.Ordinal))
                    {
                        var words = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        for (var i = 0; i < words.Length; i++)
                        {
                            if ((words[i] == "file" || words[i] == "files") && i > 0 && int.TryParse(words[i - 1], out var count))
                            {
                                filesChecked = count;
                                break;
                            }
                        }

                        break;
                    }
                }
            }
        }

        var outputLower = output.ToLowerInvariant();

        if (filesToFormat.Count == 0 && outputLower.Contains("left unchanged", StringComparison.Ordinal))
        {
            return "Ruff format: All files formatted correctly";
        }

        var result = new StringBuilder();

        if (outputLower.Contains("would reformat", StringComparison.Ordinal))
        {
            if (filesToFormat.Count == 0)
            {
                result.Append("Ruff format: All files formatted correctly\n");
            }
            else
            {
                result.Append($"Ruff format: {filesToFormat.Count} files need formatting\n");

                const int maxRuffFormatFiles = CapWarnings;
                var i = 0;
                foreach (var file in filesToFormat.Take(maxRuffFormatFiles))
                {
                    result.Append($"{i + 1}. {CompactPath(file)}\n");
                    i++;
                }

                if (filesToFormat.Count > maxRuffFormatFiles)
                {
                    result.Append($"\n... +{filesToFormat.Count - maxRuffFormatFiles} more files\n");
                }

                if (filesChecked > 0)
                {
                    result.Append($"\n{filesChecked} files already formatted\n");
                }

                result.Append("\n[hint] Run `ruff format` to format these files\n");
            }
        }
        else
        {
            result.Append(output.Trim());
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Compacts a file path by keeping only the portion from the last <c>src/</c>, <c>lib/</c>, or
    /// <c>tests/</c> segment onward, or just the file name if none of those are present. Faithful port
    /// of Rust <c>compact_path</c> (<c>ruff_cmd.rs</c>:322-336).
    /// </summary>
    /// <param name="path">The path to compact.</param>
    /// <returns>The compacted, forward-slash-normalized path.</returns>
    internal static string CompactPath(string path)
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

        var testsPos = normalized.LastIndexOf("/tests/", StringComparison.Ordinal);
        if (testsPos >= 0)
        {
            return "tests/" + normalized[(testsPos + 7)..];
        }

        var slashPos = normalized.LastIndexOf('/');
        return slashPos >= 0 ? normalized[(slashPos + 1)..] : normalized;
    }
}

/// <summary>Ruff JSON diagnostic location. Port of Rust <c>RuffLocation</c> (<c>ruff_cmd.rs</c>:11-15).</summary>
internal sealed class RuffLocation
{
    [JsonPropertyName("row")]
    public int Row { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>Ruff JSON diagnostic fix descriptor. Port of Rust <c>RuffFix</c> (<c>ruff_cmd.rs</c>:17-21).</summary>
internal sealed class RuffFix
{
    [JsonPropertyName("applicability")]
    public string? Applicability { get; set; }
}

/// <summary>Ruff <c>--output-format=json</c> per-diagnostic shape. Port of Rust <c>RuffDiagnostic</c> (<c>ruff_cmd.rs</c>:23-32).</summary>
internal sealed class RuffDiagnostic
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("location")]
    public RuffLocation Location { get; set; } = new();

    [JsonPropertyName("end_location")]
    public RuffLocation? EndLocation { get; set; }

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("fix")]
    public RuffFix? Fix { get; set; }
}

/// <summary>Source-generated JSON context for <see cref="RuffCommand"/>'s DTOs, avoiding reflection-based (de)serialization under <c>PublishAot</c>.</summary>
[JsonSerializable(typeof(List<RuffDiagnostic>))]
internal sealed partial class RuffJsonContext : JsonSerializerContext;
