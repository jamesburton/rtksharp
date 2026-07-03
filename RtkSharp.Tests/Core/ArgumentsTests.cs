using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Core;

public class ArgumentsTests
{
    [Fact]
    public void Parse_GlobalFlags_CorrectlyIdentified()
    {
        var args = new[] { "-v", "--ultra-compact", "--no-color", "git", "status" };
        var parsed = RtkArguments.Parse(args);

        Assert.Equal(1, parsed.Verbosity);
        Assert.True(parsed.UltraCompact);
        Assert.True(parsed.NoColor);
        Assert.Equal("git", parsed.CommandName);
        Assert.Single(parsed.CommandArgs);
        Assert.Equal("status", parsed.CommandArgs[0]);
    }

    [Fact]
    public void Parse_DoubleDash_ForcesPassthrough()
    {
        var args = new[] { "-vv", "--", "-v", "git", "status" };
        var parsed = RtkArguments.Parse(args);

        Assert.Equal(2, parsed.Verbosity);
        Assert.Equal("-v", parsed.CommandName);
        Assert.Equal(2, parsed.CommandArgs.Length);
        Assert.Equal("git", parsed.CommandArgs[0]);
        Assert.Equal("status", parsed.CommandArgs[1]);
    }

    [Fact]
    public void Parse_NoFlags_CommandAndArgsOnly()
    {
        var args = new[] { "dotnet", "build", "--configuration", "Release" };
        var parsed = RtkArguments.Parse(args);

        Assert.Equal(0, parsed.Verbosity);
        Assert.False(parsed.UltraCompact);
        Assert.False(parsed.NoColor);
        Assert.Equal("dotnet", parsed.CommandName);
        Assert.Equal(3, parsed.CommandArgs.Length);
        Assert.Equal("build", parsed.CommandArgs[0]);
        Assert.Equal("--configuration", parsed.CommandArgs[1]);
        Assert.Equal("Release", parsed.CommandArgs[2]);
    }
}
