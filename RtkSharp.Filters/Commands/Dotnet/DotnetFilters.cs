using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Dotnet;

/// <summary>A single MSBuild diagnostic (error or warning) with optional location.</summary>
/// <param name="Code">The diagnostic code (e.g. <c>CS1525</c>), or empty.</param>
/// <param name="File">The source file, or empty.</param>
/// <param name="Line">The 1-based line number, or 0.</param>
/// <param name="Column">The 1-based column number, or 0.</param>
/// <param name="Message">The diagnostic message.</param>
public sealed record BinlogIssue(string Code, string File, int Line, int Column, string Message);

/// <summary>Parsed summary of a <c>dotnet build</c> run.</summary>
public sealed class BuildSummary
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
public sealed class RestoreSummary
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

/// <summary>A single failed test parsed from a TRX file (or console output).</summary>
public sealed class FailedTest
{
    /// <summary>Gets or sets the fully-qualified test name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets the failure detail lines (message + clipped stack trace).</summary>
    public List<string> Details { get; set; } = new();
}

/// <summary>Aggregated test-run summary parsed from one or more TRX files (or console output).</summary>
public sealed class TestSummary
{
    /// <summary>Gets or sets the number of passing tests.</summary>
    public int Passed { get; set; }

    /// <summary>Gets or sets the number of failing tests.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the number of skipped tests.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets the total number of tests.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the number of test projects contributing to this summary.</summary>
    public int ProjectCount { get; set; }

    /// <summary>Gets or sets the failed-test detail list.</summary>
    public List<FailedTest> FailedTests { get; set; } = new();

    /// <summary>Gets or sets the human-readable duration text (e.g. <c>"4.1 s"</c>), or null if unknown.</summary>
    public string? DurationText { get; set; }
}

/// <summary>A single formatting change within a file, as reported by <c>dotnet format --report</c>.</summary>
public sealed class ChangeDetail
{
    /// <summary>Gets the 1-based line number of the change.</summary>
    public required uint LineNumber { get; init; }

    /// <summary>Gets the 1-based character (column) number of the change.</summary>
    public required uint CharNumber { get; init; }

    /// <summary>Gets the formatter diagnostic id (e.g. <c>IDE0055</c>), or empty if not reported.</summary>
    public required string DiagnosticId { get; init; }

    /// <summary>Gets the human-readable description of the formatting fix applied.</summary>
    public required string FormatDescription { get; init; }
}

/// <summary>A source file that needed formatting, with its individual changes.</summary>
public sealed class FileWithChanges
{
    /// <summary>Gets the file path as reported in the format report (relative or absolute).</summary>
    public required string Path { get; init; }

    /// <summary>Gets the changes recorded for this file.</summary>
    public required IReadOnlyList<ChangeDetail> Changes { get; init; }
}

/// <summary>Aggregated summary of a <c>dotnet format --report</c> JSON report.</summary>
public sealed class FormatSummary
{
    /// <summary>Gets the files that needed formatting, each with its change detail.</summary>
    public required IReadOnlyList<FileWithChanges> FilesWithChanges { get; init; }

    /// <summary>Gets the number of files scanned that already matched the formatting rules.</summary>
    public required int FilesUnchanged { get; init; }

    /// <summary>Gets the total number of files the report covers.</summary>
    public required int TotalFiles { get; init; }
}

/// <summary>
/// JSON shape of a single entry in a <c>dotnet format --report</c> array. The report also carries
/// a <c>FileName</c> field, which is intentionally unmapped here (unused by the summary, and
/// <see cref="System.Text.Json.JsonSerializer"/> ignores unmapped properties by default, matching
/// serde's behavior). Public (and moved here alongside <see cref="DotnetFilters.Summarize"/>) so
/// <c>RtkSharp.Commands.Dotnet.DotnetFormatReport</c>'s file I/O + deserialization step can produce
/// the list this pure type feeds into.
/// </summary>
public sealed class FormatReportEntryDto
{
    /// <summary>Gets the file path as reported in the format report (relative or absolute).</summary>
    [JsonPropertyName("FilePath")]
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Gets the changes recorded for this file.</summary>
    [JsonPropertyName("FileChanges")]
    public List<FileChangeDto> FileChanges { get; init; } = new();
}

