using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RtkSharp.Core;
using RtkSharp.Parser;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic for the <c>rtk playwright</c> CLI proxy. Extracted from
/// <c>RtkSharp.Commands.Js.PlaywrightCommand</c> (Task 7 of the filters-library extraction) —
/// everything here is a pure function of already-captured text/data, with no process execution or
/// file I/O.
/// </summary>
/// <remarks>
/// <b><see cref="FormatFullResults"/>/<see cref="FormatDegradedResults"/> are the pure halves of
/// <c>PlaywrightCommand</c>'s <c>LogFullAndFormat</c>/<c>LogDegradedAndFormat</c>.</b> Both original
/// methods mixed a verbosity-gated diagnostic (<c>Console.Error.Write</c> or
/// <c>OutputParserSupport.EmitDegradationWarning</c>) with the identical trailing pure formatting
/// expression <c>((ITokenFormatter)data).Format(mode)</c> — the diagnostic text differed (Tier 1 vs.
/// degraded-tier warning) but the formatting itself did not; unlike vitest's equivalent split (Task 7,
/// same pattern), this port keeps them as two separately-named methods rather than one shared helper,
/// since <c>PlaywrightCommand</c>'s own <c>LogFullAndFormat</c>/<c>LogDegradedAndFormat</c> wrapper
/// names distinguish the tier in their own names too. The diagnostic halves stay in
/// <c>PlaywrightCommand</c>'s own <c>LogFullAndFormat</c>/<c>LogDegradedAndFormat</c>, which now call
/// these methods for the formatting and then perform their own diagnostic write.
/// </remarks>
public static partial class PlaywrightFilters
{
    /// <summary>
    /// Renders the final printed output: appends a tee recovery hint unconditionally, i.e. via
    /// <paramref name="teeHint"/> regardless of which parser tier produced <paramref name="filtered"/>
    /// (there is no truncated-vs-not distinction that would ever pick a different, "force" hint
    /// strategy the way <see cref="VitestFilters.RenderTestOutputWithHints"/> does). Faithful port of
    /// the tee/print block at the tail of <c>run</c> (<c>playwright_cmd.rs</c>:317-321). The hint
    /// strategy is injected so this is testable without touching disk.
    /// </summary>
    /// <param name="filtered">The formatted (or passthrough) output.</param>
    /// <param name="raw">The raw combined output, used for teeing.</param>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <param name="teeHint">The (always-invoked) hint strategy.</param>
    /// <returns>The final text to print.</returns>
    public static string RenderOutput(string filtered, string raw, int exitCode, Func<string, string, int, string?> teeHint)
    {
        ArgumentNullException.ThrowIfNull(filtered);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(teeHint);

        var hint = teeHint(raw, "playwright", exitCode);
        return hint is not null ? $"{filtered}\n{hint}" : filtered;
    }

