using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RtkSharp.Filters.Commands.Dotnet;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Dotnet;

/// <summary>
/// How the targeted test project(s) run tests — determines which TRX-reporting flags to inject.
/// Ported from Rust's <c>TestRunnerMode</c> in <c>dotnet_cmd.rs</c>.
/// </summary>
internal enum TestRunnerMode
{
    /// <summary>Classic VSTest runner. Inject <c>--logger trx --results-directory</c>.</summary>
    Classic,

    /// <summary>
    /// Native Microsoft.Testing.Platform runner (global.json MTP mode). <c>--logger trx</c> breaks
    /// the run; inject <c>--report-trx</c> directly.
    /// </summary>
    MtpNative,

    /// <summary>
    /// VSTest bridge for MTP (project-file / <c>Directory.Build.props</c> properties). MTP args must
    /// come after the <c>--</c> separator; inject <c>-- --report-trx</c>.
    /// </summary>
    MtpVsTestBridge,
}

/// <summary>
/// Filters <c>dotnet</c> CLI output. This verb dispatches on its first argument (the
/// dotnet subcommand) and mirrors the Rust <c>src/cmds/dotnet/dotnet_cmd.rs</c> routing:
/// <c>build</c>/<c>restore</c> get text-based summary filters, everything else falls
/// through to raw passthrough with exit-code propagation.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>run_build</c>/<c>run_restore</c>/<c>run_passthrough</c> in
/// <c>dotnet_cmd.rs</c> plus the text-parsing helpers in <c>src/cmds/dotnet/binlog.rs</c>
/// (<c>parse_build_from_text</c>, <c>parse_restore_from_text</c>,
/// <c>parse_restore_issues_from_text</c>).
/// </para>
/// <para>
/// <b>Binlog deferral.</b> The Rust default path injects <c>-bl:&lt;tmp&gt;</c> into
/// <c>dotnet build</c>/<c>restore</c> and parses the resulting MSBuild binary log, merging
/// it with a text-parsed fallback. Binlog parsing (<c>binlog.rs</c>'s binary reader) is
/// explicitly deferred for this phase, so this port injects only the text-shaping flags
/// (<c>-v:minimal</c>, <c>-nologo</c>) and relies solely on the text parser. The binlog is
/// a side file that does not alter stdout, so the captured text is identical to the
/// oracle's; only the enrichment of individual diagnostic lines (real file/code/message
/// vs. a "details omitted" placeholder) is lost when binlog is deferred. Diagnostic counts
/// and the verdict line are unaffected. See the task report for details.
/// </para>
/// <para>
/// <b>Windows drive-letter paths (intentional deviation, ledgered).</b> The <c>IssueRegex</c> in
/// <see cref="RtkSharp.Filters.Commands.Dotnet.DotnetFilters"/> widens Rust's file-capture group
/// with an optional drive-letter prefix, so Windows-absolute diagnostic lines are enriched (real
/// file/line/col/code/message) even without the binlog — see the regex's own comment and
/// <c>docs/parity/compatibility-ledger.md</c>. The "enrichment lost without binlog" statement
/// above still holds for non-drive-letter paths.
/// </para>
/// <para>
/// <b>File-based app support (superset feature, no Rust oracle).</b> <c>dotnet run
/// &lt;file&gt;.cs</c> and the <c>dotnet &lt;file&gt;.cs</c> shorthand (.NET 10+) are detected
/// and routed to <see cref="RunFileBasedAppAsync"/>. Success is pure passthrough (a working
/// file-based app already prints nothing but its own program output). Failure is filtered by
/// <see cref="RtkSharp.Filters.Commands.Dotnet.DotnetFilters.FilterFileBasedApp"/>, which tries,
/// in order: the same <c>IssueRegex</c>-driven parsing <c>build</c>/<c>restore</c> already use
/// (covers genuine compile errors for free), a no-location MSBuild/NuGet-diagnostic path, plus
/// one narrow new regex for diagnostics with no numeric code, an unhandled-runtime-exception
/// summary, and finally a raw-passthrough fallback for anything unrecognized. See
/// <c>docs/superpowers/specs/2026-07-08-cs-file-based-app-execution-design.md</c> for the full
/// design rationale and empirical findings this is based on.
/// </para>
/// </remarks>
public static class DotnetCommand
{
    private const string DotnetCliUiLanguage = "DOTNET_CLI_UI_LANGUAGE";
    private const string DotnetCliUiLanguageValue = "en-US";

