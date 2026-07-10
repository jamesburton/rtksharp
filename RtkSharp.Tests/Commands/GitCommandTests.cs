using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Commands.Git;
using RtkSharp.Execution;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="GitCommand"/> and <see cref="GitGlobalArgs"/>. The global-arg parser,
/// log/status filters, and helpers are pure functions ported from <c>src/cmds/git/git.rs</c>, so the
/// bulk of this suite exercises them directly. Argument-threading tests use a recording
/// <see cref="IProcessExecutor"/> to assert the child <c>git</c> argv (global args before the
/// subcommand); a small set of end-to-end tests run real <c>git</c> against this repository to verify
/// the execution/print pipeline. Filter expectations are oracle-derived: the format-faithful log
/// fixtures below were confirmed byte-identical against the reference <c>rtk.exe</c> for the
/// equivalent live invocations.
/// </summary>
public sealed class GitCommandTests
{
    // ==================== GitGlobalArgs.Parse / ToArgs ====================

    [Fact]
    public void Parse_NoGlobals_SubcommandFirst()
    {
        var (globals, rest) = GitGlobalArgs.Parse(["status", "-s"]);
        Assert.Empty(globals.ToArgs());
        Assert.Equal(["status", "-s"], rest);
    }

    [Fact]
    public void Parse_SingleDirectory()
    {
        var (globals, rest) = GitGlobalArgs.Parse(["-C", "/repo", "status"]);
        Assert.Equal(["/repo"], globals.Directory);
        Assert.Equal(["-C", "/repo"], globals.ToArgs());
        Assert.Equal(["status"], rest);
    }

    [Fact]
    public void Parse_RepeatedDirectoryAndConfig()
    {
        var (globals, rest) = GitGlobalArgs.Parse(
            ["-C", "/a", "-C", "/b", "-c", "user.name=x", "-c", "core.pager=cat", "log"]);
        Assert.Equal(["/a", "/b"], globals.Directory);
        Assert.Equal(["user.name=x", "core.pager=cat"], globals.ConfigOverride);
        Assert.Equal(["log"], rest);

        // ToArgs order mirrors main.rs:1561-1589: all -C, then all -c.
        Assert.Equal(
            ["-C", "/a", "-C", "/b", "-c", "user.name=x", "-c", "core.pager=cat"],
            globals.ToArgs());
    }

    [Fact]
    public void Parse_AttachedAndEqualsForms()
    {
        var (attached, _) = GitGlobalArgs.Parse(["-C/repo", "status"]);
        Assert.Equal(["/repo"], attached.Directory);

        var (equals, _) = GitGlobalArgs.Parse(["-C=/repo", "status"]);
        Assert.Equal(["/repo"], equals.Directory);

        var (longEq, _) = GitGlobalArgs.Parse(["--git-dir=/foo/.git", "status"]);
        Assert.Equal("/foo/.git", longEq.GitDir);
    }

    [Fact]
    public void Parse_LongValueFlags()
    {
        var (globals, rest) = GitGlobalArgs.Parse(
            ["--git-dir", "/foo/.git", "--work-tree", "/foo", "status"]);
        Assert.Equal("/foo/.git", globals.GitDir);
        Assert.Equal("/foo", globals.WorkTree);
        Assert.Equal(["status"], rest);
        Assert.Equal(["--git-dir", "/foo/.git", "--work-tree", "/foo"], globals.ToArgs());
    }

    [Fact]
    public void Parse_BooleanFlags_OrderedLast()
    {
        var (globals, rest) = GitGlobalArgs.Parse(
            ["--no-pager", "--no-optional-locks", "--bare", "--literal-pathspecs", "log"]);
        Assert.True(globals.NoPager);
        Assert.True(globals.NoOptionalLocks);
        Assert.True(globals.Bare);
        Assert.True(globals.LiteralPathspecs);
        Assert.Equal(["log"], rest);
        Assert.Equal(
            ["--no-pager", "--no-optional-locks", "--bare", "--literal-pathspecs"],
            globals.ToArgs());
    }

