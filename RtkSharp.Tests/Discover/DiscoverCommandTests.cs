using System;
using System.Collections.Generic;
using System.IO;
using RtkSharp.Discover;
using Xunit;

namespace RtkSharp.Tests.Discover;

/// <summary>
/// Test-for-test port of Rust <c>src/discover/mod.rs</c>'s helper functions
/// (<c>extract_subcmd</c>/<c>truncate_command</c>) plus end-to-end coverage of
/// <see cref="DiscoverCommand.RunCore"/> against seeded JSONL fixtures (mirroring
/// <c>SessionCommandTests</c>'s <c>ClaudeConfigDirGuard</c> convention so the real
/// <c>~/.claude/projects</c> is never touched), and of <see cref="DiscoverArgs.Parse"/>'s flag
/// parsing.
/// </summary>
public sealed class DiscoverCommandTests
{
    // ===================== extract_subcmd / truncate_command (mod.rs's own free functions) =====================

    [Theory]
    [InlineData("git status", "status")]
    [InlineData("cargo test filter::", "test")]
    [InlineData("htop", "")]
    [InlineData("", "")]
    public void ExtractSubcmd_MatchesRustOracle(string cmd, string expected)
    {
        Assert.Equal(expected, DiscoverCommand.ExtractSubcmd(cmd));
    }

    [Theory]
    [InlineData("git status --porcelain", "git status")]
    [InlineData("git status", "git status")]
    [InlineData("htop", "htop")]
    [InlineData("", "")]
    public void TruncateCommand_KeepsFirstTwoWords(string cmd, string expected)
    {
        Assert.Equal(expected, DiscoverCommand.TruncateCommand(cmd));
    }

    // ===================== DiscoverArgs.Parse =====================

    [Fact]
    public void Parse_Defaults_MatchClapDefaults()
    {
        var args = DiscoverArgs.Parse([]);

        Assert.Null(args.Project);
        Assert.Equal(15, args.Limit);
        Assert.False(args.All);
        Assert.Equal(30UL, args.SinceDays);
        Assert.Equal("text", args.Format);
    }

    [Fact]
    public void Parse_AllFlags_ShortForm()
    {
        var args = DiscoverArgs.Parse(["-p", "myproject", "-l", "5", "-a", "-s", "7", "-f", "json"]);

        Assert.Equal("myproject", args.Project);
        Assert.Equal(5, args.Limit);
        Assert.True(args.All);
        Assert.Equal(7UL, args.SinceDays);
        Assert.Equal("json", args.Format);
    }

    [Fact]
    public void Parse_AllFlags_LongFormWithEquals()
    {
        var args = DiscoverArgs.Parse(["--project=myproject", "--limit=5", "--since=7", "--format=json"]);

        Assert.Equal("myproject", args.Project);
        Assert.Equal(5, args.Limit);
        Assert.Equal(7UL, args.SinceDays);
        Assert.Equal("json", args.Format);
    }

    [Fact]
    public void Parse_UnrecognizedFlag_Throws()
    {
        var ex = Assert.Throws<DiscoverArgsException>(() => DiscoverArgs.Parse(["--bogus"]));
        Assert.Contains("unexpected argument", ex.Message);
    }

    [Fact]
    public void Parse_MissingValueForFlag_Throws()
    {
        Assert.Throws<DiscoverArgsException>(() => DiscoverArgs.Parse(["--limit"]));
    }

    [Fact]
    public void Parse_NonNumericLimit_Throws()
    {
        Assert.Throws<DiscoverArgsException>(() => DiscoverArgs.Parse(["--limit", "abc"]));
    }

    [Fact]
    public void Parse_NonNumericSince_Throws()
    {
        Assert.Throws<DiscoverArgsException>(() => DiscoverArgs.Parse(["--since", "abc"]));
    }

    // ===================== RunCore: end-to-end against seeded JSONL fixtures =====================

    [Fact]
    public void RunCore_NoSessions_PrintsZeroCounts()
    {
        using var claude = new ClaudeConfigDirGuard();
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var args = DiscoverArgs.Parse(["--all"]);

        var exit = DiscoverCommand.RunCore(args, stdout, stderr, 0);

        Assert.Equal(0, exit);
        Assert.Contains("Scanned: 0 sessions", stdout.ToString());
        Assert.Contains("No missed savings found", stdout.ToString());
    }

