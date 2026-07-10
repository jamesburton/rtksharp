using RtkSharp.Filters.Commands.Dotnet;

namespace RtkSharp.Filters.Tests.Commands.Dotnet;

/// <summary>
/// Tests for <see cref="DotnetFilters"/>. Moved from <c>RtkSharp.Tests.Commands.DotnetCommandTests</c>,
/// <c>DotnetFormatTests</c>, and <c>DotnetTrxTests</c> when the underlying pure methods moved from
/// <c>RtkSharp.Commands.Dotnet.DotnetCommand</c>/<c>DotnetTrx</c>/<c>DotnetFormatReport</c> to
/// <see cref="DotnetFilters"/> (Task 6 of the filters-library extraction). Argument-parsing helpers
/// (<c>BuildEffectiveArgs</c>, <c>BuildEffectiveTestArgs</c>, <c>DetectTestRunnerMode</c>), TRX/format-
/// report file-I/O members, dispatch tests, and <c>MergeTestSummaryFromTrx</c>/<c>TestNeedsRawFallback</c>
/// were not moved — they remain alongside <c>DotnetCommand</c>/<c>DotnetTrx</c>/<c>DotnetFormatReport</c>
/// in <c>RtkSharp.Tests</c>. Expectations are derived from the Rust filter logic in
/// <c>src/cmds/dotnet/dotnet_cmd.rs</c> + <c>binlog.rs</c> + <c>dotnet_trx.rs</c> +
/// <c>dotnet_format_report.rs</c> and validated against the <c>rtk.exe</c> oracle where noted.
/// </summary>
public sealed class DotnetFiltersTests
{
    // tests/parity/fixtures/dotnet-workflow/dotnet_build_success_raw.txt
    private const string BuildSuccessRaw =
        "  Determining projects to restore...\n" +
        "  All projects are up-to-date for restore.\n" +
        "  RtkSharp -> C:\\Development\\rtksharp\\RtkSharp\\bin\\Debug\\net10.0\\RtkSharp.dll\n" +
        "C:\\Development\\rtksharp\\RtkSharp.Tests\\Rewrite\\ShellLexerTests.cs(34,9): warning xUnit2012: Do not use Assert.False() to check if a value exists in a collection. Use Assert.DoesNotContain instead. (https://xunit.net/xunit.analyzers/rules/xUnit2012) [C:\\Development\\rtksharp\\RtkSharp.Tests\\RtkSharp.Tests.csproj]\n" +
        "  RtkSharp.Tests -> C:\\Development\\rtksharp\\RtkSharp.Tests\\bin\\Debug\\net10.0\\RtkSharp.Tests.dll\n" +
        "\n" +
        "Build succeeded.\n" +
        "\n" +
        "C:\\Development\\rtksharp\\RtkSharp.Tests\\Rewrite\\ShellLexerTests.cs(34,9): warning xUnit2012: Do not use Assert.False() to check if a value exists in a collection. Use Assert.DoesNotContain instead. (https://xunit.net/xunit.analyzers/rules/xUnit2012) [C:\\Development\\rtksharp\\RtkSharp.Tests\\RtkSharp.Tests.csproj]\n" +
        "    1 Warning(s)\n" +
        "    0 Error(s)\n" +
        "\n" +
        "Time Elapsed 00:01:45.81\n";

    // tests/fixtures/dotnet/build_failed.txt
    private const string BuildFailedRaw =
        "  Determining projects to restore...\n" +
        "  All projects are up-to-date for restore.\n" +
        "/private/tmp/RtkDotnetSmoke/Broken.cs(7,17): error CS1525: Invalid expression term ';' [/private/tmp/RtkDotnetSmoke/RtkDotnetSmoke.csproj]\n" +
        "\n" +
        "Build FAILED.\n" +
        "\n" +
        "/private/tmp/RtkDotnetSmoke/Broken.cs(7,17): error CS1525: Invalid expression term ';' [/private/tmp/RtkDotnetSmoke/RtkDotnetSmoke.csproj]\n" +
        "    0 Warning(s)\n" +
        "    1 Error(s)\n" +
        "\n" +
        "Time Elapsed 00:00:00.76\n";

    // tests/parity/fixtures/dotnet-workflow/dotnet_restore_raw.txt
    private const string RestoreRaw =
        "  Determining projects to restore...\n" +
        "  All projects are up-to-date for restore.\n";

