using System.Text;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure filtering logic for the <c>rtk test</c> CLI verb: extracts a compact pass/fail summary
/// from an arbitrary test runner's combined stdout+stderr. Ported from Rust
/// <c>runner::extract_test_summary</c> (<c>src/cmds/rust/runner.rs</c>:181-280). The
/// process-execution, output-capping, and tracking logic lives in
/// <see cref="RtkSharp.Commands.System.TestCommand"/> (<c>RtkSharp</c>).
/// </summary>
public static class TestFilters
{
    // Rust `CAP_WARNINGS` (src/core/truncate.rs:7) — the max number of failure lines shown before
    // an "+N more failures" marker.
    private const int MaxRunnerFailures = 10;

    // Rust `CAP_LIST` (src/core/truncate.rs:9) — the max number of indented cargo failure-detail
    // lines shown before an "+N more" marker.
    private const int MaxRunnerLines = 20;

    /// <summary>
    /// Detects the test-runner ecosystem by substring-matching <paramref name="command"/> itself
    /// (not the output structure), extracts a per-ecosystem summary and failure-line list from
    /// <paramref name="output"/>, and formats them, capped at <see cref="MaxRunnerFailures"/>/
    /// <see cref="MaxRunnerLines"/>. Faithful port of Rust <c>extract_test_summary</c>
    /// (<c>src/cmds/rust/runner.rs</c>:181-280).
    /// </summary>
    /// <param name="output">The combined raw stdout+stderr captured from the test command.</param>
    /// <param name="command">The full command string (used only for ecosystem detection).</param>
    /// <returns>The formatted summary (failures block, if any, then summary/fallback block).</returns>
    public static string ExtractTestSummary(string output, string command)
    {
        var lines = ReadFilters.SplitLines(output);

        var isCargo = command.Contains("cargo test", StringComparison.Ordinal);
        var isPytest = command.Contains("pytest", StringComparison.Ordinal);
        var isJest = command.Contains("jest", StringComparison.Ordinal)
            || command.Contains("npm test", StringComparison.Ordinal)
            || command.Contains("yarn test", StringComparison.Ordinal);
        var isGo = command.Contains("go test", StringComparison.Ordinal);

        var summary = new List<string>();
        var failures = new List<string>();
        var inFailure = false;
        var failureLines = new List<string>();

        foreach (var line in lines)
        {
            if (isCargo)
            {
                if (line.Contains("test result:", StringComparison.Ordinal))
                {
                    summary.Add(line);
                }

                if (line.Contains("FAILED", StringComparison.Ordinal) &&
                    !line.Contains("test result", StringComparison.Ordinal))
                {
                    failures.Add(line);
                }

                if (line.StartsWith("failures:", StringComparison.Ordinal))
                {
                    inFailure = true;
                }

                if (inFailure && line.StartsWith("    ", StringComparison.Ordinal))
                {
                    failureLines.Add(line);
                }
            }

            if (isPytest)
            {
                if (line.Contains(" passed", StringComparison.Ordinal) ||
                    line.Contains(" failed", StringComparison.Ordinal) ||
                    line.Contains(" error", StringComparison.Ordinal))
                {
                    summary.Add(line);
                }

                if (line.Contains("FAILED", StringComparison.Ordinal))
                {
                    failures.Add(line);
                }
            }

            if (isJest)
            {
                if (line.Contains("Tests:", StringComparison.Ordinal) ||
                    line.Contains("Test Suites:", StringComparison.Ordinal))
                {
                    summary.Add(line);
                }

                if (line.Contains('✕') || line.Contains("FAIL", StringComparison.Ordinal))
                {
                    failures.Add(line);
                }
            }

            if (isGo)
            {
                if (line.StartsWith("ok", StringComparison.Ordinal) ||
                    line.StartsWith("FAIL", StringComparison.Ordinal) ||
                    line.StartsWith("---", StringComparison.Ordinal))
                {
                    summary.Add(line);
                }

                if (line.Contains("FAIL", StringComparison.Ordinal))
                {
                    failures.Add(line);
                }
            }
        }

        var sb = new StringBuilder();

        if (failures.Count > 0)
        {
            sb.Append("[FAIL] FAILURES:\n");
            foreach (var f in failures.Take(MaxRunnerFailures))
            {
                sb.Append("  ").Append(f).Append('\n');
            }

            if (failures.Count > MaxRunnerFailures)
            {
                sb.Append("  ... +").Append(failures.Count - MaxRunnerFailures).Append(" more failures\n");
            }

            foreach (var f in failureLines.Take(MaxRunnerLines))
            {
                sb.Append("  ").Append(f.Trim()).Append('\n');
            }

            if (failureLines.Count > MaxRunnerLines)
            {
                sb.Append("  ... +").Append(failureLines.Count - MaxRunnerLines).Append(" more\n");
            }

            sb.Append('\n');
        }

        if (summary.Count > 0)
        {
            sb.Append("SUMMARY:\n");
            foreach (var r in summary)
            {
                sb.Append("  ").Append(r).Append('\n');
            }
        }
        else
        {
            sb.Append("OUTPUT (last 5 lines):\n");
            var start = Math.Max(0, lines.Count - 5);
            for (var i = start; i < lines.Count; i++)
            {
                if (lines[i].Trim().Length > 0)
                {
                    sb.Append("  ").Append(lines[i]).Append('\n');
                }
            }
        }

        return sb.ToString();
    }
}
