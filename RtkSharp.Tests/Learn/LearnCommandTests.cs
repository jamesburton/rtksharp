using System;
using System.IO;
using RtkSharp.Learn;
using Xunit;

namespace RtkSharp.Tests.Learn;

/// <summary>
/// Covers <see cref="LearnCommand"/>, a faithful port of Rust <c>src/learn/mod.rs</c>'s <c>run</c>
/// plus the <c>Commands::Learn</c> clap arm's flag shape (<c>main.rs</c>:587-610). <c>mod.rs</c> has
/// no <c>#[cfg(test)]</c> block of its own in the Rust source, so this file's coverage is new,
/// dispatch-level testing (arg parsing + end-to-end against seeded JSONL fixtures), following
/// <c>SessionCommandTests</c>'s established convention of redirecting <c>CLAUDE_CONFIG_DIR</c> to a
/// throwaway temp directory so the real <c>~/.claude/projects</c> is never read.
/// </summary>
public sealed class LearnCommandTests
{
    // ===================== Flag parsing =====================

    [Fact]
    public void ParseArgs_Defaults()
    {
        var args = LearnCommand.LearnArgs.Parse([]);

        Assert.Null(args.Project);
        Assert.False(args.All);
        Assert.Equal(30UL, args.Since);
        Assert.Equal("text", args.Format);
        Assert.False(args.WriteRules);
        Assert.Equal(0.6, args.MinConfidence);
        Assert.Equal(1UL, args.MinOccurrences);
    }

    [Fact]
    public void ParseArgs_AllFlagsLongForm()
    {
        var args = LearnCommand.LearnArgs.Parse([
            "--project", "my-proj",
            "--all",
            "--since", "7",
            "--format", "json",
            "--write-rules",
            "--min-confidence", "0.75",
            "--min-occurrences", "2",
        ]);

        Assert.Equal("my-proj", args.Project);
        Assert.True(args.All);
        Assert.Equal(7UL, args.Since);
        Assert.Equal("json", args.Format);
        Assert.True(args.WriteRules);
        Assert.Equal(0.75, args.MinConfidence);
        Assert.Equal(2UL, args.MinOccurrences);
    }

    [Fact]
    public void ParseArgs_ShortFlags()
    {
        var args = LearnCommand.LearnArgs.Parse(["-p", "proj", "-a", "-s", "14", "-f", "json", "-w"]);

        Assert.Equal("proj", args.Project);
        Assert.True(args.All);
        Assert.Equal(14UL, args.Since);
        Assert.Equal("json", args.Format);
        Assert.True(args.WriteRules);
    }

    [Fact]
    public void ParseArgs_MinConfidenceAndMinOccurrences_HaveNoShortFlag()
    {
        // Rust's Commands::Learn arm declares `--min-confidence` and `--min-occurrences` with only
        // `#[arg(long, ...)]` (no `short`) — unlike every other field in the same struct, which all
        // have `#[arg(short, long, ...)]`. A single-dash form must therefore be rejected as an
        // unrecognized argument, not silently accepted as an alias.
        Assert.ThrowsAny<Exception>(() => LearnCommand.LearnArgs.Parse(["-c", "0.8"]));
        Assert.ThrowsAny<Exception>(() => LearnCommand.LearnArgs.Parse(["-o", "3"]));
    }

    [Fact]
    public void ParseArgs_EqualsForm()
    {
        var args = LearnCommand.LearnArgs.Parse([
            "--project=my-proj",
            "--since=7",
            "--format=json",
            "--min-confidence=0.9",
            "--min-occurrences=5",
        ]);

        Assert.Equal("my-proj", args.Project);
        Assert.Equal(7UL, args.Since);
        Assert.Equal("json", args.Format);
        Assert.Equal(0.9, args.MinConfidence);
        Assert.Equal(5UL, args.MinOccurrences);
    }