    // ---- FilterBuild ----

    [Fact]
    public void FilterBuild_Success_SummarizesProjectsAndWarnings()
    {
        var output = DotnetFilters.FilterBuild(BuildSuccessRaw, commandSuccess: true);

        Assert.Equal(
            "Warnings:\n" +
            "  C:\\Development\\rtksharp\\RtkSharp.Tests\\Rewrite\\ShellLexerTests.cs(34,9) warning xUnit2012: Do not use Assert.False() to check if a value exists in a collection. Use Assert.DoesNotContain instead. (https://xunit.net/xunit.analyzers/rules/xUnit2012) [C:\\Development\\rtks...\n" +
            "\n" +
            "ok dotnet build: 1 projects, 0 errors, 1 warnings (00:01:45.81)",
            output);
    }

    [Fact]
    public void FilterBuild_Failure_SummarizesErrorAndVerdict()
    {
        var output = DotnetFilters.FilterBuild(BuildFailedRaw, commandSuccess: false);

        Assert.Equal(
            "Errors:\n" +
            "  /private/tmp/RtkDotnetSmoke/Broken.cs(7,17) error CS1525: Invalid expression term ';' [/private/tmp/RtkDotnetSmoke/RtkDotnetSmoke.csproj]\n" +
            "\n" +
            "fail dotnet build: 1 projects, 1 errors, 0 warnings (00:00:00.76)",
            output);
    }

    [Fact]
    public void FilterBuild_WindowsDriveLetterPath_ExtractsRealDetailsNotPlaceholder()
    {
        // Regression test for the drive-letter IssueRegex widening: a Windows-absolute path
        // diagnostic must be captured whole (through the drive-letter colon) and produce real
        // file/line/col/code/message details, not the "details omitted" placeholder that Rust's
        // ISSUE_RE (binlog.rs:56) falls back to for this same shape of line.
        const string raw =
            "  Determining projects to restore...\n" +
            "C:\\src\\RtkDotnetSmoke\\Program.cs(1,40): error CS1525: Invalid expression term ';' [C:\\src\\RtkDotnetSmoke\\RtkDotnetSmoke.csproj]\n" +
            "\n" +
            "Build FAILED.\n" +
            "\n" +
            "C:\\src\\RtkDotnetSmoke\\Program.cs(1,40): error CS1525: Invalid expression term ';' [C:\\src\\RtkDotnetSmoke\\RtkDotnetSmoke.csproj]\n" +
            "    0 Warning(s)\n" +
            "    1 Error(s)\n" +
            "\n" +
            "Time Elapsed 00:00:00.50\n";

        var output = DotnetFilters.FilterBuild(raw, commandSuccess: false);

        Assert.Equal(
            "Errors:\n" +
            "  C:\\src\\RtkDotnetSmoke\\Program.cs(1,40) error CS1525: Invalid expression term ';' [C:\\src\\RtkDotnetSmoke\\RtkDotnetSmoke.csproj]\n" +
            "\n" +
            "fail dotnet build: 1 projects, 1 errors, 0 warnings (00:00:00.50)",
            output);
        Assert.DoesNotContain("details omitted", output);
    }

    [Fact]
    public void ParseBuildFromText_WindowsDriveLetterPath_ExtractsRealDetails()
    {
        const string raw =
            "C:\\src\\Program.cs(1,40): error CS1525: Invalid expression term ';' [C:\\src\\App.csproj]\n" +
            "Build FAILED.\n" +
            "    0 Warning(s)\n" +
            "    1 Error(s)\n" +
            "Time Elapsed 00:00:01.00\n";

        var summary = DotnetFilters.ParseBuildFromText(raw);

        Assert.Single(summary.Errors);
        var issue = summary.Errors[0];
        Assert.Equal("C:\\src\\Program.cs", issue.File);
        Assert.Equal(1, issue.Line);
        Assert.Equal(40, issue.Column);
        Assert.Equal("CS1525", issue.Code);
        Assert.DoesNotContain("details omitted", issue.Message);
    }

    [Fact]
    public void FilterBuild_Empty_ReportsSingleProjectOnSuccess()
    {
        // command_success normalization forces at least one project even with no text signal.
        var output = DotnetFilters.FilterBuild("", commandSuccess: true);

        Assert.Equal("ok dotnet build: 1 projects, 0 errors, 0 warnings (unknown)", output);
    }

    // ---- FilterRestore ----

