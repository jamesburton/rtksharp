using System;
using System.Collections.Generic;
using System.IO;
using RtkSharp.Discover;
using Xunit;

namespace RtkSharp.Tests.Discover;

/// <summary>
/// Covers <see cref="ClaudeProvider"/>, a faithful port of Rust
/// <c>src/discover/provider.rs</c>'s <c>ClaudeProvider</c>/<c>ExtractedCommand</c>.
/// </summary>
public sealed class ClaudeProviderTests
{
    // ===================== EncodeProjectPath (provider.rs's own mod tests) =====================

    [Theory]
    [InlineData("/Users/foo/bar", "-Users-foo-bar")]
    [InlineData("/Users/foo/bar/", "-Users-foo-bar-")]
    [InlineData("/Users/first.last/my-project", "-Users-first-last-my-project")]
    [InlineData("/Users/a.b.c/proj", "-Users-a-b-c-proj")]
    [InlineData("/home/chris/2_project-files/proj", "-home-chris-2-project-files-proj")]
    [InlineData(@"C:\Users\foo\bar", "C:-Users-foo-bar")]
    public void EncodeProjectPath_MatchesRustOracle(string input, string expected)
    {
        Assert.Equal(expected, ClaudeProvider.EncodeProjectPath(input));
    }

    [Fact]
    public void EncodeProjectPath_NonAsciiCharacters_EachReplacedWithDash()
    {
        Assert.Equal("-home-user----app", ClaudeProvider.EncodeProjectPath("/home/user/外主/app"));
    }

    [Fact]
    public void EncodeProjectPath_SpacesAndBrackets()
    {
        Assert.Equal(
            "-home-user-projects--QZX-7K42----An-lise-Gen-rica-de-Exemplo",
            ClaudeProvider.EncodeProjectPath("/home/user/projects/[QZX-7K42] - Análise Genérica de Exemplo"));
    }

    [Fact]
    public void EncodeProjectPath_ContainsExpectedSubstrings()
    {
        var encoded = ClaudeProvider.EncodeProjectPath("/Users/foo/Sites/rtk");
        Assert.Contains("rtk", encoded, StringComparison.Ordinal);
        Assert.Contains("Sites", encoded, StringComparison.Ordinal);
    }

    // ===================== ExtractCommands =====================

