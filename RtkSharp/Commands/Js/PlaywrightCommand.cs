using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Parser;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Implements the <c>rtk playwright</c> CLI verb: filters Playwright E2E test output to show only
/// failures. Faithful port of <c>src/cmds/js/playwright_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Package-manager resolution, never a direct tool-existence probe.</b> Rust's own comment
/// (<c>playwright_cmd.rs</c>:247-248, <c>"Skip `which playwright` — it can find pyenv shims or other
/// non-Node binaries. Always resolve through the package manager."</c>) is preserved verbatim as the
/// rationale here: unlike <see cref="PackageManagerDetection.PackageManagerExec"/> (used by
/// vitest/jest/tsc), this port does NOT call the generic exists-on-PATH-first helper. It always
/// resolves via <see cref="PackageManagerDetection.DetectPackageManager()"/> and builds the launcher
/// directly — <c>pnpm exec -- playwright</c>, <c>yarn exec -- playwright</c>, or <c>npx --no-install
/// -- playwright</c> — exactly matching Rust's own three-armed <c>match pm</c> (<c>playwright_cmd.rs</c>
/// :250-266), never the tsc port's outlier <c>tool_exists</c>-first pattern.
/// </para>
/// <para>
/// <b>No reporter-passthrough escape hatch.</b> Unlike <c>rtk vitest</c> (<see cref="VitestCommand"/>),
/// <c>playwright test</c> always forces <c>--reporter=json</c> and strips any user-supplied
/// <c>--reporter*</c> flag, full stop — there is no mechanism by which a user's explicit
/// <c>--reporter</c> flag is ever honored. Faithful port of the <c>is_test</c> branch
/// (<c>playwright_cmd.rs</c>:268-283).
/// </para>
/// <para>
/// <b>Manual <see cref="TimedExecution"/> calls, not the <see cref="CommandRunner"/> skeleton.</b>
/// Matches Rust's direct <c>tracking::TimedExecution::start()</c>/<c>.track(...)</c> calls
/// (<c>playwright_cmd.rs</c>:245, 323-328) — the same manual-tracking pattern as
/// <c>pnpm_cmd.rs</c>/<c>vitest_cmd.rs</c> (Phase 8 Tasks 3/5), not <c>tsc_cmd.rs</c>'s
/// implicit-via-<c>run_streamed</c> pattern (Task 4).
/// </para>
/// <para>
/// <b>Unconditional tee-hint — the key distinction from <see cref="VitestCommand"/>.</b> Rust's
/// <c>run</c> (<c>playwright_cmd.rs</c>:317-321) always calls the plain <c>tee::tee_and_hint(&amp;raw,
/// "playwright", result.exit_code)</c> — there is no <c>FormattedTestOutput</c>-style
/// truncated-vs-not distinction that would pick between a "force" hint variant and a "conditional"
/// one, unlike <c>vitest_cmd.rs</c>'s <c>render_test_output_with_hints</c> (which chooses
/// <c>force_tee_hint</c> when its own passthrough truncated, or <c>tee_and_hint</c> otherwise). This
/// port mirrors that exactly: <see cref="RunAsync"/> always calls <see cref="Tee.TeeAndHint"/>
/// directly, regardless of which parser tier produced <c>filtered</c>.
/// </para>
/// </remarks>
public static partial class PlaywrightCommand
{
    /// <summary>
    /// Registry entry point for the <c>playwright</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/>
    /// (the registry delegate cannot receive it as an argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>playwright</c> verb.</param>
    /// <returns>The child's exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunSafeAsync(args, RuntimeOptions.Verbosity, executor: null);

    /// <summary>
    /// Test-friendly overload taking an explicit verbosity value and an injectable
    /// <see cref="IProcessExecutor"/>, with the fail-loud wrapper Rust's own <c>run()</c>-adjacent
    /// callers apply (matching <c>NpmCommand</c>/<c>PnpmCommand</c>/<c>TscCommand</c>/<c>VitestCommand</c>'s
    /// convention: an rtk-level failure surfaces as <c>rtk: {message}</c> rather than propagating an
    /// exception).
    /// </summary>
    /// <param name="args">The arguments following the <c>playwright</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>verbose: u8</c> parameter).</param>
    /// <param name="executor">The process executor to run the child process with, or null for the default.</param>
    /// <returns>The child's exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunSafeAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await RunAsync(args, verbose, executor).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// The core implementation. Faithful port of <c>run</c> (<c>playwright_cmd.rs</c>:244-336).
    /// </summary>
    /// <param name="args">The arguments following the <c>playwright</c> verb.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run the child process with, or null for the default.</param>
    /// <returns>The child's exit code, or <c>0</c> on success.</returns>
    internal static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        var timer = TimedExecution.Start();

        // Skip `which playwright` — it can find pyenv shims or other non-Node binaries. Always
        // resolve through the package manager (playwright_cmd.rs:247-266).
        var pm = PackageManagerDetection.DetectPackageManager();
        var cmdArgs = new List<string>();
        string fileName;

