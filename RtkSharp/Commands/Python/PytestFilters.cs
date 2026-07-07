using System.Text;
using RtkSharp.Commands.System;
using RtkSharp.Core;

namespace RtkSharp.Commands.Python;

/// <summary>
/// Buffered filter for pytest output: state-machine parser that extracts the summary line,
/// failure blocks, and xfail/xpass short-summary entries, then renders a compact report.
/// Faithful port of <c>filter_pytest_output</c>/<c>build_pytest_summary</c>/<c>parse_summary_line</c>
/// (<c>src/cmds/python/pytest_cmd.rs</c>).
/// </summary>
internal static class PytestFilters
{
    // Rust CAP_WARNINGS from src/core/truncate.rs, reused for both MAX_XFAIL and MAX_PYTEST_FAILURES.
    private const int MaxXfail = 10;
    private const int MaxPytestFailures = 10;

    private enum ParseState
    {
        Header,
        TestProgress,
        Failures,
        Summary,
    }

    /// <summary>
    /// Parses raw pytest stdout into a compact pass/fail/skip/xfail/xpass summary with a capped list
    /// of failures and expected-failure outcomes. Faithful port of Rust <c>filter_pytest_output</c>
    /// (<c>pytest_cmd.rs</c>:63-157).
    /// </summary>
    /// <param name="output">The raw pytest stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterPytestOutput(string output)
    {
        var state = ParseState.Header;
        var failures = new List<string>();
        var currentFailure = new List<string>();
        var xfailLines = new List<string>();
        var summaryLine = "";

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("===", StringComparison.Ordinal) && trimmed.Contains("test session starts", StringComparison.Ordinal))
            {
                state = ParseState.Header;
                continue;
            }

            if (trimmed.StartsWith("===", StringComparison.Ordinal) && trimmed.Contains("FAILURES", StringComparison.Ordinal))
            {
                state = ParseState.Failures;
                continue;
            }

            if (trimmed.StartsWith("===", StringComparison.Ordinal) && trimmed.Contains("short test summary", StringComparison.Ordinal))
            {
                state = ParseState.Summary;
                if (currentFailure.Count > 0)
                {
                    failures.Add(string.Join('\n', currentFailure));
                    currentFailure.Clear();
                }

                continue;
            }

            if (trimmed.StartsWith("===", StringComparison.Ordinal)
                && (trimmed.Contains("passed", StringComparison.Ordinal)
                    || trimmed.Contains("failed", StringComparison.Ordinal)
                    || trimmed.Contains("skipped", StringComparison.Ordinal)))
            {
                summaryLine = trimmed;
                continue;
            }

            // quiet mode (-q): bare summary without === wrapper, e.g. "5 failed, 1698 passed, 2 skipped in 108.89s"
            if (summaryLine.Length == 0
                && !trimmed.StartsWith("===", StringComparison.Ordinal)
                && !trimmed.StartsWith("FAILED", StringComparison.Ordinal)
                && !trimmed.StartsWith("ERROR", StringComparison.Ordinal)
                && (trimmed.Contains(" passed", StringComparison.Ordinal)
                    || trimmed.Contains(" failed", StringComparison.Ordinal)
                    || trimmed.Contains(" skipped", StringComparison.Ordinal))
                && trimmed.Contains(" in ", StringComparison.Ordinal))
            {
                summaryLine = trimmed;
                continue;
            }

