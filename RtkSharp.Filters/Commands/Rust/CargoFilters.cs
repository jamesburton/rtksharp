using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Rust;

/// <summary>
/// Buffered (non-streaming) filters for <c>cargo build</c>/<c>cargo test</c>/<c>cargo clippy</c>/
/// <c>cargo install</c>/<c>cargo nextest</c>. The <c>build</c>/<c>test</c> members mirror the same
/// block-grouping logic used by <c>RtkSharp.Commands.Rust.CargoBuildHandler</c>/
/// <c>CargoTestHandler</c>'s streaming handlers, driven here over a fully-captured string instead of a
/// live stream (mirroring Rust's own <c>#[cfg(test)]</c> suite, which exercises these pure functions
/// directly). <c>clippy</c>/<c>install</c>/<c>nextest</c> run to completion before their output is
/// filtered — no incremental streaming. Faithful ports of Rust's <c>filter_cargo_build</c>,
/// <c>filter_cargo_test</c>, <c>filter_cargo_clippy</c>, <c>filter_cargo_install</c>,
/// <c>filter_cargo_nextest</c>, and <c>format_crate_info</c> (<c>src/cmds/rust/cargo_cmd.rs</c>).
/// </summary>
public static class CargoFilters
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

    /// <summary>Buffered equivalent of <c>RtkSharp.Commands.Rust.CargoBuildHandler</c>, driving the same
    /// block-grouping logic over a fully-captured string instead of a live stream.</summary>
    /// <param name="output">The raw <c>cargo build</c>/<c>cargo check</c> output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterCargoBuild(string output)
    {
        var handler = new CargoBuildLineClassifier();
        var blocks = new List<List<string>>();
        var currentBlock = new List<string>();
        var inBlock = false;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (handler.ShouldSkip(line))
            {
                continue;
            }

            if (handler.IsBlockStart(line))
            {
                if (inBlock && currentBlock.Count > 0)
                {
                    blocks.Add(currentBlock);
                    currentBlock = [];
                }

                inBlock = true;
                currentBlock.Add(line);
            }
            else if (inBlock)
            {
                if (handler.IsBlockContinuation(line, currentBlock))
                {
                    currentBlock.Add(line);
                }
                else
                {
                    blocks.Add(currentBlock);
                    currentBlock = [];
                    inBlock = false;
                }
            }
        }

        if (currentBlock.Count > 0)
        {
            blocks.Add(currentBlock);
        }

        if (handler.ErrorCount == 0 && handler.Warnings == 0)
        {
            var s = $"cargo build ({handler.Compiled} crates compiled)";
            if (handler.FinishedLine is not null)
            {
                s = $"{s}\n{handler.FinishedLine}";
            }

            return s;
        }

        var result = new StringBuilder();
        result.Append($"cargo build: {handler.ErrorCount} errors, {handler.Warnings} warnings ({handler.Compiled} crates)\n");

        const int maxCheckBlocks = CapErrors;
        for (var i = 0; i < blocks.Count && i < maxCheckBlocks; i++)
        {
            result.Append(string.Join('\n', blocks[i]));
            result.Append('\n');
            if (i < blocks.Count - 1)
            {
                result.Append('\n');
            }
        }

        if (blocks.Count > maxCheckBlocks)
        {
            result.Append($"\n… +{blocks.Count - maxCheckBlocks} more issues\n");
            var allBlocks = string.Join("\n\n", blocks.Select(b => string.Join('\n', b)));
            var hint = Tee.ForceTeeHint(allBlocks, "cargo-check-issues");
            if (hint is not null)
            {
                result.Append($"  {hint}\n");
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>Buffered equivalent of <c>RtkSharp.Commands.Rust.CargoTestHandler</c>, driving the same
    /// failure-block grouping and pass-aggregation logic over a fully-captured string.</summary>
    /// <param name="output">The raw <c>cargo test</c> output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterCargoTest(string output)
    {
        var failures = new List<string>();
        var summaryLines = new List<string>();
        var inFailureSection = false;
        var currentFailure = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (line.TrimStart().StartsWith("Compiling", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("Downloading", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("Downloaded", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("Finished", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("running ", StringComparison.Ordinal)
                || (line.StartsWith("test ", StringComparison.Ordinal) && line.EndsWith("... ok", StringComparison.Ordinal)))
            {
                continue;
            }

            if (line == "failures:")
            {
                inFailureSection = true;
                continue;
            }

            if (inFailureSection)
            {
                if (line.StartsWith("test result:", StringComparison.Ordinal))
                {
                    inFailureSection = false;
                    summaryLines.Add(line);
                }
                else if (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith("---- ", StringComparison.Ordinal))
                {
                    currentFailure.Add(line);
                }
                else if (line.Trim().Length == 0 && currentFailure.Count > 0)
                {
                    failures.Add(string.Join('\n', currentFailure));
                    currentFailure = [];
                }
                else if (line.Trim().Length != 0)
                {
                    currentFailure.Add(line);
                }
            }

            if (!inFailureSection && line.StartsWith("test result:", StringComparison.Ordinal))
            {
                summaryLines.Add(line);
            }
        }

        if (currentFailure.Count > 0)
        {
            failures.Add(string.Join('\n', currentFailure));
        }

        var result = new StringBuilder();

        if (failures.Count == 0 && summaryLines.Count > 0)
        {
            // All passed - try to aggregate.
            AggregatedTestResult? aggregated = null;
            var allParsed = true;

            foreach (var line in summaryLines)
            {
                var parsed = AggregatedTestResult.ParseLine(line);
                if (parsed is not null)
                {
                    if (aggregated is not null)
                    {
                        aggregated.Merge(parsed);
                    }
                    else
                    {
                        aggregated = parsed;
                    }
                }
                else
                {
                    allParsed = false;
                    break;
                }
            }

            if (allParsed && aggregated is { Suites: > 0 })
            {
                return aggregated.FormatCompact();
            }

            // Fallback: use original behavior if regex failed.
            foreach (var line in summaryLines)
            {
                result.Append(line).Append('\n');
            }

            return result.ToString().Trim();
        }

        if (failures.Count > 0)
        {
            result.Append($"FAILURES ({failures.Count}):\n");
            const int maxFailures = CapWarnings;
            for (var i = 0; i < failures.Count && i < maxFailures; i++)
            {
                result.Append($"{i + 1}. {Utils.Truncate(failures[i], 200)}\n");
            }

            if (failures.Count > maxFailures)
            {
                result.Append($"\n… +{failures.Count - maxFailures} more failures\n");
                var allFailures = string.Join("\n\n", failures);
                var hint = Tee.ForceTeeHint(allFailures, "cargo-test-failures");
                if (hint is not null)
                {
                    result.Append($"  {hint}\n");
                }
            }

            result.Append('\n');
        }

        foreach (var line in summaryLines)
        {
            result.Append(line).Append('\n');
        }

        if (result.ToString().Trim().Length == 0)
        {
            var hasCompileErrors = SourceFilterLineSplitter.SplitLines(output).Any(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith("error[", StringComparison.Ordinal) || trimmed.StartsWith("error:", StringComparison.Ordinal);
            });

            if (hasCompileErrors)
            {
                var buildFiltered = FilterCargoBuild(output);
                if (buildFiltered.StartsWith("cargo build:", StringComparison.Ordinal))
                {
                    return ReplaceFirst(buildFiltered, "cargo build:", "cargo test:");
                }
            }

            // Fallback: show last meaningful lines.
            var meaningful = SourceFilterLineSplitter.SplitLines(output)
                .Where(l => l.Trim().Length != 0 && !l.TrimStart().StartsWith("Compiling", StringComparison.Ordinal))
                .ToList();
            foreach (var line in meaningful.Skip(Math.Max(0, meaningful.Count - 5)))
            {
                result.Append(line).Append('\n');
            }
        }

        return result.ToString().Trim();
    }

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

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
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

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
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

        foreach (var rawLine in SourceFilterLineSplitter.SplitLines(output))
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

    /// <summary>
    /// Rebuilds the first occurrence of <paramref name="search"/> in <paramref name="text"/> as
    /// <paramref name="replacement"/>. Used by <see cref="FilterCargoTest"/>'s compile-error fallback to
    /// swap a <c>cargo build:</c> header for <c>cargo test:</c>.
    /// </summary>
    /// <param name="text">The text to search.</param>
    /// <param name="search">The substring to find.</param>
    /// <param name="replacement">The replacement text.</param>
    /// <returns><paramref name="text"/> with the first occurrence of <paramref name="search"/> replaced, or
    /// unchanged if not found.</returns>
    internal static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + search.Length));
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

}

/// <summary>
/// Pure build-block-grouping state machine shared by <see cref="CargoFilters.FilterCargoBuild"/> (buffered)
/// and <c>RtkSharp.Commands.Rust.CargoBuildHandler</c> (streaming, which wraps an instance of this class to
/// implement <c>IBlockHandler</c> without duplicating the classification logic — that interface lives in
/// <c>RtkSharp.Core</c> and cannot be referenced from this project). Faithful port of Rust's
/// <c>CargoBuildHandler</c> (<c>src/cmds/rust/cargo_cmd.rs</c>:37-110).
/// </summary>
internal sealed class CargoBuildLineClassifier
{
    /// <summary>The number of <c>Compiling</c>/<c>Checking</c> lines observed.</summary>
    public int Compiled { get; private set; }

    /// <summary>The number of warning blocks observed.</summary>
    public int Warnings { get; private set; }

    /// <summary>The number of error blocks observed.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>The trailing <c>Finished ...</c> line, if one was seen.</summary>
    public string? FinishedLine { get; private set; }

    /// <summary>Determines whether a line should be dropped from the output entirely.</summary>
    /// <param name="line">The raw line.</param>
    /// <returns>True if the line should be skipped.</returns>
    public bool ShouldSkip(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("Compiling", StringComparison.Ordinal) || trimmed.StartsWith("Checking", StringComparison.Ordinal))
        {
            Compiled++;
            return true;
        }

        if (trimmed.StartsWith("Downloading", StringComparison.Ordinal) || trimmed.StartsWith("Downloaded", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith("Finished", StringComparison.Ordinal))
        {
            FinishedLine = trimmed;
            return true;
        }

        if (line.StartsWith("warning:", StringComparison.Ordinal)
            && line.Contains("generated", StringComparison.Ordinal)
            && line.Contains("warning", StringComparison.Ordinal))
        {
            return true;
        }

        if ((line.StartsWith("error:", StringComparison.Ordinal) || line.StartsWith("error[", StringComparison.Ordinal))
            && (line.Contains("aborting due to", StringComparison.Ordinal) || line.Contains("could not compile", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    /// <summary>Determines whether a line starts a new error/warning block.</summary>
    /// <param name="line">The raw line.</param>
    /// <returns>True if the line starts a block.</returns>
    public bool IsBlockStart(string line)
    {
        if (line.StartsWith("error[", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))
        {
            ErrorCount++;
            return true;
        }

        if (line.StartsWith("warning:", StringComparison.Ordinal) || line.StartsWith("warning[", StringComparison.Ordinal))
        {
            Warnings++;
            return true;
        }

        return false;
    }

    /// <summary>Determines whether a line continues the current block.</summary>
    /// <param name="line">The raw line.</param>
    /// <param name="block">The lines accumulated in the current block so far.</param>
    /// <returns>True if the line continues the block.</returns>
    public bool IsBlockContinuation(string line, IReadOnlyList<string> block) =>
        !(line.Trim().Length == 0 && block.Count > 3);
}

/// <summary>
/// Aggregated pass counts across one or more <c>test result: ok. ...</c> summary lines, for compact
/// display. Shared by <see cref="CargoFilters.FilterCargoTest"/> (buffered) and
/// <c>RtkSharp.Commands.Rust.CargoTestHandler</c> (streaming). Faithful port of Rust's
/// <c>AggregatedTestResult</c> (<c>src/cmds/rust/cargo_cmd.rs</c>:826-922).
/// </summary>
internal sealed class AggregatedTestResult
{
    private static readonly Regex TestResultRegex = new(
        @"test result: (\w+)\.\s+(\d+) passed;\s+(\d+) failed;\s+(\d+) ignored;\s+(\d+) measured;\s+(\d+) filtered out(?:;\s+finished in ([\d.]+)s)?",
        RegexOptions.Compiled);

    /// <summary>Gets the total number of passed tests.</summary>
    public int Passed { get; private set; }

    /// <summary>Gets the total number of failed tests.</summary>
    public int Failed { get; private set; }

    /// <summary>Gets the total number of ignored tests.</summary>
    public int Ignored { get; private set; }

    /// <summary>Gets the total number of measured (benchmark) tests.</summary>
    public int Measured { get; private set; }

    /// <summary>Gets the total number of filtered-out tests.</summary>
    public int FilteredOut { get; private set; }

    /// <summary>Gets the number of suites aggregated so far.</summary>
    public int Suites { get; private set; }

    /// <summary>Gets the total duration in seconds, if all merged suites reported one.</summary>
    public double DurationSecs { get; private set; }

    /// <summary>Gets whether <see cref="DurationSecs"/> is meaningful (every merged suite reported a duration).</summary>
    public bool HasDuration { get; private set; }

    /// <summary>
    /// Parses a single <c>test result: ...</c> summary line. Only lines whose status is literally
    /// <c>ok</c> (i.e. every test in that suite passed) are aggregatable; anything else (e.g.
    /// <c>FAILED</c>) returns null so the caller falls back to raw display.
    /// </summary>
    /// <param name="line">The summary line to parse.</param>
    /// <returns>The parsed single-suite result, or null if the line didn't match or wasn't all-pass.</returns>
    public static AggregatedTestResult? ParseLine(string line)
    {
        var m = TestResultRegex.Match(line);
        if (!m.Success)
        {
            return null;
        }

        var status = m.Groups[1].Value;
        if (status != "ok")
        {
            return null;
        }

        var hasDuration = m.Groups[7].Success;
        return new AggregatedTestResult
        {
            Passed = int.Parse(m.Groups[2].Value),
            Failed = int.Parse(m.Groups[3].Value),
            Ignored = int.Parse(m.Groups[4].Value),
            Measured = int.Parse(m.Groups[5].Value),
            FilteredOut = int.Parse(m.Groups[6].Value),
            Suites = 1,
            DurationSecs = hasDuration ? double.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture) : 0.0,
            HasDuration = hasDuration,
        };
    }

    /// <summary>Merges another single- (or already-aggregated) suite's counts into this one.</summary>
    /// <param name="other">The result to merge in.</param>
    public void Merge(AggregatedTestResult other)
    {
        Passed += other.Passed;
        Failed += other.Failed;
        Ignored += other.Ignored;
        Measured += other.Measured;
        FilteredOut += other.FilteredOut;
        Suites += other.Suites;
        DurationSecs += other.DurationSecs;
        HasDuration = HasDuration && other.HasDuration;
    }

    /// <summary>Formats this aggregate as a single compact <c>cargo test: ...</c> line.</summary>
    /// <returns>The formatted line (no trailing newline).</returns>
    public string FormatCompact()
    {
        var parts = new List<string> { $"{Passed} passed" };
        if (Ignored > 0)
        {
            parts.Add($"{Ignored} ignored");
        }

        if (FilteredOut > 0)
        {
            parts.Add($"{FilteredOut} filtered out");
        }

        var counts = string.Join(", ", parts);
        var suiteText = Suites == 1 ? "1 suite" : $"{Suites} suites";

        return HasDuration
            ? $"cargo test: {counts} ({suiteText}, {DurationSecs.ToString("F2", CultureInfo.InvariantCulture)}s)"
            : $"cargo test: {counts} ({suiteText})";
    }
}