        switch (pm)
        {
            case "pnpm":
                fileName = "pnpm";
                cmdArgs.Add("exec");
                cmdArgs.Add("--");
                cmdArgs.Add("playwright");
                break;
            case "yarn":
                fileName = "yarn";
                cmdArgs.Add("exec");
                cmdArgs.Add("--");
                cmdArgs.Add("playwright");
                break;
            default:
                fileName = "npx";
                cmdArgs.Add("--no-install");
                cmdArgs.Add("--");
                cmdArgs.Add("playwright");
                break;
        }

        // Only inject --reporter=json for `playwright test` runs; strip any user-supplied
        // --reporter* to avoid conflicts with our forced JSON. No passthrough escape hatch exists.
        var isTest = args.Length > 0 && args[0] == "test";
        if (isTest)
        {
            cmdArgs.Add("test");
            cmdArgs.Add("--reporter=json");
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--reporter", StringComparison.Ordinal))
                {
                    cmdArgs.Add(args[i]);
                }
            }
        }
        else
        {
            cmdArgs.AddRange(args);
        }

        if (verbose > 0)
        {
            Console.Error.Write($"Running: playwright {string.Join(' ', args)}\n");
        }

        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest(fileName, cmdArgs, CaptureMode: ExecutionCaptureMode.Separate);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run playwright (try: npm install -g playwright){detail}");
        }

        var raw = $"{result.Stdout}\n{result.Stderr}";

        var parseResult = new PlaywrightParser().Parse(result.Stdout);
        var mode = FormatModeExtensions.FromVerbosity((byte)Math.Clamp(verbose, 0, byte.MaxValue));

        string filtered = parseResult switch
        {
            ParseResult<TestResult>.Full full => LogFullAndFormat(verbose, full.Data, mode),
            ParseResult<TestResult>.Degraded degraded => LogDegradedAndFormat(verbose, degraded.Data, degraded.Warnings, mode),
            ParseResult<TestResult>.Passthrough passthrough => PassthroughFallback(passthrough.Raw),
            _ => throw new InvalidOperationException("Unreachable: unknown ParseResult variant."),
        };

        Console.Out.Write(RenderOutput(filtered, raw, result.ExitCode, static (r, label, code) => Tee.TeeAndHint(r, label, code)) + "\n");

        timer.Track($"playwright {string.Join(' ', args)}", $"rtk playwright {string.Join(' ', args)}", raw, filtered);

        // Preserve exit code for CI/CD.
        return result.ExitCode != 0 ? result.ExitCode : 0;
    }

    /// <summary>
    /// Renders the final printed output: appends a tee recovery hint unconditionally, i.e. via
    /// <paramref name="teeHint"/> regardless of which parser tier produced <paramref name="filtered"/>
    /// (there is no truncated-vs-not distinction that would ever pick a different, "force" hint
    /// strategy the way <see cref="VitestCommand.RenderTestOutputWithHints"/> does). Faithful port of
    /// the tee/print block at the tail of <c>run</c> (<c>playwright_cmd.rs</c>:317-321). The hint
    /// strategy is injected so this is testable without touching disk.
    /// </summary>
    /// <param name="filtered">The formatted (or passthrough) output.</param>
    /// <param name="raw">The raw combined output, used for teeing.</param>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <param name="teeHint">The (always-invoked) hint strategy.</param>
    /// <returns>The final text to print.</returns>
    internal static string RenderOutput(string filtered, string raw, int exitCode, Func<string, string, int, string?> teeHint)
    {
        ArgumentNullException.ThrowIfNull(filtered);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(teeHint);

        var hint = teeHint(raw, "playwright", exitCode);
        return hint is not null ? $"{filtered}\n{hint}" : filtered;
    }

    private static string LogFullAndFormat(int verbose, TestResult data, FormatMode mode)
    {
        if (verbose > 0)
        {
            Console.Error.Write("playwright test (Tier 1: Full JSON parse)\n");
        }

        return ((ITokenFormatter)data).Format(mode);
    }

    private static string LogDegradedAndFormat(int verbose, TestResult data, IReadOnlyList<string> warnings, FormatMode mode)
    {
        if (verbose > 0)
        {
            OutputParserSupport.EmitDegradationWarning("playwright", string.Join(", ", warnings));
        }

        return ((ITokenFormatter)data).Format(mode);
    }

    private static string PassthroughFallback(string raw)
    {
        OutputParserSupport.EmitPassthroughWarning("playwright", "All parsing tiers failed");
        return raw;
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
    internal sealed partial class PlaywrightParser : OutputParser<TestResult>
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
    /// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming, matching the
    /// convention <c>PnpmCommand.PnpmJsonContext</c>/<c>VitestCommand.VitestJsonContext</c> established.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
    [JsonSerializable(typeof(PlaywrightJsonOutput))]
    private sealed partial class PlaywrightJsonContext : JsonSerializerContext;
}
