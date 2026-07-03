using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Direct unit tests of <see cref="SystemParityTests.CommandResult.Compare"/>'s ordering-tolerance
/// branch, synthetic (no oracle/RtkSharp process spawn) so the scoping added in the Task 6 review —
/// unordered-multiset tolerance applies ONLY when a battery entry opts in via
/// <c>allowUnorderedLines</c> — is pinned independently of the live parity battery run.
/// </summary>
public class SystemParityComparisonTests
{
    [Fact]
    public void Compare_ReorderedButEqualMultiset_AllowUnorderedTrue_IsTolerated()
    {
        var rust = "b.txt\na.txt\nc.txt";
        var dotnet = "a.txt\nb.txt\nc.txt";

        var result = SystemParityTests.CommandResult.Compare(
            "grep -rln \"x\" dir", rust, dotnet, rustExit: 0, dotnetExit: 0, allowUnorderedLines: true);

        Assert.True(result.IsPerfectMatch);
        Assert.True(result.OrderingOnlyDeviation);
        Assert.Equal(result.TotalLines, result.MatchedLines);
    }

    [Fact]
    public void Compare_ReorderedButEqualMultiset_AllowUnorderedFalse_IsNotTolerated()
    {
        var rust = "b.txt\na.txt\nc.txt";
        var dotnet = "a.txt\nb.txt\nc.txt";

        var result = SystemParityTests.CommandResult.Compare(
            "ls .", rust, dotnet, rustExit: 0, dotnetExit: 0, allowUnorderedLines: false);

        Assert.False(result.IsPerfectMatch);
        Assert.False(result.OrderingOnlyDeviation);
        Assert.NotEqual(result.TotalLines, result.MatchedLines);
    }

    [Fact]
    public void Compare_UnequalMultiset_AllowUnorderedTrue_StillMismatches()
    {
        var rust = "a.txt\nb.txt\nc.txt";
        var dotnet = "a.txt\nb.txt\nd.txt";

        var result = SystemParityTests.CommandResult.Compare(
            "grep -rln \"x\" dir", rust, dotnet, rustExit: 0, dotnetExit: 0, allowUnorderedLines: true);

        Assert.False(result.IsPerfectMatch);
        Assert.False(result.OrderingOnlyDeviation);
    }

    [Fact]
    public void Compare_UnequalMultiset_AllowUnorderedFalse_Mismatches()
    {
        var rust = "a.txt\nb.txt\nc.txt";
        var dotnet = "a.txt\nb.txt\nd.txt";

        var result = SystemParityTests.CommandResult.Compare(
            "ls .", rust, dotnet, rustExit: 0, dotnetExit: 0, allowUnorderedLines: false);

        Assert.False(result.IsPerfectMatch);
        Assert.False(result.OrderingOnlyDeviation);
    }

    [Fact]
    public void SortedEqual_SameMultisetDifferentOrder_ReturnsTrue()
    {
        var a = new[] { "b.txt", "a.txt", "c.txt" };
        var b = new[] { "a.txt", "b.txt", "c.txt" };

        Assert.True(SystemParityTests.CommandResult.SortedEqual(a, b));
    }

    [Fact]
    public void SortedEqual_DifferentMultiset_ReturnsFalse()
    {
        var a = new[] { "a.txt", "b.txt", "c.txt" };
        var b = new[] { "a.txt", "b.txt", "d.txt" };

        Assert.False(SystemParityTests.CommandResult.SortedEqual(a, b));
    }
}
