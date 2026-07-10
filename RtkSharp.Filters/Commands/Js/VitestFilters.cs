using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RtkSharp.Core;
using RtkSharp.Parser;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic shared by the <c>rtk vitest</c>/<c>rtk jest</c> CLI proxy. Extracted
/// from <c>RtkSharp.Commands.Js.VitestCommand</c> (Task 7 of the filters-library extraction) —
/// everything here is a pure function of already-captured text/data, with no process execution or
/// file I/O.
/// </summary>
/// <remarks>
/// <b><see cref="FormatVitestSummary"/> is the pure half of <c>VitestCommand</c>'s
/// <c>LogFullAndFormat</c>/<c>LogDegradedAndFormat</c>.</b> Both original methods mixed a
/// verbosity-gated diagnostic (<c>Console.Error.Write</c> or
/// <c>OutputParserSupport.EmitDegradationWarning</c>) with the identical trailing pure formatting
/// expression <c>FormattedTestOutput.New(((ITokenFormatter)data).Format(mode))</c> — the diagnostic
/// text differed (Tier 1 vs. degraded-tier warning) but the formatting itself did not, so one shared
/// pure method covers both call sites. The diagnostic halves stay in <c>VitestCommand</c>'s own
/// <c>LogFullAndFormat</c>/<c>LogDegradedAndFormat</c>, which now call this method for the formatting
/// and then perform their own diagnostic write.
/// </remarks>
public static partial class VitestFilters
{
    /// <summary>
    /// The result of formatting a test run's output, tracking whether it was truncated (which
    /// determines which tee-hint variant <see cref="RenderTestOutputWithHints"/> uses). Faithful port
    /// of Rust's private <c>FormattedTestOutput</c> struct (<c>vitest_cmd.rs</c>:282-301).
    /// </summary>
    public sealed class FormattedTestOutput
    {
        private FormattedTestOutput(string text, bool isTruncated)
        {
            Text = text;
            IsTruncated = isTruncated;
        }

        /// <summary>The formatted (or raw passthrough) text.</summary>
        public string Text { get; }

        /// <summary>Whether the underlying raw output was truncated to build <see cref="Text"/>.</summary>
        public bool IsTruncated { get; }

        /// <summary>Constructs an untruncated result.</summary>
        public static FormattedTestOutput New(string text) => new(text, isTruncated: false);

        /// <summary>Constructs a truncated result.</summary>
        public static FormattedTestOutput AsTruncated(string text) => new(text, isTruncated: true);
    }

    /// <summary>
    /// Formats an already-parsed <see cref="TestResult"/> via the shared
    /// <see cref="ITokenFormatter"/>-based formatter. The pure half of <c>VitestCommand</c>'s
    /// <c>LogFullAndFormat</c>/<c>LogDegradedAndFormat</c> (both Tier 1/Tier 2 call sites reduce to
    /// this identical formatting expression) — see class remarks.
    /// </summary>
    /// <param name="data">The parsed test result (either full or degraded-tier).</param>
    /// <param name="mode">The resolved format mode (scales with verbosity).</param>
    /// <returns>The formatted test summary, wrapped as untruncated.</returns>
    public static FormattedTestOutput FormatVitestSummary(TestResult data, FormatMode mode)
    {
        ArgumentNullException.ThrowIfNull(data);
        return FormattedTestOutput.New(((ITokenFormatter)data).Format(mode));
    }

    /// <summary>
    /// The default passthrough character limit, matching <c>Config</c>'s <c>Limits.PassthroughMaxChars</c>
    /// default. <c>RtkSharp.Filters</c> is a pure library with no knowledge of - and no dependency on - a
    /// caller's <c>~/.config/rtk/config.toml</c>, so <see cref="FormatPassthroughOutput"/> always uses
    /// this hardcoded default rather than loading <c>Config</c> (same precedent as
    /// <c>OutputParserSupport.TruncatePassthrough</c>'s <c>DefaultPassthroughMaxChars</c>, Task 6).
    /// </summary>
    private const int DefaultPassthroughMaxChars = 2000;

