using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Js;
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
public static class PlaywrightCommand
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
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // CommandArgumentParseException must propagate to RtkProgram's dispatch layer,
            // which re-routes it to the TOML-fallback/raw-passthrough path — it is not a
            // generic runtime failure, so this safety net must not swallow it. Not thrown from
            // this command today, but this guard is added proactively (playwright is
            // Rust-classified PASSTHROUGH) so a future migration here can't silently regress
            // into the same swallowed-exception bug found in DockerCommand/PrismaCommand.
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

        var parseResult = new PlaywrightFilters.PlaywrightParser().Parse(result.Stdout);
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
    /// Renders the final printed output: appends a tee recovery hint unconditionally. Delegates to
    /// <see cref="PlaywrightFilters.RenderOutput"/> (moved to <c>RtkSharp.Filters</c> in Task 7 of the
    /// filters-library extraction — pure text/hint-strategy composition, no process execution or file
    /// I/O).
    /// </summary>
    /// <param name="filtered">The formatted (or passthrough) output.</param>
    /// <param name="raw">The raw combined output, used for teeing.</param>
    /// <param name="exitCode">The child process's exit code.</param>
    /// <param name="teeHint">The (always-invoked) hint strategy.</param>
    /// <returns>The final text to print.</returns>
    internal static string RenderOutput(string filtered, string raw, int exitCode, Func<string, string, int, string?> teeHint) =>
        PlaywrightFilters.RenderOutput(filtered, raw, exitCode, teeHint);

    /// <summary>
    /// Formats a Tier-1 (full JSON parse) result and logs the diagnostic. The pure formatting is
    /// delegated to <see cref="PlaywrightFilters.FormatFullResults"/> (moved to <c>RtkSharp.Filters</c>
    /// in Task 7 of the filters-library extraction) — this wrapper keeps only the
    /// <c>Console.Error.Write</c> diagnostic.
    /// </summary>
    private static string LogFullAndFormat(int verbose, TestResult data, FormatMode mode)
    {
        if (verbose > 0)
        {
            Console.Error.Write("playwright test (Tier 1: Full JSON parse)\n");
        }

        return PlaywrightFilters.FormatFullResults(data, mode);
    }

    /// <summary>
    /// Formats a Tier-2 (regex-fallback, degraded) result and logs the diagnostic. The pure formatting
    /// is delegated to <see cref="PlaywrightFilters.FormatDegradedResults"/> — this wrapper keeps only
    /// the <see cref="OutputParserSupport.EmitDegradationWarning"/> diagnostic.
    /// </summary>
    private static string LogDegradedAndFormat(int verbose, TestResult data, IReadOnlyList<string> warnings, FormatMode mode)
    {
        if (verbose > 0)
        {
            OutputParserSupport.EmitDegradationWarning("playwright", string.Join(", ", warnings));
        }

        return PlaywrightFilters.FormatDegradedResults(data, mode);
    }

    private static string PassthroughFallback(string raw)
    {
        OutputParserSupport.EmitPassthroughWarning("playwright", "All parsing tiers failed");
        return raw;
    }

}
