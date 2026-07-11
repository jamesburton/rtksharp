using RtkSharp.Filters;

namespace RtkSharp.Filters.Tests;

public class RtkFiltersTests
{
    [Theory]
    [InlineData("git")]
    [InlineData("npm")]
    [InlineData("ls")]
    [InlineData("aws")]
    public void IsRegistered_ReturnsTrue_ForKnownFilters(string command)
    {
        Assert.True(RtkFilters.IsRegistered(command));
    }

    [Theory]
    [InlineData("notacommand")]
    [InlineData("")]
    [InlineData("err")]   // explicitly excluded per the design's non-goals
    [InlineData("run")]   // no filtering by design
    [InlineData("proxy")] // no filtering by design
    public void IsRegistered_ReturnsFalse_ForUnknownOrExcludedCommands(string command)
    {
        Assert.False(RtkFilters.IsRegistered(command));
    }

    [Fact]
    public void Filter_ThrowsInvalidOperationException_ForUnregisteredCommand()
    {
        Assert.Throws<InvalidOperationException>(
            () => RtkFilters.Filter("notacommand", [], "", "", 0));
    }

    [Fact]
    public void Filter_DispatchesToRegisteredFilter()
    {
        var filtered = RtkFilters.Filter("git", ["log"], "commit abc123\nAuthor: Test\n\n    msg\n", "", 0);

        Assert.Contains("abc123", filtered);
    }
}
