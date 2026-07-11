using RtkSharp.Filters.Commands.Cloud;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Cloud;

/// <summary>
/// Ports the <c>format_size</c>/<c>compact_url</c>/<c>parse_error</c>/<c>truncate_line</c> tests from
/// <c>src/cmds/cloud/wget_cmd.rs</c>'s own <c>#[cfg(test)] mod tests</c>.
/// <c>ExtractFilenameFromOutput</c> coverage stays in
/// <c>RtkSharp.Tests.Commands.Cloud.WgetCommandTests</c> — that method did NOT move (see
/// <see cref="WgetFilters"/>'s class remarks).
/// </summary>
public class WgetFiltersTests
{
    [Fact]
    public void CompactUrl_StripsProtocol()
    {
        Assert.Equal("example.com/file.zip", WgetFilters.CompactUrl("https://example.com/file.zip"));
        Assert.Equal("example.com/file.zip", WgetFilters.CompactUrl("http://example.com/file.zip"));
    }

    [Fact]
    public void CompactUrl_TruncatesLongUrl()
    {
        const string Long = "https://example.com/very/long/path/that/exceeds/fifty/characters/file.zip";
        var result = WgetFilters.CompactUrl(Long);
        Assert.Contains("...", result);
        Assert.True(result.Length < Long.Length);
    }

    [Fact]
    public void CompactUrl_ShortUnchanged()
    {
        const string Short = "https://x.com/f";
        Assert.Equal("x.com/f", WgetFilters.CompactUrl(Short));
    }

    [Fact]
    public void FormatSize_Zero()
    {
        Assert.Equal("?", WgetFilters.FormatSize(0));
    }

    [Fact]
    public void FormatSize_Bytes()
    {
        Assert.Equal("512B", WgetFilters.FormatSize(512));
    }

    [Fact]
    public void FormatSize_Kilobytes()
    {
        var result = WgetFilters.FormatSize(2048);
        Assert.EndsWith("KB", result);
    }

    [Fact]
    public void FormatSize_Megabytes()
    {
        var result = WgetFilters.FormatSize(2 * 1024 * 1024);
        Assert.EndsWith("MB", result);
    }

    [Fact]
    public void ParseError_404()
    {
        Assert.Equal("404 Not Found", WgetFilters.ParseError("HTTP request failed: 404", ""));
    }

    [Fact]
    public void ParseError_Dns()
    {
        Assert.Equal("DNS lookup failed", WgetFilters.ParseError("unable to resolve host example.com", ""));
    }

    [Fact]
    public void ParseError_Ssl()
    {
        Assert.Equal("SSL/TLS error", WgetFilters.ParseError("SSL certificate verification failed", ""));
    }

    [Fact]
    public void ParseError_Unknown()
    {
        Assert.Equal("Unknown error", WgetFilters.ParseError("", ""));
    }

    [Fact]
    public void TruncateLine_Short()
    {
        Assert.Equal("hello", WgetFilters.TruncateLine("hello", 10));
    }

    [Fact]
    public void TruncateLine_Exact()
    {
        Assert.Equal("hello", WgetFilters.TruncateLine("hello", 5));
    }

    [Fact]
    public void TruncateLine_Long()
    {
        var result = WgetFilters.TruncateLine("hello world this is long", 10);
        Assert.EndsWith("...", result);
        Assert.True(result.Length <= 10);
    }

    // ===================== FormatWgetOutput / FormatWgetFailure / FormatWgetStdoutOutput =====================
    // New pure-boundary coverage, carved out of WgetCommand's previously-fused RunAsync/RunStdoutAsync.

    [Fact]
    public void FormatWgetOutput_Success_FormatsUrlFilenameAndSize()
    {
        var result = WgetFilters.FormatWgetOutput("https://example.com/file.zip", "file.zip", 2048);

        Assert.Equal("example.com/file.zip ok | file.zip | 2.0KB", result);
    }

    [Fact]
    public void FormatWgetFailure_FormatsUrlAndError()
    {
        var result = WgetFilters.FormatWgetFailure("https://example.com/missing", "404 Not Found");

        Assert.Equal("example.com/missing FAILED: 404 Not Found", result);
    }

    [Fact]
    public void FormatWgetStdoutOutput_ShortContent_PrintsAllLines()
    {
        var result = WgetFilters.FormatWgetStdoutOutput("https://example.com/", "line1\nline2\n");

        Assert.Equal("example.com/ ok | 2 lines\nline1\nline2\n", result);
    }

    [Fact]
    public void FormatWgetStdoutOutput_MoreThanTwentyLines_ShowsHeadSummary()
    {
        var stdout = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"line{i}")) + "\n";

        var result = WgetFilters.FormatWgetStdoutOutput("https://example.com/big.log", stdout);

        Assert.StartsWith("example.com/big.log ok | 25 lines | ", result, StringComparison.Ordinal);
        Assert.Contains("first 10 lines:", result, StringComparison.Ordinal);
        Assert.Contains("line1\n", result, StringComparison.Ordinal);
        Assert.EndsWith("... +15 more lines", result, StringComparison.Ordinal);
        Assert.DoesNotContain("line11", result, StringComparison.Ordinal);
    }
}
