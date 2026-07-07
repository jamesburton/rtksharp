using System.Runtime.CompilerServices;
using System.Text.Json;
using RtkSharp.Commands.Git;
using RtkSharp.Execution;

namespace RtkSharp.Tests.Commands.Git;

/// <summary>
/// Tests for <see cref="GlabCommand"/>. The argument-parsing, JSON-formatting, and markdown-filtering
/// helpers are pure functions ported from <c>src/cmds/git/glab_cmd.rs</c>, so most of this suite is a
/// direct, test-for-test port of that module's <c>#[cfg(test)] mod tests</c> block — including the
/// fixture-based token-savings and format assertions, which load the same fixtures
/// (<c>tests/fixtures/glab_*</c> in the Rust tree) copied byte-identical into
/// <c>RtkSharp.Tests/Fixtures/glab/</c>. A supplementary set of dispatch tests uses a recording
/// <see cref="IProcessExecutor"/> to assert the child <c>glab</c> argv and the structured-output
/// guards (<c>--output</c>/<c>-F</c>/<c>--json</c> and per-view <c>--web</c>/<c>--comments</c> →
/// raw passthrough), plus the <c>ci</c>/<c>pipeline</c> alias-passthrough quirk.
/// </summary>
public sealed class GlabCommandTests
{
    private static string FixturesDir([CallerFilePath] string? thisFile = null) =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "Fixtures", "glab");

    // Normalize CRLF -> LF: git's autocrlf may rewrite the checked-in fixtures to CRLF on checkout.
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixturesDir(), name)).Replace("\r\n", "\n");

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ==================== state_icon ====================

    [Fact]
    public void StateIcon_Opened()
    {
        Assert.Equal("[open]", GlabCommand.StateIcon("opened", false));
        Assert.Equal("O", GlabCommand.StateIcon("opened", true));
    }

    [Fact]
    public void StateIcon_Merged()
    {
        Assert.Equal("[merged]", GlabCommand.StateIcon("merged", false));
        Assert.Equal("M", GlabCommand.StateIcon("merged", true));
    }

    [Fact]
    public void StateIcon_Closed()
    {
        Assert.Equal("[closed]", GlabCommand.StateIcon("closed", false));
        Assert.Equal("C", GlabCommand.StateIcon("closed", true));
    }

    [Fact]
    public void StateIcon_UnknownIsQuestionMarkInBothModes()
    {
        // Unlike gh's state_icon (which falls back to "[unknown]" in non-compact mode), glab's
        // fallback is the bare "?" in both compact and non-compact modes (glab_cmd.rs:115-131).
        Assert.Equal("?", GlabCommand.StateIcon("weird", false));
        Assert.Equal("?", GlabCommand.StateIcon("weird", true));
    }

    // ==================== pipeline_icon ====================

    [Fact]
    public void PipelineIcon_Success()
    {
        Assert.Equal("[ok]", GlabCommand.PipelineIcon("success", false));
        Assert.Equal("+", GlabCommand.PipelineIcon("success", true));
    }

    [Fact]
    public void PipelineIcon_Failed()
    {
        Assert.Equal("[fail]", GlabCommand.PipelineIcon("failed", false));
        Assert.Equal("x", GlabCommand.PipelineIcon("failed", true));
    }

    [Fact]
    public void PipelineIcon_Running()
    {
        Assert.Equal("[run]", GlabCommand.PipelineIcon("running", false));
        Assert.Equal("~", GlabCommand.PipelineIcon("running", true));
    }

    [Fact]
    public void PipelineIcon_PendingSplitsInNonCompactButSharesTildeInCompact()
    {
        // Asymmetry preserved from Rust: compact groups running/pending into "~"; non-compact
        // gives pending its own "[pend]" tag distinct from running's "[run]".
        Assert.Equal("[pend]", GlabCommand.PipelineIcon("pending", false));
        Assert.Equal("~", GlabCommand.PipelineIcon("pending", true));
    }

    [Fact]
    public void PipelineIcon_CanceledAndCancelledShareIcon()
    {
        Assert.Equal("[cancel]", GlabCommand.PipelineIcon("canceled", false));
        Assert.Equal("[cancel]", GlabCommand.PipelineIcon("cancelled", false));
        Assert.Equal("X", GlabCommand.PipelineIcon("canceled", true));
    }

    // ==================== extract_mr_number ====================

    [Fact]
    public void ExtractMrNumber_FromUrl()
    {
        const string url = "https://gitlab.example.com/group/project/-/merge_requests/42";
        Assert.Equal("42", GlabCommand.ExtractMrNumber(url));
    }

    [Fact]
    public void ExtractMrNumber_NoMatch() => Assert.Null(GlabCommand.ExtractMrNumber("not a url"));

    // ==================== filter_markdown_body ====================

    [Fact]
    public void FilterMarkdownBody_Empty() => Assert.Equal(string.Empty, GlabCommand.FilterMarkdownBody(string.Empty));

    [Fact]
    public void FilterMarkdownBody_HtmlComments()
    {
        var result = GlabCommand.FilterMarkdownBody("Hello\n<!-- comment -->\nWorld");
        Assert.DoesNotContain("<!--", result);
        Assert.Contains("Hello", result);
        Assert.Contains("World", result);
    }

    [Fact]
    public void FilterMarkdownBody_CodeBlockPreserved()
    {
        var result = GlabCommand.FilterMarkdownBody("Text\n```\n<!-- not stripped -->\n```\nAfter");
        Assert.Contains("<!-- not stripped -->", result);
        Assert.Contains("Text", result);
        Assert.Contains("After", result);
    }

    [Fact]
    public void FilterMarkdownBody_BlankLinesCollapse()
    {
        var result = GlabCommand.FilterMarkdownBody("Line 1\n\n\n\n\nLine 2");
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Contains("Line 1", result);
        Assert.Contains("Line 2", result);
    }

    [Fact]
    public void FilterMarkdownBody_BadgesRemoved()
    {
        const string input =
            "# Title\n[![CI](https://img.shields.io/badge.svg)](https://github.com/actions)\nText";
        var result = GlabCommand.FilterMarkdownBody(input);
        Assert.DoesNotContain("shields.io", result);
        Assert.Contains("# Title", result);
        Assert.Contains("Text", result);
    }

    [Fact]
    public void FilterMarkdownBody_MeaningfulContentPreserved()
    {
        const string input = "## Summary\n- Item 1\n- Item 2\n\n[Link](https://example.com)";
        var result = GlabCommand.FilterMarkdownBody(input);
        Assert.Contains("## Summary", result);
        Assert.Contains("- Item 1", result);
        Assert.Contains("[Link](https://example.com)", result);
    }

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

    // ==================== format_mr_list (fixture) ====================

    [Fact]
    public void MrList_TokenSavings()
    {
        var input = ReadFixture("glab_mr_list_raw.json");
        using var doc = JsonDocument.Parse(input);
        var output = GlabCommand.FormatMrList(doc.RootElement, false);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"MR list: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void MrList_Format()
    {
        var input = ReadFixture("glab_mr_list_raw.json");
        using var doc = JsonDocument.Parse(input);
        var output = GlabCommand.FormatMrList(doc.RootElement, false);
        Assert.Contains("Merge Requests", output);
        Assert.Contains("!314", output);
        Assert.Contains("[open]", output);
        Assert.Contains("[merged]", output);
        Assert.Contains("[closed]", output);
    }

    [Fact]
    public void MrList_UltraCompact()
    {
        var input = ReadFixture("glab_mr_list_raw.json");
        using var doc = JsonDocument.Parse(input);
        var output = GlabCommand.FormatMrList(doc.RootElement, true);
        Assert.StartsWith("MRs\n", output);
        Assert.Contains("O ", output);
        Assert.Contains("M ", output);
        Assert.Contains("C ", output);
    }

    // ==================== format_issue_list (fixture) ====================

    [Fact]
    public void IssueList_TokenSavings()
    {
        var input = ReadFixture("glab_issue_list_raw.json");
        using var doc = JsonDocument.Parse(input);
        var output = GlabCommand.FormatIssueList(doc.RootElement, false);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"Issue list: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void IssueList_Format()
    {
        var input = ReadFixture("glab_issue_list_raw.json");
        using var doc = JsonDocument.Parse(input);
        var output = GlabCommand.FormatIssueList(doc.RootElement, false);
        Assert.Contains("Issues", output);
        Assert.Contains("#156", output);
        Assert.Contains("[open]", output);
        Assert.Contains("[closed]", output);
    }

    // ==================== non-array edge cases ====================

    [Fact]
    public void FormatMrList_NonArrayReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Equal(string.Empty, GlabCommand.FormatMrList(doc.RootElement, false));
    }

    [Fact]
    public void FormatIssueList_NonArrayReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Equal(string.Empty, GlabCommand.FormatIssueList(doc.RootElement, false));
    }

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

    // ==================== format_release_list (fixture + inline) ====================

    [Fact]
    public void FormatReleaseList_Fixture()
    {
        var input = ReadFixture("glab_release_list_raw.txt");
        var output = GlabCommand.FormatReleaseList(input);
        Assert.NotNull(output);
        Assert.StartsWith("Releases\n", output);
        Assert.Contains("v3.2.1", output);
        Assert.Contains("about 2 days ago", output);
    }

    [Fact]
    public void FormatReleaseList_TokenSavings()
    {
        var input = ReadFixture("glab_release_list_raw.txt");
        var output = GlabCommand.FormatReleaseList(input);
        Assert.NotNull(output);

        var savings = 100.0 - (CountTokens(output!) / (double)CountTokens(input) * 100.0);
        // Release list text is already compact (tab-separated); savings are modest.
        Assert.True(savings >= 20.0, $"Release list: expected >=20% savings, got {savings:F1}%");
    }

    [Fact]
    public void FormatReleaseList_Empty()
    {
        const string input = "No releases available on owner/repo.\nName\tTag\tCreated\n";
        Assert.Null(GlabCommand.FormatReleaseList(input));
    }

    [Fact]
    public void FormatReleaseList_NameDiffersFromTag()
    {
        const string input = "Showing 1 releases\n\nName\tTag\tCreated\nMy Release\tv1.0.0\t2 days ago\n";
        var output = GlabCommand.FormatReleaseList(input);
        Assert.NotNull(output);
        Assert.Contains("My Release [v1.0.0]", output);
    }

    // ==================== filter_ci_trace (fixture) ====================

    [Fact]
    public void FilterCiTrace_StripsBoilerplate()
    {
        var input = ReadFixture("glab_ci_trace_raw.txt");
        var output = GlabCommand.FilterCiTrace(input);

        // Runner boilerplate stripped.
        Assert.DoesNotContain("Running with gitlab-runner", output);
        Assert.DoesNotContain("Using Docker executor", output);
        Assert.DoesNotContain("Fetching changes with git", output);
        Assert.DoesNotContain("Checking out", output);
        Assert.DoesNotContain("Uploading artifacts", output);

        // Build output preserved.
        Assert.Contains("npm ci", output);
        Assert.Contains("npm run build", output);
        Assert.Contains("npm test", output);

        // Test results preserved.
        Assert.Contains("FAIL", output);
        Assert.Contains("AssertionError", output);

        // Final error line preserved.
        Assert.Contains("Job failed", output);
    }

    [Fact]
    public void FilterCiTrace_TokenSavings()
    {
        var input = ReadFixture("glab_ci_trace_raw.txt");
        var output = GlabCommand.FilterCiTrace(input);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 30.0, $"CI trace: expected >=30% savings, got {savings:F1}%");
    }

    // ==================== filter_release_view (fixture + inline) ====================

    [Fact]
    public void FilterReleaseView_StripsSources()
    {
        var input = ReadFixture("glab_release_view_raw.txt");
        var output = GlabCommand.FilterReleaseView(input);

        // SOURCES section stripped.
        Assert.DoesNotContain("SOURCES", output);
        Assert.DoesNotContain("toolkit-v2.0.0.zip", output);
        Assert.DoesNotContain("toolkit-v2.0.0.tar.gz", output);

        // Content preserved.
        Assert.Contains("Test Release v2.0", output);
        Assert.Contains("Added widget support", output);
        Assert.Contains("@alice_dev @bob_dev", output);

        // Noise stripped.
        Assert.DoesNotContain("--------", output);
        Assert.DoesNotContain("Image:", output);
        Assert.DoesNotContain("<!-- internal", output);

        // Footer preserved.
        Assert.Contains("View this release", output);
    }

    [Fact]
    public void FilterReleaseView_TokenSavings()
    {
        var input = ReadFixture("glab_release_view_raw.txt");
        var output = GlabCommand.FilterReleaseView(input);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 20.0, $"Release view: expected >=20% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterReleaseView_NoSourcesSection()
    {
        const string input = "# Release 1.0\n\nJust a simple changelog entry.\n";
        var output = GlabCommand.FilterReleaseView(input);
        Assert.Contains("Release 1.0", output);
        Assert.Contains("changelog entry", output);
    }

    // ==================== further edge cases ====================

    [Fact]
    public void FormatMrList_EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No Merge Requests\n", GlabCommand.FormatMrList(doc.RootElement, false));
    }

    [Fact]
    public void FormatMrList_EmptyArrayUltraCompact()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No MRs\n", GlabCommand.FormatMrList(doc.RootElement, true));
    }

    [Fact]
    public void FormatIssueList_EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No Issues\n", GlabCommand.FormatIssueList(doc.RootElement, false));
    }

    [Fact]
    public void FormatCiList_EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No Pipelines\n", GlabCommand.FormatCiList(doc.RootElement, false));
    }

    [Fact]
    public void FormatMrView_NullNestedFields()
    {
        // Defensive: if the GitLab API omits or nulls out nested fields, formatters must render
        // placeholders without throwing.
        const string json =
            """{"iid":42,"title":"Edge","state":"opened","author":null,"web_url":"","merge_status":"unknown","description":null}""";
        using var doc = JsonDocument.Parse(json);
        var output = GlabCommand.FormatMrView(doc.RootElement, false);
        Assert.Contains("MR !42: Edge", output);
        Assert.Contains("???", output); // author fallback
    }

    [Fact]
    public void FormatIssueView_MissingDescription()
    {
        const string json =
            """{"iid":10,"title":"X","state":"closed","author":{"username":"u"},"web_url":"http://e","description":null}""";
        using var doc = JsonDocument.Parse(json);
        var output = GlabCommand.FormatIssueView(doc.RootElement);
        Assert.Contains("[closed] Issue #10: X", output);
        Assert.Contains("Author: @u", output);
        // No "Description:" section when null.
        Assert.DoesNotContain("Description:", output);
    }

    [Fact]
    public void FormatCiStatus_NonEnglishFallback()
    {
        // Non-English locale output with no recognized keyword must fall back to raw.
        const string raw = "Le pipeline est en cours d'exécution\n";
        var output = GlabCommand.FormatCiStatus(raw, false);
        Assert.Equal(raw, output);
    }

    // ==================== mr_view enrichment (branches / labels / reviewers) ====================

    private const string MrViewFull = """
        {
            "iid": 42,
            "title": "feat: widget",
            "state": "opened",
            "author": {"username": "alice_dev"},
            "web_url": "https://gitlab.example.com/acme/toolkit/-/merge_requests/42",
            "merge_status": "can_be_merged",
            "source_branch": "feat/widget",
            "target_branch": "main",
            "labels": ["enhancement", "cli"],
            "reviewers": [{"username": "bob_review"}, {"username": "carol_review"}],
            "head_pipeline": {"status": "success"},
            "description": null
        }
        """;

    [Fact]
    public void FormatMrView_Branches()
    {
        using var doc = JsonDocument.Parse(MrViewFull);
        var output = GlabCommand.FormatMrView(doc.RootElement, false);
        Assert.Contains("feat/widget -> main", output);
    }

    [Fact]
    public void FormatMrView_Labels()
    {
        using var doc = JsonDocument.Parse(MrViewFull);
        var output = GlabCommand.FormatMrView(doc.RootElement, false);
        Assert.Contains("Labels: enhancement, cli", output);
    }

    [Fact]
    public void FormatMrView_Reviewers()
    {
        using var doc = JsonDocument.Parse(MrViewFull);
        var output = GlabCommand.FormatMrView(doc.RootElement, false);
        Assert.Contains("Reviewers: @bob_review, @carol_review", output);
    }

    [Fact]
    public void FormatMrView_NoLabelsNoReviewers()
    {
        const string json = """
            {
                "iid":1, "title":"X", "state":"opened",
                "author":{"username":"u1"}, "web_url":"",
                "merge_status":"can_be_merged",
                "source_branch":"a", "target_branch":"b",
                "labels":[], "reviewers":[], "description":null
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var output = GlabCommand.FormatMrView(doc.RootElement, false);
        Assert.DoesNotContain("Labels:", output);
        Assert.DoesNotContain("Reviewers:", output);
        // Branches line still present.
        Assert.Contains("a -> b", output);
    }

    [Fact]
    public void FormatMrView_MergeableTextTag()
    {
        using var doc = JsonDocument.Parse(MrViewFull);
        var output = GlabCommand.FormatMrView(doc.RootElement, false);
        // merge_status="can_be_merged" -> "[ok]" (text tag, no emoji).
        Assert.Contains("opened | [ok]", output);
        // And no emoji anywhere in the rendered output.
        Assert.DoesNotContain('✅', output);
        Assert.DoesNotContain('❌', output);
        Assert.DoesNotContain('✓', output);
        Assert.DoesNotContain('✗', output);
    }

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
