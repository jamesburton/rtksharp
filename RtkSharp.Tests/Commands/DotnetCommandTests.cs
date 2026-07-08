using RtkSharp.Commands.Dotnet;
using RtkSharp.Execution;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="DotnetCommand"/>'s build/restore text filters. The filters are pure
/// functions of raw <c>dotnet</c> output, so they are exercised directly against the fixtures
/// captured under <c>tests/fixtures/dotnet/</c> and <c>tests/parity/fixtures/dotnet-workflow/</c>.
/// Expectations are derived from the Rust filter logic in <c>src/cmds/dotnet/dotnet_cmd.rs</c> +
/// <c>binlog.rs</c> (text path) and validated against the <c>rtk.exe</c> oracle, whose only
/// nondeterministic component is the trailing duration token.
/// </summary>
public sealed class DotnetCommandTests
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
        var output = DotnetCommand.FilterBuild(BuildSuccessRaw, commandSuccess: true);

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
        var output = DotnetCommand.FilterBuild(BuildFailedRaw, commandSuccess: false);

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

        var output = DotnetCommand.FilterBuild(raw, commandSuccess: false);

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

        var summary = DotnetCommand.ParseBuildFromText(raw);

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
        var output = DotnetCommand.FilterBuild("", commandSuccess: true);

        Assert.Equal("ok dotnet build: 1 projects, 0 errors, 0 warnings (unknown)", output);
    }

    // ---- FilterRestore ----

    [Fact]
    public void FilterRestore_UpToDate_ReportsZeroProjects()
    {
        var output = DotnetCommand.FilterRestore(RestoreRaw, commandSuccess: true);

        Assert.Equal("ok dotnet restore: 0 projects, 0 errors, 0 warnings (unknown)", output);
    }

    [Fact]
    public void FilterRestore_Failure_ForcesErrorVerdict()
    {
        // With no parsed diagnostics but a non-zero exit, normalization surfaces one error.
        var output = DotnetCommand.FilterRestore(RestoreRaw, commandSuccess: false);

        Assert.Equal("fail dotnet restore: 0 projects, 1 errors, 0 warnings (unknown)", output);
    }

    // ---- ParseBuildFromText ----

    [Fact]
    public void ParseBuildFromText_Success_ExtractsCountsAndDuration()
    {
        var summary = DotnetCommand.ParseBuildFromText(BuildSuccessRaw);

        Assert.True(summary.Succeeded);
        Assert.Equal(1, summary.ProjectCount);
        Assert.Empty(summary.Errors);
        Assert.Single(summary.Warnings);
        Assert.Equal("00:01:45.81", summary.DurationText);
    }

    [Fact]
    public void ParseBuildFromText_Failure_ExtractsDiagnostic()
    {
        var summary = DotnetCommand.ParseBuildFromText(BuildFailedRaw);

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
        var summary = DotnetCommand.ParseRestoreFromText(RestoreRaw);

        Assert.Equal(0, summary.RestoredProjects);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(0, summary.Warnings);
        Assert.Null(summary.DurationText);
    }

    // ---- BuildEffectiveArgs ----

    [Fact]
    public void BuildEffectiveArgs_InjectsVerbosityAndNoLogo()
    {
        var effective = DotnetCommand.BuildEffectiveArgs("build", new[] { "RtkSharp/RtkSharp.csproj" });

        Assert.Equal(new[] { "-v:minimal", "-nologo", "RtkSharp/RtkSharp.csproj" }, effective);
    }

    [Fact]
    public void BuildEffectiveArgs_RespectsUserVerbosityAndNoLogo()
    {
        var effective = DotnetCommand.BuildEffectiveArgs("build", new[] { "-v:detailed", "-nologo", "proj.csproj" });

        Assert.Equal(new[] { "-v:detailed", "-nologo", "proj.csproj" }, effective);
    }

    [Fact]
    public void BuildEffectiveArgs_DoesNotInjectBinlog()
    {
        // Binlog is deferred: no -bl flag should ever be injected.
        var effective = DotnetCommand.BuildEffectiveArgs("build", Array.Empty<string>());

        Assert.DoesNotContain(effective, arg => arg.StartsWith("-bl", StringComparison.OrdinalIgnoreCase));
    }

    // ---- ScrubSensitiveEnvVars ----

    [Fact]
    public void ScrubSensitiveEnvVars_RedactsTokenValues()
    {
        var scrubbed = DotnetCommand.ScrubSensitiveEnvVars("GITHUB_TOKEN=ghp_secret123 rest");

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
        var summary = DotnetCommand.ParseTestFromText(TestFailedConsole);

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

        var summary = DotnetCommand.ParseTestFromText(raw);

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
        var warnings = new List<DotnetCommand.BinlogIssue>
        {
            new(string.Empty, string.Empty, 0, 0, "Build warning #1 (details omitted)"),
        };

        var output = DotnetCommand.FormatTestOutput(summary, new List<DotnetCommand.BinlogIssue>(), warnings);

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

        var output = DotnetCommand.FormatTestOutput(summary, new List<DotnetCommand.BinlogIssue>(), new List<DotnetCommand.BinlogIssue>());

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

    // ---- TestNeedsRawFallback ----

    [Fact]
    public void TestNeedsRawFallback_CompleteFailureDetail_IsFalse()
    {
        var summary = new TestSummary
        {
            Failed = 1,
            FailedTests = { new FailedTest { Name = "T", Details = { "boom" } } },
        };

        Assert.False(DotnetCommand.TestNeedsRawFallback(summary));
    }

    [Fact]
    public void TestNeedsRawFallback_NoParsedFailures_IsTrue()
    {
        var summary = new TestSummary { Failed = 2 };

        Assert.True(DotnetCommand.TestNeedsRawFallback(summary));
    }

    [Fact]
    public void TestNeedsRawFallback_FailureWithoutDetail_IsTrue()
    {
        var summary = new TestSummary
        {
            Failed = 1,
            FailedTests = { new FailedTest { Name = "T" } },
        };

        Assert.True(DotnetCommand.TestNeedsRawFallback(summary));
    }

    // ---- BuildEffectiveTestArgs ----

    [Fact]
    public void BuildEffectiveTestArgs_Classic_InjectsLoggerAndResultsDir()
    {
        var effective = DotnetCommand.BuildEffectiveTestArgs(
            new[] { "RtkSharp.Tests" }, TestRunnerMode.Classic, "/tmp/rd");

        Assert.Equal(
            new[] { "-nologo", "--logger", "trx", "--results-directory", "/tmp/rd", "RtkSharp.Tests" },
            effective);
    }

    [Fact]
    public void BuildEffectiveTestArgs_Classic_RespectsUserLoggerAndResultsDir()
    {
        var effective = DotnetCommand.BuildEffectiveTestArgs(
            new[] { "--logger", "trx", "--results-directory", "mine", "proj.csproj" },
            TestRunnerMode.Classic,
            "/tmp/rd");

        Assert.Equal(
            new[] { "-nologo", "--logger", "trx", "--results-directory", "mine", "proj.csproj" },
            effective);
    }

    [Fact]
    public void BuildEffectiveTestArgs_MtpNative_InjectsReportTrxAndSkipsNoLogo()
    {
        var effective = DotnetCommand.BuildEffectiveTestArgs(
            new[] { "proj.csproj" }, TestRunnerMode.MtpNative, null);

        Assert.Equal(new[] { "--report-trx", "proj.csproj" }, effective);
    }

    [Fact]
    public void BuildEffectiveTestArgs_MtpVsTestBridge_InjectsReportTrxAfterSeparator()
    {
        var effective = DotnetCommand.BuildEffectiveTestArgs(
            new[] { "proj.csproj" }, TestRunnerMode.MtpVsTestBridge, null);

        Assert.Equal(new[] { "-nologo", "proj.csproj", "--", "--report-trx" }, effective);
    }

    [Fact]
    public void BuildEffectiveTestArgs_DoesNotInjectBinlog()
    {
        var effective = DotnetCommand.BuildEffectiveTestArgs(
            Array.Empty<string>(), TestRunnerMode.Classic, "/tmp/rd");

        Assert.DoesNotContain(effective, arg => arg.StartsWith("-bl", StringComparison.OrdinalIgnoreCase));
    }

    // ---- DetectTestRunnerMode ----

    [Fact]
    public void DetectTestRunnerMode_PlainProject_IsClassic()
    {
        // A non-existent, plain project path exercises no MTP property; expect Classic VSTest.
        Assert.Equal(TestRunnerMode.Classic, DotnetCommand.DetectTestRunnerMode(new[] { "Nonexistent.csproj" }));
    }

    // ---- MergeTestSummaryFromTrx ----

    [Fact]
    public void MergeTestSummaryFromTrx_TrxOverridesTextCounts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rtk_merge_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "r.trx"),
                "<TestRun>\n" +
                "  <Times start=\"2026-02-21T12:00:00.0000000+00:00\" finish=\"2026-02-21T12:00:04.0000000+00:00\" />\n" +
                "  <Counters total=\"470\" executed=\"470\" passed=\"470\" failed=\"0\" />\n" +
                "</TestRun>");

            var textSummary = new TestSummary { Passed = 0, Total = 0, ProjectCount = 1 };
            var merged = DotnetCommand.MergeTestSummaryFromTrx(textSummary, dir, null, DateTime.UtcNow.AddMinutes(-5));

            Assert.Equal(470, merged.Total);
            Assert.Equal(470, merged.Passed);
            Assert.Equal("4.0 s", merged.DurationText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MergeTestSummaryFromTrx_NoTrx_ReturnsInputUnchanged()
    {
        var textSummary = new TestSummary { Passed = 5, Total = 5, ProjectCount = 1 };

        var merged = DotnetCommand.MergeTestSummaryFromTrx(textSummary, null, null, DateTime.UtcNow);

        Assert.Equal(5, merged.Passed);
        Assert.Equal(5, merged.Total);
    }

    // ---- File-based app dispatch (dotnet run <file>.cs / dotnet <file>.cs) ----

    private sealed class RecordingExecutor : IProcessExecutor
    {
        private readonly ExecutionResult _result;

        public RecordingExecutor(ExecutionResult result) => _result = result;

        public List<ExecutionRequest> Requests { get; } = new();

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_result);
        }
    }

    [Fact]
    public async Task RunAsync_RunWithCsFile_DispatchesToFileBasedAppHandler()
    {
        var executor = new RecordingExecutor(new ExecutionResult("hello from file-based app\n", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await DotnetCommand.RunAsync(new[] { "run", "app.cs" }, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(executor.Requests);
        Assert.Equal(new[] { "run", "app.cs" }, executor.Requests[0].Arguments);
    }

    [Fact]
    public async Task RunAsync_BareCsFileShorthand_DispatchesToFileBasedAppHandler()
    {
        var executor = new RecordingExecutor(new ExecutionResult("shorthand works\n", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await DotnetCommand.RunAsync(new[] { "app.cs" }, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(executor.Requests);
        Assert.Equal(new[] { "app.cs" }, executor.Requests[0].Arguments);
    }

    [Fact]
    public async Task RunAsync_FileBasedAppSuccess_PassesOutputThroughUnmodified()
    {
        var executor = new RecordingExecutor(new ExecutionResult("hello from file-based app\n", "", 0, TimeSpan.Zero, true, null, false));
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await DotnetCommand.RunAsync(new[] { "run", "app.cs" }, executor);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal("hello from file-based app\n", writer.ToString());
    }

    [Fact]
    public async Task RunAsync_OrdinaryProjectRun_StillPassesThroughRaw()
    {
        // No .cs argument anywhere -> must NOT be treated as a file-based app.
        var executor = new RecordingExecutor(new ExecutionResult("normal project run output\n", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await DotnetCommand.RunAsync(new[] { "run", "--project", "MyApp.csproj" }, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "run", "--project", "MyApp.csproj" }, executor.Requests[0].Arguments);
    }

    [Fact]
    public async Task RunAsync_RunWithCsFileAfterDoubleDash_NotTreatedAsFileBasedApp()
    {
        // "somearg.cs" is the invoked program's own argument, not the file-based app itself.
        var executor = new RecordingExecutor(new ExecutionResult("ran\n", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await DotnetCommand.RunAsync(new[] { "run", "--project", "MyApp.csproj", "--", "somearg.cs" }, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "run", "--project", "MyApp.csproj", "--", "somearg.cs" }, executor.Requests[0].Arguments);
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
        var output = DotnetCommand.FilterFileBasedApp(FileBasedAppSyntaxErrorRaw, "syntax.cs");

        Assert.Equal(
            "Errors:\n" +
            "  C:\\scratch\\syntax.cs(1,24) error CS1002: ; expected\n" +
            "\n" +
            "fail dotnet run: syntax.cs (1 errors, 0 warnings)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_NuGetDiagnostic_ReusesRestoreDiagnosticPath()
    {
        var output = DotnetCommand.FilterFileBasedApp(FileBasedAppNuGetDiagnosticRaw, "boom2.cs");

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
        var output = DotnetCommand.FilterFileBasedApp(FileBasedAppNoCodeDiagnosticRaw, "ok.cs");

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
}
