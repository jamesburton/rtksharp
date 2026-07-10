using System.Text.Json;
using RtkSharp.Commands.Gh;
using RtkSharp.Execution;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="GhCommand"/>. The argument-parsing, JSON-formatting, and markdown-filtering
/// helpers are pure functions ported from <c>src/cmds/git/gh_cmd.rs</c>, so most of this suite
/// exercises them directly with the same inline JSON shapes the Rust module's own tests use. A set of
/// dispatch tests use a recording <see cref="IProcessExecutor"/> to assert the child <c>gh</c> argv
/// and the structured-output guard (<c>--json</c>/<c>--jq</c>/<c>--template</c>/<c>--web</c> → raw
/// passthrough). The format expectations are oracle-derived: <c>gh pr view</c>/<c>pr list</c>/
/// <c>issue list</c> were confirmed byte-identical against the reference <c>rtk.exe</c> for the
/// equivalent live invocations against this repository's GitHub remote.
/// </summary>
public sealed class GhCommandTests
{
    // ==================== has_json_flag ====================

    [Fact]
    public void HasJsonFlag_Present() => Assert.True(GhCommand.HasJsonFlag(["view", "--json", "number,url"]));

    [Fact]
    public void HasJsonFlag_Absent() => Assert.False(GhCommand.HasJsonFlag(["view", "42"]));

    // ==================== extract_identifier_and_extra_args ====================