/// <summary>JSON shape of a single change entry within a <see cref="FormatReportEntryDto"/>.</summary>
public sealed class FileChangeDto
{
    /// <summary>Gets the 1-based line number of the change.</summary>
    [JsonPropertyName("LineNumber")]
    public uint LineNumber { get; init; }

    /// <summary>Gets the 1-based character (column) number of the change.</summary>
    [JsonPropertyName("CharNumber")]
    public uint CharNumber { get; init; }

    /// <summary>Gets the formatter diagnostic id (e.g. <c>IDE0055</c>), or empty if not reported.</summary>
    [JsonPropertyName("DiagnosticId")]
    public string DiagnosticId { get; init; } = string.Empty;

    /// <summary>Gets the human-readable description of the formatting fix applied.</summary>
    [JsonPropertyName("FormatDescription")]
    public string FormatDescription { get; init; } = string.Empty;
}

/// <summary>
/// Extension helpers shared by <see cref="DotnetFilters.ParseTrxContent"/> and
/// <c>RtkSharp.Commands.Dotnet.DotnetTrx</c>'s file-scanning members.
/// </summary>
internal static class XElementExtensions
{
    /// <summary>Gets an element's local (namespace-prefix-stripped) name.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The local name.</returns>
    internal static string LocalName(this XElement element) => element.Name.LocalName;
}

/// <summary>
/// Pure output-filtering logic for the <c>dotnet</c> CLI proxy: <c>build</c>/<c>restore</c>/<c>test</c>
/// text-shaping, <c>dotnet run &lt;file&gt;.cs</c> failure filtering, TRX parsing, and format-report
/// summarization. Extracted from <c>RtkSharp.Commands.Dotnet.DotnetCommand</c>, <c>DotnetTrx</c>, and
/// <c>DotnetFormatReport</c> (Task 6 of the filters-library extraction) — everything here is a pure
/// function of already-captured text/data, with no process execution or file I/O.
/// </summary>
public static class DotnetFilters
{
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
    // is masked there by the (deferred) binlog path. The prefix is optional so Unix/relative
    // paths are unaffected. Ledgered in docs/parity/compatibility-ledger.md.
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

