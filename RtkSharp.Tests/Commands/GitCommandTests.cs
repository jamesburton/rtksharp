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

    [Fact]
    public async Task UnportedSubcommand_Branch_PassesThroughRaw()
    {
        // branch/diff/show/... are not yet filtered (Tasks 2/3): they must reach raw passthrough.
        ExecutionRequest? seen = null;
        var exec = new RecordingExecutor(req => { seen = req; return Ok("  develop\n* main\n"); });
        var (sw, ew) = Writers();

        await GitCommand.RunAsync(["branch"], exec, sw, ew);

        Assert.NotNull(seen);
        Assert.Equal(["branch"], seen!.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, seen.CaptureMode);
    }

    // ==================== log filter ====================

    // Format-faithful RTK-injected log output (two commit blocks). The relative-date and author
    // tails are opaque to the filter; the shape (hash subject (date) <author>, %b body, ---END---
    // separator) is exactly what `--pretty=format:%h %s (%ar) <%an>%n%b%n---END---` emits.
    private const string RawLogTwoCommits =
        "abc1234 Short subject line (2 hours ago) <Dev One>\n"
        + "First body line.\n"
        + "Second body line.\n"
        + "Third body line.\n"
        + "Fourth body line.\n"
        + "Signed-off-by: Dev One <dev@example.com>\n"
        + "Co-authored-by: Helper <help@example.com>\n"
        + "Co-Authored-By: Kept Author <kept@example.com>\n"
        + "\n"
        + "---END---\n"
        + "def5678 Another commit (3 hours ago) <Dev Two>\n"
        + "Only one body line.\n"
        + "\n"
        + "---END---\n";

    [Fact]
    public void FilterLogOutput_KeepsHeaderAndCapsBodyToThree()
    {
        // userSetLimit=true → width 120, no line cap. Signed-off-by and lowercase co-authored-by are
        // dropped; capital "Co-Authored-By" survives (case-sensitive, matching Rust starts_with).
        var filtered = GitCommand.FilterLogOutput(RawLogTwoCommits, limit: 2, userSetLimit: true, userFormat: false);

        var expected =
            "abc1234 Short subject line (2 hours ago) <Dev One>\n"
            + "  First body line.\n"
            + "  Second body line.\n"
            + "  Third body line.\n"
            + "  [+2 lines omitted]\n"
            + "def5678 Another commit (3 hours ago) <Dev Two>\n"
            + "  Only one body line.";
        Assert.Equal(expected, filtered);
    }

    [Fact]
    public void FilterLogOutput_DefaultWidthTruncatesHeaderAt80()
    {
        var longHeader = "abc1234 " + new string('x', 200);
        var raw = longHeader + "\n\n---END---\n";

        var filtered = GitCommand.FilterLogOutput(raw, limit: 10, userSetLimit: false, userFormat: false);

        // Default width 80: 77 chars + "..." and no body.
        Assert.Equal(80, filtered.Length);
        Assert.EndsWith("...", filtered);
        Assert.StartsWith("abc1234 ", filtered);
    }

    [Fact]
    public void FilterLogOutput_UserFormat_TruncatesLineByLine()
    {
        var raw = "abc1234 first\ndef5678 second\nghi9012 third\n";

        // userFormat=true, userSetLimit=false, limit=2 → keep first 2 lines, width 80.
        var filtered = GitCommand.FilterLogOutput(raw, limit: 2, userSetLimit: true, userFormat: true);
        Assert.Equal("abc1234 first\ndef5678 second\nghi9012 third", filtered);

        var capped = GitCommand.FilterLogOutput(raw, limit: 2, userSetLimit: false, userFormat: true);
        Assert.Equal("abc1234 first\ndef5678 second", capped);
    }

    [Fact]
    public void TruncateLine_ShortUnchanged_LongGetsEllipsis()
    {
        Assert.Equal("hello", GitCommand.TruncateLine("hello", 80));

        var truncated = GitCommand.TruncateLine(new string('a', 100), 80);
        Assert.Equal(80, truncated.Length);
        Assert.EndsWith("...", truncated);
    }

    [Fact]
    public void TruncateLine_CountsUnicodeScalars_NotUtf16()
    {
        // 10 astral emoji (each 2 UTF-16 units) — width 8 must slice on scalar boundaries.
        var line = string.Concat(Enumerable.Repeat("\U0001F600", 10));
        var truncated = GitCommand.TruncateLine(line, 8);
        Assert.EndsWith("...", truncated);
        // 5 kept emoji (width-3) + "..." → never a split surrogate.
        Assert.Equal(5, truncated[..^3].EnumerateRunes().Count());
    }

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
    public void FormatStatusOutput_RewritesBranchHeader()
    {
        var porcelain = "## port/dotnet-phase7a\n?? .docs/\n?? docs/PLANS.md\n";
        var formatted = GitCommand.FormatStatusOutput(porcelain);
        Assert.Equal("* port/dotnet-phase7a\n?? .docs/\n?? docs/PLANS.md", formatted);
    }

    [Fact]
    public void FormatStatusOutput_BranchOnly_AppendsCleanMarker() =>
        Assert.Equal("* main\nclean — nothing to commit", GitCommand.FormatStatusOutput("## main\n"));

    [Fact]
    public void FormatStatusOutput_Empty_CleanWorkingTree() =>
        Assert.Equal("Clean working tree", GitCommand.FormatStatusOutput(""));

    [Fact]
    public void FormatStatusOutputDetached_SubstitutesRef()
    {
        var porcelain = "## HEAD (no branch)\n M src/main.rs\n";
        var formatted = GitCommand.FormatStatusOutputDetached(porcelain, "HEAD detached at abc1234");
        Assert.Equal("* HEAD detached at abc1234\n M src/main.rs", formatted);
    }

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

    [Fact]
    public void FilterStatusWithArgs_StripsHintsAndBlankLines()
    {
        var raw =
            "On branch main\n"
            + "Untracked files:\n"
            + "  (use \"git add <file>...\" to include in what will be committed)\n"
            + "\n"
            + "\tnewfile.txt\n";
        var filtered = GitCommand.FilterStatusWithArgs(raw);
        Assert.Equal("On branch main\nUntracked files:\n\tnewfile.txt", filtered);
    }

    [Fact]
    public void FilterStatusWithArgs_CleanTree_ShortCircuits()
    {
        var raw = "On branch main\nnothing to commit, working tree clean\n";
        Assert.Equal("On branch main\nnothing to commit, working tree clean", GitCommand.FilterStatusWithArgs(raw));
    }

    [Fact]
    public void FilterStatusWithArgs_AllFiltered_ReturnsOk() =>
        Assert.Equal("ok", GitCommand.FilterStatusWithArgs("\n  (use \"git add foo\")\n"));

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