    /// <summary>
    /// Entry point for the <c>dotnet</c> verb. Dispatches on the first argument (the dotnet
    /// subcommand) exactly as Rust's <c>Commands::Dotnet</c> routing does.
    /// </summary>
    /// <param name="args">The arguments following the <c>dotnet</c> verb (subcommand first).</param>
    /// <returns>The child <c>dotnet</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, new ProcessExecutor());

    /// <summary>
    /// Test-friendly overload that accepts an explicit <see cref="IProcessExecutor"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>dotnet</c> verb (subcommand first).</param>
    /// <param name="executor">The process executor used to run <c>dotnet</c>.</param>
    /// <returns>The child <c>dotnet</c> process's exit code.</returns>
    internal static async Task<int> RunAsync(string[] args, IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(executor);

        if (args.Length == 0)
        {
            Console.Error.Write("dotnet: no subcommand specified\n");
            return 1;
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "build" => await RunTextFilteredAsync("build", rest, executor).ConfigureAwait(false),
            "restore" => await RunTextFilteredAsync("restore", rest, executor).ConfigureAwait(false),
            "test" => await RunTestAsync(rest, executor).ConfigureAwait(false),
            "format" => await RunFormatAsync(rest, executor).ConfigureAwait(false),
            "run" when FindFileBasedAppArg(rest) is { } csFile =>
                await RunFileBasedAppAsync(args, csFile, executor, usedRunKeyword: true).ConfigureAwait(false),
            _ when subcommand.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) =>
                await RunFileBasedAppAsync(args, subcommand, executor, usedRunKeyword: false).ConfigureAwait(false),
            _ => await RunPassthroughAsync(args, executor).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Runs <c>dotnet &lt;subcommand&gt;</c> and prints a text-parsed summary. Ports the
    /// build/restore arms of Rust's <c>run_dotnet_with_binlog</c> (text-only path).
    /// </summary>
    private static async Task<int> RunTextFilteredAsync(string subcommand, string[] rest, IProcessExecutor executor)
    {
        var invocation = new List<string> { subcommand };
        invocation.AddRange(BuildEffectiveArgs(subcommand, rest));

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DotnetCliUiLanguage] = DotnetCliUiLanguageValue,
        };

        var result = await executor
            .ExecuteAsync(new ExecutionRequest("dotnet", invocation, Environment: environment, CaptureMode: ExecutionCaptureMode.Separate))
            .ConfigureAwait(false);

        // Rust builds the parse input as format!("{}\n{}", stdout, stderr).
        var raw = result.Stdout + "\n" + result.Stderr;
        var commandSuccess = result.ExitCode == 0;

        string filtered;
        try
        {
            filtered = subcommand == "build"
                ? DotnetFilters.FilterBuild(raw, commandSuccess)
                : DotnetFilters.FilterRestore(raw, commandSuccess);
        }
        catch (Exception ex)
        {
            // Mandatory fallback contract: a filter must never crash or hide output.
            Console.Error.Write($"rtk: filter warning: {ex.Message}\n");
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            return result.ExitCode;
        }

        // Build/restore always request the raw fallback on failure (needs_raw_fallback = true).
        var outputToPrint = ComposeFailureOutput(commandSuccess, needsRawFallback: true, result.Stdout, result.Stderr, filtered);