    [Fact]
    public void FilterRestore_UpToDate_ReportsZeroProjects()
    {
        var output = DotnetFilters.FilterRestore(RestoreRaw, commandSuccess: true);

        Assert.Equal("ok dotnet restore: 0 projects, 0 errors, 0 warnings (unknown)", output);
    }

    [Fact]
    public void FilterRestore_Failure_ForcesErrorVerdict()
    {
        // With no parsed diagnostics but a non-zero exit, normalization surfaces one error.
        var output = DotnetFilters.FilterRestore(RestoreRaw, commandSuccess: false);

        Assert.Equal("fail dotnet restore: 0 projects, 1 errors, 0 warnings (unknown)", output);
    }

    // ---- ParseBuildFromText ----

    [Fact]
    public void ParseBuildFromText_Success_ExtractsCountsAndDuration()
    {
        var summary = DotnetFilters.ParseBuildFromText(BuildSuccessRaw);

        Assert.True(summary.Succeeded);
        Assert.Equal(1, summary.ProjectCount);
        Assert.Empty(summary.Errors);
        Assert.Single(summary.Warnings);
        Assert.Equal("00:01:45.81", summary.DurationText);
    }

    [Fact]
    public void ParseBuildFromText_Failure_ExtractsDiagnostic()
    {
        var summary = DotnetFilters.ParseBuildFromText(BuildFailedRaw);

        Assert.False(summary.Succeeded);
        Assert.Single(summary.Errors);
        Assert.Equal("CS1525", summary.Errors[0].Code);
        Assert.Equal(7, summary.Errors[0].Line);
        Assert.Equal(17, summary.Errors[0].Column);
        Assert.Empty(summary.Warnings);
        Assert.Equal("00:00:00.76", summary.DurationText);
    }

    // ---- ParseRestoreFromText ----

    [Fact]
    public void ParseRestoreFromText_UpToDate_HasNoIssues()
    {
        var summary = DotnetFilters.ParseRestoreFromText(RestoreRaw);

        Assert.Equal(0, summary.RestoredProjects);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(0, summary.Warnings);
        Assert.Null(summary.DurationText);
    }

    // ---- ScrubSensitiveEnvVars ----

    [Fact]
    public void ScrubSensitiveEnvVars_RedactsTokenValues()
    {
        var scrubbed = DotnetFilters.ScrubSensitiveEnvVars("GITHUB_TOKEN=ghp_secret123 rest");

        Assert.Equal("GITHUB_TOKEN=[REDACTED] rest", scrubbed);
    }

    // ---- ParseTestFromText ----

    // tests/fixtures/dotnet/test_failed.txt failure block (classic VSTest console output).
    private const string TestFailedConsole =
        "  Determining projects to restore...\n" +
        "Starting test execution, please wait...\n" +
        "[xUnit.net 00:00:00.11]     RtkDotnetSmoke.UnitTest1.Test1 [FAIL]\n" +
        "  Failed RtkDotnetSmoke.UnitTest1.Test1 [4 ms]\n" +
        "  Error Message:\n" +
        "   Assert.Equal() Failure: Values differ\n" +
        "Expected: 2\n" +
        "Actual:   3\n" +
        "  Stack Trace:\n" +
        "     at RtkDotnetSmoke.UnitTest1.Test1() in /private/tmp/RtkDotnetSmoke/UnitTest1.cs:line 8\n" +
        "\n" +
        "Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 13 ms - RtkDotnetSmoke.dll (net10.0)\n";