    // New regex, not ported from Rust (no oracle for file-based apps): matches a no-location
    // Roslyn/MSBuild diagnostic of the shape "CSC : error <NonNumericName>: <msg>" — narrower
    // than RestoreDiagnosticRegex (which requires a numeric code like NU1507 and already covers
    // that shape for free) because this one has no digits in its "code" position at all.
    private static readonly Regex FileBasedAppNoCodeDiagnosticRegex = new(
        @"^\s*CSC\s*:\s*error\s+(?<name>\S+):\s*(?<msg>.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // New regexes, not ported from Rust (no oracle for file-based apps). Matches .NET's
    // single-line unhandled-exception header ("Unhandled exception. <Type>: <message>") and
    // VSTest/CLR-style stack-trace frame lines ("   at <frame>").
    private static readonly Regex FileBasedAppExceptionRegex = new(
        @"^Unhandled exception\.\s*(?<type>[^\r\n:]+):\s*(?<message>.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex FileBasedAppStackFrameRegex = new(
        @"^\s+at\s+\S.*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

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

    // --- build filter ---

    /// <summary>
    /// Filters raw <c>dotnet build</c> output into a compact summary.
    /// </summary>
    /// <param name="raw">The combined stdout+stderr from <c>dotnet build</c>.</param>
    /// <param name="commandSuccess">Whether the build exited zero.</param>
    /// <returns>The filtered summary string.</returns>
    public static string FilterBuild(string raw, bool commandSuccess)
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
    public static string FilterRestore(string raw, bool commandSuccess)
    {
        var summary = NormalizeRestoreSummary(ParseRestoreFromText(raw), commandSuccess);
        var (errors, warnings) = ParseRestoreIssuesFromText(raw);
        return FormatRestoreOutput(summary, errors, warnings);
    }

    // --- file-based app failure filter (dotnet run <file>.cs / dotnet <file>.cs) ---

    /// <summary>
    /// Filters the failure output of a .NET 10 file-based app run (<c>dotnet run &lt;file&gt;.cs</c>
    /// or the <c>dotnet &lt;file&gt;.cs</c> shorthand) into a compact summary. Tries, in order: the
    /// same <see cref="IssueRegex"/>-driven parsing <c>build</c>/<c>restore</c> already use, a
    /// no-location MSBuild/NuGet-diagnostic path, an unhandled-runtime-exception summary, and
    /// finally throws for anything unrecognized (the caller falls back to raw passthrough).
    /// </summary>
    /// <param name="raw">The combined stdout+stderr from the failed run.</param>
    /// <param name="fileDisplayName">The <c>.cs</c> file name/path as the user typed it.</param>
    /// <param name="usedRunKeyword">Whether the user typed <c>dotnet run &lt;file&gt;.cs</c> (true) or the bare shorthand (false).</param>
    /// <returns>The filtered summary string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the failure output does not match any recognized shape.</exception>
    public static string FilterFileBasedApp(string raw, string fileDisplayName, bool usedRunKeyword = true)
    {
        // Tier 1: same IssueRegex-driven parsing dotnet build/restore already use — covers
        // genuine source-level compile errors/warnings for free (identical output shape).
        var buildSummary = ParseBuildFromText(raw);
        if (buildSummary.Errors.Count > 0 || buildSummary.Warnings.Count > 0)
        {
            return FormatFileBasedAppIssues(buildSummary.Errors, buildSummary.Warnings, fileDisplayName, usedRunKeyword);
        }

        // Tier 2a: no-location MSBuild/NuGet diagnostics with a numeric code (e.g. NU1507) —
        // already matched by the existing restore-diagnostic parser, zero new regex needed.
        var (restoreErrors, restoreWarnings) = ParseRestoreIssuesFromText(raw);
        if (restoreErrors.Count > 0 || restoreWarnings.Count > 0)
        {
            return FormatFileBasedAppIssues(restoreErrors, restoreWarnings, fileDisplayName, usedRunKeyword);
        }

        // Tier 2b: no-location diagnostics with no numeric code at all (e.g. Roslyn
        // analyzer-config errors like EnableGenerateDocumentationFile).
        var noCodeMatches = FileBasedAppNoCodeDiagnosticRegex.Matches(raw);
        if (noCodeMatches.Count > 0)
        {
            var errors = noCodeMatches
                .Select(m => new BinlogIssue(string.Empty, string.Empty, 0, 0, $"{m.Groups["name"].Value}: {m.Groups["msg"].Value.Trim()}"))
                .ToList();
            return FormatFileBasedAppIssues(errors, new List<BinlogIssue>(), fileDisplayName, usedRunKeyword);
        }

        // Tier 3: an unhandled runtime exception (not a compile-time failure at all).
        var exceptionMatch = FileBasedAppExceptionRegex.Match(raw);
        if (exceptionMatch.Success)
        {
            return FormatFileBasedAppException(raw, exceptionMatch);
        }

        // Tier 4: unrecognized failure shape -> throw, caught by the caller, which falls back to
        // raw, unfiltered passthrough (mandatory fallback contract: a filter must never hide
        // output it doesn't recognize).
        throw new InvalidOperationException("unrecognized dotnet run failure output shape");
    }

    private static string FormatFileBasedAppException(string raw, Match exceptionMatch)
    {
        var type = exceptionMatch.Groups["type"].Value.Trim();
        var message = exceptionMatch.Groups["message"].Value.Trim();

        // Scope frame matching to the text at/after the exception header only — matching
        // against the whole `raw` buffer would let a coincidentally "   at ..."-shaped line in
        // the program's own preceding output (e.g. an indented log line) be miscounted as a
        // stack frame, or even wrongly picked as the "first" frame.
        var tail = raw[exceptionMatch.Index..];
        var frames = FileBasedAppStackFrameRegex.Matches(tail);
        var firstFrame = frames.Count > 0 ? frames[0].Value.Trim() : "unknown";
        var summary = $"exception: {type}: {message} ({frames.Count} frames, first: {firstFrame})";

        var preceding = raw[..exceptionMatch.Index].Trim();
        return preceding.Length > 0 ? $"{preceding}\n\n{summary}" : summary;
    }

    private static string FormatFileBasedAppIssues(List<BinlogIssue> errors, List<BinlogIssue> warnings, string fileDisplayName, bool usedRunKeyword)
    {
        var errorsSection = FormatIssueSection(errors, "error", "Errors:", CapBuildErrors, "dotnet-run-errors");
        var warningsSection = FormatIssueSection(warnings, "warning", "Warnings:", CapBuildWarnings, "dotnet-run-warnings");

        // The verdict must name the command the user actually typed: "dotnet run <file>.cs"
        // when the "run" keyword was used, or the bare "dotnet <file>.cs" shorthand (.NET 10+)
        // otherwise — never claim "run" was typed when it wasn't.
        var verdict = usedRunKeyword
            ? $"fail dotnet run: {fileDisplayName} ({errors.Count} errors, {warnings.Count} warnings)"
            : $"fail dotnet {fileDisplayName} ({errors.Count} errors, {warnings.Count} warnings)";

        return JoinNonEmpty(warningsSection, errorsSection, verdict);
    }

    // --- format-report summarization (pure half of the DotnetFormatReport.ParseFormatReport split) ---

    /// <summary>
    /// Summarizes already-deserialized <c>dotnet format --report</c> entries into a compact
    /// <see cref="FormatSummary"/>. This is the pure half of the split of Rust-ported
    /// <c>parse_format_report</c>: file I/O and JSON deserialization stay in
    /// <c>RtkSharp.Commands.Dotnet.DotnetFormatReport.ParseFormatReport</c>, which calls this
    /// method with the deserialized entries.
    /// </summary>
    /// <param name="entries">The deserialized format-report entries.</param>
    /// <returns>The summarized report.</returns>
    public static FormatSummary Summarize(List<FormatReportEntryDto> entries)
    {
        var totalFiles = entries.Count;

        var filesWithChanges = entries
            .Where(entry => entry.FileChanges.Count > 0)
            .Select(entry => new FileWithChanges
            {
                Path = entry.FilePath,
                Changes = entry.FileChanges.Select(change => new ChangeDetail
                {
                    LineNumber = change.LineNumber,
                    CharNumber = change.CharNumber,
                    DiagnosticId = change.DiagnosticId,
                    FormatDescription = change.FormatDescription,
                }).ToList(),
            })
            .ToList();

        var filesUnchanged = Math.Max(0, totalFiles - filesWithChanges.Count);

        return new FormatSummary
        {
            FilesWithChanges = filesWithChanges,
            FilesUnchanged = filesUnchanged,
            TotalFiles = totalFiles,
        };
    }

    /// <summary>
    /// Formats a parsed format-report summary for stdout. Ports Rust's
    /// <c>format_dotnet_format_output</c>.
    /// </summary>
    /// <param name="summary">The parsed format report.</param>
    /// <param name="checkMode">Whether the run was in verify/check mode (no <c>--write</c>).</param>
    /// <returns>The filtered summary string (no trailing newline).</returns>
    public static string FormatDotnetFormatOutput(FormatSummary summary, bool checkMode)
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

    // --- test text parsing (ported from binlog.rs parse_test_from_text) ---

    /// <summary>
    /// Parses the console text of a <c>dotnet test</c> run into a <see cref="TestSummary"/>. Ports
    /// Rust's <c>parse_test_from_text</c>: the VSTest per-project result line, the MTP
    /// <c>Test summary:</c> line (last wins), and <c>Failed &lt;name&gt; [..]</c> failure blocks.
    /// </summary>
    /// <param name="raw">The combined stdout+stderr from <c>dotnet test</c>.</param>
    /// <returns>The parsed summary.</returns>
    public static TestSummary ParseTestFromText(string raw)
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
    public static string FormatTestOutput(TestSummary summary, List<BinlogIssue> errors, List<BinlogIssue> warnings)
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
                builder.Append("    ").Append(Utils.Truncate(detail, TestDetailTruncate)).Append('\n');
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
                        sb.Append("\n  ").Append(Utils.Truncate(detail, TestDetailTruncate));
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

    // --- text parsing (ported from binlog.rs) ---

    /// <summary>
    /// Parses the console text of a <c>dotnet build</c> run into a <see cref="BuildSummary"/>. Ports
    /// Rust's <c>parse_build_from_text</c>.
    /// </summary>
    /// <param name="text">The combined stdout+stderr from <c>dotnet build</c>.</param>
    /// <returns>The parsed summary.</returns>
    public static BuildSummary ParseBuildFromText(string text)
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

    /// <summary>
    /// Parses the console text of a <c>dotnet restore</c> run into a <see cref="RestoreSummary"/>.
    /// Ports Rust's <c>parse_restore_from_text</c>.
    /// </summary>
    /// <param name="text">The combined stdout+stderr from <c>dotnet restore</c>.</param>
    /// <returns>The parsed summary.</returns>
    public static RestoreSummary ParseRestoreFromText(string text)
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

    private static (List<BinlogIssue> Errors, List<BinlogIssue> Warnings) ParseRestoreIssuesFromText(string text)
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

    /// <summary>
    /// Redacts sensitive environment-variable-looking <c>KEY=value</c>/<c>KEY: value</c> pairs in
    /// captured command output. Ports Rust's <c>scrub_sensitive_env_vars</c>.
    /// </summary>
    /// <param name="input">The text to scrub.</param>
    /// <returns>The scrubbed text.</returns>
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

    private static int ParseIntOrDefault(string value, int fallback) =>
        int.TryParse(value, out var result) ? result : fallback;

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
            return $"  {kind} {Utils.Truncate(issue.Message, 180)}";
        }

        if (issue.Code.Length == 0)
        {
            return $"  {issue.File}({issue.Line},{issue.Column}) {kind}: {Utils.Truncate(issue.Message, 180)}";
        }

        return $"  {issue.File}({issue.Line},{issue.Column}) {kind} {issue.Code}: {Utils.Truncate(issue.Message, 180)}";
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

    // --- TRX parsing (ported from dotnet_trx.rs; shared XElement helpers below also serve
    // RtkSharp.Commands.Dotnet.DotnetTrx's remaining file-scanning members) ---

    /// <summary>
    /// Parses TRX content into a <see cref="TestSummary"/>. Returns null when the content is not a
    /// valid TRX document (no <c>&lt;TestRun&gt;</c> element or malformed XML). Ports Rust's
    /// <c>parse_trx_content</c>.
    /// </summary>
    /// <param name="content">The raw TRX XML text.</param>
    /// <returns>The parsed summary, or null if the content is not a valid TRX file.</returns>
    public static TestSummary? ParseTrxContent(string content)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root is null)
        {
            return null;
        }

