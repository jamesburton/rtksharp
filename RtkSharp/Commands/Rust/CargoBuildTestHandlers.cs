using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Commands.System;
using RtkSharp.Core;

namespace RtkSharp.Commands.Rust;

/// <summary>
/// Streaming block handler for <c>cargo build</c>/<c>cargo check</c>: strips <c>Compiling</c>/
/// <c>Checking</c>/<c>Downloading</c>/<c>Finished</c> noise, groups multi-line error/warning blocks,
/// and emits a compact final summary. Faithful port of Rust's <c>CargoBuildHandler</c>
/// (<c>src/cmds/rust/cargo_cmd.rs</c>:37-110).
/// </summary>
/// <remarks>
/// <b>Quirk preserved verbatim: <c>cargo check</c> reports as "cargo build".</b> Rust's <c>run_check</c>
/// drives the exact same <c>CargoBuildHandler</c> as <c>run_build</c> — its <see cref="FormatSummary"/>
/// always emits <c>"cargo build (...)"</c> / <c>"cargo build: ..."</c> text, never <c>"cargo check"</c>.
/// This looks like an oversight in the Rust source but is preserved as-is per the fidelity mandate: an
/// <c>rtk cargo check</c> run with no diagnostics prints <c>"cargo build (N crates compiled)"</c>.
/// </remarks>
internal sealed class CargoBuildHandler : IBlockHandler
{
    /// <summary>The number of <c>Compiling</c>/<c>Checking</c> lines observed.</summary>
    public int Compiled { get; private set; }

    /// <summary>The number of warning blocks observed.</summary>
    public int Warnings { get; private set; }

    /// <summary>The number of error blocks observed.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>The trailing <c>Finished ...</c> line, if one was seen.</summary>
    public string? FinishedLine { get; private set; }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public bool IsBlockContinuation(string line, IReadOnlyList<string> block) =>
        !(line.Trim().Length == 0 && block.Count > 3);

    /// <inheritdoc />
    public string? FormatSummary(int exitCode, string raw)
    {
        if (ErrorCount == 0 && Warnings == 0)
        {
            var s = $"cargo build ({Compiled} crates compiled)";
            if (FinishedLine is not null)
            {
                s = $"{s}\n{FinishedLine}";
            }

            return s + "\n";
        }

        return $"cargo build: {ErrorCount} errors, {Warnings} warnings ({Compiled} crates)\n";
    }
}

/// <summary>
/// Streaming block handler for <c>cargo test</c>: strips compiling/<c>running N tests</c>/individual
/// <c>... ok</c> lines, tracks <c>failures:</c> sections, and either aggregates all-pass
/// <c>test result: ok. ...</c> lines across suites into one compact line, or shows full failure blocks
/// when anything failed. Falls back to build-error formatting (with <c>cargo build:</c> swapped to
/// <c>cargo test:</c>) when compilation itself failed. Faithful port of Rust's <c>CargoTestHandler</c>
/// (<c>src/cmds/rust/cargo_cmd.rs</c>:112-240).
/// </summary>
/// <remarks>
/// <b>Quirk preserved verbatim: the second <c>failures:</c> line is a name-listing section to discard.</b>
/// Cargo prints two <c>failures:</c> headers per run — one introducing per-test detail blocks, a second
/// (after all detail blocks) introducing a bare list of failed test names. <see cref="ShouldSkip"/>
/// detects the second occurrence (<c>_inFailureSection</c> already true) and switches into
/// <c>_inFailureNames</c> mode, silently dropping every line until <c>test result:</c> reappears.
/// </remarks>
internal sealed class CargoTestHandler : IBlockHandler
{
    private readonly List<string> _summaryLines = [];
    private bool _inFailureSection;
    private bool _inFailureNames;
    private bool _hasCompileErrors;

