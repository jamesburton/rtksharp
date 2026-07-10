using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Ruby;

/// <summary>
/// Buffered filter for <c>rtk rake</c>: parses standard Minitest output produced by both
/// <c>rake test</c> and <c>rails test</c>, filtering down to failures/errors and the summary line.
/// Faithful port of <c>filter_minitest_output</c> and its helpers (<c>src/cmds/ruby/rake_cmd.rs</c>).
/// </summary>
public static class RakeFilters
{
    // Rust CAP_WARNINGS from src/core/truncate.rs.
    private const int MaxRakeFailures = 10;

    private static readonly Regex FailureHeaderRegex = new(@"^\d+\)\s+(Failure|Error):$", RegexOptions.Compiled);

    /// <summary>
    /// Parses Minitest output (from both <c>rake test</c> and <c>rails test</c>, plus
    /// minitest-reporters' alternate framing) using a state machine, filtering down to
    /// failures/errors and the summary line. Faithful port of Rust <c>filter_minitest_output</c>
    /// (<c>rake_cmd.rs</c>:104-163).
    /// </summary>
    /// <param name="output">The raw, unfiltered command output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterMinitestOutput(string output)
    {
        var clean = Utils.StripAnsi(output);

        var state = ParseState.Header;
        var failures = new List<string>();
        var currentFailure = new List<string>();
        var summaryLine = string.Empty;

        foreach (var line in SourceFilterLineSplitter.SplitLines(clean))
        {
            var trimmed = line.Trim();

            // Detect summary line anywhere (it's always the last meaningful line). Handles both
            // "N runs, N assertions, ..." and "N tests, N assertions, ..." (minitest-reporters).
            if ((trimmed.Contains(" runs,", StringComparison.Ordinal) || trimmed.Contains(" tests,", StringComparison.Ordinal))
                && trimmed.Contains(" assertions,", StringComparison.Ordinal))
            {
                summaryLine = trimmed;
                continue;
            }

            // State transitions -- handle both standard Minitest and minitest-reporters.
            if (trimmed == "# Running:" || trimmed.StartsWith("Started with run options", StringComparison.Ordinal))
            {
                state = ParseState.Running;
                continue;
            }

            if (trimmed.StartsWith("Finished in ", StringComparison.Ordinal))
            {
                state = ParseState.Failures;
                continue;
            }

            switch (state)
            {
                case ParseState.Header:
                case ParseState.Running:
                    // Skip seed line, blank lines, progress dots.
                    continue;
                case ParseState.Failures:
                    if (IsFailureHeader(trimmed))
                    {
                        if (currentFailure.Count > 0)
                        {
                            failures.Add(string.Join('\n', currentFailure));
                            currentFailure.Clear();
                        }

                        currentFailure.Add(trimmed);
                    }
                    else if (trimmed.Length == 0 && currentFailure.Count > 0)
                    {
                        failures.Add(string.Join('\n', currentFailure));
                        currentFailure.Clear();
                    }
                    else if (trimmed.Length != 0)
                    {
                        currentFailure.Add(line);
                    }

                    break;
            }
        }

        // Save last failure if any.
        if (currentFailure.Count > 0)
        {
            failures.Add(string.Join('\n', currentFailure));
        }

        return BuildMinitestSummary(summaryLine, failures);
    }

    private static bool IsFailureHeader(string line) => FailureHeaderRegex.IsMatch(line);

    private static string BuildMinitestSummary(string summary, IReadOnlyList<string> failures)
    {
        var (runs, _, failCount, errorCount, skips) = ParseMinitestSummary(summary);

        if (runs == 0 && summary.Length == 0)
        {
            return "rake test: no tests ran";
        }

        if (failCount == 0 && errorCount == 0)
        {
            var msg = $"ok rake test: {runs} runs, 0 failures";
            if (skips > 0)
            {
                msg += $", {skips} skips";
            }

            return msg;
        }

        var result = new StringBuilder();
        result.Append($"rake test: {runs} runs, {failCount} failures, {errorCount} errors");
        if (skips > 0)
        {
            result.Append($", {skips} skips");
        }

        result.Append('\n');

        if (failures.Count == 0)
        {
            return result.ToString().Trim();
        }

        result.Append('\n');

        var shown = Math.Min(failures.Count, MaxRakeFailures);
        for (var i = 0; i < shown; i++)
        {
            var lines = failures[i].Split('\n');

            // First line is like "  1) Failure:" or "  1) Error:".
            if (lines.Length > 0)
            {
                result.Append($"{i + 1}. {lines[0].Trim()}\n");
            }

            // Remaining lines contain test name, file:line, assertion message.
            foreach (var line in lines.Skip(1).Take(4))
            {
                var trimmed = line.Trim();
                if (trimmed.Length != 0)
                {
                    result.Append($"   {Utils.Truncate(trimmed, 120)}\n");
                }
            }

            if (i < shown - 1)
            {
                result.Append('\n');
            }
        }

        if (failures.Count > MaxRakeFailures)
        {
            result.Append($"\n... +{failures.Count - MaxRakeFailures} more failures\n");
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Parses a Minitest summary line like <c>"8 runs, 9 assertions, 0 failures, 0 errors, 0
    /// skips"</c> (or the minitest-reporters <c>"tests"</c> variant). Faithful port of Rust
    /// <c>parse_minitest_summary</c> (<c>rake_cmd.rs</c>:235-260).
    /// </summary>
    /// <param name="summary">The summary line.</param>
    /// <returns>The parsed (runs, assertions, failures, errors, skips) tuple.</returns>
    internal static (int Runs, int Assertions, int Failures, int Errors, int Skips) ParseMinitestSummary(string summary)
    {
        var runs = 0;
        var assertions = 0;
        var failures = 0;
        var errors = 0;
        var skips = 0;

        foreach (var rawPart in summary.Split(','))
        {
            var part = rawPart.Trim();
            var words = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && int.TryParse(words[0], out var n))
            {
                switch (words[1].TrimEnd(','))
                {
                    case "runs" or "run" or "tests" or "test":
                        runs = n;
                        break;
                    case "assertions" or "assertion":
                        assertions = n;
                        break;
                    case "failures" or "failure":
                        failures = n;
                        break;
                    case "errors" or "error":
                        errors = n;
                        break;
                    case "skips" or "skip":
                        skips = n;
                        break;
                }
            }
        }

        return (runs, assertions, failures, errors, skips);
    }

    private enum ParseState
    {
        Header,
        Running,
        Failures,
    }
}
