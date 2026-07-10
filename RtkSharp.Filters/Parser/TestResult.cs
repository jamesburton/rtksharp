namespace RtkSharp.Parser;

/// <summary>
/// Canonical test-execution result (vitest, playwright, jest, etc.), independent of any single
/// tool's native JSON shape. Faithful port of Rust <c>TestResult</c> (<c>src/parser/types.rs:5-14</c>),
/// including its <c>TokenFormatter</c> implementation (<c>src/parser/formatter.rs:49-120</c>).
/// This same type is consumed identically by both the vitest/jest parser (Task 5) and the
/// playwright parser (Task 6) — two separate <see cref="OutputParser{T}"/> implementations feeding
/// this one shared formatter.
/// </summary>
public sealed class TestResult : ITokenFormatter
{
    /// <summary>Maximum failures individually rendered in <see cref="FormatCompact"/> before an overflow marker.</summary>
    private const int MaxCompactFailures = 5;

    /// <summary>Maximum stack-trace lines shown per failure in <see cref="FormatVerbose"/>.</summary>
    private const int MaxVerboseStackLines = 3;

    /// <summary>Total number of tests (passed + failed + skipped, though callers are responsible for keeping this consistent).</summary>
    public required int Total { get; init; }

    /// <summary>Number of tests that passed.</summary>
    public required int Passed { get; init; }

    /// <summary>Number of tests that failed.</summary>
    public required int Failed { get; init; }

    /// <summary>Number of tests skipped or pending (e.g. <c>test.skip</c>, <c>it.skip</c>, <c>xfail</c>).</summary>
    public required int Skipped { get; init; }

    /// <summary>Total run duration in milliseconds, if the tool reported one.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Per-test failure details.</summary>
    public IReadOnlyList<TestFailure> Failures { get; init; } = [];

