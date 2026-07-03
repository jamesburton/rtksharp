using RtkSharp.Commands.Dotnet;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="DotnetFormatReport"/> (format-report JSON parsing) and
/// <see cref="DotnetCommand"/>'s format-related pure functions (arg injection, report freshness,
/// and the files-needing-formatting summary). Fixture content mirrors the real reports captured
/// under <c>tests/fixtures/dotnet/format_success.json</c> / <c>format_changes.json</c> /
/// <c>format_empty.json</c> — the shape emitted by <c>dotnet format --report</c> (PascalCase keys,
/// an unmapped <c>FileName</c> field per entry). Expectations are derived from the Rust spec in
/// <c>src/cmds/dotnet/dotnet_format_report.rs</c> + <c>run_format</c> in <c>dotnet_cmd.rs</c>.
/// </summary>
public sealed class DotnetFormatTests : IDisposable
{
    private readonly List<string> tempFiles = new();


    // tests/fixtures/dotnet/format_success.json — two files, neither has changes.
    private const string AllFormattedJson =
        "[\n" +
        "  {\n" +
        "    \"FileName\": \"Program.cs\",\n" +
        "    \"FilePath\": \"src/Program.cs\",\n" +
        "    \"FileChanges\": []\n" +
        "  },\n" +
        "  {\n" +
        "    \"FileName\": \"Utils.cs\",\n" +
        "    \"FilePath\": \"src/Utils.cs\",\n" +
        "    \"FileChanges\": []\n" +
        "  }\n" +
        "]\n";

    // tests/fixtures/dotnet/format_changes.json — 3 files total, 2 with changes, 1 unchanged.
    private const string WithChangesJson =
        "[\n" +
        "  {\n" +
        "    \"FileName\": \"Program.cs\",\n" +
        "    \"FilePath\": \"src/Program.cs\",\n" +
        "    \"FileChanges\": [\n" +
        "      {\n" +
        "        \"LineNumber\": 42,\n" +
        "        \"CharNumber\": 17,\n" +
        "        \"DiagnosticId\": \"WHITESPACE\",\n" +
        "        \"FormatDescription\": \"Fix whitespace\"\n" +
        "      }\n" +
        "    ]\n" +
        "  },\n" +
        "  {\n" +
        "    \"FileName\": \"Utils.cs\",\n" +
        "    \"FilePath\": \"src/Utils.cs\",\n" +
        "    \"FileChanges\": [\n" +
        "      {\n" +
        "        \"LineNumber\": 15,\n" +
        "        \"CharNumber\": 8,\n" +
        "        \"DiagnosticId\": \"IDE0055\",\n" +
        "        \"FormatDescription\": \"Fix formatting\"\n" +
        "      }\n" +
        "    ]\n" +
        "  },\n" +
        "  {\n" +
        "    \"FileName\": \"Tests.cs\",\n" +
        "    \"FilePath\": \"tests/Tests.cs\",\n" +
        "    \"FileChanges\": []\n" +
        "  }\n" +
        "]\n";

    // tests/fixtures/dotnet/format_empty.json
    private const string EmptyJson = "[]\n";

