using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Parser;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Which test framework a <see cref="VitestCommand"/> invocation targets. Discriminates the single
/// shared implementation the same way Rust's <c>run_test</c> discriminates on <c>command:
/// &amp;Commands</c> (matching <c>Commands::Vitest {..}</c> vs <c>Commands::Jest {..}</c>,
/// <c>vitest_cmd.rs</c>:208-245).
/// </summary>
public enum TestFramework
{
    /// <summary><c>rtk vitest</c> — supports a reporter-passthrough escape hatch.</summary>
    Vitest,

    /// <summary><c>rtk jest</c> — always forces <c>--no-watch --json</c>, NO passthrough escape hatch.</summary>
    Jest,
}

/// <summary>
/// Implements both the <c>rtk vitest</c> and <c>rtk jest</c> CLI verbs as a SINGLE shared
/// implementation discriminated by <see cref="TestFramework"/>. Faithful port of the shared
/// <c>run_test</c> function (<c>src/cmds/js/vitest_cmd.rs</c>:208-275) and its dispatch arm
/// (<c>Commands::Jest {{ ref args }} | Commands::Vitest {{ ref args }} =&gt; vitest_cmd::run_test(&amp;cli.command,
/// args, cli.verbose)?</c>, <c>main.rs</c>:2047-2049).
/// </summary>
/// <remarks>
/// <para>
/// <b>One function, not two independent classes.</b> This is the single most important structural
/// requirement of this port: Rust ships exactly one <c>run_test</c> function that both
/// <c>Commands::Vitest</c> and <c>Commands::Jest</c> route through, branching internally on which
/// variant matched. <see cref="RunTestAsync"/> mirrors that shape exactly via the
/// <see cref="TestFramework"/> discriminator — vitest-only and jest-only behavior live as internal
/// branches of one method, not as separate classes that could drift apart independently over time.
/// </para>
/// <para>
/// <b>Vitest's reporter-passthrough escape hatch has NO jest equivalent — this is deliberate and
/// verified directly against source.</b> <c>build_vitest_effective_args</c>
/// (<c>vitest_cmd.rs</c>:303-322) sets <c>passthrough = true</c> whenever the user supplies an explicit
/// <c>--reporter</c>/<c>--reporter=X</c> flag to <c>rtk vitest</c>, bypassing the parser/formatter
/// entirely in favor of raw (truncated) output. The jest branch (<c>vitest_cmd.rs</c>:221-245) has no
/// such mechanism: it unconditionally forces <c>--no-watch --json</c>, and the post-match
/// arg-filtering loop that appends the user's remaining args (<c>vitest_cmd.rs</c>:234-245, gated on
/// <c>!matches!(command, Commands::Vitest {{ .. }})</c> — i.e. it only runs for jest) actively DROPS any
/// user-supplied <c>--reporter*</c> flag rather than honoring it. <see cref="s_passthroughRequestedNeverTrueForJest"/>
/// documents this invariant; <c>VitestCommandTests.Jest_ExplicitReporterFlag_NeverTriggersPassthrough</c>
/// is the test that would fail immediately if this asymmetry were accidentally "fixed" into parity.
/// </para>
/// <para>
/// <b>Manual <see cref="TimedExecution"/> calls, not the <see cref="CommandRunner"/> skeleton.</b>
/// Rust's <c>run_test</c> calls <c>tracking::TimedExecution::start()</c>/<c>.track(...)</c> directly
/// (<c>vitest_cmd.rs</c>:209, 264-269) rather than routing through the shared <c>core::runner</c>
/// skeleton — matching <c>pnpm_cmd.rs</c>'s manual-tracking pattern (Phase 8 Task 3), not
/// <c>tsc_cmd.rs</c>'s implicit-via-<c>run_streamed</c> pattern (Task 4). This port matches that
/// exactly via a direct <see cref="TimedExecution"/> call in <see cref="RunTestAsync"/>.
/// </para>
/// <para>
/// <b>Shared JSON schema.</b> <see cref="VitestParser"/>'s tier-1 JSON shape
/// (<c>numTotalTests</c>/<c>numPassedTests</c>/<c>numFailedTests</c>/<c>numPendingTests</c>/
/// <c>testResults[].assertionResults[]</c>) is used identically by both vitest's <c>--reporter=json</c>
/// output and jest's <c>--json</c> output — this is the structural fact that justifies one shared
/// parser for both frameworks, confirmed directly against <c>VitestJsonOutput</c>
/// (<c>vitest_cmd.rs</c>:17-30) which carries no framework-specific fields.
/// </para>
/// </remarks>
public static partial class VitestCommand
{
    /// <summary>
    /// Documents (via its name, referenced from <see cref="VitestCommand"/>'s class remarks) the
    /// jest-has-no-passthrough-escape-hatch invariant this port must preserve. Not consumed at
    /// runtime — a doc anchor only.
    /// </summary>
    private const bool s_passthroughRequestedNeverTrueForJest = true;

