using RtkSharp.Discover;
using Xunit;

namespace RtkSharp.Tests.Discover;

/// <summary>
/// Covers <see cref="CommandClassifier"/>, the thin wrapper reusing the already-ported rewrite
/// engine for Rust <c>discover::registry::classify_command</c>/<c>split_command_chain</c>.
/// </summary>
public sealed class CommandClassifierTests
{
    [Theory]
    [InlineData("git status")]
    [InlineData("cargo test")]
    public void IsSupported_KnownEcosystemCommands_ReturnsTrue(string command)
    {
        Assert.True(CommandClassifier.IsSupported(command));
    }

    [Theory]
    [InlineData("echo hello")]
    [InlineData("mkdir -p /tmp/foo")]
    [InlineData("cd /tmp")]
    public void IsSupported_UnsupportedCommands_ReturnsFalse(string command)
    {
        Assert.False(CommandClassifier.IsSupported(command));
    }

    [Fact]
    public void IsSupported_RtkPrefixedCommand_ReturnsFalse()
    {
        // "rtk " is itself in registry.rs's IGNORED_PREFIXES (rules.rs:938) — classify_command alone
        // does NOT treat an already-rtk-prefixed command as Supported. session_cmd.rs's
        // count_rtk_commands relies on an explicit separate `starts_with("rtk ")` OR-check for this
        // exact reason (session_cmd.rs:42-44) — SessionCommand.CountRtkCommands ports both halves.
        Assert.False(CommandClassifier.IsSupported("rtk git status"));
    }

    [Fact]
    public void SplitCommandChain_EmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(CommandClassifier.SplitCommandChain("   "));
    }

    [Fact]
    public void SplitCommandChain_SingleCommand_ReturnsOneElement()
    {
        Assert.Equal(["git status"], CommandClassifier.SplitCommandChain("git status"));
    }

    [Fact]
    public void SplitCommandChain_AndChain_SplitsIntoParts()
    {
        Assert.Equal(
            ["cd ./your/app/path", "rtk ls"],
            CommandClassifier.SplitCommandChain("cd ./your/app/path && rtk ls"));
    }

    [Fact]
    public void SplitCommandChain_SemicolonChain_SplitsIntoParts()
    {
        Assert.Equal(
            ["cd /tmp", "git status", "echo done"],
            CommandClassifier.SplitCommandChain("cd /tmp; git status; echo done"));
    }

    [Fact]
    public void SplitCommandChain_PipeChain_DropsEverythingFromFirstPipeOnward()
    {
        // split_on_operators(cmd, stop_at_pipe=true) (lexer.rs:401-409): on hitting a pipe token, the
        // segment before it is pushed and the function returns IMMEDIATELY — everything from "|"
        // onward, including the piped-to command, is discarded, not treated as a further segment.
        var parts = CommandClassifier.SplitCommandChain("git log | head -5");
        Assert.Equal(["git log"], parts);
    }

    [Fact]
    public void SplitCommandChain_Heredoc_NeverSplit()
    {
        var parts = CommandClassifier.SplitCommandChain("cat <<EOF && git status\nhello\nEOF");
        Assert.Single(parts);
    }

    [Fact]
    public void SplitCommandChain_ArithmeticExpansion_NeverSplit()
    {
        var parts = CommandClassifier.SplitCommandChain("echo $((1 + 1)) && git status");
        Assert.Single(parts);
    }
}