    [Fact]
    public void ExtractIdentifier_Simple()
    {
        var r = GhCommand.ExtractIdentifierAndExtraArgs(["123"]);
        Assert.NotNull(r);
        Assert.Equal("123", r.Value.Identifier);
        Assert.Empty(r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_RepoFlagAfter()
    {
        var r = GhCommand.ExtractIdentifierAndExtraArgs(["185", "-R", "rtk-ai/rtk"]);
        Assert.Equal("185", r!.Value.Identifier);
        Assert.Equal(["-R", "rtk-ai/rtk"], r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_RepoFlagBefore()
    {
        var r = GhCommand.ExtractIdentifierAndExtraArgs(["-R", "rtk-ai/rtk", "185"]);
        Assert.Equal("185", r!.Value.Identifier);
        Assert.Equal(["-R", "rtk-ai/rtk"], r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_LongRepoFlag()
    {
        var r = GhCommand.ExtractIdentifierAndExtraArgs(["42", "--repo", "owner/repo"]);
        Assert.Equal("42", r!.Value.Identifier);
        Assert.Equal(["--repo", "owner/repo"], r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_Empty() => Assert.Null(GhCommand.ExtractIdentifierAndExtraArgs([]));

    [Fact]
    public void ExtractIdentifier_OnlyFlags() =>
        Assert.Null(GhCommand.ExtractIdentifierAndExtraArgs(["-R", "rtk-ai/rtk"]));

    [Fact]
    public void ExtractIdentifier_WebFlag()
    {
        var r = GhCommand.ExtractIdentifierAndExtraArgs(["123", "--web"]);
        Assert.Equal("123", r!.Value.Identifier);
        Assert.Equal(["--web"], r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_JobFlagBefore()
    {
        var r = GhCommand.ExtractIdentifierAndExtraArgs(["--job", "67890", "12345"]);
        Assert.Equal("12345", r!.Value.Identifier);
        Assert.Equal(["--job", "67890"], r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_AttemptFlag()
    {
        var r = GhCommand.ExtractIdentifierAndExtraArgs(["12345", "--attempt", "3"]);
        Assert.Equal("12345", r!.Value.Identifier);
        Assert.Equal(["--attempt", "3"], r.Value.Extra);
    }

    // ==================== parse_optional_identifier ====================

    [Fact]
    public void ParseOptionalIdentifier_EmptyYieldsNoId()
    {
        var (id, extra) = GhCommand.ParseOptionalIdentifier([]);
        Assert.Null(id);
        Assert.Empty(extra);
    }

    [Fact]
    public void ParseOptionalIdentifier_OnlyFlagsPreservesFlags()
    {
        var (id, extra) = GhCommand.ParseOptionalIdentifier(["-R", "rtk-ai/rtk"]);
        Assert.Null(id);
        Assert.Equal(["-R", "rtk-ai/rtk"], extra);
    }

    [Fact]
    public void ParseOptionalIdentifier_WithIdMatchesExtract()
    {
        var (id, extra) = GhCommand.ParseOptionalIdentifier(["-R", "rtk-ai/rtk", "42"]);
        Assert.Equal("42", id);
        Assert.Equal(["-R", "rtk-ai/rtk"], extra);
    }

    // ==================== should_passthrough_* guards ====================

    [Theory]
    [InlineData("--json")]
    [InlineData("--jq")]
    [InlineData("--web")]
    [InlineData("--comments")]
    public void ShouldPassthroughPrView_True(string flag) => Assert.True(GhCommand.ShouldPassthroughPrView([flag]));

    [Fact]
    public void ShouldPassthroughPrView_DefaultFalse() => Assert.False(GhCommand.ShouldPassthroughPrView([]));

    [Theory]
    [InlineData("--json")]
    [InlineData("--jq")]
    [InlineData("--web")]
    [InlineData("--comments")]
    public void ShouldPassthroughIssueView_True(string flag) => Assert.True(GhCommand.ShouldPassthroughIssueView([flag]));

    [Fact]
    public void ShouldPassthroughIssueView_DefaultFalse() => Assert.False(GhCommand.ShouldPassthroughIssueView([]));

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--web")]
    [InlineData("--jq")]
    [InlineData("--template")]
    public void ShouldPassthroughPrStatus_True(string flag) => Assert.True(GhCommand.ShouldPassthroughPrStatus([flag]));

    [Fact]
    public void ShouldPassthroughPrStatus_RepoFlagStaysFiltered() =>
        Assert.False(GhCommand.ShouldPassthroughPrStatus(["-R", "owner/repo"]));

    [Fact]
    public void PrStatusJsonFields_ExcludesCurrentBranch()
    {
        var fields = GhCommand.PrStatusJsonFields();
        Assert.DoesNotContain("currentBranch", fields);
        Assert.Contains("number", fields);
        Assert.Contains("title", fields);
        Assert.Contains("reviewDecision", fields);
        Assert.Contains("statusCheckRollup", fields);
    }

    // ==================== has_non_diff_format_flag ====================

    [Theory]
    [InlineData("--name-only")]
    [InlineData("--name-status")]
    [InlineData("--stat")]
    [InlineData("--numstat")]
    [InlineData("--shortstat")]
    public void HasNonDiffFormatFlag_True(string flag) => Assert.True(GhCommand.HasNonDiffFormatFlag([flag]));

    [Fact]
    public void HasNonDiffFormatFlag_Absent() => Assert.False(GhCommand.HasNonDiffFormatFlag([]));

    [Fact]
    public void HasNonDiffFormatFlag_RegularArgs() =>
        Assert.False(GhCommand.HasNonDiffFormatFlag(["123", "--color=always"]));

    // ==================== dispatch + structured-output guard (recording executor) ====================

    [Fact]
    public async Task PrList_ThreadsJsonFieldsAndUserArgs()
    {
        var exec = new RecordingExecutor(_ => Ok("[]"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["pr", "list", "--limit", "5"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal("gh", request.FileName);
        Assert.Equal(
            ["pr", "list", "--json", "number,title,state,author,updatedAt", "--limit", "5"],
            request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Separate, request.CaptureMode);
        Assert.Equal("No Pull Requests\n", sw.ToString());
    }

    [Fact]
    public async Task TopLevelJsonFlag_PassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok("""[{"number":1}]"""));
        var (sw, ew) = Writers();

        var code = await GhCommand.RunAsync(["pr", "list", "--json", "number"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        // Guard: the user's --json request is executed verbatim as a passthrough (inherited stdio).
        Assert.Equal(["pr", "list", "--json", "number"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task PrView_JqFlag_PassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok(".body value"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["pr", "view", "42", "--jq", ".body"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["pr", "view", "42", "--jq", ".body"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task PrView_TemplateFlag_IsNotAJsonGuardButFilters()
    {
        // --template is NOT in should_passthrough_pr_view; -t/--template are value-flags routed to extra,
        // so pr view still runs the filtered --json path with the template forwarded to gh.
        var exec = new RecordingExecutor(_ =>
            Ok("""{"number":42,"title":"t","state":"OPEN","author":{"login":"a"},"url":"u","mergeable":"MERGEABLE"}"""));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["pr", "view", "42", "--template", "{{.title}}"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(
            ["pr", "view", "42", "--json", "number,title,state,author,body,url,mergeable,reviews,statusCheckRollup",
             "--template", "{{.title}}"],
            request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Separate, request.CaptureMode);
    }

    [Fact]
    public async Task IssueView_CommentsFlag_PassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok("raw issue"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["issue", "view", "99", "--comments"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["issue", "view", "99", "--comments"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task IssueView_Filtered_ThreadsJsonFields()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("""{"number":99,"title":"t","state":"OPEN","author":{"login":"a"},"url":"u","body":""}"""));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["issue", "view", "99"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["issue", "view", "99", "--json", "number,title,state,author,body,url"], request.Arguments);
    }

    [Fact]
    public async Task PrStatus_HelpFlag_PassesThrough()
    {
        var exec = new RecordingExecutor(_ => Ok(""));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["pr", "status", "--help"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["pr", "status", "--help"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task UltraCompactFlag_IsThreadedToFormatter()
    {
        var exec = new RecordingExecutor(_ => Ok("[]"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["pr", "list"], ultraCompact: true, exec, sw, ew);

        Assert.Equal("No PRs\n", sw.ToString());
    }

    [Fact]
    public async Task UnfilteredSubcommand_PassesThrough()
    {
        // Subcommands with no Rust filter (e.g. gist) route to raw passthrough.
        var exec = new RecordingExecutor(_ => Ok("raw gist list"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["gist", "list"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["gist", "list"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task Failure_ForwardsRawAndPropagatesExitCode()
    {
        var exec = new RecordingExecutor(_ => Fail("partial stdout\n", "gh: some error\n", 1));
        var (sw, ew) = Writers();

        var code = await GhCommand.RunAsync(["pr", "list"], false, exec, sw, ew);

        Assert.Equal(1, code);
        Assert.Equal("partial stdout\n", sw.ToString());
        Assert.Equal("gh: some error\n", ew.ToString());
    }

    [Fact]
    public async Task InvalidJson_FallsBackToRawStdout()
    {
        var exec = new RecordingExecutor(_ => Ok("not json at all"));
        var (sw, ew) = Writers();

        var code = await GhCommand.RunAsync(["pr", "list"], false, exec, sw, ew);

        Assert.Equal(0, code);
        Assert.Equal("not json at all", sw.ToString());
    }

    // ==================== should_passthrough_run_view ====================

    [Theory]
    [InlineData("--log-failed")]
    [InlineData("--log")]
    [InlineData("--json")]
    public void ShouldPassthroughRunView_True(string flag) => Assert.True(GhCommand.ShouldPassthroughRunView([flag]));

    [Fact]
    public void ShouldPassthroughRunView_EmptyFalse() => Assert.False(GhCommand.ShouldPassthroughRunView([]));

    [Fact]
    public void ShouldPassthroughRunView_OtherFlagsFalse() =>
        Assert.False(GhCommand.ShouldPassthroughRunView(["--web"]));

    // ==================== dispatch: run / repo / api (recording executor) ====================

    [Fact]
    public async Task RunList_ThreadsJsonFieldsLimitAndUserArgs()
    {
        var exec = new RecordingExecutor(_ => Ok("[]"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["run", "list", "--workflow", "ci.yml"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal("gh", request.FileName);
        Assert.Equal(
            ["run", "list", "--json", "databaseId,name,status,conclusion,createdAt", "--limit", "10",
             "--workflow", "ci.yml"],
            request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Separate, request.CaptureMode);
        Assert.Equal("Workflow Runs\n", sw.ToString());
    }

    [Fact]
    public async Task RunView_FilteredNoJsonProjection()
    {
        var exec = new RecordingExecutor(_ => Ok("Status: completed\nConclusion: success\n"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["run", "view", "12345"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        // run view filters gh's text report — no --json is added.
        Assert.Equal(["run", "view", "12345"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Separate, request.CaptureMode);
        Assert.StartsWith("Workflow Run #12345\n", sw.ToString());
    }

    [Fact]
    public async Task RunView_LogFailedFlag_PassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok("raw log"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["run", "view", "12345", "--log-failed"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["run", "view", "12345", "--log-failed"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RunWatch_PassesThroughRaw()
    {
        // watch is not a filtered arm — it streams live, so it passes through (gh_cmd.rs:715).
        var exec = new RecordingExecutor(_ => Ok("streaming..."));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["run", "watch", "12345"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["run", "watch", "12345"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RepoView_ThreadsUserArgsBeforeJsonFields()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("""{"name":"r","owner":{"login":"o"},"description":"","url":"u","stargazerCount":0,"forkCount":0,"isPrivate":false}"""));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["repo", "view", "-R", "o/r"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(
            ["repo", "view", "-R", "o/r", "--json", "name,owner,description,url,stargazerCount,forkCount,isPrivate"],
            request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Separate, request.CaptureMode);
        Assert.StartsWith("o/r\n  [public]\n", sw.ToString());
    }

    [Fact]
    public async Task RepoView_DefaultsToViewWhenNoSubcommand()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("""{"name":"r","owner":{"login":"o"},"description":"","url":"u","stargazerCount":0,"forkCount":0,"isPrivate":false}"""));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["repo"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(
            ["repo", "view", "--json", "name,owner,description,url,stargazerCount,forkCount,isPrivate"],
            request.Arguments);
    }

    [Fact]
    public async Task RepoNonView_PassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok("cloning..."));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["repo", "clone", "o/r"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["repo", "clone", "o/r"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task Api_AlwaysPassesThroughRaw()
    {
        // gh api is never filtered — its JSON is the payload the user asked for (gh_cmd.rs:982).
        var exec = new RecordingExecutor(_ => Ok("""{"name":"rtk"}"""));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["api", "repos/rtk-ai/rtk"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["api", "repos/rtk-ai/rtk"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task Release_PassesThroughRaw()
    {
        // release has no Rust filter — passthrough (never corrupts output).
        var exec = new RecordingExecutor(_ => Ok("v1.0\tLatest\tv1.0\t2026-01-01"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["release", "list"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["release", "list"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RunList_UltraCompactThreaded()
    {
        var exec = new RecordingExecutor(_ => Ok("[]"));
        var (sw, ew) = Writers();

        await GhCommand.RunAsync(["run", "list"], ultraCompact: true, exec, sw, ew);

        Assert.Equal("Runs\n", sw.ToString());
    }

    // ==================== helpers ====================

    private static (StringWriter Stdout, StringWriter Stderr) Writers() =>
        (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });

    private static ExecutionResult Ok(string stdout, string stderr = "") =>
        new(stdout, stderr, 0, TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

    private static ExecutionResult Fail(string stdout, string stderr, int exitCode) =>
        new(stdout, stderr, exitCode, TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

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