    /// <summary>
    /// Registry entry point for the <c>vitest</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/>
    /// (the registry delegate cannot receive it as an argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>vitest</c> verb.</param>
    /// <returns>The child's exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunVitestAsync(string[] args) =>
        RunTestSafeAsync(TestFramework.Vitest, args, RuntimeOptions.Verbosity, executor: null);

    /// <summary>
    /// Registry entry point for the <c>jest</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/>
    /// (the registry delegate cannot receive it as an argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>jest</c> verb.</param>
    /// <returns>The child's exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunJestAsync(string[] args) =>
        RunTestSafeAsync(TestFramework.Jest, args, RuntimeOptions.Verbosity, executor: null);

    /// <summary>
    /// Test-friendly overload taking an explicit verbosity value and an injectable
    /// <see cref="IProcessExecutor"/>, with the fail-loud wrapper Rust's own <c>run()</c>-adjacent
    /// callers apply (matching <c>NpmCommand</c>/<c>PnpmCommand</c>/<c>TscCommand</c>'s convention: an
    /// rtk-level failure surfaces as <c>rtk: {message}</c> rather than propagating an exception).
    /// </summary>
    /// <param name="framework">Which test framework this invocation targets.</param>
    /// <param name="args">The arguments following the verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <param name="executor">The process executor to run the child process with, or null for the default.</param>
    /// <returns>The child's exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunTestSafeAsync(TestFramework framework, string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await RunTestAsync(framework, args, verbose, executor).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Guarded proactively, not thrown from this command today — see DockerCommand/
            // PrismaCommand's remarks for the swallowed-exception bug this prevents.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// The single shared implementation for both <c>rtk vitest</c> and <c>rtk jest</c>. Faithful port
    /// of <c>run_test</c> (<c>vitest_cmd.rs</c>:208-275).
    /// </summary>
    /// <param name="framework">Which test framework this invocation targets.</param>
    /// <param name="args">The arguments following the verb.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run the child process with, or null for the default.</param>
    /// <returns>The child's exit code, or <c>0</c> on success.</returns>
    internal static async Task<int> RunTestAsync(TestFramework framework, string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        var timer = TimedExecution.Start();
        var frameworkName = framework == TestFramework.Vitest ? "vitest" : "jest";
        var pmCommand = PackageManagerDetection.PackageManagerExec(frameworkName);

        var passthroughRequested = false;
        var cmdArgs = new List<string>(pmCommand.BaseArguments);

        if (framework == TestFramework.Vitest)
        {
            var effective = BuildVitestEffectiveArgs(args);
            passthroughRequested = effective.Passthrough;
            cmdArgs.AddRange(effective.Args);
        }
        else
        {
            // Force non-watch mode + JSON structured output, unconditionally - jest has no
            // reporter-passthrough escape hatch (see class remarks).
            cmdArgs.Add("--no-watch");
            cmdArgs.Add("--json");

            // Re-append the user's own args, dropping any run/--json*/--reporter*/--watch* the user
            // supplied - this is where an explicit `--reporter=X` passed to `rtk jest` gets silently
            // dropped rather than honored, unlike vitest's equivalent loop.
            foreach (var arg in args)
            {
                if (ShouldSkipJestArg(arg))
                {
                    continue;
                }

                cmdArgs.Add(arg);
            }
        }

        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest(pmCommand.FileName, cmdArgs, CaptureMode: ExecutionCaptureMode.Separate);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run {frameworkName}{detail}");
        }

