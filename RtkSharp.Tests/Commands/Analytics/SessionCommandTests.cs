using System;
using System.Collections.Generic;
using System.IO;
using RtkSharp.Commands.Analytics;
using RtkSharp.Discover;
using Xunit;

namespace RtkSharp.Tests.Commands.Analytics;

/// <summary>
/// Covers <see cref="SessionCommand"/>, a faithful port of Rust
/// <c>src/analytics/session_cmd.rs</c>. Every test that touches <see cref="SessionCommand.RunCore"/>
/// (which reads real Claude Code session files via <see cref="ClaudeProvider"/>) redirects
/// <c>CLAUDE_CONFIG_DIR</c> to a throwaway temp directory so the real <c>~/.claude/projects</c> is
/// never read. The whole assembly runs with <c>DisableTestParallelization = true</c>
/// (<c>AssemblyInfo.cs</c>), so a bare env-var save/restore is sufficient here, matching
/// <c>GainCommandTests</c>'s established convention.
/// </summary>
public sealed class SessionCommandTests
{
    // ===================== ProgressBar (session_cmd.rs's own mod tests) =====================

    [Fact]
    public void ProgressBar_ZeroPercent_AllDots()
    {
        Assert.Equal(".....", SessionCommand.ProgressBar(0.0, 5));
    }

    [Fact]
    public void ProgressBar_HundredPercent_AllFilled()
    {
        Assert.Equal("@@@@@", SessionCommand.ProgressBar(100.0, 5));
    }

    [Fact]
    public void ProgressBar_FiftyPercent_HalfFilled()
    {
        Assert.Equal("@@@..", SessionCommand.ProgressBar(50.0, 5));
    }

    // ===================== CountRtkCommands (session_cmd.rs's own mod tests) =====================

    private static ExtractedCommand MakeCmd(string command, int? outputLen) =>
        new(command, outputLen, "test", null, false, 0);

    [Fact]
    public void CountRtkCommands_AllExplicitRtk_AllCounted()
    {
        var cmds = new[]
        {
            MakeCmd("rtk git status", 200),
            MakeCmd("rtk cargo test", 5000),
            MakeCmd("rtk git log -10", 800),
        };

        var (total, rtk, output) = SessionCommand.CountRtkCommands(cmds);

        Assert.Equal(3, total);
        Assert.Equal(3, rtk);
        Assert.Equal(6000, output);
    }

    [Fact]
    public void CountRtkCommands_HookRewrittenCommands_DetectedViaClassifier()
    {
        var cmds = new[]
        {
            MakeCmd("git status", 500),
            MakeCmd("cargo test", 3000),
            MakeCmd("echo hello", 100),
        };

        var (total, rtk, output) = SessionCommand.CountRtkCommands(cmds);

        Assert.Equal(3, total);
        Assert.Equal(2, rtk); // git status + cargo test are supported; echo is not.
        Assert.Equal(3600, output);
    }

    [Fact]
    public void CountRtkCommands_MixedExplicitAndHookRewritten()
    {
        var cmds = new[]
        {
            MakeCmd("rtk git status", 200),
            MakeCmd("git log -5", 1000),
            MakeCmd("rtk cargo test", 5000),
            MakeCmd("echo hello", null),
        };

        var (total, rtk, output) = SessionCommand.CountRtkCommands(cmds);

        Assert.Equal(4, total);
        Assert.Equal(3, rtk);
        Assert.Equal(6200, output);
    }

    [Fact]
    public void CountRtkCommands_UnsupportedCommands_NoneCounted()
    {
        var cmds = new[]
        {
            MakeCmd("echo hello", 100),
            MakeCmd("mkdir -p /tmp/foo", 10),
            MakeCmd("cd /tmp", 5),
        };

        var (total, rtk, _) = SessionCommand.CountRtkCommands(cmds);

        Assert.Equal(3, total);
        Assert.Equal(0, rtk);
    }

    [Fact]
    public void CountRtkCommands_Empty_ReturnsZeros()
    {
        var (total, rtk, output) = SessionCommand.CountRtkCommands([]);

        Assert.Equal(0, total);
        Assert.Equal(0, rtk);
        Assert.Equal(0, output);
    }

    [Fact]
    public void CountRtkCommands_AndChain_SplitsAndCountsIndependently()
    {
        var cmds = new[] { MakeCmd("cd ./your/app/path && rtk ls", 200) };

        var (total, rtk, _) = SessionCommand.CountRtkCommands(cmds);

        Assert.Equal(2, total);
        Assert.Equal(1, rtk);
    }

    [Fact]
    public void CountRtkCommands_AndChain_BothSupported()
    {
        var cmds = new[] { MakeCmd("git status && git log -5", 500) };

        var (total, rtk, _) = SessionCommand.CountRtkCommands(cmds);

        Assert.Equal(2, total);
        Assert.Equal(2, rtk);
    }

    [Fact]
    public void CountRtkCommands_SemicolonChain_SplitsIntoThree()
    {
        var cmds = new[] { MakeCmd("cd /tmp; git status; echo done", 100) };

        var (total, rtk, _) = SessionCommand.CountRtkCommands(cmds);

        Assert.Equal(3, total);
        Assert.Equal(1, rtk);
    }

