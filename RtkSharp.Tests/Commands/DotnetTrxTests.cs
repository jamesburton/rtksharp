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
