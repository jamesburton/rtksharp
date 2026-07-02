using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Core;

public class ArgumentParserTests
{
    [Fact]
    public void Parse_DelegatesToRtkArgumentsParse()
    {
        IArgumentParser parser = new ArgumentParser();
        var args = new[] { "-v", "git", "status" };

        var parsed = parser.Parse(args);

        Assert.Equal(1, parsed.Verbosity);
        Assert.Equal("git", parsed.CommandName);
        Assert.Single(parsed.CommandArgs);
        Assert.Equal("status", parsed.CommandArgs[0]);
    }

    [Fact]
    public void Parse_ImplementsInterfaceContract()
    {
        IArgumentParser parser = new ArgumentParser();

        var parsed = parser.Parse(Array.Empty<string>());

        Assert.Null(parsed.CommandName);
        Assert.Empty(parsed.CommandArgs);
    }
}
