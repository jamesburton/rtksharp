using RtkSharp.Commands.Cloud;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="WgetCommand.ExtractFilenameFromOutput"/>, ported from
/// <c>src/cmds/cloud/wget_cmd.rs</c>'s own <c>#[cfg(test)] mod tests</c>. The
/// <c>format_size</c>/<c>compact_url</c>/<c>parse_error</c>/<c>truncate_line</c> tests moved to
/// <c>RtkSharp.Filters.Tests.Commands.Cloud.WgetFiltersTests</c> alongside the methods they cover.
/// </summary>
public class WgetCommandTests
{
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
