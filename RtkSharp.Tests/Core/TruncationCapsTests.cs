using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Core;

public class TruncationCapsTests
{
    [Theory]
    [InlineData(TruncationCategory.Errors, 50)]
    [InlineData(TruncationCategory.Warnings, 30)]
    [InlineData(TruncationCategory.Lists, 100)]
    [InlineData(TruncationCategory.Inventory, 200)]
    [InlineData(TruncationCategory.Logs, 50)]
    public void GetDefaultCap_ReturnsExpectedValuePerCategory(TruncationCategory category, int expectedCap)
    {
        Assert.Equal(expectedCap, TruncationCaps.GetDefaultCap(category));
    }

    [Fact]
    public void Truncate_UnderCap_ReturnsUnchangedWithNoOmissionOrHint()
    {
        var content = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line{i}"));

        var result = TruncationCaps.Truncate(content, TruncationCategory.Warnings, "test-cmd");

        Assert.Equal(content, result.Text);
        Assert.Equal(0, result.OmittedLineCount);
        Assert.Null(result.RecoveryHint);
    }

    [Fact]
    public void Truncate_OverCap_TruncatesAndReportsOmittedCount()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line{i}").ToArray();
        var content = string.Join('\n', lines);

        var result = TruncationCaps.Truncate(content, TruncationCategory.Warnings, "test-cmd");

        var resultLines = result.Text.Split('\n');
        Assert.Equal(30, resultLines.Length);
        Assert.Equal("line1", resultLines[0]);
        Assert.Equal("line30", resultLines[^1]);
        Assert.Equal(10, result.OmittedLineCount);
    }

    [Fact]
    public void Truncate_OverCap_WithTeeDisabled_HasNullRecoveryHintButStillReportsOmission()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line{i}").ToArray();
        var content = string.Join('\n', lines);
        var teeConfig = new TeeConfig { Enabled = false };

        var result = TruncationCaps.Truncate(content, TruncationCategory.Warnings, "test-cmd", teeConfig);

        Assert.Equal(10, result.OmittedLineCount);
        Assert.Null(result.RecoveryHint);
    }

    [Fact]
    public void Truncate_EmptyContent_ReturnsEmptyWithNoOmission()
    {
        var result = TruncationCaps.Truncate("", TruncationCategory.Errors, "test-cmd");

        Assert.Equal("", result.Text);
        Assert.Equal(0, result.OmittedLineCount);
        Assert.Null(result.RecoveryHint);
    }
}
