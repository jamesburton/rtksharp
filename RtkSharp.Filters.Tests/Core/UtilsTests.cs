using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Core;

public class UtilsTests
{
    [Fact]
    public void StripAnsi_RemovesColorCodes()
    {
        var colored = "\x1b[31mError\x1b[0m";
        var clean = Utils.StripAnsi(colored);
        Assert.Equal("Error", clean);
    }

    [Fact]
    public void StripAnsi_HandlesEmptyAndNull()
    {
        Assert.Equal("", Utils.StripAnsi(""));
        Assert.Null(Utils.StripAnsi(null!));
    }
}
