using RtkSharp.Commands.Git;
using RtkSharp.Execution;

namespace RtkSharp.Tests.Commands.Git;

/// <summary>
/// Tests for <see cref="GlabCommand"/>: argument-parsing helpers ported from
/// <c>src/cmds/git/glab_cmd.rs</c> and a supplementary set of dispatch tests using a recording
/// <see cref="IProcessExecutor"/> to assert the child <c>glab</c> argv and the structured-output
/// guards (<c>--output</c>/<c>-F</c>/<c>--json</c> and per-view <c>--web</c>/<c>--comments</c> →
/// raw passthrough), plus the <c>ci</c>/<c>pipeline</c> alias-passthrough quirk. The JSON-formatting
/// and markdown-filtering pure methods (and their fixture-based token-savings/format tests) moved to
/// <c>RtkSharp.Filters.Tests.Commands.Git.GlabFiltersTests</c> when they moved from
/// <c>GlabCommand</c> to <c>RtkSharp.Filters.Commands.Git.GlabFilters</c> (Task 4 of the
/// filters-library extraction).
/// </summary>
public sealed class GlabCommandTests
{
    // ==================== extract_mr_number ====================

    [Fact]
    public void ExtractMrNumber_FromUrl()
    {
        const string url = "https://gitlab.example.com/group/project/-/merge_requests/42";
        Assert.Equal("42", GlabCommand.ExtractMrNumber(url));
    }

    [Fact]
    public void ExtractMrNumber_NoMatch() => Assert.Null(GlabCommand.ExtractMrNumber("not a url"));

    // ==================== ok_confirmation ====================

    [Fact]
    public void OkConfirmation_MrCreate()
    {
        var result = GlabCommand.OkConfirmation("created", "!42 https://gitlab.example.com/-/merge_requests/42");
        Assert.Contains("ok created", result);
        Assert.Contains("!42", result);
    }

    [Fact]
    public void OkConfirmation_MrMerge() => Assert.Equal("ok merged !42", GlabCommand.OkConfirmation("merged", "!42"));

    [Fact]
    public void OkConfirmation_MrApprove() => Assert.Equal("ok approved !42", GlabCommand.OkConfirmation("approved", "!42"));

    // ==================== extract_identifier_and_extra_args ====================