        var sawTestRun = root.LocalName() == "TestRun"
            || root.DescendantsAndSelf().Any(e => e.LocalName() == "TestRun");
        if (!sawTestRun)
        {
            return null;
        }

        var summary = new TestSummary();

        foreach (var element in root.DescendantsAndSelf())
        {
            switch (element.LocalName())
            {
                case "Times":
                    var start = Attr(element, "start");
                    var finish = Attr(element, "finish");
                    if (start is not null && finish is not null)
                    {
                        summary.DurationText = ParseTrxDuration(start, finish);
                    }

                    break;

                case "Counters":
                    summary.Total = ParseIntAttr(element, "total");
                    summary.Passed = ParseIntAttr(element, "passed");
                    summary.Failed = ParseIntAttr(element, "failed");
                    break;

                case "UnitTestResult":
                    var outcome = Attr(element, "outcome") ?? "Unknown";
                    if (outcome == "Failed")
                    {
                        summary.FailedTests.Add(ParseFailedTest(element));
                    }

                    break;
            }
        }

        // Calculate skipped from counters if available.
        if (summary.Total > 0)
        {
            summary.Skipped = Math.Max(0, summary.Total - (summary.Passed + summary.Failed));
        }

        // Set project count to at least 1 if there were any tests.
        if (summary.Total > 0)
        {
            summary.ProjectCount = 1;
        }