    [Fact]
    public void ParseTestFromText_Failure_ExtractsCountsDurationAndFailedTest()
    {
        var summary = DotnetFilters.ParseTestFromText(TestFailedConsole);

        Assert.Equal(0, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(1, summary.Total);
        Assert.Equal("13 ms", summary.DurationText);
        Assert.Single(summary.FailedTests);
        Assert.Equal("RtkDotnetSmoke.UnitTest1.Test1", summary.FailedTests[0].Name);
        Assert.Contains(summary.FailedTests[0].Details, d => d.Contains("Assert.Equal() Failure"));
    }

    [Fact]
    public void ParseTestFromText_Success_ExtractsPassedFromResultLine()
    {
        const string raw =
            "Starting test execution, please wait...\n" +
            "Passed!  - Failed:     0, Passed:   470, Skipped:     0, Total:   470, Duration: 4 s - RtkSharp.Tests.dll (net10.0)\n";

        var summary = DotnetFilters.ParseTestFromText(raw);

        Assert.Equal(470, summary.Passed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(470, summary.Total);
        Assert.Equal("4 s", summary.DurationText);
        Assert.Empty(summary.FailedTests);
    }

    // ---- FormatTestOutput ----

    [Fact]
    public void FormatTestOutput_AllPass_MatchesOracleShape()
    {
        var summary = new TestSummary
        {
            Passed = 470,
            Total = 470,
            ProjectCount = 1,
            DurationText = "4.1 s",
        };
        var warnings = new List<BinlogIssue>
        {
            new(string.Empty, string.Empty, 0, 0, "Build warning #1 (details omitted)"),
        };

        var output = DotnetFilters.FormatTestOutput(summary, new List<BinlogIssue>(), warnings);

        Assert.Equal(
            "Warnings:\n" +
            "  warning Build warning #1 (details omitted)\n" +
            "\n" +
            "ok dotnet test: 470 tests passed, 1 warnings in 1 projects (4.1 s)",
            output);
    }

    [Fact]
    public void FormatTestOutput_Failure_ListsFailedTestAndVerdict()
    {
        var summary = new TestSummary
        {
            Passed = 1,
            Failed = 1,
            Skipped = 1,
            Total = 3,
            ProjectCount = 1,
            DurationText = "76 ms",
            FailedTests =
            {
                new FailedTest
                {
                    Name = "FailProj.Tests.FailingTest",
                    Details = { "Assert.Equal() Failure: Values differ", "at FailProj.Tests.FailingTest()" },
                },
            },
        };

        var output = DotnetFilters.FormatTestOutput(summary, new List<BinlogIssue>(), new List<BinlogIssue>());

        Assert.Equal(
            "Failed Tests:\n" +
            "  FailProj.Tests.FailingTest\n" +
            "    Assert.Equal() Failure: Values differ\n" +
            "    at FailProj.Tests.FailingTest()\n" +
            "\n" +
            "\n" +
            "fail dotnet test: 1 passed, 1 failed, 1 skipped, 0 warnings in 1 projects (76 ms)",
            output);
    }

    // ---- FilterFileBasedApp: tier 1 (IssueRegex-shaped) + tier 2 (no-location diagnostics) ----

    // Captured from a real `dotnet run syntax.cs` with a genuine missing-semicolon syntax error.
    private const string FileBasedAppSyntaxErrorRaw =
        "C:\\scratch\\syntax.cs(1,24): error CS1002: ; expected\n" +
        "\n" +
        "The build failed. Fix the build errors and run again.\n";

    // Captured from a real `dotnet run boom2.cs` where an ambient ~/NuGet.Config with two
    // package sources triggered a central-package-management source-mapping error — a
    // no-location MSBuild/NuGet diagnostic that already matches the existing
    // RestoreDiagnosticRegex used by ParseRestoreIssuesFromText (file + numeric code, no line/col).
    private const string FileBasedAppNuGetDiagnosticRaw =
        "C:\\scratch\\boom2.cs.csproj : error NU1507: Warning As Error: There are 2 package sources defined in your configuration. When using central package management, please map your package sources with package source mapping (https://aka.ms/nuget-package-source-mapping) or specify a single package source. The following sources are defined: nuget.org, QHubPackages\n" +
        "\n" +
        "The build failed. Fix the build errors and run again.\n";

    // Captured from a real `dotnet run ok.cs` on a trivial one-line program on this SDK, which
    // fails a Roslyn analyzer-config check with no source location and no numeric diagnostic code.
    private const string FileBasedAppNoCodeDiagnosticRaw =
        "CSC : error EnableGenerateDocumentationFile: Set MSBuild property 'GenerateDocumentationFile' to 'true' in project file to enable IDE0005 (Remove unnecessary usings/imports) on build (https://github.com/dotnet/roslyn/issues/41640)\n" +
        "\n" +
        "The build failed. Fix the build errors and run again.\n";

    [Fact]
    public void FilterFileBasedApp_SyntaxError_ReusesIssueRegexPath()
    {
        var output = DotnetFilters.FilterFileBasedApp(FileBasedAppSyntaxErrorRaw, "syntax.cs");

        Assert.Equal(
            "Errors:\n" +
            "  C:\\scratch\\syntax.cs(1,24) error CS1002: ; expected\n" +
            "\n" +
            "fail dotnet run: syntax.cs (1 errors, 0 warnings)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_SyntaxError_BareShorthand_VerdictNamesShorthandNotRun()
    {
        // The bare `dotnet app.cs` shorthand (no "run" keyword) must not claim "dotnet run"
        // in the verdict line, since the user never typed "run".
        var output = DotnetFilters.FilterFileBasedApp(FileBasedAppSyntaxErrorRaw, "syntax.cs", usedRunKeyword: false);

        Assert.Equal(
            "Errors:\n" +
            "  C:\\scratch\\syntax.cs(1,24) error CS1002: ; expected\n" +
            "\n" +
            "fail dotnet syntax.cs (1 errors, 0 warnings)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_NuGetDiagnostic_ReusesRestoreDiagnosticPath()
    {
        var output = DotnetFilters.FilterFileBasedApp(FileBasedAppNuGetDiagnosticRaw, "boom2.cs");

        // NOTE: the message is truncated to 180 chars by the existing, already-tested
        // Truncate() helper (FormatIssue), and the file field is populated because the raw
        // line's "boom2.cs.csproj : error NU1507: ..." shape matches RestoreDiagnosticRegex's
        // optional file-capture group — both confirmed against real test output, not
        // hand-computed (see task report).
        Assert.Equal(
            "Errors:\n" +
            "  C:\\scratch\\boom2.cs.csproj(0,0) error NU1507: Warning As Error: There are 2 package sources defined in your configuration. When using central package management, please map your package sources with package source mapping (...\n" +
            "\n" +
            "fail dotnet run: boom2.cs (1 errors, 0 warnings)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_NoCodeDiagnostic_UsesNewFallbackRegex()
    {
        var output = DotnetFilters.FilterFileBasedApp(FileBasedAppNoCodeDiagnosticRaw, "ok.cs");

        // NOTE: the message is truncated to 180 chars by the existing, already-tested
        // Truncate() helper (FormatIssue) — confirmed against real test output, not
        // hand-computed (see task report).
        Assert.Equal(
            "Errors:\n" +
            "  error EnableGenerateDocumentationFile: Set MSBuild property 'GenerateDocumentationFile' to 'true' in project file to enable IDE0005 (Remove unnecessary usings/imports) on build (https...\n" +
            "\n" +
            "fail dotnet run: ok.cs (1 errors, 0 warnings)",
            output);
    }

    // ---- FilterFileBasedApp: tier 3 (unhandled exception) + tier 4 (unrecognized -> throws) ----

    // Captured from a real `dotnet run boom.cs` (Console.WriteLine then `throw new
    // InvalidOperationException("boom")`). stdout/stderr were captured combined (2>&1); the
    // implementation operates on the same combined `raw` convention this file already uses
    // for every other filter, so this fixture models that combined stream faithfully.
    private const string FileBasedAppExceptionRaw =
        "before crash\n" +
        "Unhandled exception. System.InvalidOperationException: boom\n" +
        "   at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2\n";

    [Fact]
    public void FilterFileBasedApp_UnhandledException_SummarizesWithFrameCountAndPreservesPrecedingOutput()
    {
        var output = DotnetFilters.FilterFileBasedApp(FileBasedAppExceptionRaw, "boom.cs");

        Assert.Equal(
            "before crash\n" +
            "\n" +
            "exception: System.InvalidOperationException: boom (1 frames, first: at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_UnhandledExceptionWithNoPrecedingOutput_OmitsBlankPrefix()
    {
        const string raw =
            "Unhandled exception. System.InvalidOperationException: boom\n" +
            "   at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2\n";

        var output = DotnetFilters.FilterFileBasedApp(raw, "boom.cs");

        Assert.Equal(
            "exception: System.InvalidOperationException: boom (1 frames, first: at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2)",
            output);
    }

    // Regression test: a "   at ..."-shaped line appearing in the program's own PRECEDING
    // output (before the "Unhandled exception." marker) must not be miscounted as a stack
    // frame, and must not be selected as the "first" frame — only lines at/after the exception
    // header are real stack-trace frames.
    [Fact]
    public void FilterFileBasedApp_UnhandledException_IgnoresLookalikeAtLineInPrecedingOutput()
    {
        const string raw =
            "   at the beginning, things were fine\n" +
            "Unhandled exception. System.InvalidOperationException: boom\n" +
            "   at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2\n";

        var output = DotnetFilters.FilterFileBasedApp(raw, "boom.cs");

        Assert.Equal(
            "at the beginning, things were fine\n" +
            "\n" +
            "exception: System.InvalidOperationException: boom (1 frames, first: at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_UnrecognizedFailureShape_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DotnetFilters.FilterFileBasedApp("some completely unrecognized failure text\n", "mystery.cs"));
    }

    // ---- Summarize (new pure boundary carved out of the fused ParseFormatReport in Task 6's
    // Step 3 split; file I/O + JSON deserialization stay in DotnetFormatReport.ParseFormatReport,
    // which is still covered end-to-end by RtkSharp.Tests.Commands.DotnetFormatTests) ----

    [Fact]
    public void Summarize_AllFormatted_ReportsZeroChangedFiles()
    {
        var entries = new List<FormatReportEntryDto>
        {
            new() { FilePath = "src/Program.cs", FileChanges = new List<FileChangeDto>() },
            new() { FilePath = "src/Utils.cs", FileChanges = new List<FileChangeDto>() },
        };

        var summary = DotnetFilters.Summarize(entries);

        Assert.Equal(2, summary.TotalFiles);
        Assert.Equal(2, summary.FilesUnchanged);
        Assert.Empty(summary.FilesWithChanges);
    }

    [Fact]
    public void Summarize_WithChanges_ExtractsFilesAndChangeDetail()
    {
        var entries = new List<FormatReportEntryDto>
        {
            new()
            {
                FilePath = "src/Program.cs",
                FileChanges = new List<FileChangeDto>
                {
                    new() { LineNumber = 42, CharNumber = 17, DiagnosticId = "WHITESPACE", FormatDescription = "Fix whitespace" },
                },
            },
            new()
            {
                FilePath = "src/Utils.cs",
                FileChanges = new List<FileChangeDto>
                {
                    new() { LineNumber = 15, CharNumber = 8, DiagnosticId = "IDE0055", FormatDescription = "Fix formatting" },
                },
            },
            new() { FilePath = "tests/Tests.cs", FileChanges = new List<FileChangeDto>() },
        };

        var summary = DotnetFilters.Summarize(entries);

        Assert.Equal(3, summary.TotalFiles);
        Assert.Equal(1, summary.FilesUnchanged);
        Assert.Equal(2, summary.FilesWithChanges.Count);
        Assert.Contains("Program.cs", summary.FilesWithChanges[0].Path);
        Assert.Equal(42u, summary.FilesWithChanges[0].Changes[0].LineNumber);
        Assert.Equal(17u, summary.FilesWithChanges[0].Changes[0].CharNumber);
        Assert.Equal("WHITESPACE", summary.FilesWithChanges[0].Changes[0].DiagnosticId);
    }

    [Fact]
    public void Summarize_Empty_ReportsZeroFiles()
    {
        var summary = DotnetFilters.Summarize(new List<FormatReportEntryDto>());

        Assert.Equal(0, summary.TotalFiles);
        Assert.Equal(0, summary.FilesUnchanged);
        Assert.Empty(summary.FilesWithChanges);
    }

    // ---- FormatDotnetFormatOutput (fed via Summarize rather than file-backed ParseFormatReport,
    // since this is a pure formatting test with no file I/O involved) ----

    private static List<FormatReportEntryDto> AllFormattedEntries() => new()
    {
        new() { FilePath = "src/Program.cs", FileChanges = new List<FileChangeDto>() },
        new() { FilePath = "src/Utils.cs", FileChanges = new List<FileChangeDto>() },
    };

    private static List<FormatReportEntryDto> WithChangesEntries() => new()
    {
        new()
        {
            FilePath = "src/Program.cs",
            FileChanges = new List<FileChangeDto>
            {
                new() { LineNumber = 42, CharNumber = 17, DiagnosticId = "WHITESPACE", FormatDescription = "Fix whitespace" },
            },
        },
        new()
        {
            FilePath = "src/Utils.cs",
            FileChanges = new List<FileChangeDto>
            {
                new() { LineNumber = 15, CharNumber = 8, DiagnosticId = "IDE0055", FormatDescription = "Fix formatting" },
            },
        },
        new() { FilePath = "tests/Tests.cs", FileChanges = new List<FileChangeDto>() },
    };

    [Fact]
    public void FormatDotnetFormatOutput_NoChanges_ReportsFormattedCorrectly()
    {
        var summary = DotnetFilters.Summarize(AllFormattedEntries());

        var output = DotnetFilters.FormatDotnetFormatOutput(summary, checkMode: true);

        Assert.Equal("ok dotnet format: 2 files formatted correctly", output);
    }

    [Fact]
    public void FormatDotnetFormatOutput_CheckModeWithChanges_ListsFilesAndRecoveryHint()
    {
        var summary = DotnetFilters.Summarize(WithChangesEntries());

        var output = DotnetFilters.FormatDotnetFormatOutput(summary, checkMode: true);

        Assert.Equal(
            "Format: 2 files need formatting\n" +
            "1. src/Program.cs (line 42, col 17, WHITESPACE)\n" +
            "2. src/Utils.cs (line 15, col 8, IDE0055)\n" +
            "\n" +
            "ok 1 files already formatted\n" +
            "Run `dotnet format` to apply fixes",
            output);
    }

    [Fact]
    public void FormatDotnetFormatOutput_WriteModeWithChanges_ReportsFormattedCount()
    {
        var summary = DotnetFilters.Summarize(WithChangesEntries());

        var output = DotnetFilters.FormatDotnetFormatOutput(summary, checkMode: false);

        Assert.Equal("ok dotnet format: formatted 2 files (1 already formatted)", output);
    }

    [Fact]
    public void FormatDotnetFormatOutput_ExceedsCap_AddsOverflowLineAndHint()
    {
        var entries = Enumerable.Range(1, 25).Select(i => new FormatReportEntryDto
        {
            FilePath = $"src/F{i}.cs",
            FileChanges = new List<FileChangeDto>
            {
                new() { LineNumber = (uint)i, CharNumber = 1, DiagnosticId = "IDE0055", FormatDescription = "Fix" },
            },
        }).ToList();
        var summary = DotnetFilters.Summarize(entries);

        var output = DotnetFilters.FormatDotnetFormatOutput(summary, checkMode: true);

        Assert.Contains("Format: 25 files need formatting", output);
        Assert.Contains("… +5 more files", output);
        Assert.Contains("20. src/F20.cs", output);
        Assert.DoesNotContain("21. src/F21.cs", output);
    }

    // ---- ParseTrxContent ----

    // Real VSTest TRX carries this default namespace; the parser must match element/attribute
    // names by local part regardless.
    private const string Namespaced =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<TestRun id=\"6f8c\" name=\"run\" xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">\n" +
        "  <Times creation=\"2026-07-03T02:37:43.6864359+01:00\" start=\"2026-07-03T02:37:41.9374505+01:00\" finish=\"2026-07-03T02:37:46.4349668+01:00\" />\n" +
        "  <ResultSummary outcome=\"Completed\">\n" +
        "    <Counters total=\"470\" executed=\"470\" passed=\"470\" failed=\"0\" />\n" +
        "  </ResultSummary>\n" +
        "</TestRun>";

    [Fact]
    public void ParseTrxContent_ExtractsPassedCountsAndDuration()
    {
        const string trx =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">\n" +
            "  <Times creation=\"2026-02-21T12:57:28.3323710+01:00\" start=\"2026-02-21T12:57:27.7149650+01:00\" finish=\"2026-02-21T12:57:30.2214710+01:00\" />\n" +
            "  <ResultSummary outcome=\"Completed\">\n" +
            "    <Counters total=\"42\" executed=\"42\" passed=\"40\" failed=\"2\" error=\"0\" />\n" +
            "  </ResultSummary>\n" +
            "</TestRun>";

        var summary = DotnetFilters.ParseTrxContent(trx);

        Assert.NotNull(summary);
        Assert.Equal(42, summary!.Total);
        Assert.Equal(40, summary.Passed);
        Assert.Equal(2, summary.Failed);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(1, summary.ProjectCount);
        Assert.Equal("2.5 s", summary.DurationText);
    }

    [Fact]
    public void ParseTrxContent_ComputesSkippedFromCounters()
    {
        // total=3, passed=1, failed=1 → skipped = 3 - (1+1) = 1 (real captured Counters shape).
        const string trx =
            "<TestRun>\n" +
            "  <Counters total=\"3\" executed=\"2\" passed=\"1\" failed=\"1\" error=\"0\" />\n" +
            "</TestRun>";

        var summary = DotnetFilters.ParseTrxContent(trx);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Skipped);
        Assert.Equal(1, summary.ProjectCount);
    }

    [Fact]
    public void ParseTrxContent_ExtractsFailedTestWithMessageAndClippedStack()
    {
        // Mirrors the real VSTest failure block: &#xD;-encoded CRs in the message and a 3+ line
        // stack trace that must be clipped to the first three lines.
        const string trx =
            "<TestRun>\n" +
            "  <Results>\n" +
            "    <UnitTestResult testName=\"FailProj.Tests.FailingTest\" outcome=\"Failed\">\n" +
            "      <Output>\n" +
            "        <ErrorInfo>\n" +
            "          <Message>Assert.Equal() Failure: Values differ&#xD;\nExpected: 2&#xD;\nActual:   3</Message>\n" +
            "          <StackTrace>   at FailProj.Tests.FailingTest() in C:\\src\\Tests.cs:line 5&#xD;\n   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method()&#xD;\n   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs()&#xD;\n   at Xunit.Sdk.TestInvoker.Invoke()</StackTrace>\n" +
            "        </ErrorInfo>\n" +
            "      </Output>\n" +
            "    </UnitTestResult>\n" +
            "  </Results>\n" +
            "  <ResultSummary><Counters total=\"1\" executed=\"1\" passed=\"0\" failed=\"1\" /></ResultSummary>\n" +
            "</TestRun>";

        var summary = DotnetFilters.ParseTrxContent(trx);

        Assert.NotNull(summary);
        Assert.Single(summary!.FailedTests);
        var failed = summary.FailedTests[0];
        Assert.Equal("FailProj.Tests.FailingTest", failed.Name);
        Assert.Equal(2, failed.Details.Count);
        Assert.Contains("Assert.Equal() Failure: Values differ", failed.Details[0]);
        Assert.Contains("Expected: 2", failed.Details[0]);

        // Stack trace clipped to the first three lines.
        var stack = failed.Details[1];
        Assert.Contains("at FailProj.Tests.FailingTest()", stack);
        Assert.Equal(3, stack.Split('\n').Length);
        Assert.DoesNotContain("Xunit.Sdk.TestInvoker", stack);
    }

    [Fact]
    public void ParseTrxContent_ExtractsCountersWhenAttributeOrderVaries()
    {
        const string trx =
            "<TestRun>\n" +
            "  <ResultSummary outcome=\"Completed\">\n" +
            "    <Counters failed=\"3\" passed=\"7\" executed=\"10\" total=\"10\" />\n" +
            "  </ResultSummary>\n" +
            "</TestRun>";

        var summary = DotnetFilters.ParseTrxContent(trx);

        Assert.NotNull(summary);
        Assert.Equal(10, summary!.Total);
        Assert.Equal(7, summary.Passed);
        Assert.Equal(3, summary.Failed);
    }

    [Fact]
    public void ParseTrxContent_SelfClosingFailedResult_HasEmptyDetails()
    {
        const string trx =
            "<TestRun>\n" +
            "  <Results>\n" +
            "    <UnitTestResult testName=\"MyTests.NoInfo\" outcome=\"Failed\" />\n" +
            "  </Results>\n" +
            "  <Counters total=\"1\" passed=\"0\" failed=\"1\" />\n" +
            "</TestRun>";

        var summary = DotnetFilters.ParseTrxContent(trx);

        Assert.NotNull(summary);
        Assert.Single(summary!.FailedTests);
        Assert.Equal("MyTests.NoInfo", summary.FailedTests[0].Name);
        Assert.Empty(summary.FailedTests[0].Details);
    }

    [Fact]
    public void ParseTrxContent_Namespaced_ParsesThroughDefaultNamespace()
    {
        var summary = DotnetFilters.ParseTrxContent(Namespaced);

        Assert.NotNull(summary);
        Assert.Equal(470, summary!.Total);
        Assert.Equal(470, summary.Passed);
        Assert.Equal("4.5 s", summary.DurationText); // 4.4975163 s → 4497 ms → "4.5 s"
    }

    [Fact]
    public void ParseTrxContent_ReturnsNullForNonTrxXml()
    {
        Assert.Null(DotnetFilters.ParseTrxContent("<Root><Child/></Root>"));
    }

    [Fact]
    public void ParseTrxContent_ReturnsNullForMalformedXml()
    {
        Assert.Null(DotnetFilters.ParseTrxContent("This is not a TRX file"));
    }
}