    [Fact]
    public void ExtractIdentifier_Simple()
    {
        var r = GlabCommand.ExtractIdentifierAndExtraArgs(["42"]);
        Assert.NotNull(r);
        Assert.Equal("42", r!.Value.Identifier);
        Assert.Empty(r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_WithRepoFlagBefore()
    {
        // glab mr view -R group/project 42
        var r = GlabCommand.ExtractIdentifierAndExtraArgs(["-R", "group/project", "42"]);
        Assert.Equal("42", r!.Value.Identifier);
        Assert.Equal(["-R", "group/project"], r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_WithRepoFlagAfter()
    {
        // glab mr view 42 -R group/project
        var r = GlabCommand.ExtractIdentifierAndExtraArgs(["42", "-R", "group/project"]);
        Assert.Equal("42", r!.Value.Identifier);
        Assert.Equal(["-R", "group/project"], r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_WithGroupFlag()
    {
        var r = GlabCommand.ExtractIdentifierAndExtraArgs(["-g", "mygroup", "7"]);
        Assert.Equal("7", r!.Value.Identifier);
        Assert.Equal(["-g", "mygroup"], r.Value.Extra);
    }

    [Fact]
    public void ExtractIdentifier_Empty() => Assert.Null(GlabCommand.ExtractIdentifierAndExtraArgs([]));

    [Fact]
    public void ExtractIdentifier_OnlyFlags() =>
        Assert.Null(GlabCommand.ExtractIdentifierAndExtraArgs(["-R", "group/project"]));

    [Fact]
    public void ExtractIdentifier_WithMessageFlag()
    {
        // glab mr note -m "comment" 42 — number should be 42, not "comment".
        var r = GlabCommand.ExtractIdentifierAndExtraArgs(["-m", "comment", "42"]);
        Assert.Equal("42", r!.Value.Identifier);
        Assert.Equal(["-m", "comment"], r.Value.Extra);
    }

    // ==================== parse_optional_identifier ====================

    [Fact]
    public void ParseOptionalIdentifier_EmptyYieldsNoId()
    {
        var (id, extra) = GlabCommand.ParseOptionalIdentifier([]);
        Assert.Null(id);
        Assert.Empty(extra);
    }

    [Fact]
    public void ParseOptionalIdentifier_OnlyFlagsPreservesFlags()
    {
        var (id, extra) = GlabCommand.ParseOptionalIdentifier(["-R", "group/project"]);
        Assert.Null(id);
        Assert.Equal(["-R", "group/project"], extra);
    }

    [Fact]
    public void ParseOptionalIdentifier_WithIdMatchesExtract()
    {
        var (id, extra) = GlabCommand.ParseOptionalIdentifier(["-R", "group/project", "42"]);
        Assert.Equal("42", id);
        Assert.Equal(["-R", "group/project"], extra);
    }

    // ==================== has_output_flag ====================

    [Fact]
    public void HasOutputFlag_Json() => Assert.True(GlabCommand.HasOutputFlag(["--json"]));

    [Fact]
    public void HasOutputFlag_Format()
    {
        Assert.True(GlabCommand.HasOutputFlag(["-F", "json"]));
        Assert.True(GlabCommand.HasOutputFlag(["--output", "text"]));
    }

    [Fact]
    public void HasOutputFlag_None() => Assert.False(GlabCommand.HasOutputFlag(["mr", "list"]));

    // ==================== should_passthrough_view ====================

    [Fact]
    public void ShouldPassthroughView_Web() => Assert.True(GlabCommand.ShouldPassthroughView(["--web"]));

    [Fact]
    public void ShouldPassthroughView_Comments() => Assert.True(GlabCommand.ShouldPassthroughView(["--comments"]));

    [Fact]
    public void ShouldPassthroughView_Output() => Assert.True(GlabCommand.ShouldPassthroughView(["-F", "json"]));

    [Fact]
    public void ShouldPassthroughView_Default() => Assert.False(GlabCommand.ShouldPassthroughView([]));

    // ==================== dispatch (recording executor) ====================

    [Fact]
    public async Task MrList_ThreadsFJsonAndUserArgs()
    {
        var exec = new RecordingExecutor(_ => Ok("[]"));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["mr", "list", "--assignee", "me"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal("glab", request.FileName);
        Assert.Equal(["mr", "list", "-F", "json", "--assignee", "me"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Separate, request.CaptureMode);
        Assert.Equal("No Merge Requests\n", sw.ToString());
    }

    [Fact]
    public async Task TopLevelOutputFlag_PassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok("""[{"iid":1}]"""));
        var (sw, ew) = Writers();

        var code = await GlabCommand.RunAsync(["mr", "list", "--json"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["mr", "list", "--json"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task MrView_WebFlag_PassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok("opening browser..."));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["mr", "view", "42", "--web"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["mr", "view", "42", "--web"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task MrView_NoIdentifier_OmitsPositionalArg()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("""{"iid":7,"title":"t","state":"opened","author":{"username":"a"},"web_url":"u","merge_status":"can_be_merged","source_branch":"a","target_branch":"b"}"""));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["mr", "view"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["mr", "view", "-F", "json"], request.Arguments);
    }

    [Fact]
    public async Task IssueView_Filtered_ThreadsFJson()
    {
        var exec = new RecordingExecutor(_ =>
            Ok("""{"iid":99,"title":"t","state":"opened","author":{"username":"a"},"web_url":"u","description":null}"""));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["issue", "view", "99"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["issue", "view", "99", "-F", "json"], request.Arguments);
    }

    [Fact]
    public async Task IssueView_CommentsFlag_PassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok("raw issue"));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["issue", "view", "99", "--comments"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["issue", "view", "99", "--comments"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task CiPipelineAlias_UnmatchedSubcommand_PassesThroughAsCi()
    {
        // Byte-for-byte port of the Rust quirk: run_ci's unmatched-arm passthrough hardcodes the
        // literal prefix "ci" (glab_cmd.rs:663), even when the user invoked `glab pipeline`.
        var exec = new RecordingExecutor(_ => Ok("streaming..."));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["pipeline", "foo"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["ci", "foo"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task CiView_PassesThroughAsInteractiveTui()
    {
        var exec = new RecordingExecutor(_ => Ok("tui output"));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["ci", "view"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["ci", "view"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task Api_AlwaysPassesThroughRaw()
    {
        var exec = new RecordingExecutor(_ => Ok("""{"id":1}"""));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["api", "projects/1"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["api", "projects/1"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task UnfilteredSubcommand_PassesThrough()
    {
        var exec = new RecordingExecutor(_ => Ok("raw label list"));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["label", "list"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["label", "list"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task InvalidJson_FallsBackToRawStdout()
    {
        var exec = new RecordingExecutor(_ => Ok("not json at all"));
        var (sw, ew) = Writers();

        var code = await GlabCommand.RunAsync(["mr", "list"], false, exec, sw, ew);

        Assert.Equal(0, code);
        Assert.Equal("not json at all", sw.ToString());
    }

    [Fact]
    public async Task Failure_ForwardsRawAndPropagatesExitCode()
    {
        var exec = new RecordingExecutor(_ => Fail("partial stdout\n", "glab: some error\n", 1));
        var (sw, ew) = Writers();

        var code = await GlabCommand.RunAsync(["mr", "list"], false, exec, sw, ew);

        Assert.Equal(1, code);
        Assert.Equal("partial stdout\n", sw.ToString());
        Assert.Equal("glab: some error\n", ew.ToString());
    }

    [Fact]
    public async Task MrCreate_FormatsOkConfirmation()
    {
        var exec = new RecordingExecutor(_ => Ok("https://gitlab.example.com/acme/toolkit/-/merge_requests/99\n"));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["mr", "create", "--title", "t"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["mr", "create", "--title", "t"], request.Arguments);
        Assert.Contains("!99", sw.ToString());
        Assert.Contains("ok created", sw.ToString());
    }

    [Fact]
    public async Task MrMerge_UsesIdentifierFromArgsForConfirmation()
    {
        var exec = new RecordingExecutor(_ => Ok(string.Empty));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["mr", "merge", "42"], false, exec, sw, ew);

        var request = Assert.Single(exec.Requests);
        Assert.Equal(["mr", "merge", "42"], request.Arguments);
        Assert.Equal("ok merged !42\n", sw.ToString());
    }

    [Fact]
    public async Task UltraCompactFlag_IsThreadedToFormatter()
    {
        var exec = new RecordingExecutor(_ => Ok("[]"));
        var (sw, ew) = Writers();

        await GlabCommand.RunAsync(["mr", "list"], ultraCompact: true, exec, sw, ew);

        Assert.Equal("No MRs\n", sw.ToString());
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