        return summary;
    }

    private static FailedTest ParseFailedTest(XElement result)
    {
        var name = Attr(result, "testName") ?? "unknown";
        var details = new List<string>();

        var errorInfo = result.Descendants().FirstOrDefault(e => e.LocalName() == "ErrorInfo");
        if (errorInfo is not null)
        {
            var message = errorInfo.Elements().FirstOrDefault(e => e.LocalName() == "Message")?.Value.Trim() ?? string.Empty;
            if (message.Length > 0)
            {
                details.Add(message);
            }

            var stack = errorInfo.Elements().FirstOrDefault(e => e.LocalName() == "StackTrace")?.Value.Trim() ?? string.Empty;
            if (stack.Length > 0)
            {
                var stackLines = stack
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Take(3)
                    .ToArray();
                if (stackLines.Length > 0)
                {
                    details.Add(string.Join("\n", stackLines));
                }
            }
        }

        return new FailedTest { Name = name, Details = details };
    }

    private static string? ParseTrxDuration(string start, string finish)
    {
        if (!TryParseRfc3339(start, out var startDt) || !TryParseRfc3339(finish, out var finishDt))
        {
            return null;
        }

        return FormatDurationBetween(startDt, finishDt);
    }

    private static int ParseIntAttr(XElement element, string localName)
    {
        var value = Attr(element, localName);
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    /// <summary>
    /// Reads an attribute's value by local (namespace-prefix-stripped) name. Shared by
    /// <see cref="ParseTrxContent"/> and <c>RtkSharp.Commands.Dotnet.DotnetTrx.ParseTrxTimeBounds</c>.
    /// </summary>
    /// <param name="element">The element to read from.</param>
    /// <param name="localName">The attribute's local name.</param>
    /// <returns>The attribute's value, or null if not present.</returns>
    internal static string? Attr(XElement element, string localName)
    {
        foreach (var attr in element.Attributes())
        {
            if (attr.Name.LocalName == localName)
            {
                return attr.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses an RFC 3339 timestamp (the format TRX <c>Times</c> attributes use). Shared by
    /// <see cref="ParseTrxContent"/> and <c>RtkSharp.Commands.Dotnet.DotnetTrx.ParseTrxTimeBounds</c>.
    /// </summary>
    /// <param name="value">The timestamp text.</param>
    /// <param name="result">The parsed timestamp, if successful.</param>
    /// <returns>Whether parsing succeeded.</returns>
    internal static bool TryParseRfc3339(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out result);

    /// <summary>
    /// Formats the wall-clock span between two timestamps as human-readable duration text
    /// (seconds with one decimal place once at/above 1000ms, otherwise whole milliseconds).
    /// Shared by <see cref="ParseTrxContent"/> (via <see cref="ParseTrxDuration"/>) and
    /// <c>RtkSharp.Commands.Dotnet.DotnetTrx.ParseTrxFilesInDirSince</c>'s wall-clock aggregation.
    /// </summary>
    /// <param name="startDt">The start timestamp.</param>
    /// <param name="finishDt">The finish timestamp.</param>
    /// <returns>The formatted duration, or null if the span is zero or negative.</returns>
    internal static string? FormatDurationBetween(DateTimeOffset startDt, DateTimeOffset finishDt)
    {
        var millis = (long)(finishDt - startDt).TotalMilliseconds;
        if (millis <= 0)
        {
            return null;
        }

        if (millis >= 1000)
        {
            var seconds = millis / 1000.0;
            return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0} s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{millis} ms");
    }
}
