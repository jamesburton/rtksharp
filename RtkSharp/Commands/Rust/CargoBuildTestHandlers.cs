using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Filters.Commands.Rust;

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
/// <remarks>
/// <b>Delegates line-classification to <see cref="CargoBuildLineClassifier"/>.</b> That pure
/// state machine lives in <c>RtkSharp.Filters</c> so it can be shared with
/// <see cref="CargoFilters.FilterCargoBuild"/>'s buffered equivalent without duplicating the logic;
/// this class wraps an instance of it purely to satisfy the streaming <see cref="IBlockHandler"/>
/// interface, which (being execution-pipeline plumbing, not pure filtering) stays in
/// <c>RtkSharp.Core</c>.
/// </remarks>
internal sealed class CargoBuildHandler : IBlockHandler
{
    private readonly CargoBuildLineClassifier _classifier = new();

    /// <summary>The number of <c>Compiling</c>/<c>Checking</c> lines observed.</summary>
    public int Compiled => _classifier.Compiled;

    /// <summary>The number of warning blocks observed.</summary>
    public int Warnings => _classifier.Warnings;

    /// <summary>The number of error blocks observed.</summary>
    public int ErrorCount => _classifier.ErrorCount;

    /// <summary>The trailing <c>Finished ...</c> line, if one was seen.</summary>
    public string? FinishedLine => _classifier.FinishedLine;

    /// <inheritdoc />
    public bool ShouldSkip(string line) => _classifier.ShouldSkip(line);

    /// <inheritdoc />
    public bool IsBlockStart(string line) => _classifier.IsBlockStart(line);

    /// <inheritdoc />
    public bool IsBlockContinuation(string line, IReadOnlyList<string> block) =>
        _classifier.IsBlockContinuation(line, block);

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
            var buildFiltered = CargoFilters.FilterCargoBuild(raw);
            if (buildFiltered.StartsWith("cargo build:", StringComparison.Ordinal))
            {
                return CargoFilters.ReplaceFirst(buildFiltered, "cargo build:", "cargo test:") + "\n";
            }

            // Fallback: last 5 meaningful lines.
            var meaningful = RtkSharp.Core.SourceFilterLineSplitter.SplitLines(raw)
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
}