    [Fact]
    public void RunCore_UnrewrittenGitLog_ReportedAsMissedSavings()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "proj",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git log -5"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"commit abc\ncommit def"}]}}""");
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var args = DiscoverArgs.Parse(["--all"]);

        var exit = DiscoverCommand.RunCore(args, stdout, stderr, 0);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("MISSED SAVINGS", output);
        Assert.Contains("git log", output);
        Assert.Contains("rtk git", output);
    }

    [Fact]
    public void RunCore_AlreadyRtkCommand_CountedAsAlreadyUsingRtk()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "proj",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rtk git status"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"On branch main"}]}}""");
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var args = DiscoverArgs.Parse(["--all"]);

        var exit = DiscoverCommand.RunCore(args, stdout, stderr, 0);

        Assert.Equal(0, exit);
        Assert.Contains("Already using RTK: 1 commands (100.0%)", stdout.ToString());
    }

    [Fact]
    public void RunCore_UnsupportedCommand_ReportedInUnhandledSection()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "proj",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"htop -d 10"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"..."}]}}""");
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var args = DiscoverArgs.Parse(["--all"]);

        var exit = DiscoverCommand.RunCore(args, stdout, stderr, 0);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("TOP UNHANDLED COMMANDS", output);
        Assert.Contains("htop", output);
    }

    [Fact]
    public void RunCore_RtkDisabledBypassOnSupportedCommand_CountedSeparately()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "proj",
            "session1.jsonl",
            // A second, non-bypassed missed-savings command is included so the report's
            // supported/unsupported lists aren't BOTH empty — an all-empty report hits
            // format_text's early "No missed savings found" return (report.rs:144-148) before the
            // RTK_DISABLED section is ever considered, so a bypass-only report would render nothing
            // about the bypass at all (a genuine, faithfully-preserved Rust quirk).
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"RTK_DISABLED=1 git status"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"On branch main"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git log -5"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"commit abc"}]}}""");
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var args = DiscoverArgs.Parse(["--all"]);

        var exit = DiscoverCommand.RunCore(args, stdout, stderr, 0);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("RTK_DISABLED BYPASS -- 1 commands ran without filtering", output);
        Assert.Contains("git status (1x)", output);
        // The bypassed command must not also inflate the already-rtk counter.
        Assert.Contains("Already using RTK: 0 commands (0.0%)", output);
    }

    [Fact]
    public void RunCore_JsonFormat_ProducesParsableJsonWithTrailingNewline()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "proj",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git log -5"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"commit abc"}]}}""");
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var args = DiscoverArgs.Parse(["--all", "--format", "json"]);

        var exit = DiscoverCommand.RunCore(args, stdout, stderr, 0);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.EndsWith("}\n", output);
        Assert.Contains("\"sessions_scanned\"", output);
    }

    [Fact]
    public void RunCore_VerboseFlag_PrintsSessionScanNoticeToStderr()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "proj",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git log -5"}}]}}""");
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var args = DiscoverArgs.Parse(["--all"]);

        DiscoverCommand.RunCore(args, stdout, stderr, 1);

        Assert.Contains("Scanning 1 session files...", stderr.ToString());
    }

    private static void WriteSession(ClaudeConfigDirGuard claude, string projectDirName, string fileName, params string[] lines)
    {
        var projectDir = Path.Combine(claude.ConfigDir, "projects", projectDirName);
        Directory.CreateDirectory(projectDir);
        File.WriteAllLines(Path.Combine(projectDir, fileName), lines);
    }

    /// <summary>Redirects <c>CLAUDE_CONFIG_DIR</c> to a throwaway temp directory, mirroring <c>SessionCommandTests</c>'s convention.</summary>
    private sealed class ClaudeConfigDirGuard : IDisposable
    {
        private readonly string? _previous;

        public string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "rtk-discover-test-" + Guid.NewGuid().ToString("N"));

        public ClaudeConfigDirGuard()
        {
            Directory.CreateDirectory(ConfigDir);
            _previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", ConfigDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _previous);
            try
            {
                Directory.Delete(ConfigDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