    [Fact]
    public void CountRtkCommands_SingleCommand_NoInflation()
    {
        var cmds = new[] { MakeCmd("git status", 100) };

        var (total, rtk, _) = SessionCommand.CountRtkCommands(cmds);

        Assert.Equal(1, total);
        Assert.Equal(1, rtk);
    }

    // ===================== RunCore: end-to-end against seeded JSONL fixtures =====================

    [Fact]
    public void RunCore_NoProjectsDirectory_PrintsNoSessionsMessage()
    {
        using var claude = new ClaudeConfigDirGuard();
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = SessionCommand.RunCore(stdout);

        Assert.Equal(0, exit);
        Assert.Equal(
            "No Claude Code sessions found in the last 30 days.\n" +
            "Make sure Claude Code has been used at least once.\n",
            stdout.ToString());
    }

    [Fact]
    public void RunCore_SessionWithOnlyNonBashActivity_PrintsNoSessionsWithBashMessage()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(claude, "proj", "session1.jsonl",
            """{"type":"user","message":{"role":"user","content":"Hello"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":"Hi there!"}}""");
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = SessionCommand.RunCore(stdout);

        Assert.Equal(0, exit);
        Assert.Equal("No sessions with Bash commands found.\n", stdout.ToString());
    }

    [Fact]
    public void RunCore_MixedSession_PrintsFullTableWithAdoptionAndTip()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(claude, "-Users-test-rtk", "abcd1234-session.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rtk git status"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"On branch main"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git log -5"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"commit abc123\ncommit def456"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t3","name":"Bash","input":{"command":"echo hello"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t3","content":"hello"}]}}""");

        var stdout = new StringWriter { NewLine = "\n" };
        var exit = SessionCommand.RunCore(stdout);

        Assert.Equal(0, exit);
        Assert.Equal(
            "RTK Session Overview (last 10)\n" +
            "----------------------------------------------------------------------\n" +
            "Session      Date          Cmds   RTK  Adoption           Output\n" +
            "----------------------------------------------------------------------\n" +
            "abcd1234     Today            3     2       67% @@@..         46\n" +
            "----------------------------------------------------------------------\n" +
            "Average adoption: 67%\n" +
            "Tip: Run `rtk discover` to find missed RTK opportunities\n",
            stdout.ToString());
    }

    [Fact]
    public void RunCore_SubagentsFile_Excluded()
    {
        using var claude = new ClaudeConfigDirGuard();
        var projectsDir = Path.Combine(claude.ConfigDir, "projects", "-Users-test-rtk");
        var subagentsDir = Path.Combine(projectsDir, "subagents");
        Directory.CreateDirectory(subagentsDir);
        File.WriteAllLines(
            Path.Combine(subagentsDir, "sub-session.jsonl"),
            new[] { """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rtk git status"}}]}}""" });

        var stdout = new StringWriter { NewLine = "\n" };
        var exit = SessionCommand.RunCore(stdout);

        Assert.Equal(0, exit);
        // DiscoverSessions itself finds the file (so the earlier "no sessions found" branch is NOT
        // hit); run()'s own subagents-substring filter (session_cmd.rs:74-78) removes it afterward,
        // leaving zero session files to summarize.
        Assert.Equal("No sessions with Bash commands found.\n", stdout.ToString());
    }

    [Fact]
    public void RunCore_MoreThanTenSessions_OnlyTheTenNewestByMtimeShownInDescOrder()
    {
        using var claude = new ClaudeConfigDirGuard();
        var projectDir = Path.Combine(claude.ConfigDir, "projects", "-Users-test-rtk");
        Directory.CreateDirectory(projectDir);

        // 12 sessions, each with a distinct (<=8-char, so untruncated) id and a distinct mtime a
        // minute apart — id "sess-00" is newest (i=0), "sess-11" is oldest (i=11).
        for (var i = 0; i < 12; i++)
        {
            var path = Path.Combine(projectDir, $"sess-{i:D2}.jsonl");
            File.WriteAllLines(path, new[]
            {
                """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rtk git status"}}]}}""",
            });
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-i));
        }

        var stdout = new StringWriter { NewLine = "\n" };
        SessionCommand.RunCore(stdout);
        var output = stdout.ToString();

        var shownIds = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("sess-", StringComparison.Ordinal))
            {
                shownIds.Add(line[..7]);
            }
        }

        // The two oldest (sess-10, sess-11) must be dropped; the ten newest must appear, in
        // descending-mtime order — a broken selection (wrong sort direction, wrong Take count, an
        // unstable/reversed order) would fail this even though a row-count-only check would not.
        Assert.Equal(
            ["sess-00", "sess-01", "sess-02", "sess-03", "sess-04", "sess-05", "sess-06", "sess-07", "sess-08", "sess-09"],
            shownIds);
    }

    private static void WriteSession(ClaudeConfigDirGuard claude, string projectDirName, string fileName, params string[] lines)
    {
        var projectDir = Path.Combine(claude.ConfigDir, "projects", projectDirName);
        Directory.CreateDirectory(projectDir);
        File.WriteAllLines(Path.Combine(projectDir, fileName), lines);
    }

    /// <summary>Redirects <c>CLAUDE_CONFIG_DIR</c> (honored directly by the Rust oracle, not a .NET-only escape hatch) to a throwaway temp directory.</summary>
    private sealed class ClaudeConfigDirGuard : IDisposable
    {
        private readonly string? _previous;

        public string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "rtk-session-test-" + Guid.NewGuid().ToString("N"));

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