    private static string WriteFixture(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rtk_format_fixture_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    // ---- DotnetFormatReport.ParseFormatReport ----

    [Fact]
    public void ParseFormatReport_AllFormatted_ReportsZeroChangedFiles()
    {
        var path = WriteFixture(AllFormattedJson);
        try
        {
            var summary = DotnetFormatReport.ParseFormatReport(path);

            Assert.Equal(2, summary.TotalFiles);
            Assert.Equal(2, summary.FilesUnchanged);
            Assert.Empty(summary.FilesWithChanges);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFormatReport_WithChanges_ExtractsFilesAndChangeDetail()
    {
        var path = WriteFixture(WithChangesJson);
        try
        {
            var summary = DotnetFormatReport.ParseFormatReport(path);

            Assert.Equal(3, summary.TotalFiles);
            Assert.Equal(1, summary.FilesUnchanged);
            Assert.Equal(2, summary.FilesWithChanges.Count);
            Assert.Contains("Program.cs", summary.FilesWithChanges[0].Path);
            Assert.Equal(42u, summary.FilesWithChanges[0].Changes[0].LineNumber);
            Assert.Equal(17u, summary.FilesWithChanges[0].Changes[0].CharNumber);
            Assert.Equal("WHITESPACE", summary.FilesWithChanges[0].Changes[0].DiagnosticId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFormatReport_Empty_ReportsZeroFiles()
    {
        var path = WriteFixture(EmptyJson);
        try
        {
            var summary = DotnetFormatReport.ParseFormatReport(path);

            Assert.Equal(0, summary.TotalFiles);
            Assert.Equal(0, summary.FilesUnchanged);
            Assert.Empty(summary.FilesWithChanges);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFormatReport_MissingFile_Throws()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"rtk_format_missing_{Guid.NewGuid():N}.json");

        Assert.Throws<InvalidOperationException>(() => DotnetFormatReport.ParseFormatReport(missingPath));
    }

    [Fact]
    public void ParseFormatReport_MalformedJson_Throws()
    {
        var path = WriteFixture("{ not valid json");
        try
        {
            Assert.Throws<InvalidOperationException>(() => DotnetFormatReport.ParseFormatReport(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- DotnetCommand.FormatDotnetFormatOutput ----

    [Fact]
    public void FormatDotnetFormatOutput_NoChanges_ReportsFormattedCorrectly()
    {
        var summary = DotnetFormatReport.ParseFormatReport(WriteAndTrack(AllFormattedJson));

        var output = DotnetCommand.FormatDotnetFormatOutput(summary, checkMode: true);

        Assert.Equal("ok dotnet format: 2 files formatted correctly", output);
    }

    [Fact]
    public void FormatDotnetFormatOutput_CheckModeWithChanges_ListsFilesAndRecoveryHint()
    {
        var summary = DotnetFormatReport.ParseFormatReport(WriteAndTrack(WithChangesJson));

        var output = DotnetCommand.FormatDotnetFormatOutput(summary, checkMode: true);

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
        var summary = DotnetFormatReport.ParseFormatReport(WriteAndTrack(WithChangesJson));

        var output = DotnetCommand.FormatDotnetFormatOutput(summary, checkMode: false);

        Assert.Equal("ok dotnet format: formatted 2 files (1 already formatted)", output);
    }

    [Fact]
    public void FormatDotnetFormatOutput_ExceedsCap_AddsOverflowLineAndHint()
    {
        var entries = string.Join(
            ",\n",
            Enumerable.Range(1, 25).Select(i =>
                $"{{ \"FileName\": \"F{i}.cs\", \"FilePath\": \"src/F{i}.cs\", " +
                $"\"FileChanges\": [{{ \"LineNumber\": {i}, \"CharNumber\": 1, " +
                "\"DiagnosticId\": \"IDE0055\", \"FormatDescription\": \"Fix\" }] }"));
        var json = $"[\n{entries}\n]\n";
        var summary = DotnetFormatReport.ParseFormatReport(WriteAndTrack(json));

        var output = DotnetCommand.FormatDotnetFormatOutput(summary, checkMode: true);

        Assert.Contains("Format: 25 files need formatting", output);
        Assert.Contains("… +5 more files", output);
        Assert.Contains("20. src/F20.cs", output);
        Assert.DoesNotContain("21. src/F21.cs", output);
    }

    private string WriteAndTrack(string content)
    {
        var path = WriteFixture(content);
        this.tempFiles.Add(path);
        return path;
    }

    // ---- DotnetCommand.BuildEffectiveDotnetFormatArgs ----

    [Fact]
    public void BuildEffectiveDotnetFormatArgs_Default_InjectsVerifyAndReport()
    {
        var effective = DotnetCommand.BuildEffectiveDotnetFormatArgs(
            new[] { "RtkSharp.slnx" }, "/tmp/report.json");

        Assert.Equal(
            new[] { "RtkSharp.slnx", "--verify-no-changes", "--report", "/tmp/report.json" },
            effective);
    }

    [Fact]
    public void BuildEffectiveDotnetFormatArgs_WriteFlag_StripsItAndSkipsVerify()
    {
        var effective = DotnetCommand.BuildEffectiveDotnetFormatArgs(
            new[] { "--write", "RtkSharp.slnx" }, "/tmp/report.json");

        Assert.Equal(
            new[] { "RtkSharp.slnx", "--report", "/tmp/report.json" },
            effective);
    }

    [Fact]
    public void BuildEffectiveDotnetFormatArgs_UserSuppliedVerify_DoesNotDuplicate()
    {
        var effective = DotnetCommand.BuildEffectiveDotnetFormatArgs(
            new[] { "--verify-no-changes", "RtkSharp.slnx" }, "/tmp/report.json");

        Assert.Equal(
            new[] { "--verify-no-changes", "RtkSharp.slnx", "--report", "/tmp/report.json" },
            effective);
    }

    [Fact]
    public void BuildEffectiveDotnetFormatArgs_UserSuppliedReport_DoesNotInjectAnother()
    {
        var effective = DotnetCommand.BuildEffectiveDotnetFormatArgs(
            new[] { "--report", "mine.json", "RtkSharp.slnx" }, "/tmp/report.json");

        Assert.Equal(
            new[] { "--report", "mine.json", "RtkSharp.slnx", "--verify-no-changes" },
            effective);
    }

    // ---- DotnetCommand.FormatReportSummaryOrRaw ----

    [Fact]
    public void FormatReportSummaryOrRaw_NoReportPath_ReturnsRaw()
    {
        var output = DotnetCommand.FormatReportSummaryOrRaw(null, checkMode: true, "raw output", DateTime.UtcNow);

        Assert.Equal("raw output", output);
    }

    [Fact]
    public void FormatReportSummaryOrRaw_StaleReport_ReturnsRaw()
    {
        var path = WriteAndTrack(AllFormattedJson);

        // commandStartedAt is far in the future relative to the file's actual write time, so the
        // report reads as stale (a leftover from a previous run at a user-specified path).
        var output = DotnetCommand.FormatReportSummaryOrRaw(
            path, checkMode: true, "raw output", DateTime.UtcNow.AddMinutes(5));

        Assert.Equal("raw output", output);
    }

    [Fact]
    public void FormatReportSummaryOrRaw_FreshReport_ReturnsFilteredSummary()
    {
        var path = WriteAndTrack(AllFormattedJson);

        var output = DotnetCommand.FormatReportSummaryOrRaw(
            path, checkMode: true, "raw output", DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal("ok dotnet format: 2 files formatted correctly", output);
    }

    [Fact]
    public void FormatReportSummaryOrRaw_MissingReportFile_ReturnsRaw()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"rtk_format_missing_{Guid.NewGuid():N}.json");

        var output = DotnetCommand.FormatReportSummaryOrRaw(
            missingPath, checkMode: true, "raw output", DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal("raw output", output);
    }

    /// <summary>Cleans up every fixture file written via <see cref="WriteAndTrack"/>.</summary>
    public void Dispose()
    {
        foreach (var path in this.tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