            switch (state)
            {
                case ParseState.Header:
                    if (trimmed.StartsWith("collected", StringComparison.Ordinal))
                    {
                        state = ParseState.TestProgress;
                    }

                    break;

                case ParseState.TestProgress:
                    // Lines like "tests/test_foo.py ....  [ 40%]" — test_files is parsed but unused by
                    // build_pytest_summary (Rust's `_test_files` parameter), kept here only to mirror
                    // the state machine's control flow faithfully.
                    break;

                case ParseState.Failures:
                    if (trimmed.StartsWith("___", StringComparison.Ordinal))
                    {
                        if (currentFailure.Count > 0)
                        {
                            failures.Add(string.Join('\n', currentFailure));
                            currentFailure.Clear();
                        }

                        currentFailure.Add(trimmed);
                    }
                    else if (trimmed.Length != 0 && !trimmed.StartsWith("===", StringComparison.Ordinal))
                    {
                        currentFailure.Add(trimmed);
                    }

                    break;

                case ParseState.Summary:
                    if (trimmed.StartsWith("FAILED", StringComparison.Ordinal) || trimmed.StartsWith("ERROR", StringComparison.Ordinal))
                    {
                        failures.Add(trimmed);
                    }
                    else if (trimmed.StartsWith("XFAIL", StringComparison.Ordinal) || trimmed.StartsWith("XPASS", StringComparison.Ordinal))
                    {
                        xfailLines.Add(trimmed);
                    }

                    break;
            }
        }

        if (currentFailure.Count > 0)
        {
            failures.Add(string.Join('\n', currentFailure));
        }

        return BuildPytestSummary(summaryLine, failures, xfailLines);
    }

    /// <summary>The per-outcome counts parsed from a pytest summary line. Exposed <c>internal</c> (rather than
    /// private, as Rust's own module-private <c>PytestCounts</c> effectively is to its same-module
    /// <c>#[cfg(test)]</c> block) so <c>ParseSummaryLine</c> can be unit tested directly.</summary>
    internal readonly record struct PytestCounts(int Passed, int Failed, int Skipped, int Xfailed, int Xpassed);

    private static string BuildPytestSummary(string summary, IReadOnlyList<string> failures, IReadOnlyList<string> xfailLines)
    {
        var counts = ParseSummaryLine(summary);
        var (passed, failed, skipped, xfailed, xpassed) = counts;

        if (passed == 0 && failed == 0 && skipped == 0 && xfailed == 0 && xpassed == 0)
        {
            return "Pytest: No tests collected";
        }

        var extrasPresent = skipped > 0 || xfailed > 0 || xpassed > 0 || xfailLines.Count > 0;

        if (failed == 0 && passed > 0 && !extrasPresent)
        {
            return $"Pytest: {passed} passed";
        }

        var result = new StringBuilder();
        result.Append($"Pytest: {passed} passed, {failed} failed");
        if (skipped > 0)
        {
            result.Append($", {skipped} skipped");
        }

        if (xfailed > 0)
        {
            result.Append($", {xfailed} xfailed");
        }

        if (xpassed > 0)
        {
            result.Append($", {xpassed} xpassed");
        }

        result.Append('\n');

        // Surface xfail/xpass entries (with their reasons) — XPASS in particular signals that
        // something expected-to-fail now passes.
        if (xfailLines.Count > 0)
        {
            result.Append("\nExpected-failure outcomes:\n");
            foreach (var line in xfailLines.Take(MaxXfail))
            {
                result.Append($"  {Utils.Truncate(line, 120)}\n");
            }

            if (xfailLines.Count > MaxXfail)
            {
                result.Append($"  … +{xfailLines.Count - MaxXfail} more\n");
                var allXfail = string.Join('\n', xfailLines);
                if (Tee.ForceTeeTailHint(allXfail, "pytest-xfail", MaxXfail + 1) is { } hint)
                {
                    result.Append($"  {hint}\n");
                }
            }
        }

        if (failures.Count == 0)
        {
            return result.ToString().Trim();
        }

        // Show failures (limit to key information)
        result.Append("\nFailures:\n");

        var takeCount = Math.Min(failures.Count, MaxPytestFailures);
        for (var i = 0; i < takeCount; i++)
        {
            var failure = failures[i];
            var lines = ReadCommand.SplitLines(failure).ToList();
            var handledAsFailedSummary = false;

            if (lines.Count > 0)
            {
                var firstLine = lines[0];
                if (firstLine.StartsWith("___", StringComparison.Ordinal))
                {
                    var testName = firstLine.Trim('_').Trim();
                    result.Append($"{i + 1}. [FAIL] {testName}\n");
                }
                else if (firstLine.StartsWith("FAILED", StringComparison.Ordinal))
                {
                    var parts = firstLine.Split(" - ");
                    if (parts.Length > 0)
                    {
                        var testName = parts[0].StartsWith("FAILED ", StringComparison.Ordinal)
                            ? parts[0]["FAILED ".Length..]
                            : parts[0];
                        result.Append($"{i + 1}. [FAIL] {testName}\n");
                    }

                    if (parts.Length > 1)
                    {
                        result.Append($"     {Utils.Truncate(parts[1], 100)}\n");
                    }

                    // Rust's `continue` here skips the relevant-lines scan AND the trailing
                    // blank-line separator for this entry entirely.
                    handledAsFailedSummary = true;
                }
            }

            if (handledAsFailedSummary)
            {
                continue;
            }

            var relevantLines = 0;
            for (var lineIdx = 1; lineIdx < lines.Count && relevantLines < 3; lineIdx++)
            {
                var line = lines[lineIdx];
                var lineLower = line.ToLowerInvariant();
                var isRelevant = line.Trim().StartsWith('>')
                    || line.Trim().StartsWith('E')
                    || lineLower.Contains("assert", StringComparison.Ordinal)
                    || lineLower.Contains("error", StringComparison.Ordinal)
                    || line.Contains(".py:", StringComparison.Ordinal);

                if (isRelevant)
                {
                    result.Append($"     {Utils.Truncate(line, 100)}\n");
                    relevantLines++;
                }
            }

            if (i < failures.Count - 1)
            {
                result.Append('\n');
            }
        }

        if (failures.Count > MaxPytestFailures)
        {
            result.Append($"\n… +{failures.Count - MaxPytestFailures} more failures\n");
            var allFailures = string.Join("\n\n", failures);
            if (Tee.ForceTeeHint(allFailures, "pytest-failures") is { } hint)
            {
                result.Append($"  {hint}\n");
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Parses a pytest summary line (e.g. <c>"=== 4 passed, 1 failed, 2 xfailed, 1 xpassed in
    /// 0.50s ==="</c>) into per-outcome counts. Faithful port of Rust <c>parse_summary_line</c>
    /// (<c>pytest_cmd.rs</c>:288-317) — order matters: "xpassed"/"xfailed" are checked before
    /// "passed"/"failed" since the former contain the latter as substrings.
    /// </summary>
    /// <param name="summary">The pytest summary line to parse.</param>
    /// <returns>The parsed per-outcome counts.</returns>
    internal static PytestCounts ParseSummaryLine(string summary)
    {
        int passed = 0, failed = 0, skipped = 0, xfailed = 0, xpassed = 0;

        foreach (var part in summary.Split(','))
        {
            var words = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < words.Length; i++)
            {
                if (!int.TryParse(words[i - 1], out var n))
                {
                    continue;
                }

                var word = words[i];
                if (word.Contains("xpassed", StringComparison.Ordinal))
                {
                    xpassed = n;
                }
                else if (word.Contains("xfailed", StringComparison.Ordinal))
                {
                    xfailed = n;
                }
                else if (word.Contains("passed", StringComparison.Ordinal))
                {
                    passed = n;
                }
                else if (word.Contains("failed", StringComparison.Ordinal))
                {
                    failed = n;
                }
                else if (word.Contains("skipped", StringComparison.Ordinal))
                {
                    skipped = n;
                }
            }
        }

        return new PytestCounts(passed, failed, skipped, xfailed, xpassed);
    }
}
