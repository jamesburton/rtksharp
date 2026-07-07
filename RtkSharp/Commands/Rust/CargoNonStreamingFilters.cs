using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Commands.System;
using RtkSharp.Core;

namespace RtkSharp.Commands.Rust;

/// <summary>
/// Buffered (non-streaming) filters for <c>cargo clippy</c>, <c>cargo install</c>, and
/// <c>cargo nextest</c>. Unlike <c>build</c>/<c>test</c>/<c>check</c>, these three run to completion
/// before their output is filtered — no incremental streaming. Faithful ports of
/// <c>filter_cargo_clippy</c>, <c>filter_cargo_install</c>, and <c>filter_cargo_nextest</c>
/// (<c>src/cmds/rust/cargo_cmd.rs</c>).
/// </summary>
internal static class CargoNonStreamingFilters
{
    // Rust CAP_ERRORS / CAP_WARNINGS / CAP_LIST from src/core/truncate.rs.
    private const int CapErrors = 20;
    private const int CapWarnings = 10;
    private const int CapList = 20;

    private static readonly Regex NextestSummaryRegex = new(
        @"Summary \[\s*([\d.]+)s\]\s+(\d+) tests? run:\s+(\d+) passed(?:,\s+(\d+) failed)?(?:,\s+(\d+) skipped)?",
        RegexOptions.Compiled);

    private static readonly Regex NextestStartingRegex = new(
        @"Starting \d+ tests? across (\d+) binar(?:y|ies)",
        RegexOptions.Compiled);

    /// <summary>
    /// Formats a crate name + version into a display string. Faithful port of Rust's
    /// <c>format_crate_info</c> (<c>cargo_cmd.rs</c>:341-349).
    /// </summary>
    /// <param name="name">The crate name, or empty if unknown.</param>
    /// <param name="version">The crate version, or empty if unknown.</param>
    /// <param name="fallback">The text to use when <paramref name="name"/> is empty.</param>
    /// <returns>The formatted display string.</returns>
    public static string FormatCrateInfo(string name, string version, string fallback)
    {
        if (name.Length == 0)
        {
            return fallback;
        }

        return version.Length == 0 ? name : $"{name} {version}";
    }

    /// <summary>
    /// Filters <c>cargo install</c> output: strips dependency-compilation noise, keeps
    /// Installing/Installed/Replacing/Replaced lines and actionable PATH warnings, and handles the
    /// "already installed" case. Faithful port of Rust's <c>filter_cargo_install</c>
    /// (<c>cargo_cmd.rs</c>:352-526).
    /// </summary>
    /// <param name="output">The raw <c>cargo install</c> output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterCargoInstall(string output)
    {
        var errors = new List<string>();
        var errorCount = 0;
        var compiled = 0;
        var inError = false;
        var currentError = new List<string>();
        var installedCrate = string.Empty;
        var installedVersion = string.Empty;
        var replacedLines = new List<string>();
        var alreadyInstalled = false;
        var ignoredLine = string.Empty;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("Compiling", StringComparison.Ordinal))
            {
                compiled++;
                continue;
            }

            if (trimmed.StartsWith("Downloading", StringComparison.Ordinal)
                || trimmed.StartsWith("Downloaded", StringComparison.Ordinal)
                || trimmed.StartsWith("Locking", StringComparison.Ordinal)
                || trimmed.StartsWith("Updating", StringComparison.Ordinal)
                || trimmed.StartsWith("Adding", StringComparison.Ordinal)
                || trimmed.StartsWith("Finished", StringComparison.Ordinal)
                || trimmed.StartsWith("Blocking waiting for file lock", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("Installing", StringComparison.Ordinal))
            {
                var rest = trimmed["Installing".Length..].Trim();
                if (rest.Length != 0 && !rest.StartsWith('/'))
                {
                    var spaceIdx = rest.IndexOf(' ');
                    if (spaceIdx >= 0)
                    {
                        installedCrate = rest[..spaceIdx];
                        installedVersion = rest[(spaceIdx + 1)..];
                    }
                    else
                    {
                        installedCrate = rest;
                    }
                }

                continue;
            }

            if (trimmed.StartsWith("Installed", StringComparison.Ordinal))
            {
                var rest = trimmed["Installed".Length..].Trim();
                if (rest.Length != 0 && installedCrate.Length == 0)
                {
                    var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        installedCrate = parts[0];
                        installedVersion = parts[1];
                    }
                }

                continue;
            }

            if (trimmed.StartsWith("Replacing", StringComparison.Ordinal) || trimmed.StartsWith("Replaced", StringComparison.Ordinal))
            {
                replacedLines.Add(trimmed);
                continue;
            }

            if (trimmed.StartsWith("Ignored package", StringComparison.Ordinal))
            {
                alreadyInstalled = true;
                ignoredLine = trimmed;
                continue;
            }