    [Fact]
    public void Parse_FullOrder_MatchesRustDispatch()
    {
        var (globals, rest) = GitGlobalArgs.Parse(
            ["-C", "/r", "-c", "k=v", "--git-dir", "/g", "--work-tree", "/w", "--no-pager", "--bare", "diff", "HEAD"]);
        Assert.Equal(
            ["-C", "/r", "-c", "k=v", "--git-dir", "/g", "--work-tree", "/w", "--no-pager", "--bare"],
            globals.ToArgs());
        Assert.Equal(["diff", "HEAD"], rest);
    }

    [Fact]
    public void Parse_SubcommandArgsNotConsumedAsGlobals()
    {
        // Globals only bind before the subcommand; a -C after `status` belongs to the subcommand.
        var (globals, rest) = GitGlobalArgs.Parse(["status", "-C", "/nope"]);
        Assert.Empty(globals.Directory);
        Assert.Equal(["status", "-C", "/nope"], rest);
    }

    // ==================== argument threading (recording executor) ====================

    [Fact]
    public async Task Log_ThreadsGlobalArgsBeforeSubcommand()
    {
        var exec = new RecordingExecutor(_ => Ok(string.Empty));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["-C", "/repo", "-c", "core.pager=cat", "log"], exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal("git", request.FileName);
        Assert.Equal(
            ["-C", "/repo", "-c", "core.pager=cat", "log",
             "--pretty=format:%h %s (%ar) <%an>%n%b%n---END---", "-10", "--no-merges"],
            request.Arguments);
    }

    [Fact]
    public async Task Log_UserLimit_SkipsInjectedLimitAndNoMerges()
    {
        var exec = new RecordingExecutor(_ => Ok(string.Empty));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["log", "-3"], exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        // hasLimitFlag: RTK injects --pretty but neither -N nor --no-merges; the user's -3 follows.
        Assert.Equal(
            ["log", "--pretty=format:%h %s (%ar) <%an>%n%b%n---END---", "-3"],
            request.Arguments);
    }

    [Fact]
    public async Task Log_UserFormat_AllowsFiftyAndKeepsNoMerges()
    {
        var exec = new RecordingExecutor(_ => Ok(string.Empty));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["log", "--oneline"], exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        // hasFormatFlag without a limit: no --pretty injected, -50 and --no-merges added, user flag last.
        Assert.Equal(["log", "-50", "--no-merges", "--oneline"], request.Arguments);
    }

