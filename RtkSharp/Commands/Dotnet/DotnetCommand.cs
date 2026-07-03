using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RtkSharp.Core;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Dotnet;

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
/// <b>Windows drive-letter paths (intentional deviation, ledgered).</b> <see cref="IssueRegex"/>
/// widens Rust's file-capture group with an optional drive-letter prefix, so Windows-absolute
/// diagnostic lines are enriched (real file/line/col/code/message) even without the binlog —
/// see the regex's own comment and <c>docs/parity/compatibility-ledger.md</c>. The "enrichment
/// lost without binlog" statement above still holds for non-drive-letter paths.
/// </para>
/// </remarks>
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

public static class DotnetCommand
{
    private const string DotnetCliUiLanguage = "DOTNET_CLI_UI_LANGUAGE";
    private const string DotnetCliUiLanguageValue = "en-US";

    // Rust CAP_ERRORS / CAP_WARNINGS from src/core/truncate.rs. These are the dotnet-filter
    // caps, distinct from RtkSharp.Core.TruncationCaps' per-category defaults.
    private const int CapBuildErrors = 20;
    private const int CapBuildWarnings = 10;

    // Rust format_test_output uses CAP_WARNINGS (=10) for MAX_DOTNET_FAILURES, MAX_TEST_ERRORS,
    // and MAX_TEST_WARNINGS alike.
    private const int CapTestSection = 10;

    // Bound on failure-detail lines kept per test, matching truncate(detail, 320) in Rust.
    private const int TestDetailTruncate = 320;

    // Rust CAP_LIST (src/core/truncate.rs) — cap on files listed in the format-report summary.
    private const int CapFormatFiles = 20;

    private const string PlaceholderMessage = "diagnostic without message";

    // --- Regexes ported verbatim from binlog.rs's lazy_static! block ---

