using System.Text;
using System.Text.RegularExpressions;
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
public static class DotnetCommand
{
    private const string DotnetCliUiLanguage = "DOTNET_CLI_UI_LANGUAGE";
    private const string DotnetCliUiLanguageValue = "en-US";

    // Rust CAP_ERRORS / CAP_WARNINGS from src/core/truncate.rs. These are the dotnet-filter
    // caps, distinct from RtkSharp.Core.TruncationCaps' per-category defaults.
    private const int CapBuildErrors = 20;
    private const int CapBuildWarnings = 10;

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
            // TODO Task 2: replace with a real run_test port (TRX parsing).
            "test" => await RunPassthroughAsync(args, executor).ConfigureAwait(false),
            // TODO Task 3: replace with a real run_format port (format-report parsing).
            "format" => await RunPassthroughAsync(args, executor).ConfigureAwait(false),
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
        var outputToPrint = ComposeFailureOutput(commandSuccess, result.Stdout, result.Stderr, filtered);

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
    private static string ComposeFailureOutput(bool commandSuccess, string stdout, string stderr, string filtered)
    {
        if (commandSuccess)
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