    /// <summary>
    /// Truncates raw output for passthrough rendering using the default passthrough character limit.
    /// Faithful port of <c>format_passthrough_output</c> (<c>vitest_cmd.rs</c>:366-369) — except that,
    /// per this library's Config-independence (see <see cref="DefaultPassthroughMaxChars"/>'s remarks),
    /// a customized <c>PassthroughMaxChars</c> in the CLI's local config is not honored here.
    /// </summary>
    /// <param name="raw">The raw output to (possibly) truncate.</param>
    /// <returns>The formatted result.</returns>
    public static FormattedTestOutput FormatPassthroughOutput(string raw) =>
        FormatPassthroughOutputWithLimit(raw, DefaultPassthroughMaxChars);

    /// <summary>
    /// Truncates <paramref name="raw"/> to at most <paramref name="maxChars"/> Unicode scalar values,
    /// marking the result truncated if it exceeded the limit. Faithful port of
    /// <c>format_passthrough_output_with_limit</c> (<c>vitest_cmd.rs</c>:371-379).
    /// </summary>
    /// <param name="raw">The raw output to (possibly) truncate.</param>
    /// <param name="maxChars">The maximum number of Unicode scalar values to keep.</param>
    /// <returns>The formatted result.</returns>
    public static FormattedTestOutput FormatPassthroughOutputWithLimit(string raw, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var text = OutputParserSupport.TruncateOutput(raw, maxChars);
        var rawCharCount = raw.EnumerateRunes().Count();

        return rawCharCount > maxChars ? FormattedTestOutput.AsTruncated(text) : FormattedTestOutput.New(text);
    }

    // -----------------------------------------------------------------------
    // render_test_output(_with_hints) (vitest_cmd.rs:381-419)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Testable core of <c>VitestCommand.RenderTestOutput</c>: the two tee-hint strategies are injected
    /// so truncated-vs-non-truncated hint selection can be verified deterministically without touching
    /// disk. Faithful port of <c>render_test_output_with_hints</c> (<c>vitest_cmd.rs</c>:397-419).
    /// </summary>
    /// <param name="filtered">The formatted (or passthrough) output.</param>
    /// <param name="raw">The raw combined output, used for teeing.</param>
    /// <param name="teeLabel">The tee file slug.</param>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <param name="forceHint">Hint strategy used when <see cref="FormattedTestOutput.IsTruncated"/> is true.</param>
    /// <param name="teeHint">Hint strategy used when <see cref="FormattedTestOutput.IsTruncated"/> is false.</param>
    /// <returns>The final text to print.</returns>
    public static string RenderTestOutputWithHints(
        FormattedTestOutput filtered,
        string raw,
        string teeLabel,
        int exitCode,
        Func<string, string, string?> forceHint,
        Func<string, string, int, string?> teeHint)
    {
        ArgumentNullException.ThrowIfNull(filtered);

        var hint = filtered.IsTruncated ? forceHint(raw, teeLabel) : teeHint(raw, teeLabel, exitCode);
        return hint is not null ? $"{filtered.Text}\n{hint}" : filtered.Text;
    }

    // -----------------------------------------------------------------------
    // VitestParser (vitest_cmd.rs:17-115) - the shared jest+vitest JSON schema
    // -----------------------------------------------------------------------

    /// <summary>
    /// The jest-JSON-reporter-compatible schema shared by BOTH vitest's <c>--reporter=json</c> output
    /// and jest's <c>--json</c> output. Faithful port of <c>VitestJsonOutput</c>
    /// (<c>vitest_cmd.rs</c>:17-30) - no framework-specific fields, confirming the two tools'
    /// structural compatibility.
    /// </summary>
    private sealed class VitestJsonOutput
    {
        [JsonPropertyName("testResults")]
        public List<VitestTestFile> TestResults { get; set; } = [];

        [JsonPropertyName("numTotalTests")]
        public int NumTotalTests { get; set; }