    [Fact]
    public void ExtractCommands_AssistantBashWithResult_ExtractsCommandAndOutputLen()
    {
        using var tmp = new TempJsonl(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_abc","name":"Bash","input":{"command":"git status"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_abc","content":"On branch master\nnothing to commit"}]}}""");

        var cmds = ClaudeProvider.ExtractCommands(tmp.Path);

        Assert.Single(cmds);
        Assert.Equal("git status", cmds[0].Command);
        Assert.Equal("On branch master\nnothing to commit".Length, cmds[0].OutputLen);
    }

    [Fact]
    public void ExtractCommands_NonBashTool_Ignored()
    {
        using var tmp = new TempJsonl(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_abc","name":"Read","input":{"file_path":"/tmp/foo"}}]}}""");

        Assert.Empty(ClaudeProvider.ExtractCommands(tmp.Path));
    }

    [Fact]
    public void ExtractCommands_NonMessageEntry_Ignored()
    {
        using var tmp = new TempJsonl("""{"type":"file-history-snapshot","messageId":"abc","snapshot":{}}""");

        Assert.Empty(ClaudeProvider.ExtractCommands(tmp.Path));
    }

    [Fact]
    public void ExtractCommands_MultipleToolsInOneMessage_ExtractsAllInOrder()
    {
        using var tmp = new TempJsonl(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"git status"}},{"type":"tool_use","id":"toolu_2","name":"Bash","input":{"command":"git diff"}}]}}""");

        var cmds = ClaudeProvider.ExtractCommands(tmp.Path);

        Assert.Equal(2, cmds.Count);
        Assert.Equal("git status", cmds[0].Command);
        Assert.Equal("git diff", cmds[1].Command);
    }

    [Fact]
    public void ExtractCommands_MalformedLine_SkippedNotThrown()
    {
        using var tmp = new TempJsonl(
            "this is not json at all",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_ok","name":"Bash","input":{"command":"ls"}}]}}""");

        var cmds = ClaudeProvider.ExtractCommands(tmp.Path);

        Assert.Single(cmds);
        Assert.Equal("ls", cmds[0].Command);
    }

    [Fact]
    public void ExtractCommands_ErrorResultWithContent_CapturesIsErrorAndContent()
    {
        using var tmp = new TempJsonl(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_abc","name":"Bash","input":{"command":"git commit --ammend"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_abc","content":"error: unexpected argument '--ammend'","is_error":true}]}}""");

        var cmds = ClaudeProvider.ExtractCommands(tmp.Path);

        Assert.Single(cmds);
        Assert.True(cmds[0].IsError);
        Assert.Equal("error: unexpected argument '--ammend'", cmds[0].OutputContent);
    }

    [Fact]
    public void ExtractCommands_MixedErrorFlags_TrackedPerCommand()
    {
        using var tmp = new TempJsonl(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}},{"type":"tool_use","id":"toolu_2","name":"Bash","input":{"command":"invalid_cmd"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"file1.txt","is_error":false},{"type":"tool_result","tool_use_id":"toolu_2","content":"command not found","is_error":true}]}}""");

        var cmds = ClaudeProvider.ExtractCommands(tmp.Path);

        Assert.Equal(2, cmds.Count);
        Assert.False(cmds[0].IsError);
        Assert.True(cmds[1].IsError);
    }

    [Fact]
    public void ExtractCommands_SequenceIndex_AssignedInEncounterOrder()
    {
        using var tmp = new TempJsonl(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"first"}},{"type":"tool_use","id":"toolu_2","name":"Bash","input":{"command":"second"}},{"type":"tool_use","id":"toolu_3","name":"Bash","input":{"command":"third"}}]}}""");

        var cmds = ClaudeProvider.ExtractCommands(tmp.Path);

        Assert.Equal(3, cmds.Count);
        Assert.Equal([0, 1, 2], cmds.ConvertAll(c => c.SequenceIndex));
        Assert.Equal(["first", "second", "third"], cmds.ConvertAll(c => c.Command));
    }

    [Fact]
    public void ExtractCommands_MessageFieldIsNonObjectString_SkippedNotThrown()
    {
        // "message" is a bare string here, not an object — JsonElement.TryGetProperty throws on a
        // non-object receiver, whereas Rust's serde_json::Value::pointer("/message/content") just
        // returns None for the same shape. The line also passes the "\"Bash\""/"\"tool_result\""
        // pre-filter substring check (the escaped \"Bash\" inside the string contains it), so this
        // exercises the exact path that would otherwise crash instead of skip.
        using var tmp = new TempJsonl(
            """{"type":"assistant","message":"say \"Bash\""}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_ok","name":"Bash","input":{"command":"ls"}}]}}""");

        var cmds = ClaudeProvider.ExtractCommands(tmp.Path);

        Assert.Single(cmds);
        Assert.Equal("ls", cmds[0].Command);
    }

    [Fact]
    public void ExtractCommands_NoMatchingToolResult_OutputLenIsNull()
    {
        using var tmp = new TempJsonl(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"echo hi"}}]}}""");

        var cmds = ClaudeProvider.ExtractCommands(tmp.Path);

        Assert.Single(cmds);
        Assert.Null(cmds[0].OutputLen);
        Assert.Null(cmds[0].OutputContent);
        Assert.False(cmds[0].IsError);
    }

    // ===================== DiscoverSessionsInProjectsDir =====================

    [Fact]
    public void DiscoverSessionsInProjectsDir_MissingDirectory_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Root, "does-not-exist");

        var sessions = ClaudeProvider.DiscoverSessionsInProjectsDir(missing, null, 30);

        Assert.Empty(sessions);
    }

    [Fact]
    public void DiscoverSessionsInProjectsDir_AppliesProjectFilter()
    {
        using var tmp = new TempDir();
        var matching = Path.Combine(tmp.Root, "-Users-test-rtk");
        var other = Path.Combine(tmp.Root, "-Users-test-other");
        Directory.CreateDirectory(matching);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(matching, "matching.jsonl"), "");
        File.WriteAllText(Path.Combine(other, "other.jsonl"), "");

        var sessions = ClaudeProvider.DiscoverSessionsInProjectsDir(tmp.Root, "rtk", null);

        Assert.Single(sessions);
        Assert.Equal("matching.jsonl", Path.GetFileName(sessions[0]));
    }

    [Fact]
    public void DiscoverSessionsInProjectsDir_RecursesIntoSubagentsSubdirectory()
    {
        using var tmp = new TempDir();
        var project = Path.Combine(tmp.Root, "-Users-test-rtk");
        var subagents = Path.Combine(project, "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(project, "main.jsonl"), "");
        File.WriteAllText(Path.Combine(subagents, "sub.jsonl"), "");

        var sessions = ClaudeProvider.DiscoverSessionsInProjectsDir(tmp.Root, null, null);

        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public void DiscoverSessionsInProjectsDir_AppliesSinceDaysCutoff()
    {
        using var tmp = new TempDir();
        var project = Path.Combine(tmp.Root, "-Users-test-rtk");
        Directory.CreateDirectory(project);
        var oldFile = Path.Combine(project, "old.jsonl");
        var recentFile = Path.Combine(project, "recent.jsonl");
        File.WriteAllText(oldFile, "");
        File.WriteAllText(recentFile, "");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-60));
        File.SetLastWriteTimeUtc(recentFile, DateTime.UtcNow.AddDays(-1));

        var sessions = ClaudeProvider.DiscoverSessionsInProjectsDir(tmp.Root, null, 30);

        Assert.Single(sessions);
        Assert.Equal("recent.jsonl", Path.GetFileName(sessions[0]));
    }

    [Fact]
    public void DiscoverSessionsInProjectsDir_NullSinceDays_NoCutoffApplied()
    {
        using var tmp = new TempDir();
        var project = Path.Combine(tmp.Root, "-Users-test-rtk");
        Directory.CreateDirectory(project);
        var oldFile = Path.Combine(project, "old.jsonl");
        File.WriteAllText(oldFile, "");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-365));

        var sessions = ClaudeProvider.DiscoverSessionsInProjectsDir(tmp.Root, null, null);

        Assert.Single(sessions);
    }

    [Fact]
    public void ProjectsDir_JoinsResolvedClaudeDirWithProjects()
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", @"C:\fake\claude");
            Assert.Equal(Path.Combine(@"C:\fake\claude", "projects"), ClaudeProvider.ProjectsDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }

    private sealed class TempJsonl : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rtk-claudeprovider-test-" + Guid.NewGuid().ToString("N") + ".jsonl");

        public TempJsonl(params string[] lines)
        {
            File.WriteAllLines(Path, lines);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rtk-claudeprovider-test-" + Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