    [Fact]
    public void ParseArgs_UnknownFlag_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => LearnCommand.LearnArgs.Parse(["--bogus"]));
        Assert.Contains("unexpected argument", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArgs_MissingValue_Throws()
    {
        Assert.ThrowsAny<Exception>(() => LearnCommand.LearnArgs.Parse(["--since"]));
    }

    [Fact]
    public void ParseArgs_InvalidSince_Throws()
    {
        Assert.ThrowsAny<Exception>(() => LearnCommand.LearnArgs.Parse(["--since", "notanumber"]));
    }

    [Fact]
    public void ParseArgs_InvalidMinConfidence_Throws()
    {
        Assert.ThrowsAny<Exception>(() => LearnCommand.LearnArgs.Parse(["--min-confidence", "notafloat"]));
    }

    [Fact]
    public void Run_BadFlag_ExitsTwoWithCleanError()
    {
        var exit = LearnCommand.Run(["--bogus"]);
        Assert.Equal(2, exit);
    }

    // ===================== RunCore: end-to-end against seeded JSONL fixtures =====================

    [Fact]
    public void RunCore_NoProjectsDirectory_PrintsNoSessionsMessage()
    {
        using var claude = new ClaudeConfigDirGuard();
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = LearnCommand.RunCore(stdout, new LearnCommand.LearnArgs());

        Assert.Equal(0, exit);
        Assert.Equal("No Claude Code sessions found in the last 30 days.\n", stdout.ToString());
    }

    [Fact]
    public void RunCore_SessionsButNoCorrections_PrintsNoCorrectionsMessage()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "-Users-test-rtk",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git status"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"On branch main"}]}}""");

        var stdout = new StringWriter { NewLine = "\n" };
        var args = LearnCommand.LearnArgs.Parse(["--all"]);

        var exit = LearnCommand.RunCore(stdout, args);

        Assert.Equal(0, exit);
        Assert.Equal("No CLI corrections detected in 1 sessions.\n", stdout.ToString());
    }

    [Fact]
    public void RunCore_DetectsCorrectionAndPrintsReport()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "-Users-test-rtk",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git commit --ammend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"error: unexpected argument '--ammend'"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git commit --amend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"[main abc123] Fix bug"}]}}""");

        var stdout = new StringWriter { NewLine = "\n" };
        var args = LearnCommand.LearnArgs.Parse(["--all"]);

        var exit = LearnCommand.RunCore(stdout, args);
        var output = stdout.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("1 rules from 1 corrections", output);
        Assert.Contains("git commit --ammend", output);
        Assert.Contains("git commit --amend", output);
    }

    [Fact]
    public void RunCore_JsonFormat_PrintsValidJson()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "-Users-test-rtk",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git commit --ammend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"error: unexpected argument '--ammend'"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git commit --amend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"[main abc123] Fix bug"}]}}""");

        var stdout = new StringWriter { NewLine = "\n" };
        var args = LearnCommand.LearnArgs.Parse(["--format", "json", "--all"]);

        var exit = LearnCommand.RunCore(stdout, args);
        var output = stdout.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("\"sessions_scanned\": 1", output);
        Assert.Contains("\"wrong\": \"git commit --ammend\"", output);
        Assert.Contains("\"right\": \"git commit --amend\"", output);
    }

    [Fact]
    public void RunCore_MinConfidenceFiltersOutLowConfidenceCorrections()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "-Users-test-rtk",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git commit --ammend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"error: unexpected argument '--ammend'"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git commit --amend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"[main abc123] Fix bug"}]}}""");

        var stdout = new StringWriter { NewLine = "\n" };
        var args = LearnCommand.LearnArgs.Parse(["--min-confidence", "0.99", "--all"]);

        var exit = LearnCommand.RunCore(stdout, args);
        var output = stdout.ToString();

        // The raw detector finds a correction at confidence 0.7 (find_corrections' own MIN_CONFIDENCE
        // gate is 0.6), but mod.rs's separate --min-confidence=0.99 filter (mod.rs:79-82) then drops
        // it before deduplication — so the "corrections.is_empty()" early-return message is NOT hit
        // (that only fires when find_corrections itself finds nothing); instead the normal report path
        // runs with zero surviving rules.
        Assert.Equal(0, exit);
        Assert.Contains("0 rules from 0 corrections", output);
    }

    [Fact]
    public void RunCore_WriteRules_WritesMarkdownFile()
    {
        using var claude = new ClaudeConfigDirGuard();
        WriteSession(
            claude,
            "-Users-test-rtk",
            "session1.jsonl",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git commit --ammend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"error: unexpected argument '--ammend'"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git commit --amend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"[main abc123] Fix bug"}]}}""");

        var stdout = new StringWriter { NewLine = "\n" };
        var args = LearnCommand.LearnArgs.Parse(["--write-rules", "--all"]);

        var originalCwd = Directory.GetCurrentDirectory();
        var workDir = Path.Combine(Path.GetTempPath(), "rtk-learn-cmd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            Directory.SetCurrentDirectory(workDir);

            var exit = LearnCommand.RunCore(stdout, args);

            Assert.Equal(0, exit);
            var rulesPath = Path.Combine(workDir, ".claude", "rules", "cli-corrections.md");
            Assert.True(File.Exists(rulesPath));
            var content = File.ReadAllText(rulesPath);
            Assert.Contains("Use `git commit --amend` not `git commit --ammend`", content);
            Assert.Contains("Written to: ", stdout.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            Directory.Delete(workDir, recursive: true);
        }
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

        public string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "rtk-learn-test-" + Guid.NewGuid().ToString("N"));

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