            if (line.StartsWith("warning:", StringComparison.Ordinal))
            {
                if (!(line.Contains("generated", StringComparison.Ordinal) && line.Contains("warning", StringComparison.Ordinal)))
                {
                    replacedLines.Add(line);
                }

                continue;
            }

            if (line.StartsWith("error[", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))
            {
                if (line.Contains("aborting due to", StringComparison.Ordinal) || line.Contains("could not compile", StringComparison.Ordinal))
                {
                    continue;
                }

                if (inError && currentError.Count > 0)
                {
                    errors.Add(string.Join('\n', currentError));
                    currentError = [];
                }

                errorCount++;
                inError = true;
                currentError.Add(line);
            }
            else if (inError)
            {
                if (line.Trim().Length == 0 && currentError.Count > 3)
                {
                    errors.Add(string.Join('\n', currentError));
                    currentError = [];
                    inError = false;
                }
                else
                {
                    currentError.Add(line);
                }
            }
        }

        if (currentError.Count > 0)
        {
            errors.Add(string.Join('\n', currentError));
        }

        if (alreadyInstalled)
        {
            var parts = ignoredLine.Split('`');
            var info = parts.Length > 1 ? parts[1] : ignoredLine;
            return $"cargo install: {info} already installed";
        }

        if (errorCount > 0)
        {
            var crateInfo = FormatCrateInfo(installedCrate, installedVersion, "");
            var depsInfo = compiled > 0 ? $", {compiled} deps compiled" : string.Empty;

            var result = new StringBuilder();
            if (crateInfo.Length == 0)
            {
                result.Append($"cargo install: {errorCount} error{(errorCount > 1 ? "s" : string.Empty)}{depsInfo}\n");
            }
            else
            {
                result.Append($"cargo install: {errorCount} error{(errorCount > 1 ? "s" : string.Empty)} ({crateInfo}{depsInfo})\n");
            }

            const int maxInstallErrors = CapErrors;
            for (var i = 0; i < errors.Count && i < maxInstallErrors; i++)
            {
                result.Append(errors[i]);
                result.Append('\n');
                if (i < errors.Count - 1)
                {
                    result.Append('\n');
                }
            }

            if (errors.Count > maxInstallErrors)
            {
                result.Append($"\n… +{errors.Count - maxInstallErrors} more issues\n");
                var allErrors = string.Join("\n\n", errors);
                var hint = Tee.ForceTeeHint(allErrors, "cargo-build-errors");
                if (hint is not null)
                {
                    result.Append($"  {hint}\n");
                }
            }

            return result.ToString().Trim();
        }

        var successCrateInfo = FormatCrateInfo(installedCrate, installedVersion, "package");
        var successResult = new StringBuilder($"cargo install ({successCrateInfo}, {compiled} deps compiled)");

        foreach (var line in replacedLines)
        {
            successResult.Append($"\n  {line}");
        }

        return successResult.ToString();
    }

    /// <summary>
    /// Filters <c>cargo clippy</c> output: shows full multi-line error blocks verbatim and groups
    /// warnings by lint rule (extracted from the trailing <c>[rule_name]</c> bracket). Faithful port
    /// of Rust's <c>filter_cargo_clippy</c> (<c>cargo_cmd.rs</c>:1059-1228).
    /// </summary>
    /// <param name="output">The raw <c>cargo clippy</c> output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterCargoClippy(string output)
    {
        var byRule = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var errorCount = 0;
        var warningCount = 0;
        var errorBlocks = new List<List<string>>();

        var currentRule = string.Empty;
        var inError = false;
        var currentBlock = new List<string>();

        foreach (var line in ReadCommand.SplitLines(output))
        {
            if (line.TrimStart().StartsWith("Compiling", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("Checking", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("Downloading", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("Downloaded", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("Finished", StringComparison.Ordinal))
            {
                if (inError && currentBlock.Count > 0)
                {
                    errorBlocks.Add([.. currentBlock]);
                    currentBlock.Clear();
                    inError = false;
                }

                continue;
            }

            if ((line.Contains("generated", StringComparison.Ordinal) && line.Contains("warning", StringComparison.Ordinal))
                || line.Contains("aborting due to", StringComparison.Ordinal)
                || line.Contains("could not compile", StringComparison.Ordinal))
            {
                continue;
            }

            var isErrorLine = line.StartsWith("error:", StringComparison.Ordinal) || line.StartsWith("error[", StringComparison.Ordinal);
            var isWarningLine = line.StartsWith("warning:", StringComparison.Ordinal) || line.StartsWith("warning[", StringComparison.Ordinal);

            if (isErrorLine || isWarningLine)
            {
                if (inError && currentBlock.Count > 0)
                {
                    errorBlocks.Add([.. currentBlock]);
                    currentBlock.Clear();
                }

                inError = false;

                if (isErrorLine)
                {
                    errorCount++;
                    inError = true;
                    currentBlock.Add(line);
                }
                else
                {
                    warningCount++;
                }

                var bracketStart = line.LastIndexOf('[');
                var bracketEnd = line.LastIndexOf(']');
                if (bracketStart >= 0 && bracketEnd >= 0)
                {
                    currentRule = line[(bracketStart + 1)..bracketEnd];
                }
                else if (bracketStart >= 0)
                {
                    currentRule = line;
                }
                else
                {
                    var prefix = isErrorLine ? "error: " : "warning: ";
                    currentRule = line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line;
                }
            }
            else if (line.TrimStart().StartsWith("--> ", StringComparison.Ordinal))
            {
                var location = line.TrimStart()["--> ".Length..];
                if (currentRule.Length != 0)
                {
                    if (!byRule.TryGetValue(currentRule, out var locations))
                    {
                        locations = [];
                        byRule[currentRule] = locations;
                    }

                    locations.Add(location);
                }

                if (inError)
                {
                    currentBlock.Add(line);
                }
            }
            else if (inError)
            {
                if (line.Trim().Length == 0)
                {
                    if (currentBlock.Count > 0)
                    {
                        errorBlocks.Add([.. currentBlock]);
                        currentBlock.Clear();
                    }

                    inError = false;
                }
                else if (currentBlock.Count < 15)
                {
                    currentBlock.Add(line);
                }
            }
        }

        if (inError && currentBlock.Count > 0)
        {
            errorBlocks.Add(currentBlock);
        }

        if (errorCount == 0 && warningCount == 0)
        {
            return "cargo clippy: No issues found";
        }

        var result = new StringBuilder();
        result.Append($"cargo clippy: {errorCount} errors, {warningCount} warnings\n");

        if (errorBlocks.Count > 0)
        {
            const int maxClippyErrors = CapWarnings;
            result.Append("\nErrors:\n");
            for (var i = 0; i < errorBlocks.Count && i < maxClippyErrors; i++)
            {
                foreach (var blockLine in errorBlocks[i])
                {
                    result.Append($"  {Utils.Truncate(blockLine, 160)}\n");
                }

                result.Append('\n');
            }

            if (errorBlocks.Count > maxClippyErrors)
            {
                result.Append($"  … +{errorBlocks.Count - maxClippyErrors} more errors\n");
                var allBlocks = string.Join("\n\n", errorBlocks.Select(b => string.Join('\n', b)));
                var hint = Tee.ForceTeeHint(allBlocks, "cargo-clippy-errors");
                if (hint is not null)
                {
                    result.Append($"  {hint}\n");
                }
            }
        }

        var ruleCounts = byRule.ToList();
        ruleCounts.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

        const int maxRules = CapList;
        foreach (var (rule, locations) in ruleCounts.Take(maxRules))
        {
            result.Append($"  {rule} ({locations.Count}x)\n");
            foreach (var loc in locations.Take(3))
            {
                result.Append($"    {loc}\n");
            }

            if (locations.Count > 3)
            {
                result.Append($"    … +{locations.Count - 3} more\n");
            }
        }

        if (byRule.Count > maxRules)
        {
            result.Append($"\n… +{byRule.Count - maxRules} more rules\n");
            var allRules = string.Join('\n', ruleCounts.Select(kv => $"{kv.Key} ({kv.Value.Count}x)"));
            var hint = Tee.ForceTeeTailHint(allRules, "cargo-clippy-rules", maxRules + 1);
            if (hint is not null)
            {
                result.Append($"  {hint}\n");
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Pushes a completed failure block (header + body) into <paramref name="failures"/>, then clears
    /// the buffers. Faithful port of Rust's <c>flush_failure_block</c> (<c>cargo_cmd.rs</c>:528-541).
    /// </summary>
    private static void FlushFailureBlock(ref string header, List<string> body, List<string> failures)
    {
        if (header.Length == 0)
        {
            return;
        }

        var block = header;
        if (body.Count > 0)
        {
            block += "\n" + string.Join('\n', body);
        }

        failures.Add(block);
        header = string.Empty;
        body.Clear();
    }

    /// <summary>
    /// Filters <c>cargo nextest</c> output: shows failures (stderr body only) plus a compact summary
    /// parsed via <see cref="NextestSummaryRegex"/>, discarding everything after the <c>Summary</c>
    /// line (post-summary FAIL recaps and <c>error: test run failed</c> are noise). Faithful port of
    /// Rust's <c>filter_cargo_nextest</c> (<c>cargo_cmd.rs</c>:544-758).
    /// </summary>
    /// <param name="output">The raw <c>cargo nextest</c> output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterCargoNextest(string output)
    {
        var failures = new List<string>();
        var inFailureBlock = false;
        var pastSummary = false;
        var currentFailureHeader = string.Empty;
        var currentFailureBody = new List<string>();
        var summaryLine = string.Empty;
        uint binaries = 0;
        var hasCancelLine = false;

        foreach (var rawLine in ReadCommand.SplitLines(output))
        {
            var trimmed = rawLine.Trim();

            if (trimmed.StartsWith("Compiling", StringComparison.Ordinal)
                || trimmed.StartsWith("Downloading", StringComparison.Ordinal)
                || trimmed.StartsWith("Downloaded", StringComparison.Ordinal)
                || trimmed.StartsWith("Finished", StringComparison.Ordinal)
                || trimmed.StartsWith("Locking", StringComparison.Ordinal)
                || trimmed.StartsWith("Updating", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("────", StringComparison.Ordinal))
            {
                continue;
            }

            if (pastSummary)
            {
                continue;
            }

            if (trimmed.StartsWith("Starting", StringComparison.Ordinal))
            {
                var m = NextestStartingRegex.Match(trimmed);
                if (m.Success)
                {
                    _ = uint.TryParse(m.Groups[1].Value, out binaries);
                }

                continue;
            }

            if (trimmed.StartsWith("PASS", StringComparison.Ordinal))
            {
                if (inFailureBlock)
                {
                    FlushFailureBlock(ref currentFailureHeader, currentFailureBody, failures);
                    inFailureBlock = false;
                }

                continue;
            }

            if (trimmed.StartsWith("FAIL", StringComparison.Ordinal))
            {
                if (inFailureBlock)
                {
                    FlushFailureBlock(ref currentFailureHeader, currentFailureBody, failures);
                }

                currentFailureHeader = trimmed;
                inFailureBlock = true;
                continue;
            }

            if (trimmed.StartsWith("Cancelling", StringComparison.Ordinal) || trimmed.StartsWith("Canceling", StringComparison.Ordinal))
            {
                hasCancelLine = true;
                continue;
            }

            if (trimmed.StartsWith("Nextest run ID", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("Summary", StringComparison.Ordinal))
            {
                summaryLine = trimmed;
                if (inFailureBlock)
                {
                    FlushFailureBlock(ref currentFailureHeader, currentFailureBody, failures);
                    inFailureBlock = false;
                }

                pastSummary = true;
                continue;
            }

            if (inFailureBlock)
            {
                currentFailureBody.Add(rawLine);
            }
        }

        if (inFailureBlock)
        {
            FlushFailureBlock(ref currentFailureHeader, currentFailureBody, failures);
        }

        var summaryMatch = NextestSummaryRegex.Match(summaryLine);
        if (summaryMatch.Success)
        {
            var duration = summaryMatch.Groups[1].Success ? summaryMatch.Groups[1].Value : "?";
            var passed = summaryMatch.Groups[3].Success ? uint.Parse(summaryMatch.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            var failed = summaryMatch.Groups[4].Success ? uint.Parse(summaryMatch.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            var skipped = summaryMatch.Groups[5].Success ? uint.Parse(summaryMatch.Groups[5].Value, CultureInfo.InvariantCulture) : 0;

            var binaryText = binaries switch
            {
                > 1 => $"{binaries} binaries",
                1 => "1 binary",
                _ => string.Empty,
            };

            if (failed == 0)
            {
                var parts = new List<string> { $"{passed} passed" };
                if (skipped > 0)
                {
                    parts.Add($"{skipped} skipped");
                }

                var meta = binaryText.Length == 0 ? $"{duration}s" : $"{binaryText}, {duration}s";
                return $"cargo nextest: {string.Join(", ", parts)} ({meta})";
            }

            var result = new StringBuilder();

            foreach (var failure in failures)
            {
                result.Append(failure);
                result.Append('\n');
            }

            if (hasCancelLine)
            {
                result.Append("Cancelling due to test failure\n");
            }

            var summaryParts = new List<string> { $"{passed} passed" };
            if (failed > 0)
            {
                summaryParts.Add($"{failed} failed");
            }

            if (skipped > 0)
            {
                summaryParts.Add($"{skipped} skipped");
            }

            var meta2 = binaryText.Length == 0 ? $"{duration}s" : $"{binaryText}, {duration}s";
            result.Append($"cargo nextest: {string.Join(", ", summaryParts)} ({meta2})");

            return result.ToString().Trim();
        }

        // Fallback: if summary regex didn't match, show what we have.
        if (failures.Count > 0)
        {
            var result = new StringBuilder();
            foreach (var failure in failures)
            {
                result.Append(failure);
                result.Append('\n');
            }

            if (summaryLine.Length != 0)
            {
                result.Append(summaryLine);
            }

            return result.ToString().Trim();
        }

        if (summaryLine.Length != 0)
        {
            return summaryLine;
        }

        // Empty or unrecognized.
        return string.Empty;
    }
}
