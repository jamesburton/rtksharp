using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Rewrite;

public class RewriteEngineTests
{
    private static string? Rewrite(string cmd) =>
        RewriteEngine.RewriteCommand(cmd, Array.Empty<string>(), Array.Empty<string>());

    [Theory]
    [InlineData("git status", "rtk git status")]
    [InlineData("gh pr list", "rtk gh pr list")]
    [InlineData("cd foo && git status", "cd foo && rtk git status")]
    [InlineData("rtk git status", "rtk git status")] // already rtk — returned as-is
    public void RewriteCommand_KnownCases(string input, string expected)
    {
        Assert.Equal(expected, Rewrite(input));
    }

    [Theory]
    [InlineData("echo hello")]           // no rule
    [InlineData("")]                      // empty
    [InlineData("cat <<EOF\nhi\nEOF")]    // heredoc guard
    public void RewriteCommand_NoRewrite_ReturnsNull(string input)
    {
        Assert.Null(Rewrite(input));
    }

    // Oracle-verified edge cases (probed against target/release/rtk.exe rewrite).
    [Theory]
    [InlineData("git status | grep foo", "rtk git status | grep foo")] // pipe: only left rewritten
    [InlineData("git status && cargo test", "rtk git status && rtk cargo test")]
    [InlineData("cargo test; git status", "rtk cargo test; rtk git status")]
    [InlineData("git status 2>&1", "rtk git status 2>&1")] // trailing redirect preserved
    [InlineData("sudo git status", "sudo rtk git status")] // env/sudo prefix preserved
    [InlineData("GIT_DISABLED=1 git status", "GIT_DISABLED=1 rtk git status")]
    [InlineData("uv run pytest", "uv run rtk pytest")] // builtin transparent prefix
    [InlineData("head -5 file.txt", "rtk read file.txt --max-lines 5")]
    [InlineData("cat -n file.txt", "rtk read -n file.txt")]
    public void RewriteCommand_OracleVerified(string input, string expected)
    {
        Assert.Equal(expected, Rewrite(input));
    }

    // Oracle-verified guards that produce no rewrite (engine returns null).
    [Theory]
    [InlineData("find . -name x | head")]      // pipe-incompatible left + raw target
    [InlineData("RTK_DISABLED=1 git status")]  // RTK_DISABLED short-circuits
    [InlineData("gh pr view 1 --json title")]  // structured-output flag guard
    [InlineData("cat -v file.txt")]            // unsupported cat flag
    [InlineData("/usr/bin/grep foo bar")]      // absolute path: prefix strip fails
    public void RewriteCommand_Guards_ReturnNull(string input)
    {
        Assert.Null(Rewrite(input));
    }
}
