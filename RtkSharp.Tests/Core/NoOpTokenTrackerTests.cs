using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Core;

public class NoOpTokenTrackerTests
{
    [Fact]
    public void Record_DoesNotThrow_ForTypicalInput()
    {
        ITokenTracker tracker = new NoOpTokenTracker();

        var exception = Record.Exception(() => tracker.Record("git status", 1000, 200));

        Assert.Null(exception);
    }

    [Fact]
    public void Record_DoesNotThrow_ForEdgeCaseInput()
    {
        ITokenTracker tracker = new NoOpTokenTracker();

        var exception = Record.Exception(() => tracker.Record("", 0, 0));

        Assert.Null(exception);
    }
}
