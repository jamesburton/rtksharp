using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Js;
using RtkSharp.Parser;
using FormattedTestOutput = RtkSharp.Filters.Commands.Js.VitestFilters.FormattedTestOutput;

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
/// <b>Shared JSON schema.</b> <see cref="RtkSharp.Filters.Commands.Js.VitestFilters.VitestParser"/>'s tier-1 JSON shape
/// (<c>numTotalTests</c>/<c>numPassedTests</c>/<c>numFailedTests</c>/<c>numPendingTests</c>/
/// <c>testResults[].assertionResults[]</c>) is used identically by both vitest's <c>--reporter=json</c>
/// output and jest's <c>--json</c> output — this is the structural fact that justifies one shared
/// parser for both frameworks, confirmed directly against <c>VitestJsonOutput</c>
/// (<c>vitest_cmd.rs</c>:17-30) which carries no framework-specific fields.
/// </para>
/// </remarks>
public static class VitestCommand
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
    /// Formats a test run's raw output: the passthrough escape hatch (vitest only) bypasses the parser
    /// entirely; otherwise runs the shared 3-tier <see cref="VitestFilters.VitestParser"/> and renders
    /// via <see cref="TestResult.Format(FormatMode)"/>. Faithful port of <c>format_test_output</c>
    /// (<c>vitest_cmd.rs</c>:333-364). <c>LogFullAndFormat</c>/<c>LogDegradedAndFormat</c> below keep
    /// their verbosity-gated <c>Console.Error.Write</c>/<c>EmitDegradationWarning</c> diagnostic calls
    /// and delegate the actual formatting to <see cref="VitestFilters.FormatVitestSummary"/> (moved to
    /// <c>RtkSharp.Filters</c> in Task 7 of the filters-library extraction — pure text formatting, no
    /// Console I/O).
    /// </summary>
    /// <param name="framework">The framework name (<c>"vitest"</c> or <c>"jest"</c>), used in diagnostics.</param>
    /// <param name="stdout">The child process's captured stdout (parsed by <see cref="VitestFilters.VitestParser"/>).</param>
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

        var parseResult = new VitestFilters.VitestParser().Parse(stdout);
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

        return VitestFilters.FormatVitestSummary(data, mode);
    }

    private static FormattedTestOutput LogDegradedAndFormat(string framework, int verbose, TestResult data, IReadOnlyList<string> warnings, FormatMode mode)
    {
        if (verbose > 0)
        {
            OutputParserSupport.EmitDegradationWarning(framework, string.Join(", ", warnings));
        }

        return VitestFilters.FormatVitestSummary(data, mode);
    }

    private static FormattedTestOutput PassthroughFallback(string framework, string stdout)
    {
        OutputParserSupport.EmitPassthroughWarning(framework, "All parsing tiers failed");
        return FormatPassthroughOutput(stdout);
    }

    /// <summary>
    /// Truncates raw output for passthrough rendering using the configured passthrough character
    /// limit. Delegates to <see cref="VitestFilters.FormatPassthroughOutput"/> (moved to
    /// <c>RtkSharp.Filters</c> in Task 7 of the filters-library extraction, which uses a hardcoded
    /// default rather than reading <c>Config</c> — see that method's remarks).
    /// </summary>
    /// <param name="raw">The raw output to (possibly) truncate.</param>
    /// <returns>The formatted result.</returns>
    internal static FormattedTestOutput FormatPassthroughOutput(string raw) => VitestFilters.FormatPassthroughOutput(raw);

    /// <summary>
    /// Truncates <paramref name="raw"/> to at most <paramref name="maxChars"/> Unicode scalar values,
    /// marking the result truncated if it exceeded the limit. Delegates to
    /// <see cref="VitestFilters.FormatPassthroughOutputWithLimit"/>.
    /// </summary>
    /// <param name="raw">The raw output to (possibly) truncate.</param>
    /// <param name="maxChars">The maximum number of Unicode scalar values to keep.</param>
    /// <returns>The formatted result.</returns>
    internal static FormattedTestOutput FormatPassthroughOutputWithLimit(string raw, int maxChars) =>
        VitestFilters.FormatPassthroughOutputWithLimit(raw, maxChars);

    // -----------------------------------------------------------------------
    // render_test_output(_with_hints) (vitest_cmd.rs:381-419)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Renders the final printed output: appends a tee recovery hint when applicable. Faithful port of
    /// <c>render_test_output</c> (<c>vitest_cmd.rs</c>:381-395), wiring the real
    /// <see cref="Tee.ForceTeeHint(string, string, TeeConfig?)"/>/<see cref="Tee.TeeAndHint(string, string, int, TeeConfig?)"/>
    /// helpers into <see cref="VitestFilters.RenderTestOutputWithHints"/>.
    /// </summary>
    /// <param name="filtered">The formatted (or passthrough) output.</param>
    /// <param name="raw">The raw combined output, used for teeing.</param>
    /// <param name="teeLabel">The tee file slug (e.g. <c>"vitest_run"</c>).</param>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <returns>The final text to print.</returns>
    internal static string RenderTestOutput(FormattedTestOutput filtered, string raw, string teeLabel, int exitCode) =>
        VitestFilters.RenderTestOutputWithHints(
            filtered,
            raw,
            teeLabel,
            exitCode,
            static (r, label) => Tee.ForceTeeHint(r, label),
            static (r, label, code) => Tee.TeeAndHint(r, label, code));

}