    // Intentional deviation from Rust's ISSUE_RE (binlog.rs:56): the file-capturing group is
    // widened with an optional drive-letter prefix so Windows-absolute paths (e.g.
    // "C:\src\Program.cs(1,40): error CS1525: ...") are captured whole instead of the regex
    // stopping at the drive-letter colon. Rust's text parser has this same limitation, but it
    // is masked there by the (deferred, see class remarks) binlog path. The prefix is optional
    // so Unix/relative paths are unaffected. Ledgered in docs/parity/compatibility-ledger.md.
    private static readonly Regex IssueRegex = new(
        @"^\s*(?<file>(?:[A-Za-z]:)?[^\r\n:(]+)\((?<line>\d+),(?<column>\d+)\):\s*(?<kind>error|warning)\s*(?:(?<code>[A-Za-z]+\d+)\s*:\s*)?(?<msg>.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex BuildSummaryRegex = new(
        @"^\s*(?<count>\d+)\s+(?<kind>warning|error)\(s\)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ErrorCountRegex = new(
        @"\b(?<count>\d+)\s+error\(s\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WarningCountRegex = new(
        @"\b(?<count>\d+)\s+warning\(s\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FallbackErrorLineRegex = new(
        @"^.+\(\d+,\d+\):\s*error(?:\s+[A-Za-z]{2,}\d{3,})?(?:\s*:.*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FallbackWarningLineRegex = new(
        @"^.+\(\d+,\d+\):\s*warning(?:\s+[A-Za-z]{2,}\d{3,})?(?:\s*:.*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DurationRegex = new(
        @"^\s*Time Elapsed\s+(?<duration>[^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex RestoreProjectRegex = new(
        @"^\s*Restored\s+.+\.csproj\s*\(",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex RestoreDiagnosticRegex = new(
        @"^\s*(?:(?<file>.+?)\s+:\s+)?(?<kind>warning|error)\s+(?<code>[A-Za-z]{2,}\d{3,})\s*:\s*(?<msg>.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProjectPathRegex = new(
        @"^\s*([A-Za-z]:)?[^\r\n]*\.csproj(?:\s|$)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // --- test-output regexes ported verbatim from binlog.rs's lazy_static! block ---

    // TEST_RESULT_RE (binlog.rs:74): the VSTest per-project summary line
    // "Passed!/Failed!  - Failed: N, Passed: N, Skipped: N, Total: N, Duration: <text>".
    private static readonly Regex TestResultRegex = new(
        @"(?:Passed!|Failed!)\s*-\s*Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+),\s*Duration:\s*(?<duration>[^\r\n-]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // TEST_SUMMARY_RE (binlog.rs:78): the MTP-style "Test summary: total: N, failed: N,
    // succeeded/passed: N, skipped: N, duration: <text>" line.
    private static readonly Regex TestSummaryLineRegex = new(
        @"^\s*Test summary:\s*total:\s*(?<total>\d+),\s*failed:\s*(?<failed>\d+),\s*(?:succeeded|passed):\s*(?<passed>\d+),\s*skipped:\s*(?<skipped>\d+),\s*duration:\s*(?<duration>[^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // FAILED_TEST_HEAD_RE (binlog.rs:82): "  Failed <Name> [<duration>]" heading emitted by VSTest.
    private static readonly Regex FailedTestHeadRegex = new(
        @"^\s*Failed\s+(?<name>[^\r\n\[]+)\s+\[[^\]\r\n]+\]\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SensitiveEnvRegex = BuildSensitiveEnvRegex();

    private static Regex BuildSensitiveEnvRegex()
    {
        string[] keys =
        {
            "PATH", "HOME", "USERPROFILE", "USERNAME", "USER", "APPDATA", "LOCALAPPDATA",
            "TEMP", "TMP", "SSH_AUTH_SOCK", "SSH_AGENT_LAUNCHER", "GH_TOKEN", "GITHUB_TOKEN",
            "GITHUB_PAT", "NUGET_API_KEY", "NUGET_AUTH_TOKEN", "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS",
            "AZURE_DEVOPS_TOKEN", "AZURE_CLIENT_SECRET", "AZURE_TENANT_ID", "AZURE_CLIENT_ID",
            "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN", "API_TOKEN",
            "AUTH_TOKEN", "ACCESS_TOKEN", "BEARER_TOKEN", "PASSWORD", "CONNECTION_STRING",
            "DATABASE_URL", "DOCKER_CONFIG", "KUBECONFIG",
        };
        var joined = string.Join('|', keys.Select(Regex.Escape));
        return new Regex(
            $@"(?<prefix>\b(?:{joined})\s*(?:=|:)\s*)(?<value>[^\s;]+)",
            RegexOptions.Compiled);
    }

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
                ? FilterBuild(raw, commandSuccess)
                : FilterRestore(raw, commandSuccess);
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
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DotnetCliUiLanguage] = DotnetCliUiLanguageValue,
        };

        var result = await executor
            .ExecuteAsync(new ExecutionRequest("dotnet", args, Environment: environment, CaptureMode: ExecutionCaptureMode.Separate))
            .ConfigureAwait(false);

        Console.Out.Write(result.Stdout);
        Console.Error.Write(result.Stderr);
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
            return FormatDotnetFormatOutput(summary, checkMode);
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

    /// <summary>
    /// Formats a parsed format-report summary for stdout. Ports Rust's
    /// <c>format_dotnet_format_output</c>.
    /// </summary>
    /// <param name="summary">The parsed format report.</param>
    /// <param name="checkMode">Whether the run was in verify/check mode (no <c>--write</c>).</param>
    /// <returns>The filtered summary string (no trailing newline).</returns>
    internal static string FormatDotnetFormatOutput(FormatSummary summary, bool checkMode)
    {
        var changedCount = summary.FilesWithChanges.Count;

        if (changedCount == 0)
        {
            return $"ok dotnet format: {summary.TotalFiles} files formatted correctly";
        }

        if (!checkMode)
        {
            return $"ok dotnet format: formatted {changedCount} files ({summary.FilesUnchanged} already formatted)";
        }

        var builder = new StringBuilder($"Format: {changedCount} files need formatting");

        foreach (var (file, index) in summary.FilesWithChanges.Take(CapFormatFiles).Select((f, i) => (f, i)))
        {
            var firstChange = file.Changes[0];
            var rule = firstChange.DiagnosticId.Length == 0 ? firstChange.FormatDescription : firstChange.DiagnosticId;
            builder.Append(
                $"\n{index + 1}. {file.Path} (line {firstChange.LineNumber}, col {firstChange.CharNumber}, {rule})");
        }

        if (changedCount > CapFormatFiles)
        {
            builder.Append($"\n… +{changedCount - CapFormatFiles} more files");
            var allFiles = string.Join('\n', summary.FilesWithChanges.Select(f => f.Path));
            var hint = Tee.ForceTeeTailHint(allFiles, "dotnet-format-files", CapFormatFiles + 1);
            if (hint is not null)
            {
                builder.Append($" {hint}");
            }
        }

        builder.Append($"\n\nok {summary.FilesUnchanged} files already formatted\nRun `dotnet format` to apply fixes");
        return builder.ToString();
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
            var rawSummary = ParseTestFromText(raw);
            var merged = MergeTestSummaries(new TestSummary(), rawSummary);
            var summary = MergeTestSummaryFromTrx(
                merged,
                resultsDir,
                DotnetTrx.FindRecentTrxInTestResults(),
                commandStartedAt);
            summary = NormalizeTestSummary(summary, commandSuccess);

            // Build diagnostics carried alongside test results come from the text parser only
            // (binlog deferred).
            var testBuildSummary = ParseBuildFromText(raw);

            needsRawFallback = TestNeedsRawFallback(summary);
            filtered = FormatTestOutput(summary, testBuildSummary.Errors, testBuildSummary.Warnings);
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

    // --- test text parsing (ported from binlog.rs parse_test_from_text) ---

    /// <summary>
    /// Parses the console text of a <c>dotnet test</c> run into a <see cref="TestSummary"/>. Ports
    /// Rust's <c>parse_test_from_text</c>: the VSTest per-project result line, the MTP
    /// <c>Test summary:</c> line (last wins), and <c>Failed &lt;name&gt; [..]</c> failure blocks.
    /// </summary>
    /// <param name="raw">The combined stdout+stderr from <c>dotnet test</c>.</param>
    /// <returns>The parsed summary.</returns>
    internal static TestSummary ParseTestFromText(string raw)
    {
        var text = raw.Replace("\r\n", "\n");
        var clean = Utils.StripAnsi(text);
        var scrubbed = ScrubSensitiveEnvVars(clean);

        var summary = new TestSummary
        {
            ProjectCount = Math.Max(CountProjects(scrubbed), 1),
            DurationText = ExtractDuration(scrubbed),
        };

        var foundSummaryLine = false;
        string? fallbackDuration = null;
        foreach (Match m in TestResultRegex.Matches(scrubbed))
        {
            foundSummaryLine = true;
            summary.Passed += ParseIntOrZero(m.Groups["passed"].Value);
            summary.Failed += ParseIntOrZero(m.Groups["failed"].Value);
            summary.Skipped += ParseIntOrZero(m.Groups["skipped"].Value);
            summary.Total += ParseIntOrZero(m.Groups["total"].Value);

            if (m.Groups["duration"].Success)
            {
                fallbackDuration = m.Groups["duration"].Value.Trim();
            }
        }

        if (foundSummaryLine && summary.DurationText is null)
        {
            summary.DurationText = fallbackDuration;
        }

        Match? last = null;
        foreach (Match m in TestSummaryLineRegex.Matches(scrubbed))
        {
            last = m;
        }

        if (last is not null)
        {
            summary.Passed = ParseIntOrDefault(last.Groups["passed"].Value, summary.Passed);
            summary.Failed = ParseIntOrDefault(last.Groups["failed"].Value, summary.Failed);
            summary.Skipped = ParseIntOrDefault(last.Groups["skipped"].Value, summary.Skipped);
            summary.Total = ParseIntOrDefault(last.Groups["total"].Value, summary.Total);

            if (last.Groups["duration"].Success)
            {
                summary.DurationText = last.Groups["duration"].Value.Trim();
            }
        }

        var lines = scrubbed.Split('\n');
        var idx = 0;
        while (idx < lines.Length)
        {
            var headMatch = FailedTestHeadRegex.Match(lines[idx]);
            if (headMatch.Success)
            {
                var name = headMatch.Groups["name"].Success
                    ? headMatch.Groups["name"].Value.Trim()
                    : "unknown";
                var details = new List<string>();
                idx += 1;
                while (idx < lines.Length)
                {
                    var detailLine = lines[idx].TrimEnd();
                    if (FailedTestHeadRegex.IsMatch(detailLine))
                    {
                        idx = Math.Max(0, idx - 1);
                        break;
                    }

                    var detailTrimmed = detailLine.TrimStart();
                    if (detailTrimmed.StartsWith("Failed!  -", StringComparison.Ordinal)
                        || detailTrimmed.StartsWith("Passed!  -", StringComparison.Ordinal)
                        || detailTrimmed.StartsWith("Test summary:", StringComparison.Ordinal)
                        || detailTrimmed.StartsWith("Build ", StringComparison.Ordinal))
                    {
                        idx = Math.Max(0, idx - 1);
                        break;
                    }

                    if (detailLine.Trim().Length == 0)
                    {
                        if (details.Count > 0)
                        {
                            details.Add(string.Empty);
                        }
                    }
                    else
                    {
                        details.Add(detailLine.Trim());
                    }

                    if (details.Count >= 20)
                    {
                        break;
                    }

                    idx += 1;
                }

                summary.FailedTests.Add(new FailedTest { Name = name, Details = details });
            }

            idx += 1;
        }

        if (summary.Failed == 0)
        {
            summary.Failed = summary.FailedTests.Count;
        }

        if (summary.Total == 0)
        {
            summary.Total = summary.Passed + summary.Failed + summary.Skipped;
        }

        return summary;
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

    // --- test formatting (ported from format_test_output in dotnet_cmd.rs) ---

    /// <summary>
    /// Formats a test summary for stdout: a failed-tests section, build warnings/errors sections, and
    /// a trailing verdict line (emitted last so tail readers get a definitive result). Ports Rust's
    /// <c>format_test_output</c>.
    /// </summary>
    /// <param name="summary">The merged test summary.</param>
    /// <param name="errors">Build errors captured alongside the test run.</param>
    /// <param name="warnings">Build warnings captured alongside the test run.</param>
    /// <returns>The filtered summary string (no trailing newline).</returns>
    internal static string FormatTestOutput(TestSummary summary, List<BinlogIssue> errors, List<BinlogIssue> warnings)
    {
        var hasFailures = summary.Failed > 0 || summary.FailedTests.Count > 0;
        var statusIcon = hasFailures ? "fail" : "ok";
        var duration = summary.DurationText ?? "unknown";
        var warningCount = warnings.Count;
        var countsUnavailable = summary.Passed == 0
            && summary.Failed == 0
            && summary.Skipped == 0
            && summary.Total == 0
            && summary.FailedTests.Count == 0;

        string header;
        if (countsUnavailable)
        {
            header =
                $"{statusIcon} dotnet test: completed (binlog-only mode, counts unavailable, {warningCount} warnings) ({duration})";
        }
        else if (hasFailures)
        {
            header =
                $"{statusIcon} dotnet test: {summary.Passed} passed, {summary.Failed} failed, {summary.Skipped} skipped, {warningCount} warnings in {summary.ProjectCount} projects ({duration})";
        }
        else
        {
            header =
                $"{statusIcon} dotnet test: {summary.Passed} tests passed, {warningCount} warnings in {summary.ProjectCount} projects ({duration})";
        }

        var failedSection = FormatFailedTestsSection(summary, hasFailures);
        var warningsSection = FormatIssueSection(warnings, "warning", "Warnings:", CapTestSection, "dotnet-test-warnings");
        var errorsSection = FormatIssueSection(errors, "error", "Errors:", CapTestSection, "dotnet-test-errors");

        // Warnings before errors so errors survive `| tail -N` immediately above the verdict;
        // verdict last so tail readers always get a definitive result. See issue #1574.
        return JoinNonEmpty(failedSection, warningsSection, errorsSection, header);
    }

    private static string FormatFailedTestsSection(TestSummary summary, bool hasFailures)
    {
        if (!hasFailures || summary.FailedTests.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("Failed Tests:\n");

        foreach (var failed in summary.FailedTests.Take(CapTestSection))
        {
            builder.Append("  ").Append(failed.Name).Append('\n');
            foreach (var detail in failed.Details)
            {
                builder.Append("    ").Append(Truncate(detail, TestDetailTruncate)).Append('\n');
            }

            builder.Append('\n');
        }

        if (summary.FailedTests.Count > CapTestSection)
        {
            builder.Append($"… +{summary.FailedTests.Count - CapTestSection} more failed tests\n");
            var allFailed = string.Join(
                "\n\n",
                summary.FailedTests.Skip(CapTestSection).Select(t =>
                {
                    var sb = new StringBuilder(t.Name);
                    foreach (var detail in t.Details)
                    {
                        sb.Append("\n  ").Append(Truncate(detail, TestDetailTruncate));
                    }

                    return sb.ToString();
                }));
            var hint = Tee.ForceTeeHint(allFailed, "dotnet-test-failures");
            if (hint is not null)
            {
                builder.Append($"  {hint}\n");
            }
        }

        return builder.ToString();
    }

    private static int ParseIntOrDefault(string value, int fallback) =>
        int.TryParse(value, out var result) ? result : fallback;

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

    // --- build filter ---

    /// <summary>
    /// Filters raw <c>dotnet build</c> output into a compact summary.
    /// </summary>
    /// <param name="raw">The combined stdout+stderr from <c>dotnet build</c>.</param>
    /// <param name="commandSuccess">Whether the build exited zero.</param>
    /// <returns>The filtered summary string.</returns>
    internal static string FilterBuild(string raw, bool commandSuccess)
    {
        var summary = NormalizeBuildSummary(ParseBuildFromText(raw), commandSuccess);
        return FormatBuildOutput(summary);
    }

    // --- restore filter ---

    /// <summary>
    /// Filters raw <c>dotnet restore</c> output into a compact summary.
    /// </summary>
    /// <param name="raw">The combined stdout+stderr from <c>dotnet restore</c>.</param>
    /// <param name="commandSuccess">Whether the restore exited zero.</param>
    /// <returns>The filtered summary string.</returns>
    internal static string FilterRestore(string raw, bool commandSuccess)
    {
        var summary = NormalizeRestoreSummary(ParseRestoreFromText(raw), commandSuccess);
        var (errors, warnings) = ParseRestoreIssuesFromText(raw);
        return FormatRestoreOutput(summary, errors, warnings);
    }

    // --- text parsing (ported from binlog.rs) ---

    internal static BuildSummary ParseBuildFromText(string text)
    {
        text = text.Replace("\r\n", "\n");
        var clean = Utils.StripAnsi(text);
        var scrubbed = ScrubSensitiveEnvVars(clean);

        var summary = new BuildSummary
        {
            Succeeded = scrubbed.Contains("Build succeeded") && !scrubbed.Contains("Build FAILED"),
            ProjectCount = CountProjects(scrubbed),
            DurationText = ExtractDuration(scrubbed),
        };

        var seenErrors = new HashSet<(string, string, int, int, string)>();
        var seenWarnings = new HashSet<(string, string, int, int, string)>();

        foreach (Match m in IssueRegex.Matches(scrubbed))
        {
            var msg = m.Groups["msg"].Value.Trim();
            var issue = new BinlogIssue(
                m.Groups["code"].Success ? m.Groups["code"].Value : string.Empty,
                m.Groups["file"].Success ? m.Groups["file"].Value : string.Empty,
                ParseIntOrZero(m.Groups["line"].Value),
                ParseIntOrZero(m.Groups["column"].Value),
                msg.Length == 0 ? PlaceholderMessage : msg);

            var key = (issue.Code, issue.File, issue.Line, issue.Column, issue.Message);
            var kind = m.Groups["kind"].Value;
            if (kind == "error")
            {
                if (seenErrors.Add(key))
                {
                    summary.Errors.Add(issue);
                }
            }
            else if (kind == "warning")
            {
                if (seenWarnings.Add(key))
                {
                    summary.Warnings.Add(issue);
                }
            }
        }

        if (summary.Errors.Count == 0 || summary.Warnings.Count == 0)
        {
            var warningCount = 0;
            var errorCount = 0;

            foreach (Match m in BuildSummaryRegex.Matches(scrubbed))
            {
                var count = ParseIntOrZero(m.Groups["count"].Value);
                var kind = m.Groups["kind"].Value.ToLowerInvariant();
                if (kind == "warning")
                {
                    warningCount = Math.Max(warningCount, count);
                }
                else if (kind == "error")
                {
                    errorCount = Math.Max(errorCount, count);
                }
            }

            var inlineError = MaxCount(ErrorCountRegex, scrubbed);
            var inlineWarning = MaxCount(WarningCountRegex, scrubbed);
            warningCount = Math.Max(warningCount, inlineWarning);
            errorCount = Math.Max(errorCount, inlineError);

            if (summary.Errors.Count == 0)
            {
                for (var idx = 0; idx < errorCount; idx++)
                {
                    summary.Errors.Add(OmittedIssue("Build error", idx));
                }
            }

            if (summary.Warnings.Count == 0)
            {
                for (var idx = 0; idx < warningCount; idx++)
                {
                    summary.Warnings.Add(OmittedIssue("Build warning", idx));
                }
            }

            if (summary.Errors.Count == 0)
            {
                var fallback = FallbackErrorLineRegex.Matches(scrubbed).Count;
                for (var idx = 0; idx < fallback; idx++)
                {
                    summary.Errors.Add(OmittedIssue("Build error", idx));
                }
            }

            if (summary.Warnings.Count == 0)
            {
                var fallback = FallbackWarningLineRegex.Matches(scrubbed).Count;
                for (var idx = 0; idx < fallback; idx++)
                {
                    summary.Warnings.Add(OmittedIssue("Build warning", idx));
                }
            }
        }

        if (summary.Errors.Count == 0 || summary.Warnings.Count == 0)
        {
            var (diagErrors, diagWarnings) = ParseRestoreIssuesFromText(scrubbed);
            if (summary.Errors.Count == 0)
            {
                summary.Errors = diagErrors;
            }

            if (summary.Warnings.Count == 0)
            {
                summary.Warnings = diagWarnings;
            }
        }

        if (summary.ProjectCount == 0
            && (scrubbed.Contains("Build succeeded")
                || scrubbed.Contains("Build FAILED")
                || scrubbed.Contains(" -> ")))
        {
            summary.ProjectCount = 1;
        }

        return summary;
    }

    internal static RestoreSummary ParseRestoreFromText(string text)
    {
        text = text.Replace("\r\n", "\n");
        var (errors, warnings) = ParseRestoreIssuesFromText(text);
        var clean = Utils.StripAnsi(text);
        var scrubbed = ScrubSensitiveEnvVars(clean);

        return new RestoreSummary
        {
            RestoredProjects = RestoreProjectRegex.Matches(scrubbed).Count,
            Warnings = warnings.Count,
            Errors = errors.Count,
            DurationText = ExtractDuration(scrubbed),
        };
    }

    internal static (List<BinlogIssue> Errors, List<BinlogIssue> Warnings) ParseRestoreIssuesFromText(string text)
    {
        text = text.Replace("\r\n", "\n");
        var clean = Utils.StripAnsi(text);
        var scrubbed = ScrubSensitiveEnvVars(clean);

        var errors = new List<BinlogIssue>();
        var warnings = new List<BinlogIssue>();
        var seenErrors = new HashSet<(string, string, int, int, string)>();
        var seenWarnings = new HashSet<(string, string, int, int, string)>();

        foreach (Match m in RestoreDiagnosticRegex.Matches(scrubbed))
        {
            var issue = new BinlogIssue(
                m.Groups["code"].Success ? m.Groups["code"].Value.Trim() : string.Empty,
                m.Groups["file"].Success ? m.Groups["file"].Value.Trim() : string.Empty,
                0,
                0,
                m.Groups["msg"].Success ? m.Groups["msg"].Value.Trim() : string.Empty);

            var key = (issue.Code, issue.File, issue.Line, issue.Column, issue.Message);
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            if (kind == "error")
            {
                if (seenErrors.Add(key))
                {
                    errors.Add(issue);
                }
            }
            else if (kind == "warning")
            {
                if (seenWarnings.Add(key))
                {
                    warnings.Add(issue);
                }
            }
        }

        return (errors, warnings);
    }

    private static int CountProjects(string text) => ProjectPathRegex.Matches(text).Count;

    private static string? ExtractDuration(string text)
    {
        var m = DurationRegex.Match(text);
        return m.Success ? m.Groups["duration"].Value.Trim() : null;
    }

    internal static string ScrubSensitiveEnvVars(string input) =>
        SensitiveEnvRegex.Replace(input, "${prefix}[REDACTED]");

    private static int MaxCount(Regex regex, string text)
    {
        var max = 0;
        foreach (Match m in regex.Matches(text))
        {
            max = Math.Max(max, ParseIntOrZero(m.Groups["count"].Value));
        }

        return max;
    }

    private static BinlogIssue OmittedIssue(string label, int idx) =>
        new(string.Empty, string.Empty, 0, 0, $"{label} #{idx + 1} (details omitted)");

    private static int ParseIntOrZero(string value) =>
        int.TryParse(value, out var result) ? result : 0;

    // --- normalize (ported from dotnet_cmd.rs) ---

    private static BuildSummary NormalizeBuildSummary(BuildSummary summary, bool commandSuccess)
    {
        if (commandSuccess)
        {
            summary.Succeeded = true;
            if (summary.ProjectCount == 0)
            {
                summary.ProjectCount = 1;
            }
        }

        return summary;
    }

    private static RestoreSummary NormalizeRestoreSummary(RestoreSummary summary, bool commandSuccess)
    {
        if (!commandSuccess && summary.Errors == 0)
        {
            summary.Errors = 1;
        }

        return summary;
    }

    // --- formatting (ported from dotnet_cmd.rs) ---

    private static string FormatIssue(BinlogIssue issue, string kind)
    {
        if (issue.File.Length == 0)
        {
            return $"  {kind} {Truncate(issue.Message, 180)}";
        }

        if (issue.Code.Length == 0)
        {
            return $"  {issue.File}({issue.Line},{issue.Column}) {kind}: {Truncate(issue.Message, 180)}";
        }

        return $"  {issue.File}({issue.Line},{issue.Column}) {kind} {issue.Code}: {Truncate(issue.Message, 180)}";
    }

    private static string FormatBuildOutput(BuildSummary summary)
    {
        var statusIcon = summary.Succeeded ? "ok" : "fail";
        var duration = summary.DurationText ?? "unknown";

        var errors = FormatIssueSection(summary.Errors, "error", "Errors:", CapBuildErrors, "dotnet-build-errors");
        var warnings = FormatIssueSection(summary.Warnings, "warning", "Warnings:", CapBuildWarnings, "dotnet-build-warnings");

        var verdict =
            $"{statusIcon} dotnet build: {summary.ProjectCount} projects, {summary.Errors.Count} errors, {summary.Warnings.Count} warnings ({duration})";

        // Warnings before errors so errors survive `| tail -N` immediately above the verdict;
        // verdict last so tail readers always get a definitive result. See issue #1574.
        return JoinNonEmpty(warnings, errors, verdict);
    }

    private static string FormatRestoreOutput(RestoreSummary summary, List<BinlogIssue> errors, List<BinlogIssue> warnings)
    {
        var statusIcon = summary.Errors > 0 ? "fail" : "ok";
        var duration = summary.DurationText ?? "unknown";

        var errorsSection = FormatIssueSection(errors, "error", "Errors:", CapBuildErrors, "dotnet-format-errors");
        var warningsSection = FormatIssueSection(warnings, "warning", "Warnings:", CapBuildWarnings, "dotnet-format-warnings");

        var verdict =
            $"{statusIcon} dotnet restore: {summary.RestoredProjects} projects, {summary.Errors} errors, {summary.Warnings} warnings ({duration})";

        return JoinNonEmpty(warningsSection, errorsSection, verdict);
    }

    private static string FormatIssueSection(List<BinlogIssue> issues, string kind, string header, int cap, string teeLabel)
    {
        if (issues.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(header).Append('\n');

        foreach (var issue in issues.Take(cap))
        {
            builder.Append(FormatIssue(issue, kind)).Append('\n');
        }

        if (issues.Count > cap)
        {
            builder.Append($"  … +{issues.Count - cap} more {kind}s\n");
            var all = string.Join('\n', issues.Select(i => FormatIssue(i, kind)));
            var hint = Tee.ForceTeeTailHint(all, teeLabel, cap + 1);
            if (hint is not null)
            {
                builder.Append($"  {hint}\n");
            }
        }

        return builder.ToString();
    }

    private static string JoinNonEmpty(params string[] parts) =>
        string.Join('\n', parts.Where(p => p.Length > 0));

    private static string Truncate(string value, int maxLen)
    {
        // Ported from core/utils.rs truncate (char-count based, "..." suffix).
        var charCount = value.Length;
        if (charCount <= maxLen)
        {
            return value;
        }

        if (maxLen < 3)
        {
            return "...";
        }

        return string.Concat(value.AsSpan(0, maxLen - 3), "...");
    }

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

    /// <summary>A single MSBuild diagnostic (error or warning) with optional location.</summary>
    /// <param name="Code">The diagnostic code (e.g. <c>CS1525</c>), or empty.</param>
    /// <param name="File">The source file, or empty.</param>
    /// <param name="Line">The 1-based line number, or 0.</param>
    /// <param name="Column">The 1-based column number, or 0.</param>
    /// <param name="Message">The diagnostic message.</param>
    internal sealed record BinlogIssue(string Code, string File, int Line, int Column, string Message);

    /// <summary>Parsed summary of a <c>dotnet build</c> run.</summary>
    internal sealed class BuildSummary
    {
        /// <summary>Gets or sets whether the build succeeded.</summary>
        public bool Succeeded { get; set; }

        /// <summary>Gets or sets the number of projects built.</summary>
        public int ProjectCount { get; set; }

        /// <summary>Gets the parsed build errors.</summary>
        public List<BinlogIssue> Errors { get; set; } = new();

        /// <summary>Gets the parsed build warnings.</summary>
        public List<BinlogIssue> Warnings { get; set; } = new();

        /// <summary>Gets or sets the elapsed-time text, or null if unknown.</summary>
        public string? DurationText { get; set; }
    }

    /// <summary>Parsed summary of a <c>dotnet restore</c> run.</summary>
    internal sealed class RestoreSummary
    {
        /// <summary>Gets or sets the number of restored projects.</summary>
        public int RestoredProjects { get; set; }

        /// <summary>Gets or sets the warning count.</summary>
        public int Warnings { get; set; }

        /// <summary>Gets or sets the error count.</summary>
        public int Errors { get; set; }

        /// <summary>Gets or sets the elapsed-time text, or null if unknown.</summary>
        public string? DurationText { get; set; }
    }
}