        [JsonPropertyName("numPassedTests")]
        public int NumPassedTests { get; set; }

        [JsonPropertyName("numFailedTests")]
        public int NumFailedTests { get; set; }

        [JsonPropertyName("numPendingTests")]
        public int NumPendingTests { get; set; }
    }

    /// <summary>Faithful port of <c>VitestTestFile</c> (<c>vitest_cmd.rs</c>:32-37).</summary>
    private sealed class VitestTestFile
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("assertionResults")]
        public List<VitestTest> AssertionResults { get; set; } = [];
    }

    /// <summary>Faithful port of <c>VitestTest</c> (<c>vitest_cmd.rs</c>:39-46).</summary>
    private sealed class VitestTest
    {
        [JsonPropertyName("fullName")]
        public required string FullName { get; set; }

        [JsonPropertyName("status")]
        public required string Status { get; set; }

        [JsonPropertyName("failureMessages")]
        public List<string> FailureMessages { get; set; } = [];
    }

    /// <summary>
    /// The shared 3-tier <see cref="OutputParser{T}"/> for BOTH <c>rtk vitest</c> and <c>rtk jest</c>.
    /// Faithful port of <c>VitestParser</c>/<c>impl OutputParser for VitestParser</c>
    /// (<c>vitest_cmd.rs</c>:48-94): tier 1 parses the shared JSON schema (falling back through
    /// <see cref="JsonExtraction.ExtractJsonObject"/> to strip pnpm/dotenv banner prefixes); tier 2
    /// regex-parses <c>Tests</c>/<c>Duration</c> summary lines plus <c>[x]</c>/<c>FAIL</c> failure
    /// blocks; tier 3 falls back to truncated passthrough via the base class.
    /// </summary>
    public sealed partial class VitestParser : OutputParser<TestResult>
    {
        /// <summary>
        /// Matches the <c>Tests</c> summary line (e.g. <c>Tests  2 failed | 13 passed (15)</c>).
        /// Faithful port of <c>TESTS_RE</c> (<c>vitest_cmd.rs</c>:123-125).
        /// </summary>
        [GeneratedRegex(@"Tests\s+(?:(\d+)\s+failed\s+\|\s+)?(\d+)\s+passed")]
        private static partial Regex TestsRegex();

        /// <summary>
        /// Matches the <c>Duration</c> summary line (e.g. <c>Duration  450ms</c>). Faithful port of
        /// <c>DURATION_RE</c> (<c>vitest_cmd.rs</c>:126-128).
        /// </summary>
        [GeneratedRegex(@"Duration\s+([\d.]+)(ms|s)")]
        private static partial Regex DurationRegex();

        // Note: Rust also defines a `TEST_FILES_RE` (`Test Files\s+...`, vitest_cmd.rs:120-122)
        // alongside TESTS_RE/DURATION_RE, but never actually uses it anywhere in
        // `extract_stats_regex` - it is dead code in the Rust source itself. Faithfully NOT ported
        // here: an unused `[GeneratedRegex]` would trip analyzer warnings for behavior that has no
        // observable effect in either implementation.

        /// <inheritdoc/>
        protected override TestResult? TryFull(string input)
        {
            var json = TryDeserialize(input);

            if (json is null)
            {
                var extracted = JsonExtraction.ExtractJsonObject(input);
                if (extracted is not null)
                {
                    json = TryDeserialize(extracted);
                }
            }

            if (json is null)
            {
                return null;
            }

            return new TestResult
            {
                Total = json.NumTotalTests,
                Passed = json.NumPassedTests,
                Failed = json.NumFailedTests,
                Skipped = json.NumPendingTests,
                DurationMs = null,
                Failures = ExtractFailuresFromJson(json),
            };
        }

        /// <inheritdoc/>
        protected override (TestResult Data, IReadOnlyList<string> Warnings)? TryDegraded(string input)
        {
            var result = ExtractStatsRegex(input);
            return result is null ? null : (result, new[] { "JSON parse failed" });
        }

        private static VitestJsonOutput? TryDeserialize(string input)
        {
            try
            {
                return JsonSerializer.Deserialize(input, VitestJsonContext.Default.VitestJsonOutput);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts failures from the parsed JSON structure. Faithful port of
        /// <c>extract_failures_from_json</c> (<c>vitest_cmd.rs</c>:96-115).
        /// </summary>
        private static List<TestFailure> ExtractFailuresFromJson(VitestJsonOutput json)
        {
            var failures = new List<TestFailure>();

            foreach (var file in json.TestResults)
            {
                foreach (var test in file.AssertionResults)
                {
                    if (test.Status == "failed")
                    {
                        failures.Add(new TestFailure
                        {
                            TestName = test.FullName,
                            FilePath = file.Name,
                            ErrorMessage = string.Join('\n', test.FailureMessages),
                            StackTrace = null,
                        });
                    }
                }
            }

            return failures;
        }

        /// <summary>
        /// Tier 2: extracts test statistics using regex (degraded mode). Faithful port of
        /// <c>extract_stats_regex</c> (<c>vitest_cmd.rs</c>:117-172).
        /// </summary>
        private static TestResult? ExtractStatsRegex(string output)
        {
            var cleanOutput = Utils.StripAnsi(output);

            var passed = 0;
            var failed = 0;

            var testsMatch = TestsRegex().Match(cleanOutput);
            if (testsMatch.Success)
            {
                if (testsMatch.Groups[1].Success)
                {
                    failed = int.TryParse(testsMatch.Groups[1].Value, out var f) ? f : 0;
                }

                if (testsMatch.Groups[2].Success)
                {
                    passed = int.TryParse(testsMatch.Groups[2].Value, out var p) ? p : 0;
                }
            }

            var total = passed + failed;

            long? durationMs = null;
            var durationMatch = DurationRegex().Match(cleanOutput);
            if (durationMatch.Success
                && double.TryParse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var value))
            {
                var unit = durationMatch.Groups[2].Value;
                durationMs = unit == "ms" ? (long)value : (long)(value * 1000.0);
            }

            if (total <= 0)
            {
                return null;
            }

            return new TestResult
            {
                Total = total,
                Passed = passed,
                Failed = failed,
                Skipped = 0,
                DurationMs = durationMs,
                Failures = ExtractFailuresRegex(cleanOutput),
            };
        }

        /// <summary>
        /// Extracts failures using regex: any line containing <c>[x]</c> or <c>FAIL</c>, plus its
        /// subsequent 2-space-indented continuation lines. Faithful port of
        /// <c>extract_failures_regex</c> (<c>vitest_cmd.rs</c>:174-206).
        /// </summary>
        private static List<TestFailure> ExtractFailuresRegex(string output)
        {
            var failures = new List<TestFailure>();
            var lines = SourceFilterLineSplitter.SplitLines(output);
            var i = 0;

            while (i < lines.Count)
            {
                var line = lines[i];

                if (line.Contains("[x]", StringComparison.Ordinal) || line.Contains("FAIL", StringComparison.Ordinal))
                {
                    var errorLines = new List<string> { line };
                    i++;

                    while (i < lines.Count && lines[i].StartsWith("  ", StringComparison.Ordinal))
                    {
                        errorLines.Add(lines[i].Trim());
                        i++;
                    }

                    failures.Add(new TestFailure
                    {
                        TestName = errorLines[0],
                        FilePath = string.Empty,
                        ErrorMessage = string.Join('\n', errorLines.Skip(1)),
                        StackTrace = null,
                    });
                }
                else
                {
                    i++;
                }
            }

            return failures;
        }
    }

    /// <summary>
    /// Source-generated JSON metadata for the shared vitest/jest JSON schema, required because
    /// <c>RtkSharp.csproj</c> publishes with <c>PublishAot=true</c> - reflection-based
    /// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
    [JsonSerializable(typeof(VitestJsonOutput))]
    private sealed partial class VitestJsonContext : JsonSerializerContext;
}