    [Fact]
    public async Task Status_Compact_RunsPlainThenPorcelain_WithLocaleEnv()
    {
        var exec = new RecordingExecutor(req =>
            req.Arguments.Contains("--porcelain") ? Ok("## main\n M src/main.rs\n") : Ok("On branch main\n"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["-C", "/repo", "status"], exec, sw, ew);

        Assert.Equal(2, exec.Requests.Count);

        // Plain status first (LC_ALL=C), threaded with the global -C.
        var plain = exec.Requests[0];
        Assert.Equal(["-C", "/repo", "status"], plain.Arguments);
        Assert.NotNull(plain.Environment);
        Assert.Equal("C", plain.Environment!["LC_ALL"]);

        // Porcelain second: --porcelain -b appended after the global args and subcommand.
        Assert.Equal(["-C", "/repo", "status", "--porcelain", "-b"], exec.Requests[1].Arguments);

        Assert.Equal("* main\n M src/main.rs\n", sw.ToString());
    }

    [Fact]
    public async Task Status_MinimalPath_RunsPlainStatusWithArgs()
    {
        // --long is not a compact flag, so status takes the minimal-filter path (single invocation).
        var exec = new RecordingExecutor(_ => Ok("On branch main\nnothing to commit, working tree clean\n"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["status", "--long"], exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["status", "--long"], request.Arguments);
    }

    [Fact]
    public async Task UnknownSubcommand_PassesThroughRawWithGlobalArgs_AndPropagatesExit()
    {
        ExecutionRequest? seen = null;
        var exec = new RecordingExecutor(req =>
        {
            seen = req;
            return new ExecutionResult(string.Empty, string.Empty, 3, TimeSpan.Zero, true, null, false);
        });
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["-C", "/repo", "cherry", "-v"], exec, sw, ew);

        Assert.Equal(3, exit);
        Assert.NotNull(seen);
        Assert.Equal(["-C", "/repo", "cherry", "-v"], seen!.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, seen.CaptureMode);
    }

    // ==================== log filter (helper tests moved to GitFiltersTests) ====================

    [Theory]
    [InlineData("-20", 20)]
    [InlineData("-1", 1)]
    public void ParseUserLimit_CombinedDigitForm(string arg, int expected) =>
        Assert.Equal(expected, GitCommand.ParseUserLimit([arg, "extra"]));

    [Fact]
    public void ParseUserLimit_VariousForms()
    {
        Assert.Equal(20, GitCommand.ParseUserLimit(["-n", "20"]));
        Assert.Equal(5, GitCommand.ParseUserLimit(["--max-count=5"]));
        Assert.Equal(7, GitCommand.ParseUserLimit(["--max-count", "7"]));
        Assert.Null(GitCommand.ParseUserLimit(["--oneline", "--graph"]));
    }

    // ==================== status filter ====================

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "-b" }, true)]
    [InlineData(new[] { "--branch" }, true)]
    [InlineData(new[] { "-s" }, false)]
    [InlineData(new[] { "-s", "-b" }, true)]
    [InlineData(new[] { "-sb" }, true)]
    [InlineData(new[] { "-bs" }, true)]
    [InlineData(new[] { "--porcelain" }, false)]
    [InlineData(new[] { "--long" }, false)]
    public void UsesCompactStatusPath_MatchesRust(string[] args, bool expected) =>
        Assert.Equal(expected, GitCommand.UsesCompactStatusPath(args));

    [Fact]
    public void ExtractDetachedHead_FindsExplicitLine()
    {
        var raw = "HEAD detached at abc1234\nUntracked files:\n\t.docs/\n";
        Assert.Equal("HEAD detached at abc1234", GitCommand.ExtractDetachedHead(raw));
        Assert.Null(GitCommand.ExtractDetachedHead("On branch main\nnothing to commit\n"));
    }

    [Fact]
    public void ExtractStateHeader_DetectsRebase()
    {
        var raw = "interactive rebase in progress; onto 1234567\n"
            + "Last command done (1 command done):\n"
            + "Changes to be committed:\n";
        Assert.Equal("rebase in progress", GitCommand.ExtractStateHeader(raw));
    }

    [Fact]
    public void ExtractStateHeader_DetectsMergeVariants()
    {
        Assert.Equal(
            "merge in progress. unresolved conflicts",
            GitCommand.ExtractStateHeader("You have unmerged paths.\n"));
        Assert.Equal(
            "merge in progress. no conflicts",
            GitCommand.ExtractStateHeader("All conflicts fixed but you are still merging.\n"));
        Assert.Equal(
            "cherry-pick in progress",
            GitCommand.ExtractStateHeader("You are currently cherry-picking commit abc.\n"));
    }

    [Fact]
    public void ExtractStateHeader_StopsAtChangeBlocks()
    {
        // A stopper before any state line → no state surfaced.
        var raw = "Untracked files:\nYou are currently rebasing.\n";
        Assert.Null(GitCommand.ExtractStateHeader(raw));
    }

    // ==================== end-to-end (real git in this repo) ====================

    private static readonly Regex LogHeaderRegex =
        new(@"^[0-9a-f]{7,40} .+", RegexOptions.CultureInvariant);

    [Fact]
    public async Task EndToEnd_Status_EmitsBranchMarker()
    {
        var (sw, ew) = Writers();
        var exit = await GitCommand.RunAsync(["status"], new ProcessExecutor(), sw, ew);

        Assert.Equal(0, exit);
        var output = sw.ToString();
        // This repo is on a branch with a working tree → compact status starts with "* <branch>".
        Assert.StartsWith("* ", output);
        Assert.EndsWith("\n", output);
    }

    [Fact]
    public async Task EndToEnd_Log_ProducesHashHeaderLines()
    {
        var (sw, ew) = Writers();
        var exit = await GitCommand.RunAsync(["log", "-1"], new ProcessExecutor(), sw, ew);

        Assert.Equal(0, exit);
        var firstLine = sw.ToString().Split('\n')[0];
        Assert.Matches(LogHeaderRegex, firstLine);
    }

    [Fact]
    public async Task EndToEnd_LogOneline_EveryLineIsHashPrefixed()
    {
        var (sw, ew) = Writers();
        var exit = await GitCommand.RunAsync(["log", "--oneline"], new ProcessExecutor(), sw, ew);

        Assert.Equal(0, exit);
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Matches(LogHeaderRegex, line));
    }

    // ==================== compact diff (Task 2; filter tests moved to GitFiltersTests) ====================

    [Theory]
    [InlineData("HEAD:src/main.rs", true)]
    [InlineData("abc123:path/to/file", true)]
    [InlineData("HEAD", false)]
    [InlineData("--pretty=format:%h", false)]
    [InlineData("--stat", false)]
    public void IsBlobShowArg_MatchesRust(string arg, bool expected) =>
        Assert.Equal(expected, GitCommand.IsBlobShowArg(arg));

    // ==================== restore_double_dash (issue #1215) ====================

    [Fact]
    public void RestoreDoubleDash_SingleDashSwallowed_Restored()
    {
        // rtk git diff -- file → clap gave ["file"], restore "--".
        var restored = GitCommand.RestoreDoubleDashWithRaw(
            ["file"], ["rtk", "git", "diff", "--", "file"]);
        Assert.Equal(["--", "file"], restored);
    }

    [Fact]
    public void RestoreDoubleDash_ArgsBeforeDash_PositionPreserved()
    {
        var restored = GitCommand.RestoreDoubleDashWithRaw(
            ["HEAD", "file"], ["rtk", "git", "diff", "HEAD", "--", "file"]);
        Assert.Equal(["HEAD", "--", "file"], restored);
    }

    [Fact]
    public void RestoreDoubleDash_DashAlreadyPresent_ReturnedUnchanged()
    {
        // RtkSharp's parser preserves "--", so raw and parsed dash counts match → identity.
        var restored = GitCommand.RestoreDoubleDashWithRaw(
            ["--cached", "--", "file"], ["rtk", "git", "diff", "--cached", "--", "file"]);
        Assert.Equal(["--cached", "--", "file"], restored);
    }

    [Fact]
    public void RestoreDoubleDash_NoDashInRaw_ReturnedUnchanged()
    {
        var restored = GitCommand.RestoreDoubleDashWithRaw(
            ["main...feature"], ["rtk", "git", "diff", "main...feature"]);
        Assert.Equal(["main...feature"], restored);
    }

    [Fact]
    public void RestoreDoubleDash_MultipleFiles_RestoresLeadingDash()
    {
        var restored = GitCommand.RestoreDoubleDashWithRaw(
            ["file1", "file2", "file3"], ["rtk", "git", "diff", "--", "file1", "file2", "file3"]);
        Assert.Equal(["--", "file1", "file2", "file3"], restored);
    }

    // ==================== diff / show argument threading ====================

    [Fact]
    public async Task Diff_Default_RunsStatThenFullDiff_ThreadsGlobalArgs()
    {
        var exec = new RecordingExecutor(req =>
            req.Arguments.Contains("--stat")
                ? Ok(" f.txt | 1 +\n 1 file changed, 1 insertion(+)\n")
                : Ok("diff --git a/f.txt b/f.txt\n@@ -0,0 +1 @@\n+hello\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["-C", "/repo", "diff", "HEAD~1"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(2, exec.Requests.Count);
        Assert.Equal(["-C", "/repo", "diff", "--stat", "HEAD~1"], exec.Requests[0].Arguments);
        Assert.Equal(["-C", "/repo", "diff", "HEAD~1"], exec.Requests[1].Arguments);

        var output = sw.ToString();
        Assert.StartsWith("f.txt | 1 +", output);
        Assert.Contains("\nChanges:\n", output);
        Assert.Contains("\nf.txt\n  @@ -0,0 +1 @@\n  +hello", output);
    }

    [Fact]
    public async Task Diff_NoCompact_PassesThroughAndStripsRtkFlag()
    {
        ExecutionRequest? seen = null;
        var exec = new RecordingExecutor(req => { seen = req; return Ok("diff --git a/f b/f\n@@ -1 +1 @@\n-a\n+b\n"); });
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["diff", "--no-compact", "HEAD"], exec, sw, ew);

        Assert.Equal(0, exit);
        // Single invocation; the RTK-only --no-compact is stripped, no --stat prepended.
        var request = Assert.Single(exec.Requests);
        Assert.Equal(["diff", "HEAD"], request.Arguments);
        Assert.DoesNotContain("--no-compact", seen!.Arguments);
        // Raw diff, trimmed, no "Changes:" scaffolding.
        Assert.DoesNotContain("Changes:", sw.ToString());
    }

    [Fact]
    public async Task Diff_Stat_PassesThroughSingleInvocation()
    {
        var exec = new RecordingExecutor(_ => Ok(" f.txt | 2 +-\n 1 file changed\n"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["diff", "--stat"], exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["diff", "--stat"], request.Arguments);
        Assert.Equal("f.txt | 2 +-\n 1 file changed\n", sw.ToString());
    }

    [Fact]
    public async Task Diff_Failure_EmitsStderrAndPropagatesExit()
    {
        var exec = new RecordingExecutor(_ =>
            new ExecutionResult(string.Empty, "fatal: bad revision 'nope'\n", 128, TimeSpan.Zero, true, null, false));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["diff", "nope"], exec, sw, ew);

        Assert.Equal(128, exit);
        Assert.Contains("fatal: bad revision", ew.ToString());
        Assert.Equal(string.Empty, sw.ToString());
    }

    [Fact]
    public async Task Show_Compact_RunsSummaryStatAndDiff()
    {
        var exec = new RecordingExecutor(req =>
        {
            if (req.Arguments.Contains("--no-patch"))
            {
                return Ok("abc1234 subject (2 hours ago) <Dev>");
            }

            return req.Arguments.Contains("--stat")
                ? Ok(" f.txt | 1 +\n 1 file changed, 1 insertion(+)\n")
                : Ok("diff --git a/f.txt b/f.txt\n@@ -0,0 +1 @@\n+hello\n");
        });
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["show", "b315fe6"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(3, exec.Requests.Count);
        Assert.Equal(
            ["show", "--no-patch", "--pretty=format:%h %s (%ar) <%an>", "b315fe6"],
            exec.Requests[0].Arguments);
        Assert.Equal(["show", "--stat", "--pretty=format:", "b315fe6"], exec.Requests[1].Arguments);
        Assert.Equal(["show", "--pretty=format:", "b315fe6"], exec.Requests[2].Arguments);

        var output = sw.ToString();
        Assert.StartsWith("abc1234 subject (2 hours ago) <Dev>\n", output);
        Assert.Contains("f.txt | 1 +", output);
        Assert.Contains("\nf.txt\n  @@ -0,0 +1 @@\n  +hello", output);
    }

    [Fact]
    public async Task Show_StatOnly_PassesThrough()
    {
        var exec = new RecordingExecutor(_ => Ok(" f.txt | 1 +\n 1 file changed\n"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["show", "--stat", "HEAD"], exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["show", "--stat", "HEAD"], request.Arguments);
    }

    [Fact]
    public async Task Show_Blob_PassesThroughRawBytes()
    {
        // `git show HEAD:file` prints a blob; output is emitted verbatim (no trailing-newline trim).
        var exec = new RecordingExecutor(_ => Ok("raw file contents\nno trailing normalization"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["show", "HEAD:src/main.rs"], exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["show", "HEAD:src/main.rs"], request.Arguments);
        Assert.Equal("raw file contents\nno trailing normalization", sw.ToString());
    }

    // ==================== diff / show end-to-end (real git in this repo) ====================

    [Fact]
    public async Task EndToEnd_Diff_FixedShas_MatchesOracleStructure()
    {
        var (sw, ew) = Writers();
        var exit = await GitCommand.RunAsync(["diff", "01d5d1f", "4e045a7"], new ProcessExecutor(), sw, ew);

        Assert.Equal(0, exit);
        var output = sw.ToString();
        // Stat summary trimmed (leading space stripped) then the compacted diff scaffolding.
        Assert.StartsWith(".gitignore", output);
        Assert.Contains("4 files changed, 186 insertions(+)", output);
        Assert.Contains("\nChanges:\n", output);
        Assert.Contains("\n.gitignore\n  @@ -1,6 +1,9 @@\n  +# .NET pack/publish output (RtkSharp port)", output);
        Assert.Contains("  +3 -0", output);
    }

    [Fact]
    public async Task EndToEnd_Show_FixedSha_EmitsSummaryStatAndCompactDiff()
    {
        var (sw, ew) = Writers();
        var exit = await GitCommand.RunAsync(["show", "b315fe6"], new ProcessExecutor(), sw, ew);

        Assert.Equal(0, exit);
        var lines = sw.ToString().Split('\n');
        // Summary line: hash + subject (relative date is non-deterministic, so match only the shape).
        Assert.Matches(LogHeaderRegex, lines[0]);
        Assert.StartsWith("b315fe6 docs(cli): record Phase 1 dotnet pack verification", lines[0]);

        var output = sw.ToString();
        Assert.Contains("2 files changed, 65 insertions(+)", output);
        Assert.Contains("\n.gitignore\n  @@ -1,6 +1,9 @@", output);
        Assert.Contains("  +3 -0", output);
    }

    // ==================== add (Task 3) ====================

    [Fact]
    public async Task Add_NoArgs_DefaultsToDotThenQueriesStagedStat()
    {
        var exec = new RecordingExecutor(req =>
            req.Arguments.Contains("--cached")
                ? Ok(" README.md | 1 +\n a.txt     | 3 +++\n 2 files changed, 4 insertions(+)\n 2 files changed, 4 insertions(+)\n")
                : Ok(string.Empty));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["-C", "/repo", "add"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(2, exec.Requests.Count);
        Assert.Equal(["-C", "/repo", "add", "."], exec.Requests[0].Arguments);
        Assert.Equal(["-C", "/repo", "diff", "--cached", "--stat", "--shortstat"], exec.Requests[1].Arguments);
        Assert.Equal("ok 2 files changed, 4 insertions(+)\n", sw.ToString());
    }

    [Fact]
    public async Task Add_WithPaths_ThreadsThemAndStaysSilentWhenNothingStaged()
    {
        var exec = new RecordingExecutor(_ => Ok(string.Empty));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["add", "a.txt"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["add", "a.txt"], exec.Requests[0].Arguments);
        // Empty shortstat → no "ok" line (git itself is silent on a no-op stage).
        Assert.Equal(string.Empty, sw.ToString());
    }

    [Fact]
    public async Task Add_Failure_EmitsFailedAndPropagatesExit()
    {
        var exec = new RecordingExecutor(_ =>
            new ExecutionResult(string.Empty, "fatal: pathspec 'nope' did not match\n", 128, TimeSpan.Zero, true, null, false));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["add", "nope"], exec, sw, ew);

        Assert.Equal(128, exit);
        var request = Assert.Single(exec.Requests); // no stat query on failure
        Assert.Equal(["add", "nope"], request.Arguments);
        Assert.Contains("FAILED: git add", ew.ToString());
        Assert.Contains("fatal: pathspec", ew.ToString());
    }

    // ==================== commit (Task 3; filter tests moved to GitFiltersTests) ====================

    [Fact]
    public async Task Commit_Success_PrintsShortHash()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("[main e8f957d] second commit\n 1 file changed, 1 insertion(+)\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["commit", "-m", "second commit"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["commit", "-m", "second commit"], exec.Requests[0].Arguments);
        Assert.Equal("ok e8f957d\n", sw.ToString());
    }

    [Fact]
    public async Task Commit_NothingToCommit_ReportsOk()
    {
        var exec = new RecordingExecutor(_ =>
            new ExecutionResult("On branch main\nnothing to commit, working tree clean\n", string.Empty, 1, TimeSpan.Zero, true, null, false));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["commit", "-m", "noop"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal("ok (nothing to commit)\n", sw.ToString());
    }

    [Fact]
    public async Task Commit_Failure_ForwardsOutputAndPropagatesExit()
    {
        var exec = new RecordingExecutor(_ =>
            new ExecutionResult(string.Empty, "error: pathspec did not match\n", 1, TimeSpan.Zero, true, null, false));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["commit", "-m", "x"], exec, sw, ew);

        Assert.Equal(1, exit);
        Assert.Contains("error: pathspec did not match", ew.ToString());
        Assert.Equal(string.Empty, sw.ToString());
    }

    // ==================== push (Task 3) ====================

    [Fact]
    public async Task Push_ThreadsGlobalArgsBeforeSubcommand()
    {
        var exec = new RecordingExecutor(_ =>
            new ExecutionResult(string.Empty, "To ../bare\n * [new branch]      main -> main\n", 0, TimeSpan.Zero, true, null, false));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["-C", "/repo", "push", "-u", "origin", "main"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["-C", "/repo", "push", "-u", "origin", "main"], exec.Requests[0].Arguments);
        // git push writes to stderr; kept lines plus the "ok main" summary land on stderr.
        Assert.Contains("main -> main", ew.ToString());
        Assert.EndsWith("ok main\n", ew.ToString());
        Assert.Equal(string.Empty, sw.ToString());
    }

    [Theory]
    [InlineData(" * [new branch]      main -> main", "main")]
    [InlineData("   abc1234..def5678  feature -> origin/feature", "origin/feature")]
    [InlineData("branch 'main' set up to track 'origin/main'.", null)]
    public void ExtractPushedRef_MatchesRust(string line, string? expected) =>
        Assert.Equal(expected, GitCommand.ExtractPushedRef(line));

    // ==================== pull (Task 3; filter tests moved to GitFiltersTests) ====================

    [Fact]
    public async Task Pull_ThreadsArgsAndPrintsSummary()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("Updating a1b2c3..d4e5f6\nFast-forward\n a.txt | 2 +-\n 3 files changed, 10 insertions(+), 2 deletions(-)\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["pull", "--rebase"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["pull", "--rebase"], exec.Requests[0].Arguments);
        Assert.Equal("ok 3 files +10 -2\n", sw.ToString());
    }

    [Fact]
    public async Task Pull_Failure_EmitsFailedAndPropagatesExit()
    {
        var exec = new RecordingExecutor(_ =>
            new ExecutionResult(string.Empty, "fatal: no remote\n", 1, TimeSpan.Zero, true, null, false));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["pull"], exec, sw, ew);

        Assert.Equal(1, exit);
        Assert.Contains("FAILED: git pull", ew.ToString());
        Assert.Contains("fatal: no remote", ew.ToString());
    }

    // ==================== branch (Task 3; filter tests moved to GitFiltersTests) ====================

    [Fact]
    public async Task Branch_NoArgs_ListMode_AddsAllAndNoColor()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("  develop\n* main\n  remotes/origin/HEAD -> origin/main\n  remotes/origin/main\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["branch"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["branch", "-a", "--no-color"], exec.Requests[0].Arguments);
        Assert.Equal("* main\n  develop\n", sw.ToString());
    }

    [Fact]
    public async Task Branch_Create_Positional_CollapsesToOk()
    {
        var exec = new RecordingExecutor(_ => Ok(string.Empty));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["branch", "newbranch"], exec, sw, ew);

        Assert.Equal(0, exit);
        // Positional without a list flag → write path (no -a/--no-color injected).
        Assert.Equal(["branch", "newbranch"], exec.Requests[0].Arguments);
        Assert.Equal("ok\n", sw.ToString());
    }

    [Fact]
    public async Task Branch_Delete_ActionFlag_CollapsesToOk()
    {
        var exec = new RecordingExecutor(_ => Ok("Deleted branch old (was abc1234).\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["branch", "-d", "old"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["branch", "-d", "old"], exec.Requests[0].Arguments);
        Assert.Equal("ok\n", sw.ToString());
    }

    [Fact]
    public async Task Branch_ShowCurrent_PassesRawRefThrough()
    {
        var exec = new RecordingExecutor(_ => Ok("main\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["branch", "--show-current"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["branch", "--show-current"], exec.Requests[0].Arguments);
        Assert.Equal("main\n", sw.ToString());
    }

    // ==================== fetch (Task 3; filter tests moved to GitFiltersTests) ====================

    [Fact]
    public async Task Fetch_ThreadsArgsAndCountsNewRefsFromStderr()
    {
        var exec = new RecordingExecutor(_ =>
            new ExecutionResult(
                string.Empty,
                "From github.com:org/repo\n   abc1234..def5678  main       -> origin/main\n * [new branch]      feature    -> origin/feature\n",
                0, TimeSpan.Zero, true, null, false));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["fetch", "origin"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["fetch", "origin"], exec.Requests[0].Arguments);
        Assert.Equal("ok fetched (2 new refs)\n", sw.ToString());
    }

    // ==================== stash (Task 3; filter tests moved to GitFiltersTests) ====================

    [Fact]
    public async Task Stash_List_Empty_ReportsNoStashes()
    {
        var exec = new RecordingExecutor(_ => Ok(string.Empty));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["stash", "list"], exec, sw, ew);

        Assert.Equal(["stash", "list"], exec.Requests[0].Arguments);
        Assert.Equal("No stashes\n", sw.ToString());
    }

    [Fact]
    public async Task Stash_List_CompactsEntries()
    {
        var exec = new RecordingExecutor(_ => Ok("stash@{0}: WIP on main: b375c31 second\n"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["stash", "list"], exec, sw, ew);

        Assert.Equal("stash@{0}: b375c31 second\n", sw.ToString());
    }

    [Fact]
    public async Task Stash_Show_AddsPatchFlagAndCompactsDiff()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("diff --git a/a.txt b/a.txt\n@@ -2,3 +2,4 @@ l1\n+dirty\n"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["stash", "show"], exec, sw, ew);

        Assert.Equal(["stash", "show", "-p"], exec.Requests[0].Arguments);
        Assert.Contains("\na.txt\n  @@ -2,3 +2,4 @@ l1\n  +dirty", sw.ToString());
    }

    [Fact]
    public async Task Stash_Show_Empty_ReportsEmptyStash()
    {
        var exec = new RecordingExecutor(_ => Ok(string.Empty));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["stash", "show"], exec, sw, ew);

        Assert.Equal("Empty stash\n", sw.ToString());
    }

    [Fact]
    public async Task Stash_Pop_CollapsesToOkStashPop()
    {
        var exec = new RecordingExecutor(_ => Ok("Dropped refs/stash@{0} (abc1234)\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["stash", "pop"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["stash", "pop"], exec.Requests[0].Arguments);
        Assert.Equal("ok stash pop\n", sw.ToString());
    }

    [Fact]
    public async Task Stash_Bare_RunsPushAndReportsStashed()
    {
        var exec = new RecordingExecutor(_ => Ok("Saved working directory and index state WIP on main: abc1234 msg\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["stash"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["stash", "push"], exec.Requests[0].Arguments);
        Assert.Equal("ok stashed\n", sw.ToString());
    }

    [Fact]
    public async Task Stash_PushWithMessage_ThreadsSubAndArgs()
    {
        var exec = new RecordingExecutor(_ => Ok("Saved working directory and index state On main: wip\n"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["stash", "push", "-m", "wip"], exec, sw, ew);

        Assert.Equal(["stash", "push", "-m", "wip"], exec.Requests[0].Arguments);
        Assert.Equal("ok stashed\n", sw.ToString());
    }

    [Fact]
    public async Task Stash_NoLocalChanges_SurfacedNotOk()
    {
        var exec = new RecordingExecutor(_ => Ok("No local changes to save\n"));
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["stash"], exec, sw, ew);

        Assert.Equal("No local changes to save\n", sw.ToString());
    }

    // ==================== worktree (Task 3; filter tests moved to GitFiltersTests) ====================

    [Fact]
    public async Task Worktree_NoArgs_ListMode()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("/some/path/repo  abc1234 [main]\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["worktree"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["worktree", "list"], exec.Requests[0].Arguments);
        Assert.Equal("/some/path/repo abc1234 [main]\n", sw.ToString());
    }

    [Fact]
    public async Task Worktree_Add_ActionCollapsesToOk()
    {
        var exec = new RecordingExecutor(_ => Ok("Preparing worktree ...\n"));
        var (sw, ew) = Writers();

        var exit = await GitCommand.RunAsync(["worktree", "add", "../wt"], exec, sw, ew);

        Assert.Equal(0, exit);
        Assert.Equal(["worktree", "add", "../wt"], exec.Requests[0].Arguments);
        Assert.Equal("ok\n", sw.ToString());
    }

    // ==================== helpers ====================

    private static (StringWriter Stdout, StringWriter Stderr) Writers() =>
        (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });

    private static ExecutionResult Ok(string stdout, string stderr = "") =>
        new(stdout, stderr, 0, TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

    /// <summary>Records every <see cref="ExecutionRequest"/> and returns a canned result per request.</summary>
    private sealed class RecordingExecutor : IProcessExecutor
    {
        private readonly Func<ExecutionRequest, ExecutionResult> _responder;

        public RecordingExecutor(Func<ExecutionRequest, ExecutionResult> responder) => _responder = responder;

        public List<ExecutionRequest> Requests { get; } = new();

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_responder(request));
        }
    }
}
