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
    // ==================== Truncate (utils::truncate) ====================

    [Fact]
    public void Truncate_ShortUnchanged() => Assert.Equal("short", GhCommand.Truncate("short", 10));

    [Fact]
    public void Truncate_LongGetsEllipsis() =>
        Assert.Equal("this is a ve...", GhCommand.Truncate("this is a very long string", 15));

    [Fact]
    public void Truncate_MaxLenBelowThree() => Assert.Equal("...", GhCommand.Truncate("abcdef", 2));

    [Fact]
    public void Truncate_MultibyteCountsRunes()
    {
        Assert.Equal("🚀🎉🔥abc", GhCommand.Truncate("🚀🎉🔥abc", 6)); // exact fit
        Assert.Equal("🚀🎉🔥ab...", GhCommand.Truncate("🚀🎉🔥abcdef", 8)); // truncated on rune boundary
    }

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

    // ==================== state_icon ====================

    [Theory]
    [InlineData("OPEN", "[open]")]
    [InlineData("MERGED", "[merged]")]
    [InlineData("CLOSED", "[closed]")]
    [InlineData("WEIRD", "[unknown]")]
    public void StateIcon_NonUltra(string state, string expected) => Assert.Equal(expected, GhCommand.StateIcon(state, false));

    [Theory]
    [InlineData("OPEN", "O")]
    [InlineData("MERGED", "M")]
    [InlineData("CLOSED", "C")]
    [InlineData("WEIRD", "?")]
    public void StateIcon_Ultra(string state, string expected) => Assert.Equal(expected, GhCommand.StateIcon(state, true));

    // ==================== format_pr_list ====================

    [Fact]
    public void FormatPrList_Basic()
    {
        const string json = """
        [{"number":10,"title":"Fix bug","state":"OPEN","author":{"login":"octo"}},
         {"number":9,"title":"Add feature","state":"MERGED","author":{"login":"cat"}}]
        """;
        using var doc = JsonDocument.Parse(json);
        var result = GhCommand.FormatPrList(doc.RootElement, false);
        Assert.Equal(
            "Pull Requests\n  [open] #10 Fix bug (octo)\n  [merged] #9 Add feature (cat)\n",
            result);
    }

    [Fact]
    public void FormatPrList_EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No Pull Requests\n", GhCommand.FormatPrList(doc.RootElement, false));
        Assert.Equal("No PRs\n", GhCommand.FormatPrList(doc.RootElement, true));
    }

    [Fact]
    public void FormatPrList_UltraHeaderAndIcons()
    {
        const string json = """[{"number":1,"title":"x","state":"OPEN","author":{"login":"a"}}]""";
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PRs\n  O #1 x (a)\n", GhCommand.FormatPrList(doc.RootElement, true));
    }

    [Fact]
    public void FormatPrList_NonArrayReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"number":1}""");
        Assert.Equal(string.Empty, GhCommand.FormatPrList(doc.RootElement, false));
    }

    // ==================== format_pr_view ====================

    [Fact]
    public void FormatPrView_BadgesOnlyBodyShowsFallbackNote()
    {
        const string json = """
        {"number":42,"title":"Test PR","state":"OPEN","author":{"login":"octocat"},
         "url":"https://github.com/foo/bar/pull/42","mergeable":"MERGEABLE",
         "body":"<!-- Auto-generated by bot -->\n[![CI](https://shields.io/badge.svg)](https://ci.example.com)\n![screenshot](https://example.com/img.png)\n---\n"}
        """;
        using var doc = JsonDocument.Parse(json);
        var result = GhCommand.FormatPrView(doc.RootElement, false);
        Assert.Contains("(body contained only badges/images/comments)", result);
    }

    [Fact]
    public void FormatPrView_ContentBodyNoFallbackNote()
    {
        const string json = """
        {"number":42,"title":"Test PR","state":"OPEN","author":{"login":"octocat"},
         "url":"https://github.com/foo/bar/pull/42","mergeable":"MERGEABLE",
         "body":"## Summary\nFix the thing.\n"}
        """;
        using var doc = JsonDocument.Parse(json);
        var result = GhCommand.FormatPrView(doc.RootElement, false);
        Assert.DoesNotContain("(body contained only badges/images/comments)", result);
        Assert.Contains("## Summary", result);
        Assert.Contains("Fix the thing.", result);
    }

    [Fact]
    public void FormatPrView_EmptyBodyNoFallbackNote()
    {
        const string json = """
        {"number":42,"title":"Test PR","state":"OPEN","author":{"login":"octocat"},
         "url":"https://github.com/foo/bar/pull/42","mergeable":"MERGEABLE","body":""}
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.DoesNotContain("(body contained only badges/images/comments)", GhCommand.FormatPrView(doc.RootElement, false));
    }

    [Fact]
    public void FormatPrView_HeaderAuthorMergeableAndChecks()
    {
        // statusCheckRollup counts SUCCESS via conclusion OR state; failure via conclusion OR state.
        const string json = """
        {"number":7,"title":"Big change","state":"OPEN","author":{"login":"dev"},
         "url":"https://x/7","mergeable":"CONFLICTING",
         "statusCheckRollup":[{"conclusion":"SUCCESS"},{"state":"SUCCESS"},{"conclusion":"FAILURE"}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var result = GhCommand.FormatPrView(doc.RootElement, false);
        Assert.StartsWith("[open] PR #7: Big change\n  dev\n  OPEN | [x]\n", result);
        Assert.Contains("  Checks: 2/3 passed\n", result);
        Assert.Contains("  [warn] 1 checks failed\n", result);
    }

    [Fact]
    public void FormatPrView_UltraChecksCompact()
    {
        const string json = """
        {"number":7,"title":"t","state":"OPEN","author":{"login":"d"},"url":"u","mergeable":"MERGEABLE",
         "statusCheckRollup":[{"conclusion":"SUCCESS"},{"conclusion":"FAILURE"}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var result = GhCommand.FormatPrView(doc.RootElement, true);
        Assert.Contains("  [x]1/2  1 fail\n", result);
    }

    [Fact]
    public void FormatPrView_ReviewsAsArraySkipsReviewsLine()
    {
        // Real gh --json reviews returns a plain array (not {nodes:[...]}), so the reviews line is skipped.
        const string json = """
        {"number":1,"title":"t","state":"MERGED","author":{"login":"a"},"url":"u","mergeable":"UNKNOWN",
         "reviews":[{"state":"APPROVED"}]}
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.DoesNotContain("Reviews:", GhCommand.FormatPrView(doc.RootElement, false));
    }

    [Fact]
    public void FormatPrView_ReviewsNodesShapeRendersReviewsLine()
    {
        // GraphQL-shaped reviews.nodes IS honored when present.
        const string json = """
        {"number":1,"title":"t","state":"OPEN","author":{"login":"a"},"url":"u","mergeable":"MERGEABLE",
         "reviews":{"nodes":[{"state":"APPROVED"},{"state":"CHANGES_REQUESTED"},{"state":"APPROVED"}]}}
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("  Reviews: 2 approved, 1 changes requested\n", GhCommand.FormatPrView(doc.RootElement, false));
    }

    // ==================== format_issue_list ====================

    [Fact]
    public void FormatIssueList_Basic()
    {
        const string json = """
        [{"number":5,"title":"Broken","state":"OPEN","author":{"login":"x"}},
         {"number":4,"title":"Done","state":"CLOSED","author":{"login":"y"}}]
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "Issues\n  [open] #5 Broken\n  [closed] #4 Done\n",
            GhCommand.FormatIssueList(doc.RootElement, false));
    }

    [Fact]
    public void FormatIssueList_EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No Issues\n", GhCommand.FormatIssueList(doc.RootElement, false));
    }

    [Fact]
    public void FormatIssueList_UltraIcons()
    {
        const string json = """
        [{"number":5,"title":"a","state":"OPEN"},{"number":4,"title":"b","state":"CLOSED"}]
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Issues\n  O #5 a\n  C #4 b\n", GhCommand.FormatIssueList(doc.RootElement, true));
    }

    // ==================== format_issue_view ====================

    [Fact]
    public void FormatIssueView_Basic()
    {
        const string json = """
        {"number":99,"title":"Crash","state":"OPEN","author":{"login":"octocat"},
         "url":"https://github.com/foo/bar/issues/99","body":"## Repro\nDo the thing.\n"}
        """;
        using var doc = JsonDocument.Parse(json);
        var result = GhCommand.FormatIssueView(doc.RootElement);
        Assert.StartsWith("[open] Issue #99: Crash\n  Author: @octocat\n  Status: OPEN\n  URL: https://github.com/foo/bar/issues/99\n", result);
        Assert.Contains("\n  Description:\n    ## Repro\n    Do the thing.\n", result);
    }

    [Fact]
    public void FormatIssueView_BadgesOnlyBodyShowsFallbackNote()
    {
        const string json = """
        {"number":99,"title":"t","state":"OPEN","author":{"login":"octocat"},"url":"u",
         "body":"<!-- Auto-generated -->\n[![status](https://shields.io/s.svg)](https://example.com)\n"}
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("Description: (body contained only badges/images/comments)", GhCommand.FormatIssueView(doc.RootElement));
    }

    // ==================== format_pr_status ====================

    [Fact]
    public void FormatPrStatus_IncludesCurrentBranchSummary()
    {
        const string json = """
        {"currentBranch":{"number":934,"title":"fix wrappers for standardization and exit codes",
          "reviewDecision":"CHANGES_REQUESTED",
          "statusCheckRollup":[{"conclusion":"SUCCESS"},{"state":"SUCCESS"},{"conclusion":"FAILURE"}]},
         "createdBy":[]}
        """;
        using var doc = JsonDocument.Parse(json);
        var result = GhCommand.FormatPrStatus(doc.RootElement);
        Assert.Contains("Current Branch", result);
        Assert.Contains("#934", result);
        Assert.Contains("CHANGES_REQUESTED", result);
        Assert.Contains("checks 2/3", result);
        Assert.Contains("fail 1", result);
    }

    [Fact]
    public void FormatPrStatus_ListsCreatedByPrs()
    {
        const string json = """
        {"currentBranch":null,
         "createdBy":[{"number":1,"title":"one","reviewDecision":"APPROVED"},
                      {"number":2,"title":"two"}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var result = GhCommand.FormatPrStatus(doc.RootElement);
        Assert.DoesNotContain("Current Branch", result);
        Assert.Contains("Your PRs (2):\n", result);
        Assert.Contains("  #1 one [APPROVED]\n", result);
        Assert.Contains("  #2 two [PENDING]\n", result);
    }

    // ==================== format_pr_checks ====================

    [Fact]
    public void FormatPrChecks_SummarizesCounts()
    {
        const string stdout = "build\tpass\t1s\nlint\tfail\t2s\ndeploy\tpending\nunit\tpass\t3s\n";
        var result = GhCommand.FormatPrChecks(stdout);
        Assert.Contains("CI Checks Summary:\n", result);
        Assert.Contains("  [ok] Passed: 2\n", result);
        Assert.Contains("  [FAIL] Failed: 1\n", result);
        Assert.Contains("  [pending] Pending: 1\n", result);
        Assert.Contains("  Failed checks:\n", result);
        Assert.Contains("lint", result);
    }

    // ==================== filter_markdown_body ====================

    [Fact]
    public void FilterMarkdownBody_HtmlCommentSingleLine()
    {
        var result = GhCommand.FilterMarkdownBody("Hello\n<!-- this is a comment -->\nWorld");
        Assert.DoesNotContain("<!--", result);
        Assert.Contains("Hello", result);
        Assert.Contains("World", result);
    }

    [Fact]
    public void FilterMarkdownBody_HtmlCommentMultiline()
    {
        var result = GhCommand.FilterMarkdownBody("Before\n<!--\nmultiline\ncomment\n-->\nAfter");
        Assert.DoesNotContain("<!--", result);
        Assert.DoesNotContain("multiline", result);
        Assert.Contains("Before", result);
        Assert.Contains("After", result);
    }

    [Fact]
    public void FilterMarkdownBody_BadgeLines()
    {
        var result = GhCommand.FilterMarkdownBody(
            "# Title\n[![CI](https://img.shields.io/badge.svg)](https://github.com/actions)\nSome text");
        Assert.DoesNotContain("shields.io", result);
        Assert.Contains("# Title", result);
        Assert.Contains("Some text", result);
    }

    [Fact]
    public void FilterMarkdownBody_ImageOnlyLines()
    {
        var result = GhCommand.FilterMarkdownBody("# Title\n![screenshot](https://example.com/img.png)\nSome text");
        Assert.DoesNotContain("![screenshot]", result);
        Assert.Contains("# Title", result);
        Assert.Contains("Some text", result);
    }

    [Fact]
    public void FilterMarkdownBody_HorizontalRules()
    {
        var result = GhCommand.FilterMarkdownBody("Section 1\n---\nSection 2\n***\nSection 3\n___\nEnd");
        Assert.DoesNotContain("---", result);
        Assert.DoesNotContain("***", result);
        Assert.DoesNotContain("___", result);
        Assert.Contains("Section 1", result);
        Assert.Contains("Section 2", result);
        Assert.Contains("Section 3", result);
    }

    [Fact]
    public void FilterMarkdownBody_BlankLinesCollapse()
    {
        var result = GhCommand.FilterMarkdownBody("Line 1\n\n\n\n\nLine 2");
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Contains("Line 1", result);
        Assert.Contains("Line 2", result);
    }

    [Fact]
    public void FilterMarkdownBody_CodeBlockPreserved()
    {
        var result = GhCommand.FilterMarkdownBody(
            "Text before\n```python\n<!-- not a comment -->\n![not an image](url)\n---\n```\nText after");
        Assert.Contains("<!-- not a comment -->", result);
        Assert.Contains("![not an image](url)", result);
        Assert.Contains("---", result);
        Assert.Contains("Text before", result);
        Assert.Contains("Text after", result);
    }

    [Fact]
    public void FilterMarkdownBody_Empty() => Assert.Equal(string.Empty, GhCommand.FilterMarkdownBody(""));

    [Fact]
    public void FilterMarkdownBody_MeaningfulContentPreserved()
    {
        var result = GhCommand.FilterMarkdownBody(
            "## Summary\n- Item 1\n- Item 2\n\n[Link](https://example.com)\n\n| Col1 | Col2 |\n| --- | --- |\n| a | b |");
        Assert.Contains("## Summary", result);
        Assert.Contains("- Item 1", result);
        Assert.Contains("- Item 2", result);
        Assert.Contains("[Link](https://example.com)", result);
        Assert.Contains("| Col1 | Col2 |", result);
    }

    [Fact]
    public void FilterMarkdownBody_TokenSavings()
    {
        const string input = """
        <!-- This PR template is auto-generated -->
        <!-- Please fill in the following sections -->

        ## Summary

        Added smart markdown filtering for gh issue/pr view commands.

        [![CI](https://img.shields.io/github/actions/workflow/status/rtk-ai/rtk/ci.yml)](https://github.com/rtk-ai/rtk/actions)
        [![Coverage](https://img.shields.io/codecov/c/github/rtk-ai/rtk)](https://codecov.io/gh/rtk-ai/rtk)

        ![screenshot](https://user-images.githubusercontent.com/123/screenshot.png)

        ---

        ## Changes

        - Filter HTML comments
        - Filter badge lines
        - Filter image-only lines
        - Collapse blank lines

        ***

        ## Test Plan

        - [x] Unit tests added
        - [x] Snapshot tests pass
        - [ ] Manual testing

        ___

        <!-- Do not edit below this line -->
        <!-- Auto-generated footer -->
        """;

        var result = GhCommand.FilterMarkdownBody(input);

        static int CountTokens(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        var savings = 100.0 - (CountTokens(result) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 30.0, $"Expected >=30% savings, got {savings:F1}%");

        Assert.Contains("## Summary", result);
        Assert.Contains("## Changes", result);
        Assert.Contains("## Test Plan", result);
        Assert.Contains("Filter HTML comments", result);
    }

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

    // ==================== format_run_list ====================

    [Fact]
    public void FormatRunList_Basic()
    {
        const string json = """
        [{"databaseId":111,"name":"CI","status":"completed","conclusion":"success"},
         {"databaseId":222,"name":"Deploy","status":"completed","conclusion":"failure"},
         {"databaseId":333,"name":"Lint","status":"in_progress","conclusion":null},
         {"databaseId":444,"name":"Nightly","status":"completed","conclusion":"cancelled"},
         {"databaseId":555,"name":"Queued","status":"queued","conclusion":null}]
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "Workflow Runs\n  [ok] CI [111]\n  [FAIL] Deploy [222]\n  [time] Lint [333]\n  [X] Nightly [444]\n  [pending] Queued [555]\n",
            GhCommand.FormatRunList(doc.RootElement, false));
    }

    [Fact]
    public void FormatRunList_UltraIcons()
    {
        const string json = """
        [{"databaseId":1,"name":"CI","status":"completed","conclusion":"success"},
         {"databaseId":2,"name":"D","status":"completed","conclusion":"failure"},
         {"databaseId":3,"name":"L","status":"in_progress","conclusion":null},
         {"databaseId":4,"name":"N","status":"completed","conclusion":"cancelled"},
         {"databaseId":5,"name":"Q","status":"queued","conclusion":null}]
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "Runs\n  [ok] CI [1]\n  [x] D [2]\n  ~ L [3]\n  X N [4]\n  ? Q [5]\n",
            GhCommand.FormatRunList(doc.RootElement, true));
    }

    [Fact]
    public void FormatRunList_EmptyArrayJustHeader()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("Workflow Runs\n", GhCommand.FormatRunList(doc.RootElement, false));
        Assert.Equal("Runs\n", GhCommand.FormatRunList(doc.RootElement, true));
    }

    [Fact]
    public void FormatRunList_NonArrayReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"databaseId":1}""");
        Assert.Equal(string.Empty, GhCommand.FormatRunList(doc.RootElement, false));
    }

    // ==================== format_run_view ====================

    [Fact]
    public void FormatRunView_WithId() =>
        Assert.StartsWith("Workflow Run #12345\n", GhCommand.FormatRunView(string.Empty, "12345"));

    [Fact]
    public void FormatRunView_WithoutIdNoEmptyHash()
    {
        var output = GhCommand.FormatRunView(string.Empty, string.Empty);
        Assert.StartsWith("Workflow Run\n", output);
        Assert.DoesNotContain("#\n", output);
    }

    [Fact]
    public void FormatRunView_KeepsStatusAndConclusionSkipsJobsSuccess()
    {
        const string stdout =
            "X CI · 12345\nTriggered via push\n\nStatus: completed\nConclusion: failure\n\nJOBS\n✓ build in 1m\n✓ lint (success)\nX deploy failed\n";
        var result = GhCommand.FormatRunView(stdout, "12345");
        Assert.Equal(
            "Workflow Run #12345\n  Status: completed\n  Conclusion: failure\n  [FAIL] X deploy failed\n",
            result);
    }

    // ==================== format_repo_view ====================

    [Fact]
    public void FormatRepoView_PublicWithDescription()
    {
        const string json = """
        {"name":"rtk","owner":{"login":"rtk-ai"},"description":"CLI proxy",
         "url":"https://github.com/rtk-ai/rtk","stargazerCount":68000,"forkCount":4200,"isPrivate":false}
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "rtk-ai/rtk\n  [public]\n  CLI proxy\n  68000 stars | 4200 forks\n  https://github.com/rtk-ai/rtk\n",
            GhCommand.FormatRepoView(doc.RootElement));
    }

    [Fact]
    public void FormatRepoView_PrivateNoDescriptionOmitsLine()
    {
        const string json = """
        {"name":"secret","owner":{"login":"me"},"description":"","url":"u",
         "stargazerCount":0,"forkCount":0,"isPrivate":true}
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "me/secret\n  [private]\n  0 stars | 0 forks\n  u\n",
            GhCommand.FormatRepoView(doc.RootElement));
    }

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