    /// <summary>
    /// Formats a Tier-1 (full JSON parse) <see cref="TestResult"/> via the shared
    /// <see cref="ITokenFormatter"/>-based formatter. The pure half of <c>PlaywrightCommand</c>'s
    /// <c>LogFullAndFormat</c> — see class remarks.
    /// </summary>
    /// <param name="data">The parsed test result.</param>
    /// <param name="mode">The resolved format mode (scales with verbosity).</param>
    /// <returns>The formatted test summary.</returns>
    public static string FormatFullResults(TestResult data, FormatMode mode)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ((ITokenFormatter)data).Format(mode);
    }

    /// <summary>
    /// Formats a Tier-2 (regex-fallback, degraded) <see cref="TestResult"/> via the shared
    /// <see cref="ITokenFormatter"/>-based formatter. The pure half of <c>PlaywrightCommand</c>'s
    /// <c>LogDegradedAndFormat</c> — see class remarks.
    /// </summary>
    /// <param name="data">The parsed test result.</param>
    /// <param name="mode">The resolved format mode (scales with verbosity).</param>
    /// <returns>The formatted test summary.</returns>
    public static string FormatDegradedResults(TestResult data, FormatMode mode)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ((ITokenFormatter)data).Format(mode);
    }

    // -----------------------------------------------------------------------
    // PlaywrightParser (playwright_cmd.rs:15-242)
    // -----------------------------------------------------------------------

    /// <summary>Faithful port of <c>PlaywrightJsonOutput</c> (<c>playwright_cmd.rs</c>:16-21).</summary>
    private sealed class PlaywrightJsonOutput
    {
        [JsonPropertyName("stats")]
        public required PlaywrightStats Stats { get; set; }

        [JsonPropertyName("suites")]
        public List<PlaywrightSuite> Suites { get; set; } = [];
    }

    /// <summary>Faithful port of <c>PlaywrightStats</c> (<c>playwright_cmd.rs</c>:23-31).</summary>
    private sealed class PlaywrightStats
    {
        [JsonPropertyName("expected")]
        public int Expected { get; set; }

        [JsonPropertyName("unexpected")]
        public int Unexpected { get; set; }

        [JsonPropertyName("skipped")]
        public int Skipped { get; set; }

        /// <summary>Duration in milliseconds (float in real Playwright output).</summary>
        [JsonPropertyName("duration")]
        public double Duration { get; set; }
    }

    /// <summary>
    /// File-level or describe-level suite. Faithful port of <c>PlaywrightSuite</c>
    /// (<c>playwright_cmd.rs</c>:34-45), including its self-referential <c>suites</c> field that
    /// enables recursion into nested <c>describe</c> blocks.
    /// </summary>
    private sealed class PlaywrightSuite
    {
        [JsonPropertyName("title")]
        public required string Title { get; set; }

        [JsonPropertyName("file")]
        public string? File { get; set; }

        /// <summary>Individual test specs (test functions).</summary>
        [JsonPropertyName("specs")]
        public List<PlaywrightSpec> Specs { get; set; } = [];

        /// <summary>Nested describe blocks.</summary>
        [JsonPropertyName("suites")]
        public List<PlaywrightSuite> Suites { get; set; } = [];
    }

    /// <summary>
    /// A single test function (may run in multiple browsers/projects). Faithful port of
    /// <c>PlaywrightSpec</c> (<c>playwright_cmd.rs</c>:48-56).
    /// </summary>
    private sealed class PlaywrightSpec
    {
        [JsonPropertyName("title")]
        public required string Title { get; set; }

        /// <summary>Overall pass/fail status across all projects.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        /// <summary>Per-project/browser executions.</summary>
        [JsonPropertyName("tests")]
        public List<PlaywrightExecution> Tests { get; set; } = [];
    }

    /// <summary>
    /// A test execution in a specific browser/project. Faithful port of <c>PlaywrightExecution</c>
    /// (<c>playwright_cmd.rs</c>:59-65).
    /// </summary>
    private sealed class PlaywrightExecution
    {
        /// <summary><c>"expected"</c>, <c>"unexpected"</c>, <c>"skipped"</c>, <c>"flaky"</c>.</summary>
        [JsonPropertyName("status")]
        public required string Status { get; set; }

        [JsonPropertyName("results")]
        public List<PlaywrightAttempt> Results { get; set; } = [];
    }

    /// <summary>
    /// A single attempt/result for a test execution. Faithful port of <c>PlaywrightAttempt</c>
    /// (<c>playwright_cmd.rs</c>:68-75).
    /// </summary>
    private sealed class PlaywrightAttempt
    {
        /// <summary><c>"passed"</c>, <c>"failed"</c>, <c>"timedOut"</c>, <c>"interrupted"</c>.</summary>
        [JsonPropertyName("status")]
        public required string Status { get; set; }

        /// <summary>Error details (array in Playwright >= v1.30).</summary>
        [JsonPropertyName("errors")]
        public List<PlaywrightError> Errors { get; set; } = [];
    }

    /// <summary>Faithful port of <c>PlaywrightError</c> (<c>playwright_cmd.rs</c>:77-81).</summary>
    private sealed class PlaywrightError
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Parser for Playwright JSON output. Faithful port of <c>PlaywrightParser</c>/
    /// <c>impl OutputParser for PlaywrightParser</c> (<c>playwright_cmd.rs</c>:84-122).
    /// </summary>
    public sealed partial class PlaywrightParser : OutputParser<TestResult>
    {
        /// <summary>
        /// Matches the summary counts line (e.g. <c>3 passed (7.3s)</c>). Faithful port of
        /// <c>SUMMARY_RE</c> (<c>playwright_cmd.rs</c>:167-169).
        /// </summary>
        [GeneratedRegex(@"(\d+)\s+(passed|failed|flaky|skipped)")]
        private static partial Regex SummaryRegex();

        /// <summary>
        /// Matches the run duration (e.g. <c>(7.3s)</c>). Unlike vitest/tsc, minutes (<c>m</c>) are
        /// also supported here. Faithful port of <c>DURATION_RE</c> (<c>playwright_cmd.rs</c>:170-172).
        /// </summary>
        [GeneratedRegex(@"\((\d+(?:\.\d+)?)(ms|s|m)\)")]
        private static partial Regex DurationRegex();

        /// <summary>
        /// Matches Playwright's default-reporter failure lines (e.g. <c>× 1 login.spec.ts:5:1 ›
        /// should log in</c>). Faithful port of <c>TEST_PATTERN</c> (<c>playwright_cmd.rs</c>:223-225).
        /// </summary>
        [GeneratedRegex(@"[×✗]\s+.*?›\s+([^›]+\.spec\.[tj]sx?)")]
        private static partial Regex TestPatternRegex();

        /// <inheritdoc/>
        protected override TestResult? TryFull(string input)
        {
            PlaywrightJsonOutput? json;
            try
            {
                json = JsonSerializer.Deserialize(input, PlaywrightJsonContext.Default.PlaywrightJsonOutput);
            }
            catch (JsonException)
            {
                return null;
            }

            if (json is null)
            {
                return null;
            }

            var failures = new List<TestFailure>();
            var total = 0;
            CollectTestResults(json.Suites, ref total, failures);

            return new TestResult
            {
                Total = total,
                Passed = json.Stats.Expected,
                Failed = json.Stats.Unexpected,
                Skipped = json.Stats.Skipped,
                DurationMs = (long)json.Stats.Duration,
                Failures = failures,
            };
        }

        /// <inheritdoc/>
        protected override (TestResult Data, IReadOnlyList<string> Warnings)? TryDegraded(string input)
        {
            var result = ExtractPlaywrightRegex(input);
            return result is null ? null : (result, new[] { "JSON parse failed" });
        }

        /// <summary>
        /// Recursively collects per-spec failures across a suite tree, descending into nested
        /// <c>describe</c>-block <c>suites</c>. Faithful port of <c>collect_test_results</c>
        /// (<c>playwright_cmd.rs</c>:124-162).
        /// </summary>
        /// <param name="suites">The suites to walk (top-level, or a nested describe block's children).</param>
        /// <param name="total">Running total of test specs seen, threaded by reference across recursive calls.</param>
        /// <param name="failures">Accumulates one <see cref="TestFailure"/> per non-<c>ok</c> spec.</param>
        private static void CollectTestResults(List<PlaywrightSuite> suites, ref int total, List<TestFailure> failures)
        {
            foreach (var suite in suites)
            {
                var filePath = suite.File ?? suite.Title;

                foreach (var spec in suite.Specs)
                {
                    total++;

                    if (!spec.Ok)
                    {
                        // Find the first unexpected execution's first failed/timedOut result's first
                        // error message, defaulting to "Test failed" if none is found.
                        var errorMsg = "Test failed";
                        var unexpected = spec.Tests.FirstOrDefault(t => t.Status == "unexpected");
                        var attempt = unexpected?.Results.FirstOrDefault(r => r.Status is "failed" or "timedOut");
                        var firstError = attempt?.Errors.FirstOrDefault();
                        if (firstError is not null)
                        {
                            errorMsg = firstError.Message;
                        }

                        failures.Add(new TestFailure
                        {
                            TestName = spec.Title,
                            FilePath = filePath,
                            ErrorMessage = errorMsg,
                            StackTrace = null,
                        });
                    }
                }

                // Recurse into nested suites (describe blocks).
                CollectTestResults(suite.Suites, ref total, failures);
            }
        }

        /// <summary>
        /// Tier 2: extracts test statistics using regex (degraded mode). Faithful port of
        /// <c>extract_playwright_regex</c> (<c>playwright_cmd.rs</c>:165-218).
        /// </summary>
        private static TestResult? ExtractPlaywrightRegex(string output)
        {
            var cleanOutput = Utils.StripAnsi(output);

            var passed = 0;
            var failed = 0;
            var skipped = 0;

            foreach (Match match in SummaryRegex().Matches(cleanOutput))
            {
                var count = int.TryParse(match.Groups[1].Value, out var c) ? c : 0;
                switch (match.Groups[2].Value)
                {
                    case "passed":
                        passed = count;
                        break;
                    case "failed":
                        failed = count;
                        break;
                    case "skipped":
                        skipped = count;
                        break;
                }
            }

            long? durationMs = null;
            var durationMatch = DurationRegex().Match(cleanOutput);
            if (durationMatch.Success
                && double.TryParse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var value))
            {
                durationMs = durationMatch.Groups[2].Value switch
                {
                    "ms" => (long)value,
                    "s" => (long)(value * 1000.0),
                    "m" => (long)(value * 60000.0),
                    _ => (long)value,
                };
            }

            var total = passed + failed + skipped;
            if (total <= 0)
            {
                return null;
            }

            return new TestResult
            {
                Total = total,
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                DurationMs = durationMs,
                Failures = ExtractFailuresRegex(cleanOutput),
            };
        }

        /// <summary>
        /// Extracts failures using regex. Faithful port of <c>extract_failures_regex</c>
        /// (<c>playwright_cmd.rs</c>:221-242).
        /// </summary>
        private static List<TestFailure> ExtractFailuresRegex(string output)
        {
            var failures = new List<TestFailure>();

            foreach (Match match in TestPatternRegex().Matches(output))
            {
                var specGroup = match.Groups[1];
                if (specGroup.Success)
                {
                    failures.Add(new TestFailure
                    {
                        TestName = match.Value,
                        FilePath = specGroup.Value,
                        ErrorMessage = string.Empty,
                        StackTrace = null,
                    });
                }
            }

            return failures;
        }
    }

    /// <summary>
    /// Source-generated JSON metadata for <see cref="PlaywrightJsonOutput"/>, required because
    /// <c>RtkSharp.csproj</c> publishes with <c>PublishAot=true</c> - reflection-based
    /// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
    [JsonSerializable(typeof(PlaywrightJsonOutput))]
    private sealed partial class PlaywrightJsonContext : JsonSerializerContext;
}
