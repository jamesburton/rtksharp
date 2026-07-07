using RtkSharp.Commands.Cloud;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Ports every test in <c>src/cmds/cloud/wget_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>.
/// </summary>
public class WgetCommandTests
{
    [Fact]
    public void CompactUrl_StripsProtocol()
    {
        Assert.Equal("example.com/file.zip", WgetCommand.CompactUrl("https://example.com/file.zip"));
        Assert.Equal("example.com/file.zip", WgetCommand.CompactUrl("http://example.com/file.zip"));
    }

    [Fact]
    public void CompactUrl_TruncatesLongUrl()
    {
        const string Long = "https://example.com/very/long/path/that/exceeds/fifty/characters/file.zip";
        var result = WgetCommand.CompactUrl(Long);
        Assert.Contains("...", result);
        Assert.True(result.Length < Long.Length);
    }

    [Fact]
    public void CompactUrl_ShortUnchanged()
    {
        const string Short = "https://x.com/f";
        Assert.Equal("x.com/f", WgetCommand.CompactUrl(Short));
    }

    [Fact]
    public void FormatSize_Zero()
    {
        Assert.Equal("?", WgetCommand.FormatSize(0));
    }

    [Fact]
    public void FormatSize_Bytes()
    {
        Assert.Equal("512B", WgetCommand.FormatSize(512));
    }

    [Fact]
    public void FormatSize_Kilobytes()
    {
        var result = WgetCommand.FormatSize(2048);
        Assert.EndsWith("KB", result);
    }

    [Fact]
    public void FormatSize_Megabytes()
    {
        var result = WgetCommand.FormatSize(2 * 1024 * 1024);
        Assert.EndsWith("MB", result);
    }

    [Fact]
    public void ParseError_404()
    {
        Assert.Equal("404 Not Found", WgetCommand.ParseError("HTTP request failed: 404", ""));
    }

    [Fact]
    public void ParseError_Dns()
    {
        Assert.Equal("DNS lookup failed", WgetCommand.ParseError("unable to resolve host example.com", ""));
    }

    [Fact]
    public void ParseError_Ssl()
    {
        Assert.Equal("SSL/TLS error", WgetCommand.ParseError("SSL certificate verification failed", ""));
    }

    [Fact]
    public void ParseError_Unknown()
    {
        Assert.Equal("Unknown error", WgetCommand.ParseError("", ""));
    }

    [Fact]
    public void TruncateLine_Short()
    {
        Assert.Equal("hello", WgetCommand.TruncateLine("hello", 10));
    }

    [Fact]
    public void TruncateLine_Exact()
    {
        Assert.Equal("hello", WgetCommand.TruncateLine("hello", 5));
    }

    [Fact]
    public void TruncateLine_Long()
    {
        var result = WgetCommand.TruncateLine("hello world this is long", 10);
        Assert.EndsWith("...", result);
        Assert.True(result.Length <= 10);
    }

    [Fact]
    public void ExtractFilenameFromOutput_ExplicitOutputFlag()
    {
        var args = new[] { "-O", "myfile.zip" };
        Assert.Equal(
            "myfile.zip",
            WgetCommand.ExtractFilenameFromOutput("", "https://example.com/x", args));
    }

    [Fact]
    public void ExtractFilenameFromOutput_UrlFallback()
    {
        var result = WgetCommand.ExtractFilenameFromOutput("", "https://example.com/file.tar.gz", []);
        Assert.Equal("file.tar.gz", result);
    }

    [Fact]
    public void ExtractFilenameFromOutput_EmptyUrlFallback()
    {
        var result = WgetCommand.ExtractFilenameFromOutput("", "https://example.com/", []);
        Assert.Equal("index.html", result);
    }
}
