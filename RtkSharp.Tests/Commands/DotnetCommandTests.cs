using RtkSharp.Commands.Dotnet;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Dotnet;

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

    [Fact]
    public async Task RunAsync_BareCsFileShorthandFailure_VerdictDoesNotClaimRun()
    {
        // Real `dotnet <file>.cs` failure output, no "run" keyword typed by the user.
        var executor = new RecordingExecutor(new ExecutionResult(
            string.Empty,
            "C:\\scratch\\syntax.cs(1,24): error CS1002: ; expected\n\nThe build failed. Fix the build errors and run again.\n",
            1,
            TimeSpan.Zero,
            true,
            null,
            false));
        var originalOut = Console.Out;
        var outWriter = new StringWriter();
        Console.SetOut(outWriter);
        int exitCode;
        try
        {
            exitCode = await DotnetCommand.RunAsync(new[] { "syntax.cs" }, executor);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(1, exitCode);
        Assert.Contains("fail dotnet syntax.cs (1 errors, 0 warnings)", outWriter.ToString());
        Assert.DoesNotContain("fail dotnet run:", outWriter.ToString());
    }

    // Integration-boundary regression test: unlike the pure-function tests in
    // RtkSharp.Filters.Tests.Commands.Dotnet.DotnetFiltersTests (which feed a
    // single pre-combined fixture string straight into FilterFileBasedApp), this test drives
    // the exception path through RunAsync/RunFileBasedAppAsync with genuinely separate
    // Stdout/Stderr fields on the ExecutionResult — matching how real `dotnet run` sends the
    // program's own output to stdout and the unhandled-exception trace to stderr. Locks in that
    // RunFileBasedAppAsync's `raw = result.Stdout + "\n" + result.Stderr` concatenation produces
    // the expected "preceding output, then exception summary" layout, not just the pure filter.
    [Fact]
    public async Task RunAsync_UnhandledExceptionAcrossSeparateStdoutStderrStreams_SummarizesCorrectly()
    {
        var executor = new RecordingExecutor(new ExecutionResult(
            "before crash\n",
            "Unhandled exception. System.InvalidOperationException: boom\n" +
            "   at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2\n",
            1,
            TimeSpan.Zero,
            true,
            null,
            false));
        var originalOut = Console.Out;
        var outWriter = new StringWriter();
        Console.SetOut(outWriter);
        int exitCode;
        try
        {
            exitCode = await DotnetCommand.RunAsync(new[] { "run", "boom.cs" }, executor);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "before crash\n" +
            "\n" +
            "exception: System.InvalidOperationException: boom (1 frames, first: at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2)\n",
            outWriter.ToString());
    }

    [Fact]
    public async Task RunFileBasedAppAsync_UnrecognizedFailureShape_FallsBackToRawPassthrough()
    {
        var executor = new RecordingExecutor(new ExecutionResult(
            "some completely unrecognized failure text\n", "", 1, TimeSpan.Zero, true, null, false));
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        int exitCode;
        try
        {
            exitCode = await DotnetCommand.RunAsync(new[] { "run", "mystery.cs" }, executor);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        Assert.Equal(1, exitCode);
        Assert.Contains("some completely unrecognized failure text", outWriter.ToString());
        Assert.Contains("rtk: filter warning:", errWriter.ToString());
    }
}