    /// <summary>
    /// Compact rendering: a one-line PASS/FAIL (and, when nonzero, skipped) summary, followed by up
    /// to <see cref="MaxCompactFailures"/> failures with their full (unclipped) error message, an
    /// overflow marker if more remain, and a trailing duration line if known.
    /// </summary>
    /// <remarks>
    /// Always surfaces skipped/pending tests — hiding them lets coverage gaps (<c>test.skip</c> /
    /// <c>it.skip</c> / <c>xfail</c>) accumulate invisibly. Deliberately does not clip
    /// <see cref="TestFailure.ErrorMessage"/> to a fixed number of lines: playwright errors contain
    /// the expected/received diff and call log starting at line 3+, and truncating would leave the
    /// agent with no debug info (matching Rust's corresponding RED tests, ported below in
    /// <c>RtkSharp.Tests</c>).
    /// </remarks>
    /// <returns>The compact rendering.</returns>
    public string FormatCompact()
    {
        var summary = $"PASS ({Passed}) FAIL ({Failed})";
        if (Skipped > 0)
        {
            summary += $" skipped ({Skipped})";
        }

        var lines = new List<string> { summary };

        if (Failures.Count > 0)
        {
            lines.Add(string.Empty);
            var shown = Failures.Take(MaxCompactFailures).ToList();
            for (var idx = 0; idx < shown.Count; idx++)
            {
                var failure = shown[idx];
                lines.Add($"{idx + 1}. {failure.TestName}");
                foreach (var line in SplitLines(failure.ErrorMessage))
                {
                    lines.Add($"   {line}");
                }
            }

            if (Failures.Count > MaxCompactFailures)
            {
                lines.Add($"\n... +{Failures.Count - MaxCompactFailures} more failures");
            }
        }

        if (DurationMs is { } duration)
        {
            lines.Add($"\nTime: {duration}ms");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Verbose rendering: full pass/fail/skipped/total summary, then every failure with its file
    /// path, full error message, and up to <see cref="MaxVerboseStackLines"/> stack-trace lines,
    /// followed by a trailing duration line if known.
    /// </summary>
    /// <returns>The verbose rendering.</returns>
    public string FormatVerbose()
    {
        var lines = new List<string>
        {
            $"Tests: {Passed} passed, {Failed} failed, {Skipped} skipped (total: {Total})",
        };

        if (Failures.Count > 0)
        {
            lines.Add("\nFailures:");
            for (var idx = 0; idx < Failures.Count; idx++)
            {
                var failure = Failures[idx];
                lines.Add($"\n{idx + 1}. {failure.TestName} ({failure.FilePath})");
                lines.Add($"   {failure.ErrorMessage}");
                if (failure.StackTrace is { } stack)
                {
                    var preview = string.Join("\n   ", SplitLines(stack).Take(MaxVerboseStackLines));
                    lines.Add($"   {preview}");
                }
            }
        }

        if (DurationMs is { } duration)
        {
            lines.Add($"\nDuration: {duration}ms");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Ultra-compact rendering: <c>[ok]N [x]N [skip]N (Nms)</c>, with an unknown duration rendered as <c>0</c>.
    /// </summary>
    /// <returns>The ultra rendering.</returns>
    public string FormatUltra() => $"[ok]{Passed} [x]{Failed} [skip]{Skipped} ({DurationMs ?? 0}ms)";

    /// <summary>
    /// Splits text into lines the same way Rust's <c>str::lines()</c> does: split on <c>\n</c>, with
    /// any trailing <c>\r</c> stripped from each line.
    /// </summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            yield return line.EndsWith('\r') ? line[..^1] : line;
        }
    }
}

/// <summary>
/// A single test failure's details. Faithful port of Rust <c>TestFailure</c>
/// (<c>src/parser/types.rs:16-22</c>).
/// </summary>
public sealed class TestFailure
{
    /// <summary>The failing test's name/description.</summary>
    public required string TestName { get; init; }

    /// <summary>The file path the failing test lives in.</summary>
    public required string FilePath { get; init; }

    /// <summary>The failure's error message (assertion diff, thrown message, etc.).</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>The failure's stack trace, if the tool reported one.</summary>
    public string? StackTrace { get; init; }
}

/// <summary>
/// Output verbosity mode for <see cref="ITokenFormatter.Format"/>. Faithful port of Rust
/// <c>FormatMode</c> (<c>src/parser/formatter.rs:7-26</c>).
/// </summary>
public enum FormatMode
{
    /// <summary>Ultra-compact: summary only (default).</summary>
    Compact,

    /// <summary>Verbose: include full details.</summary>
    Verbose,

    /// <summary>Ultra-compressed: symbols and abbreviations.</summary>
    Ultra,
}

/// <summary>
/// Extension helpers for <see cref="FormatMode"/>.
/// </summary>
public static class FormatModeExtensions
{
    /// <summary>
    /// Maps a numeric verbosity level (as threaded through RTK's CLI <c>-v</c>/<c>-vv</c> flags) to
    /// a <see cref="FormatMode"/>. Faithful port of Rust <c>FormatMode::from_verbosity</c>
    /// (<c>src/parser/formatter.rs:19-25</c>): <c>0</c> is <see cref="FormatMode.Compact"/>, <c>1</c>
    /// is <see cref="FormatMode.Verbose"/>, and anything else (2+) is <see cref="FormatMode.Ultra"/>.
    /// </summary>
    /// <param name="verbosity">The numeric verbosity level.</param>
    /// <returns>The corresponding <see cref="FormatMode"/>.</returns>
    public static FormatMode FromVerbosity(byte verbosity) => verbosity switch
    {
        0 => FormatMode.Compact,
        1 => FormatMode.Verbose,
        _ => FormatMode.Ultra,
    };
}

/// <summary>
/// Token-efficient formatting contract for canonical parser types. Faithful port of Rust
/// <c>TokenFormatter</c> trait (<c>src/parser/formatter.rs:29-47</c>): three per-mode renderers plus
/// a default <see cref="Format"/> dispatcher. Implemented identically by <see cref="TestResult"/>
/// (consumed by both the vitest/jest parser and the playwright parser — Tasks 5 and 6) and by
/// <see cref="DependencyState"/> (consumed by pnpm's <c>outdated</c> parser — Task 3).
/// </summary>
public interface ITokenFormatter
{
    /// <summary>Formats as a compact summary (default mode).</summary>
    /// <returns>The compact rendering.</returns>
    string FormatCompact();

    /// <summary>Formats with full details (verbose mode).</summary>
    /// <returns>The verbose rendering.</returns>
    string FormatVerbose();

    /// <summary>Formats with symbols/abbreviations (ultra-compressed mode).</summary>
    /// <returns>The ultra rendering.</returns>
    string FormatUltra();

    /// <summary>
    /// Formats according to <paramref name="mode"/>, dispatching to <see cref="FormatCompact"/>,
    /// <see cref="FormatVerbose"/>, or <see cref="FormatUltra"/>. Faithful port of Rust
    /// <c>TokenFormatter::format</c> (<c>src/parser/formatter.rs:40-46</c>).
    /// </summary>
    /// <param name="mode">The verbosity mode to render.</param>
    /// <returns>The rendered text.</returns>
    string Format(FormatMode mode) => mode switch
    {
        FormatMode.Compact => FormatCompact(),
        FormatMode.Verbose => FormatVerbose(),
        FormatMode.Ultra => FormatUltra(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown format mode."),
    };
}