    /// <inheritdoc />
    public bool ShouldSkip(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("Compiling", StringComparison.Ordinal)
            || trimmed.StartsWith("Downloading", StringComparison.Ordinal)
            || trimmed.StartsWith("Downloaded", StringComparison.Ordinal)
            || trimmed.StartsWith("Finished", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.StartsWith("running ", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.StartsWith("test ", StringComparison.Ordinal) && line.EndsWith("... ok", StringComparison.Ordinal))
        {
            return true;
        }

        // Track compile errors for fallback.
        if (trimmed.StartsWith("error[", StringComparison.Ordinal) || trimmed.StartsWith("error:", StringComparison.Ordinal))
        {
            _hasCompileErrors = true;
        }

        // "failures:" toggles section state.
        if (line == "failures:")
        {
            if (_inFailureSection)
            {
                // Second "failures:" = list of failure names — skip them.
                _inFailureNames = true;
            }

            _inFailureSection = true;
            return true;
        }

        // Skip the failure name listing section.
        if (_inFailureNames)
        {
            if (line.StartsWith("test result:", StringComparison.Ordinal))
            {
                _inFailureNames = false;
                _inFailureSection = false;
                _summaryLines.Add(line);
                return true;
            }

            return true;
        }

        if (line.StartsWith("test result:", StringComparison.Ordinal))
        {
            _summaryLines.Add(line);
            _inFailureSection = false;
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsBlockStart(string line) => _inFailureSection && line.StartsWith("---- ", StringComparison.Ordinal);

    /// <inheritdoc />
    public bool IsBlockContinuation(string line, IReadOnlyList<string> block) =>
        _inFailureSection && !line.StartsWith("---- ", StringComparison.Ordinal);

    /// <inheritdoc />
    public string? FormatSummary(int exitCode, string raw)
    {
        if (_summaryLines.Count == 0 && _hasCompileErrors)
        {
            var buildFiltered = CargoBuildTestFilters.FilterCargoBuild(raw);
            if (buildFiltered.StartsWith("cargo build:", StringComparison.Ordinal))
            {
                return ReplaceFirst(buildFiltered, "cargo build:", "cargo test:") + "\n";
            }

            // Fallback: last 5 meaningful lines.
            var meaningful = ReadCommand.SplitLines(raw)
                .Where(l => l.Trim().Length != 0 && !l.TrimStart().StartsWith("Compiling", StringComparison.Ordinal))
                .ToList();
            var last5 = meaningful.Skip(Math.Max(0, meaningful.Count - 5));
            return string.Join('\n', last5) + "\n";
        }

        // No failures emitted — aggregate pass results.
        AggregatedTestResult? aggregated = null;
        var allParsed = true;

        foreach (var line in _summaryLines)
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
            return aggregated.FormatCompact() + "\n";
        }

        // Fallback: show raw summary lines.
        if (_summaryLines.Count > 0)
        {
            return string.Join("", _summaryLines.Select(l => l + "\n"));
        }

        return null;
    }

    internal static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + search.Length));
    }
}

/// <summary>
/// Aggregated pass counts across one or more <c>test result: ok. ...</c> summary lines, for compact
/// display. Faithful port of Rust's <c>AggregatedTestResult</c> (<c>src/cmds/rust/cargo_cmd.rs</c>
/// :826-922).
/// </summary>
internal sealed class AggregatedTestResult
{
    private static readonly Regex TestResultRegex = new(
        @"test result: (\w+)\.\s+(\d+) passed;\s+(\d+) failed;\s+(\d+) ignored;\s+(\d+) measured;\s+(\d+) filtered out(?:;\s+finished in ([\d.]+)s)?",
        RegexOptions.Compiled);

    public int Passed { get; private set; }

    public int Failed { get; private set; }

    public int Ignored { get; private set; }

    public int Measured { get; private set; }

    public int FilteredOut { get; private set; }

    public int Suites { get; private set; }

    public double DurationSecs { get; private set; }

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

/// <summary>
/// Buffered (non-streaming) equivalents of <see cref="CargoBuildHandler"/>/<see cref="CargoTestHandler"/>,
/// used directly by unit tests (mirroring Rust's own <c>#[cfg(test)]</c> suite, which exercises these
/// pure functions) and internally by <see cref="CargoTestHandler.FormatSummary"/>'s compile-error
/// fallback. Faithful port of Rust's <c>filter_cargo_build</c> (<c>cargo_cmd.rs</c>:760-824) and
/// <c>filter_cargo_test</c> (<c>cargo_cmd.rs</c>:924-1057).
/// </summary>
internal static class CargoBuildTestFilters
{
    // Rust CAP_ERRORS / CAP_WARNINGS from src/core/truncate.rs.
    private const int CapErrors = 20;
    private const int CapWarnings = 10;

    /// <summary>Buffered equivalent of <see cref="CargoBuildHandler"/>, driving the same block-grouping
    /// logic over a fully-captured string instead of a live stream.</summary>
    /// <param name="output">The raw <c>cargo build</c>/<c>cargo check</c> output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterCargoBuild(string output)
    {
        var handler = new CargoBuildHandler();
        var blocks = new List<List<string>>();
        var currentBlock = new List<string>();
        var inBlock = false;

        foreach (var line in ReadCommand.SplitLines(output))
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

    /// <summary>Buffered equivalent of <see cref="CargoTestHandler"/>, driving the same failure-block
    /// grouping and pass-aggregation logic over a fully-captured string.</summary>
    /// <param name="output">The raw <c>cargo test</c> output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterCargoTest(string output)
    {
        var failures = new List<string>();
        var summaryLines = new List<string>();
        var inFailureSection = false;
        var currentFailure = new List<string>();

        foreach (var line in ReadCommand.SplitLines(output))
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
            var hasCompileErrors = ReadCommand.SplitLines(output).Any(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith("error[", StringComparison.Ordinal) || trimmed.StartsWith("error:", StringComparison.Ordinal);
            });

            if (hasCompileErrors)
            {
                var buildFiltered = FilterCargoBuild(output);
                if (buildFiltered.StartsWith("cargo build:", StringComparison.Ordinal))
                {
                    return CargoTestHandler.ReplaceFirst(buildFiltered, "cargo build:", "cargo test:");
                }
            }

            // Fallback: show last meaningful lines.
            var meaningful = ReadCommand.SplitLines(output)
                .Where(l => l.Trim().Length != 0 && !l.TrimStart().StartsWith("Compiling", StringComparison.Ordinal))
                .ToList();
            foreach (var line in meaningful.Skip(Math.Max(0, meaningful.Count - 5)))
            {
                result.Append(line).Append('\n');
            }
        }

        return result.ToString().Trim();
    }
}
