using RtkSharp.Commands.Dotnet;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="DotnetTrx"/>, the TRX (Visual Studio Test Results XML) parser. The inline
/// fixtures mirror the shapes emitted by the real VSTest <c>trx</c> logger (verified by capturing a
/// live run of a tiny xunit project), including the default TRX namespace, <c>&amp;#xD;</c>-encoded
/// carriage returns in messages, and multi-line stack traces. Expectations are derived from the Rust
/// spec in <c>src/cmds/dotnet/dotnet_trx.rs</c>.
/// </summary>
public sealed class DotnetTrxTests
{
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

        var summary = DotnetTrx.ParseTrxContent(trx);

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

        var summary = DotnetTrx.ParseTrxContent(trx);

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

        var summary = DotnetTrx.ParseTrxContent(trx);

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

        var summary = DotnetTrx.ParseTrxContent(trx);

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

        var summary = DotnetTrx.ParseTrxContent(trx);

        Assert.NotNull(summary);
        Assert.Single(summary!.FailedTests);
        Assert.Equal("MyTests.NoInfo", summary.FailedTests[0].Name);
        Assert.Empty(summary.FailedTests[0].Details);
    }

    [Fact]
    public void ParseTrxContent_Namespaced_ParsesThroughDefaultNamespace()
    {
        var summary = DotnetTrx.ParseTrxContent(Namespaced);

        Assert.NotNull(summary);
        Assert.Equal(470, summary!.Total);
        Assert.Equal(470, summary.Passed);
        Assert.Equal("4.5 s", summary.DurationText); // 4.4975163 s → 4497 ms → "4.5 s"
    }

    [Fact]
    public void ParseTrxContent_ReturnsNullForNonTrxXml()
    {
        Assert.Null(DotnetTrx.ParseTrxContent("<Root><Child/></Root>"));
    }

    [Fact]
    public void ParseTrxContent_ReturnsNullForMalformedXml()
    {
        Assert.Null(DotnetTrx.ParseTrxContent("This is not a TRX file"));
    }

    [Fact]
    public void ParseTrxFilesInDir_AggregatesCountsAndWallClockDuration()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "a.trx"),
                "<TestRun>\n" +
                "  <Times start=\"2026-02-21T12:57:27.0000000+01:00\" finish=\"2026-02-21T12:57:30.0000000+01:00\" />\n" +
                "  <Counters total=\"10\" executed=\"10\" passed=\"9\" failed=\"1\" />\n" +
                "</TestRun>");
            File.WriteAllText(
                Path.Combine(dir, "b.trx"),
                "<TestRun>\n" +
                "  <Times start=\"2026-02-21T12:57:28.0000000+01:00\" finish=\"2026-02-21T12:57:29.0000000+01:00\" />\n" +
                "  <Counters total=\"20\" executed=\"20\" passed=\"20\" failed=\"0\" />\n" +
                "</TestRun>");

            var summary = DotnetTrx.ParseTrxFilesInDir(dir);

            Assert.NotNull(summary);
            Assert.Equal(30, summary!.Total);
            Assert.Equal(29, summary.Passed);
            Assert.Equal(1, summary.Failed);
            // Wall clock: earliest start 27.0 → latest finish 30.0 = 3.0 s.
            Assert.Equal("3.0 s", summary.DurationText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseTrxFilesInDirSince_IgnoresOlderFiles()
    {
        var dir = NewTempDir();
        try
        {
            var oldPath = Path.Combine(dir, "old.trx");
            File.WriteAllText(oldPath, "<TestRun><Counters total=\"2\" passed=\"2\" failed=\"0\" /></TestRun>");
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddMinutes(-10));

            var since = DateTime.UtcNow.AddMinutes(-1);
            File.WriteAllText(
                Path.Combine(dir, "new.trx"),
                "<TestRun><Counters total=\"3\" passed=\"2\" failed=\"1\" /></TestRun>");

            var summary = DotnetTrx.ParseTrxFilesInDirSince(dir, since);

            Assert.NotNull(summary);
            Assert.Equal(3, summary!.Total);
            Assert.Equal(1, summary.Failed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseTrxFilesInDirSince_HandlesUppercaseExtension()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "UPPER.TRX"),
                "<TestRun><Counters total=\"3\" passed=\"2\" failed=\"1\" /></TestRun>");

            var summary = DotnetTrx.ParseTrxFilesInDirSince(dir, null);

            Assert.NotNull(summary);
            Assert.Equal(3, summary!.Total);
            Assert.Equal(1, summary.Failed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseTrxFilesInDir_ReturnsNullWhenDirMissing()
    {
        Assert.Null(DotnetTrx.ParseTrxFilesInDir(Path.Combine(Path.GetTempPath(), "rtk_missing_" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void FindRecentTrxInDir_PicksNewestTrx()
    {
        var dir = NewTempDir();
        try
        {
            var older = Path.Combine(dir, "old.trx");
            var newer = Path.Combine(dir, "new.trx");
            File.WriteAllText(older, "old");
            File.WriteAllText(newer, "new");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            var found = DotnetTrx.FindRecentTrxInDir(dir);

            Assert.Equal(newer, found);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindRecentTrxInDir_IgnoresNonTrxFiles()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "noop");

            Assert.Null(DotnetTrx.FindRecentTrxInDir(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindRecentTrxInDir_ReturnsNullWhenMissing()
    {
        Assert.Null(DotnetTrx.FindRecentTrxInDir(Path.Combine(Path.GetTempPath(), "rtk_missing_" + Guid.NewGuid().ToString("N"))));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rtk_trx_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