        // Rust println! terminates with "\n"; Console.WriteLine would emit Environment.NewLine.
        Console.Out.Write(outputToPrint + "\n");
        return result.ExitCode;
    }

    /// <summary>
    /// Runs an unhandled <c>dotnet</c> subcommand unfiltered, propagating both streams and
    /// the exit code. Ports Rust's <c>run_passthrough</c>.
    /// </summary>
    private static async Task<int> RunPassthroughAsync(string[] args, IProcessExecutor executor)
    {
        // args[0] is the subcommand; args are already non-empty (checked by the caller).
        var result = await ExecuteDotnetAsync(args, executor).ConfigureAwait(false);

        Console.Out.Write(result.Stdout);
        Console.Error.Write(result.Stderr);
        return result.ExitCode;
    }

    /// <summary>
    /// Executes <c>dotnet &lt;invocationArgs&gt;</c> with the shared <see cref="DotnetCliUiLanguage"/>
    /// environment override and <see cref="ExecutionCaptureMode.Separate"/> capture. Shared by
    /// <see cref="RunPassthroughAsync"/> and <see cref="RunFileBasedAppAsync"/>, whose invocations
    /// are otherwise byte-for-byte identical (DRY extraction; see project CLAUDE.md).
    /// </summary>
    private static ValueTask<ExecutionResult> ExecuteDotnetAsync(IReadOnlyList<string> invocationArgs, IProcessExecutor executor)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DotnetCliUiLanguage] = DotnetCliUiLanguageValue,
        };

        return executor.ExecuteAsync(new ExecutionRequest("dotnet", invocationArgs, Environment: environment, CaptureMode: ExecutionCaptureMode.Separate));
    }

    // --- file-based app path (dotnet run <file>.cs / dotnet <file>.cs; .NET 10+ only, no Rust equivalent) ---

    /// <summary>
    /// Finds the first non-option argument ending in <c>.cs</c> before any bare <c>--</c>
    /// separator, identifying a <c>dotnet run &lt;file&gt;.cs</c> file-based-app invocation.
    /// Returns null for ordinary project-based <c>dotnet run</c> (no <c>.cs</c> argument, or
    /// a <c>.cs</c>-looking token that is actually a post-<c>--</c> program argument).
    /// </summary>
    /// <remarks>
    /// Known, accepted limitation: this heuristic cannot distinguish the file-based-app path
    /// from an option's *value* — e.g. a hypothetical future <c>dotnet run --someopt value.cs</c>
    /// flag whose value happens to end in <c>.cs</c> would be misdetected as the app file. No
    /// real <c>dotnet run</c> flag takes a <c>.cs</c>-suffixed value today, so this is not
    /// treated as a bug; revisit if that ever changes.
    /// </remarks>
    private static string? FindFileBasedAppArg(string[] rest)
    {
        foreach (var arg in rest)
        {
            if (arg == "--")
            {
                return null;
            }

            if (arg.Length > 0 && arg[0] != '-' && arg.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs a .NET 10 file-based app (<c>dotnet run &lt;file&gt;.cs</c> or the <c>dotnet
    /// &lt;file&gt;.cs</c> shorthand). A superset feature with no Rust equivalent: success is
    /// pure passthrough (a working file-based app already prints nothing but its own program
    /// output), failure is filtered by <see cref="RtkSharp.Filters.Commands.Dotnet.DotnetFilters.FilterFileBasedApp"/>.
    /// </summary>
    /// <remarks>
    /// Detection tiers in <see cref="RtkSharp.Filters.Commands.Dotnet.DotnetFilters.FilterFileBasedApp"/> key off stdout/stderr content
    /// markers, never off a specific exit-code value — an unhandled runtime exception was
    /// observed to exit 127 during design-phase testing, which is unusual for .NET (typically
    /// a large HRESULT-style code) and was not independently reconfirmed. This method always
    /// propagates <c>result.ExitCode</c> verbatim regardless of which tier matched, so that
    /// uncertainty has no effect on correctness here.
    /// </remarks>
    private static async Task<int> RunFileBasedAppAsync(string[] args, string fileDisplayName, IProcessExecutor executor, bool usedRunKeyword)
    {
        var result = await ExecuteDotnetAsync(args, executor).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            return result.ExitCode;
        }

        var raw = result.Stdout + "\n" + result.Stderr;
        try
        {
            var filtered = DotnetFilters.FilterFileBasedApp(raw, fileDisplayName, usedRunKeyword);
            Console.Out.Write(filtered + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: filter warning: {ex.Message}\n");
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
        }

        return result.ExitCode;
    }

    // --- format path (ported from run_format in dotnet_cmd.rs) ---

    /// <summary>
    /// Runs <c>dotnet format</c>, injects a JSON <c>--report</c> target (plus <c>--verify-no-changes</c>
    /// unless the caller passed <c>--write</c>), then prints a compact files-needing-formatting summary
    /// parsed from the report. Ports Rust's <c>run_format</c>.
    /// </summary>
    private static async Task<int> RunFormatAsync(string[] rest, IProcessExecutor executor)
    {
        var (reportPath, cleanupReportPath) = ResolveFormatReportPath(rest);

        var invocation = new List<string> { "format" };
        invocation.AddRange(BuildEffectiveDotnetFormatArgs(rest, reportPath));

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DotnetCliUiLanguage] = DotnetCliUiLanguageValue,
        };

        var commandStartedAt = DateTime.UtcNow;
        var result = await executor
            .ExecuteAsync(new ExecutionRequest("dotnet", invocation, Environment: environment, CaptureMode: ExecutionCaptureMode.Separate))
            .ConfigureAwait(false);

        var raw = result.Stdout + "\n" + result.Stderr;
        var checkMode = !HasWriteModeOverride(rest);

        string filtered;
        try
        {
            filtered = FormatReportSummaryOrRaw(reportPath, checkMode, raw, commandStartedAt);
        }
        catch (Exception ex)
        {
            // Mandatory fallback contract: a filter must never crash or hide output.
            Console.Error.Write($"rtk: filter warning: {ex.Message}\n");
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            CleanupTempFile(cleanupReportPath, reportPath);
            return result.ExitCode;
        }

        Console.Out.Write(filtered + "\n");
        CleanupTempFile(cleanupReportPath, reportPath);
        return result.ExitCode;
    }

    private static void CleanupTempFile(bool cleanup, string? path)
    {
        if (!cleanup || path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup — a leftover temp report file must never fail the command.
        }
    }

    /// <summary>
    /// Resolves the format-report JSON path: honors an explicit <c>--report</c> arg (no cleanup),
    /// otherwise mints an isolated temp file that should be cleaned up afterwards. Ports Rust's
    /// <c>resolve_format_report_path</c>.
    /// </summary>
    private static (string? Path, bool Cleanup) ResolveFormatReportPath(string[] args)
    {
        var userPath = ExtractReportArg(args);
        if (userPath is not null)
        {
            return (userPath, false);
        }

        return (BuildFormatReportPath(), true);
    }

    private static string BuildFormatReportPath() =>
        Path.Combine(Path.GetTempPath(), $"rtk_dotnet_format_{UniqueTempSuffix()}.json");

    /// <summary>
    /// Builds the effective <c>dotnet format</c> arguments: strips any user <c>--write</c> flag (it is
    /// re-added only via <see cref="HasWriteModeOverride"/> semantics — check mode is the default),
    /// injects <c>--verify-no-changes</c> unless writing was explicitly requested or the user already
    /// supplied it, and injects <c>--report &lt;path&gt;</c> unless the user already supplied one. Ports
    /// Rust's <c>build_effective_dotnet_format_args</c>.
    /// </summary>
    /// <param name="args">The user-supplied arguments (after the <c>format</c> subcommand).</param>
    /// <param name="reportPath">The report path to inject, or null to skip injection.</param>
    /// <returns>The effective argument list.</returns>
    internal static List<string> BuildEffectiveDotnetFormatArgs(string[] args, string? reportPath)
    {
        var effective = args.Where(arg => !arg.Equals("--write", StringComparison.OrdinalIgnoreCase)).ToList();
        var forceWriteMode = HasWriteModeOverride(args);

        if (!forceWriteMode && !HasVerifyNoChangesArg(args))
        {
            effective.Add("--verify-no-changes");
        }

        if (!HasReportArg(args) && reportPath is not null)
        {
            effective.Add("--report");
            effective.Add(reportPath);
        }

        return effective;
    }

    private static bool HasReportArg(string[] args) => args.Any(arg =>
    {
        var lower = arg.ToLowerInvariant();
        return lower == "--report" || lower.StartsWith("--report=", StringComparison.Ordinal);
    });

    private static string? ExtractReportArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--report", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                continue;
            }

            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0 && arg[..eq].Equals("--report", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(eq + 1)..];
            }
        }

        return null;
    }

    private static bool HasVerifyNoChangesArg(string[] args) => args.Any(arg =>
    {
        var lower = arg.ToLowerInvariant();
        return lower == "--verify-no-changes" || lower.StartsWith("--verify-no-changes=", StringComparison.Ordinal);
    });

    private static bool HasWriteModeOverride(string[] args) =>
        args.Any(arg => arg.Equals("--write", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Chooses between the parsed format-report summary and raw passthrough: falls back to
    /// <paramref name="raw"/> when there is no report path, the report file predates the command
    /// (a stale leftover at a user-specified path), or the report fails to read/parse. Ports Rust's
    /// <c>format_report_summary_or_raw</c>.
    /// </summary>
    /// <param name="reportPath">The report path used for this run, or null if none was resolved.</param>
    /// <param name="checkMode">Whether the run was in verify/check mode (no <c>--write</c>).</param>
    /// <param name="raw">The combined stdout+stderr from <c>dotnet format</c>, used as the fallback.</param>
    /// <param name="commandStartedAt">When the <c>dotnet format</c> invocation started.</param>
    /// <returns>The filtered summary, or <paramref name="raw"/> on any fallback condition.</returns>
    internal static string FormatReportSummaryOrRaw(string? reportPath, bool checkMode, string raw, DateTime commandStartedAt)
    {
        if (reportPath is null)
        {
            return raw;
        }

        if (!IsFreshReport(reportPath, commandStartedAt))
        {
            return raw;
        }

        try
        {
            var summary = DotnetFormatReport.ParseFormatReport(reportPath);
            return DotnetFilters.FormatDotnetFormatOutput(summary, checkMode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return raw;
        }
    }

    /// <summary>
    /// Reports whether the report file at <paramref name="path"/> was written at or after
    /// <paramref name="commandStartedAt"/> — i.e. it is fresh output from this run rather than a stale
    /// leftover. Ports Rust's <c>is_fresh_report</c>.
    /// </summary>
    private static bool IsFreshReport(string path, DateTime commandStartedAt)
    {
        try
        {
            var modifiedAt = File.GetLastWriteTimeUtc(path);
            return modifiedAt >= commandStartedAt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // --- test path (ported from run_dotnet_with_binlog's "test" arm in dotnet_cmd.rs) ---

    private static long testTempCounter;

    /// <summary>
    /// Runs <c>dotnet test</c>, injects a TRX logger + isolated results directory (Classic VSTest)
    /// or the equivalent MTP flags, then prints a compact pass/fail summary with failed-test detail
    /// parsed from the resulting TRX. Ports the <c>"test"</c> arm of Rust's
    /// <c>run_dotnet_with_binlog</c> plus <c>run_test</c>.
    /// </summary>
    /// <remarks>
    /// <b>Binlog deferral.</b> For <c>test</c>, Rust only expects a binlog when the user explicitly
    /// passes <c>-bl</c> (<c>should_expect_binlog = has_binlog_arg(args)</c>). The default path — the
    /// one this port implements — never injects <c>-bl</c> and relies entirely on the console text
    /// parser plus TRX. Binlog-enhanced diagnostics are therefore not lost relative to the default
    /// oracle behavior. See the task report.
    /// </remarks>
    private static async Task<int> RunTestAsync(string[] rest, IProcessExecutor executor)
    {
        var (resultsDir, cleanupResultsDir) = ResolveTrxResultsDir(rest);
        var runnerMode = DetectTestRunnerMode(rest);

        var invocation = new List<string> { "test" };
        invocation.AddRange(BuildEffectiveTestArgs(rest, runnerMode, resultsDir));

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DotnetCliUiLanguage] = DotnetCliUiLanguageValue,
        };

        var commandStartedAt = DateTime.UtcNow;
        var result = await executor
            .ExecuteAsync(new ExecutionRequest("dotnet", invocation, Environment: environment, CaptureMode: ExecutionCaptureMode.Separate))
            .ConfigureAwait(false);

        var raw = result.Stdout + "\n" + result.Stderr;
        var commandSuccess = result.ExitCode == 0;

        string filtered;
        bool needsRawFallback;
        try
        {
            // Binlog path deferred: parsed_summary starts empty and the text parser fills it.
            var rawSummary = DotnetFilters.ParseTestFromText(raw);
            var merged = MergeTestSummaries(new TestSummary(), rawSummary);
            var summary = MergeTestSummaryFromTrx(
                merged,
                resultsDir,
                DotnetTrx.FindRecentTrxInTestResults(),
                commandStartedAt);
            summary = NormalizeTestSummary(summary, commandSuccess);

            // Build diagnostics carried alongside test results come from the text parser only
            // (binlog deferred).
            var testBuildSummary = DotnetFilters.ParseBuildFromText(raw);

            needsRawFallback = TestNeedsRawFallback(summary);
            filtered = DotnetFilters.FormatTestOutput(summary, testBuildSummary.Errors, testBuildSummary.Warnings);
        }
        catch (Exception ex)
        {
            // Mandatory fallback contract: a filter must never crash or hide output.
            Console.Error.Write($"rtk: filter warning: {ex.Message}\n");
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            CleanupResultsDir(cleanupResultsDir, resultsDir);
            return result.ExitCode;
        }

        var outputToPrint = ComposeFailureOutput(commandSuccess, needsRawFallback, result.Stdout, result.Stderr, filtered);
        Console.Out.Write(outputToPrint + "\n");

        CleanupResultsDir(cleanupResultsDir, resultsDir);
        return result.ExitCode;
    }

    /// <summary>
    /// Resolves the TRX results directory for a <c>dotnet test</c> run: honors an explicit
    /// <c>--results-directory</c> (no cleanup), otherwise mints an isolated temp directory that
    /// should be cleaned up afterwards. Ports Rust's <c>resolve_trx_results_dir</c>.
    /// </summary>
    private static (string? Dir, bool Cleanup) ResolveTrxResultsDir(string[] args)
    {
        var userDir = ExtractResultsDirectoryArg(args);
        if (userDir is not null)
        {
            return (userDir, false);
        }

        return (BuildTrxResultsDir(), true);
    }

    private static string BuildTrxResultsDir() =>
        Path.Combine(Path.GetTempPath(), $"rtk_dotnet_testresults_{UniqueTempSuffix()}");

    private static string UniqueTempSuffix()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pid = Environment.ProcessId;
        var seq = Interlocked.Increment(ref testTempCounter) - 1;
        return $"{ts:x}{pid:x}{seq:x}";
    }

    private static void CleanupResultsDir(bool cleanup, string? dir)
    {
        if (!cleanup || dir is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup — a leftover temp directory must never fail the command.
        }
    }

    /// <summary>
    /// Builds the effective <c>dotnet test</c> arguments, injecting the TRX-reporting flags that
    /// match the detected runner mode. Ports the <c>test</c> branch of Rust's
    /// <c>build_effective_dotnet_args</c> (binlog injection deferred).
    /// </summary>
    /// <param name="args">The user-supplied arguments (after the <c>test</c> subcommand).</param>
    /// <param name="mode">The detected test runner mode.</param>
    /// <param name="resultsDir">The TRX results directory to inject (Classic mode only).</param>
    /// <returns>The effective argument list.</returns>
    internal static List<string> BuildEffectiveTestArgs(string[] args, TestRunnerMode mode, string? resultsDir)
    {
        var effective = new List<string>();

        // -bl (binlog) and -v:minimal are non-test-only in Rust; skipped for test.

        // --nologo: skipped for MtpNative — args pass directly to the MTP runtime.
        if (mode != TestRunnerMode.MtpNative && !HasNoLogoArg(args))
        {
            effective.Add("-nologo");
        }

        switch (mode)
        {
            case TestRunnerMode.Classic:
                if (!HasTrxLoggerArg(args))
                {
                    effective.Add("--logger");
                    effective.Add("trx");
                }

                if (!HasResultsDirectoryArg(args) && resultsDir is not null)
                {
                    effective.Add("--results-directory");
                    effective.Add(resultsDir);
                }

                effective.AddRange(args);
                break;

            case TestRunnerMode.MtpNative:
                if (!HasReportTrxArg(args))
                {
                    effective.Add("--report-trx");
                }

                effective.AddRange(args);
                break;

            case TestRunnerMode.MtpVsTestBridge:
                if (!HasReportTrxArg(args))
                {
                    effective.AddRange(InjectReportTrxIntoArgs(args));
                }
                else
                {
                    effective.AddRange(args);
                }

                break;
        }

        return effective;
    }

    // --- test arg predicates (ported from dotnet_cmd.rs) ---

    private static bool HasTrxLoggerArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var lower = args[i].ToLowerInvariant();
            if (lower == "--logger")
            {
                if (i + 1 < args.Length)
                {
                    var next = args[i + 1].ToLowerInvariant();
                    if (next == "trx" || next.StartsWith("trx;", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                continue;
            }

            foreach (var prefix in new[] { "--logger:", "--logger=" })
            {
                if (lower.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var value = lower[prefix.Length..];
                    if (value == "trx" || value.StartsWith("trx;", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool HasResultsDirectoryArg(string[] args) => args.Any(arg =>
    {
        var lower = arg.ToLowerInvariant();
        return lower == "--results-directory" || lower.StartsWith("--results-directory=", StringComparison.Ordinal);
    });

    private static bool HasReportTrxArg(string[] args) =>
        args.Any(a => a.Equals("--report-trx", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractResultsDirectoryArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--results-directory", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                continue;
            }

            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0 && arg[..eq].Equals("--results-directory", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(eq + 1)..];
            }
        }

        return null;
    }

    /// <summary>
    /// Injects <c>--report-trx</c> after the <c>--</c> separator, or appends <c>-- --report-trx</c>
    /// if there is none. Ports Rust's <c>inject_report_trx_into_args</c>.
    /// </summary>
    private static List<string> InjectReportTrxIntoArgs(string[] args)
    {
        var result = new List<string>(args);
        var sep = result.IndexOf("--");
        if (sep >= 0)
        {
            result.Insert(sep + 1, "--report-trx");
        }
        else
        {
            result.Add("--");
            result.Add("--report-trx");
        }

        return result;
    }

    /// <summary>
    /// Detects which test runner the targeted project(s) use, mirroring Rust's
    /// <c>detect_test_runner_mode</c>. Priority: <c>global.json</c> native MTP mode &gt;
    /// project-file / <c>Directory.Build.props</c> VSTest-bridge MTP &gt; Classic VSTest.
    /// </summary>
    /// <param name="args">The user-supplied test arguments (scanned for explicit project paths).</param>
    /// <returns>The detected runner mode.</returns>
    internal static TestRunnerMode DetectTestRunnerMode(string[] args)
    {
        if (IsGlobalJsonMtpMode())
        {
            return TestRunnerMode.MtpNative;
        }

        string[] projectExtensions = { "csproj", "fsproj", "vbproj" };

        var explicitProjects = args
            .Where(a =>
            {
                var lower = a.ToLowerInvariant();
                return projectExtensions.Any(ext => lower.EndsWith($".{ext}", StringComparison.Ordinal));
            })
            .ToList();

        var foundBridge = false;

        if (explicitProjects.Count > 0)
        {
            foreach (var project in explicitProjects)
            {
                if (ScanIsVsTestBridge(project))
                {
                    foundBridge = true;
                }
            }
        }
        else
        {
            try
            {
                foreach (var entry in Directory.EnumerateFiles("."))
                {
                    var name = Path.GetFileName(entry).ToLowerInvariant();
                    if (projectExtensions.Any(ext => name.EndsWith($".{ext}", StringComparison.Ordinal))
                        && ScanIsVsTestBridge(entry))
                    {
                        foundBridge = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Directory unreadable — fall through to the Directory.Build.props / Classic checks.
            }
        }

        if (foundBridge)
        {
            return TestRunnerMode.MtpVsTestBridge;
        }

        try
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir is not null)
            {
                var props = Path.Combine(dir.FullName, "Directory.Build.props");
                if (File.Exists(props))
                {
                    return ScanIsVsTestBridge(props)
                        ? TestRunnerMode.MtpVsTestBridge
                        : TestRunnerMode.Classic;
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore — default to Classic.
        }

        return TestRunnerMode.Classic;
    }

    private static bool IsGlobalJsonMtpMode()
    {
        try
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir is not null)
            {
                var path = Path.Combine(dir.FullName, "global.json");
                if (File.Exists(path))
                {
                    return ParseGlobalJsonMtpMode(path); // stop at first global.json found
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore — treat as non-MTP.
        }

        return false;
    }

    private static bool ParseGlobalJsonMtpMode(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("test", out var test)
                && test.TryGetProperty("runner", out var runner)
                && runner.ValueKind == JsonValueKind.String)
            {
                return string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Malformed or unreadable global.json — treat as non-MTP.
        }

        return false;
    }

    private static readonly string[] VsTestBridgeProperties =
    {
        "usemicrosofttestingplatformrunner",
        "usetestingplatformrunner",
        "testingplatformdotnettestsupport",
    };

    /// <summary>
    /// Scans an MSBuild file for an MTP VSTest-bridge property set to <c>true</c>. Ports Rust's
    /// <c>scan_mtp_kind_in_file</c> (returns whether the file declares a bridge property).
    /// </summary>
    private static bool ScanIsVsTestBridge(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (XmlException)
        {
            return false;
        }

        return doc.Descendants().Any(e =>
            VsTestBridgeProperties.Contains(e.Name.LocalName.ToLowerInvariant())
            && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    // --- test merge / normalize (ported from dotnet_cmd.rs) ---

    private static TestSummary MergeTestSummaries(TestSummary binlogSummary, TestSummary rawSummary)
    {
        if (binlogSummary.Total == 0 && rawSummary.Total > 0)
        {
            binlogSummary.Passed = rawSummary.Passed;
            binlogSummary.Failed = rawSummary.Failed;
            binlogSummary.Skipped = rawSummary.Skipped;
            binlogSummary.Total = rawSummary.Total;
        }

        if (rawSummary.FailedTests.Count > 0)
        {
            binlogSummary.FailedTests = rawSummary.FailedTests;
        }

        if (binlogSummary.ProjectCount == 0)
        {
            binlogSummary.ProjectCount = rawSummary.ProjectCount;
        }

        binlogSummary.DurationText ??= rawSummary.DurationText;

        return binlogSummary;
    }

    /// <summary>
    /// Overlays authoritative TRX counts/failures/duration onto a text-parsed summary. Ports Rust's
    /// <c>merge_test_summary_from_trx</c>: prefers the isolated results directory (filtered by run
    /// start time, then unfiltered), then a fallback <c>./TestResults</c> TRX.
    /// </summary>
    internal static TestSummary MergeTestSummaryFromTrx(
        TestSummary summary,
        string? trxResultsDir,
        string? fallbackTrxPath,
        DateTime commandStartedAt)
    {
        TestSummary? trxSummary = null;

        if (trxResultsDir is not null && Directory.Exists(trxResultsDir))
        {
            trxSummary = DotnetTrx.ParseTrxFilesInDirSince(trxResultsDir, commandStartedAt)
                ?? DotnetTrx.ParseTrxFilesInDir(trxResultsDir);
        }

        if (trxSummary is null && fallbackTrxPath is not null)
        {
            trxSummary = DotnetTrx.ParseTrxFileSince(fallbackTrxPath, commandStartedAt);
        }

        if (trxSummary is null)
        {
            return summary;
        }

        if (trxSummary.Total > 0 && (summary.Total == 0 || trxSummary.Total >= summary.Total))
        {
            summary.Passed = trxSummary.Passed;
            summary.Failed = trxSummary.Failed;
            summary.Skipped = trxSummary.Skipped;
            summary.Total = trxSummary.Total;
        }

        if (summary.FailedTests.Count == 0 && trxSummary.FailedTests.Count > 0)
        {
            summary.FailedTests = trxSummary.FailedTests;
        }

        if (trxSummary.DurationText is not null)
        {
            summary.DurationText = trxSummary.DurationText;
        }

        if (trxSummary.ProjectCount > summary.ProjectCount)
        {
            summary.ProjectCount = trxSummary.ProjectCount;
        }

        return summary;
    }

    private static TestSummary NormalizeTestSummary(TestSummary summary, bool commandSuccess)
    {
        if (!commandSuccess && summary.Failed == 0 && summary.FailedTests.Count == 0)
        {
            summary.Failed = 1;
            if (summary.Total == 0)
            {
                summary.Total = 1;
            }
        }

        if (commandSuccess && summary.Total == 0 && summary.Passed == 0)
        {
            summary.ProjectCount = Math.Max(summary.ProjectCount, 1);
        }

        return summary;
    }

    /// <summary>
    /// Decides whether the raw stdout/stderr should be prepended ahead of the filtered summary on a
    /// failing test run. Ports Rust's <c>test_needs_raw_fallback</c>: keep the raw fallback only when
    /// the structured <c>Failed Tests:</c> section can't stand on its own (no failures parsed, fewer
    /// parsed failures than the failed count, or a parsed failure with no detail).
    /// </summary>
    internal static bool TestNeedsRawFallback(TestSummary summary) =>
        summary.FailedTests.Count == 0
        || summary.FailedTests.Count < summary.Failed
        || summary.FailedTests.Any(t => t.Details.Count == 0);

    /// <summary>
    /// Computes the effective <c>dotnet</c> arguments, injecting the text-shaping flags
    /// <c>-v:minimal</c> and <c>-nologo</c> unless the user already supplied them. Mirrors
    /// Rust's <c>build_effective_dotnet_args</c> for non-test subcommands, minus the deferred
    /// <c>-bl</c> binlog injection.
    /// </summary>
    /// <param name="subcommand">The dotnet subcommand (e.g. <c>"build"</c>).</param>
    /// <param name="args">The user-supplied arguments.</param>
    /// <returns>The effective argument list (injected flags first, then user args).</returns>
    internal static List<string> BuildEffectiveArgs(string subcommand, string[] args)
    {
        var effective = new List<string>();

        // Binlog (-bl) injection is deferred; see the class remarks.

        if (!HasVerbosityArg(args))
        {
            effective.Add("-v:minimal");
        }

        if (!HasNoLogoArg(args))
        {
            effective.Add("-nologo");
        }

        effective.AddRange(args);
        return effective;
    }

    private static bool HasVerbosityArg(string[] args) => args.Any(arg =>
    {
        var lower = arg.ToLowerInvariant();
        return lower.StartsWith("-v:", StringComparison.Ordinal)
            || lower.StartsWith("/v:", StringComparison.Ordinal)
            || lower == "-v"
            || lower == "/v"
            || lower == "--verbosity"
            || lower.StartsWith("--verbosity=", StringComparison.Ordinal);
    });

    private static bool HasNoLogoArg(string[] args) =>
        args.Any(arg => arg.Equals("-nologo", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("/nologo", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Composes the final output for a possibly-failing run. On success (or when the raw
    /// fallback is not needed) only the filtered summary is emitted; on failure the raw
    /// stdout (or stderr) is prepended so nothing is lost. Ports Rust's
    /// <c>compose_failure_output</c> with <c>needs_raw_fallback = true</c> (build/restore).
    /// </summary>
    private static string ComposeFailureOutput(bool commandSuccess, bool needsRawFallback, string stdout, string stderr, string filtered)
    {
        if (commandSuccess || !needsRawFallback)
        {
            return filtered;
        }

        var stdoutTrimmed = stdout.Trim();
        var stderrTrimmed = stderr.Trim();
        if (stdoutTrimmed.Length > 0)
        {
            return $"{stdoutTrimmed}\n\n{filtered}";
        }

        if (stderrTrimmed.Length > 0)
        {
            return $"{stderrTrimmed}\n\n{filtered}";
        }

        return filtered;
    }
}