        // CaptureResult::combined() (stream.rs:529-531) is a plain concatenation, no separator.
        var combined = result.Stdout + result.Stderr;

        var filtered = FormatTestOutput(frameworkName, result.Stdout, combined, passthroughRequested, verbose);
        var teeLabel = $"{frameworkName}_run";

        Console.Out.Write(RenderTestOutput(filtered, combined, teeLabel, result.ExitCode) + "\n");

        timer.Track($"{frameworkName} run", $"rtk {frameworkName} run", combined, filtered.Text);

        return result.ExitCode != 0 ? result.ExitCode : 0;
    }

    // -----------------------------------------------------------------------
    // build_vitest_effective_args / should_skip_vitest_arg (vitest_cmd.rs:303-331) - VITEST ONLY
    // -----------------------------------------------------------------------

    /// <summary>
    /// The resolved effective argument vector for a <c>rtk vitest</c> invocation, plus whether it
    /// triggers the reporter-passthrough escape hatch. Faithful port of Rust's private
    /// <c>EffectiveVitestArgs</c> struct (<c>vitest_cmd.rs</c>:277-280).
    /// </summary>
    /// <param name="Args">The effective argument vector to pass to <c>vitest</c>.</param>
    /// <param name="Passthrough">
    /// True when the user supplied an explicit <c>--reporter</c>/<c>--reporter=X</c> flag - output then
    /// bypasses the parser entirely via <see cref="FormatPassthroughOutputWithLimit"/>.
    /// </param>
    internal readonly record struct EffectiveVitestArgs(IReadOnlyList<string> Args, bool Passthrough);

    /// <summary>
    /// Builds the effective <c>vitest</c> argument vector: always prepends <c>run</c>; appends
    /// <c>--reporter=json</c> only when the user did not already supply an explicit reporter. Faithful
    /// port of <c>build_vitest_effective_args</c> (<c>vitest_cmd.rs</c>:303-322).
    /// </summary>
    /// <param name="args">The user's raw arguments following the <c>vitest</c> verb.</param>
    /// <returns>The resolved effective arguments and passthrough flag.</returns>
    internal static EffectiveVitestArgs BuildVitestEffectiveArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var passthrough = HasExplicitVitestReporter(args);
        var effective = new List<string> { "run" };

        if (!passthrough)
        {
            effective.Add("--reporter=json");
        }

        foreach (var arg in args)
        {
            if (ShouldSkipVitestArg(arg))
            {
                continue;
            }

            effective.Add(arg);
        }

        return new EffectiveVitestArgs(effective, passthrough);
    }

    /// <summary>
    /// Reports whether the user supplied an explicit <c>--reporter</c>/<c>--reporter=X</c> flag.
    /// Faithful port of <c>has_explicit_vitest_reporter</c> (<c>vitest_cmd.rs</c>:324-327).
    /// </summary>
    /// <param name="args">The user's raw arguments.</param>
    /// <returns>True if an explicit reporter flag is present.</returns>
    internal static bool HasExplicitVitestReporter(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a == "--reporter" || a.StartsWith("--reporter=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reports whether a raw user argument should be dropped when building the vitest effective
    /// argument vector (redundant <c>run</c>, or any <c>--json*</c>/<c>--watch*</c> flag). Faithful
    /// port of <c>should_skip_vitest_arg</c> (<c>vitest_cmd.rs</c>:329-331).
    /// </summary>
    /// <param name="arg">The raw argument to test.</param>
    /// <returns>True if this argument should be dropped.</returns>
    internal static bool ShouldSkipVitestArg(string arg) =>
        arg == "run"
        || arg.StartsWith("--json", StringComparison.Ordinal)
        || arg.StartsWith("--watch", StringComparison.Ordinal);

    /// <summary>
    /// Reports whether a raw user argument should be dropped when re-appending the user's own args to
    /// the jest-forced <c>--no-watch --json</c> base. Faithful port of the jest-only arg-filtering loop
    /// (<c>vitest_cmd.rs</c>:234-245): unlike <see cref="ShouldSkipVitestArg"/>, this ALSO drops any
    /// <c>--reporter*</c> flag - jest has no passthrough escape hatch, so a user-supplied reporter flag
    /// is silently discarded rather than honored.
    /// </summary>
    /// <param name="arg">The raw argument to test.</param>
    /// <returns>True if this argument should be dropped.</returns>
    internal static bool ShouldSkipJestArg(string arg) =>
        arg == "run"
        || arg.StartsWith("--json", StringComparison.Ordinal)
        || arg.StartsWith("--reporter", StringComparison.Ordinal)
        || arg.StartsWith("--watch", StringComparison.Ordinal);

    // -----------------------------------------------------------------------
    // format_test_output / format_passthrough_output (vitest_cmd.rs:333-379)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The result of formatting a test run's output, tracking whether it was truncated (which
    /// determines which tee-hint variant <see cref="RenderTestOutputWithHints"/> uses). Faithful port
    /// of Rust's private <c>FormattedTestOutput</c> struct (<c>vitest_cmd.rs</c>:282-301).
    /// </summary>
    internal sealed class FormattedTestOutput
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
    /// Formats a test run's raw output: the passthrough escape hatch (vitest only) bypasses the parser
    /// entirely; otherwise runs the shared 3-tier <see cref="VitestParser"/> and renders via
    /// <see cref="TestResult.Format(FormatMode)"/>. Faithful port of <c>format_test_output</c>
    /// (<c>vitest_cmd.rs</c>:333-364).
    /// </summary>
    /// <param name="framework">The framework name (<c>"vitest"</c> or <c>"jest"</c>), used in diagnostics.</param>
    /// <param name="stdout">The child process's captured stdout (parsed by <see cref="VitestParser"/>).</param>
    /// <param name="combined">The combined stdout+stderr (used for passthrough rendering).</param>
    /// <param name="passthroughRequested">Whether the vitest-only reporter-passthrough escape hatch is active.</param>
    /// <param name="verbose">The verbosity level; a nonzero value logs tier diagnostics to stderr.</param>
    /// <returns>The formatted output.</returns>
    internal static FormattedTestOutput FormatTestOutput(
        string framework, string stdout, string combined, bool passthroughRequested, int verbose)
    {
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(combined);

        if (passthroughRequested)
        {
            return FormatPassthroughOutput(combined);
        }

        var parseResult = new VitestParser().Parse(stdout);
        var mode = FormatModeExtensions.FromVerbosity((byte)Math.Clamp(verbose, 0, byte.MaxValue));

        return parseResult switch
        {
            ParseResult<TestResult>.Full full => LogFullAndFormat(framework, verbose, full.Data, mode),
            ParseResult<TestResult>.Degraded degraded => LogDegradedAndFormat(framework, verbose, degraded.Data, degraded.Warnings, mode),
            ParseResult<TestResult>.Passthrough => PassthroughFallback(framework, stdout),
            _ => throw new InvalidOperationException("Unreachable: unknown ParseResult variant."),
        };
    }

    private static FormattedTestOutput LogFullAndFormat(string framework, int verbose, TestResult data, FormatMode mode)
    {
        if (verbose > 0)
        {
            Console.Error.Write($"{framework} run (Tier 1: Full JSON parse)\n");
        }

        return FormattedTestOutput.New(((ITokenFormatter)data).Format(mode));
    }

    private static FormattedTestOutput LogDegradedAndFormat(string framework, int verbose, TestResult data, IReadOnlyList<string> warnings, FormatMode mode)
    {
        if (verbose > 0)
        {
            OutputParserSupport.EmitDegradationWarning(framework, string.Join(", ", warnings));
        }

        return FormattedTestOutput.New(((ITokenFormatter)data).Format(mode));
    }

    private static FormattedTestOutput PassthroughFallback(string framework, string stdout)
    {
        OutputParserSupport.EmitPassthroughWarning(framework, "All parsing tiers failed");
        return FormatPassthroughOutput(stdout);
    }

    /// <summary>
    /// Truncates raw output for passthrough rendering using the configured passthrough character
    /// limit. Faithful port of <c>format_passthrough_output</c> (<c>vitest_cmd.rs</c>:366-369).
    /// </summary>
    /// <param name="raw">The raw output to (possibly) truncate.</param>
    /// <returns>The formatted result.</returns>
    internal static FormattedTestOutput FormatPassthroughOutput(string raw)
    {
        var maxChars = Config.LoadOrDefault().Limits.PassthroughMaxChars;
        return FormatPassthroughOutputWithLimit(raw, maxChars);
    }

    /// <summary>
    /// Truncates <paramref name="raw"/> to at most <paramref name="maxChars"/> Unicode scalar values,
    /// marking the result truncated if it exceeded the limit. Faithful port of
    /// <c>format_passthrough_output_with_limit</c> (<c>vitest_cmd.rs</c>:371-379).
    /// </summary>
    /// <param name="raw">The raw output to (possibly) truncate.</param>
    /// <param name="maxChars">The maximum number of Unicode scalar values to keep.</param>
    /// <returns>The formatted result.</returns>
    internal static FormattedTestOutput FormatPassthroughOutputWithLimit(string raw, int maxChars)
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
    /// Renders the final printed output: appends a tee recovery hint when applicable. Faithful port of
    /// <c>render_test_output</c> (<c>vitest_cmd.rs</c>:381-395), wiring the real
    /// <see cref="Tee.ForceTeeHint(string, string, TeeConfig?)"/>/<see cref="Tee.TeeAndHint(string, string, int, TeeConfig?)"/>
    /// helpers into <see cref="RenderTestOutputWithHints"/>.
    /// </summary>
    /// <param name="filtered">The formatted (or passthrough) output.</param>
    /// <param name="raw">The raw combined output, used for teeing.</param>
    /// <param name="teeLabel">The tee file slug (e.g. <c>"vitest_run"</c>).</param>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <returns>The final text to print.</returns>
    internal static string RenderTestOutput(FormattedTestOutput filtered, string raw, string teeLabel, int exitCode) =>
        RenderTestOutputWithHints(
            filtered,
            raw,
            teeLabel,
            exitCode,
            static (r, label) => Tee.ForceTeeHint(r, label),
            static (r, label, code) => Tee.TeeAndHint(r, label, code));

    /// <summary>
    /// Testable core of <see cref="RenderTestOutput"/>: the two tee-hint strategies are injected so
    /// truncated-vs-non-truncated hint selection can be verified deterministically without touching
    /// disk. Faithful port of <c>render_test_output_with_hints</c> (<c>vitest_cmd.rs</c>:397-419).
    /// </summary>
    /// <param name="filtered">The formatted (or passthrough) output.</param>
    /// <param name="raw">The raw combined output, used for teeing.</param>
    /// <param name="teeLabel">The tee file slug.</param>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <param name="forceHint">Hint strategy used when <see cref="FormattedTestOutput.IsTruncated"/> is true.</param>
    /// <param name="teeHint">Hint strategy used when <see cref="FormattedTestOutput.IsTruncated"/> is false.</param>
    /// <returns>The final text to print.</returns>
    internal static string RenderTestOutputWithHints(
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
    internal sealed partial class VitestParser : OutputParser<TestResult>
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
            var lines = ReadCommand.SplitLines(output);
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
    /// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming, matching the
    /// convention <c>PnpmCommand.PnpmJsonContext</c> established.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
    [JsonSerializable(typeof(VitestJsonOutput))]
    private sealed partial class VitestJsonContext : JsonSerializerContext;
}
