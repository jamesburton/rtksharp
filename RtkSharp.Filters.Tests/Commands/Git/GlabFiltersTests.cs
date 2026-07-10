using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RtkSharp.Filters.Commands.Git;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Git;

/// <summary>
/// Tests for <see cref="GlabFilters"/>. The argument-parsing, JSON-formatting, and markdown-filtering
/// helpers are pure functions ported from <c>src/cmds/git/glab_cmd.rs</c> — including the
/// fixture-based token-savings and format assertions, which load the same fixtures
/// (<c>tests/fixtures/glab_*</c> in the Rust tree) copied byte-identical into
/// <c>RtkSharp.Tests/Fixtures/glab/</c>. Moved from
/// <c>RtkSharp.Tests.Commands.Git.GlabCommandTests</c> when the underlying pure methods moved from
/// <c>RtkSharp.Commands.Git.GlabCommand</c> to <see cref="GlabFilters"/> (Task 4 of the
/// filters-library extraction). Argument-parsing helpers (<c>ExtractMrNumber</c>,
/// <c>OkConfirmation</c>, <c>ExtractIdentifierAndExtraArgs</c>, <c>ParseOptionalIdentifier</c>,
/// <c>HasOutputFlag</c>, <c>ShouldPassthroughView</c>) and dispatch tests were not moved — they remain
/// in the original <c>GlabCommandTests</c> alongside <c>GlabCommand</c>.
/// </summary>
public sealed class GlabFiltersTests
{
    private static string FixturesDir([CallerFilePath] string? thisFile = null) =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "RtkSharp.Tests", "Fixtures", "glab");

    // Normalize CRLF -> LF: git's autocrlf may rewrite the checked-in fixtures to CRLF on checkout.
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixturesDir(), name)).Replace("\r\n", "\n");

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ==================== state_icon ====================

    [Fact]
    public void StateIcon_Opened()
    {
        Assert.Equal("[open]", GlabFilters.StateIcon("opened", false));
        Assert.Equal("O", GlabFilters.StateIcon("opened", true));
    }

    [Fact]
    public void StateIcon_Merged()
    {
        Assert.Equal("[merged]", GlabFilters.StateIcon("merged", false));
        Assert.Equal("M", GlabFilters.StateIcon("merged", true));
    }

    [Fact]
    public void StateIcon_Closed()
    {
        Assert.Equal("[closed]", GlabFilters.StateIcon("closed", false));
        Assert.Equal("C", GlabFilters.StateIcon("closed", true));
    }

    [Fact]
    public void StateIcon_UnknownIsQuestionMarkInBothModes()
    {
        // Unlike gh's state_icon (which falls back to "[unknown]" in non-compact mode), glab's
        // fallback is the bare "?" in both compact and non-compact modes (glab_cmd.rs:115-131).
        Assert.Equal("?", GlabFilters.StateIcon("weird", false));
        Assert.Equal("?", GlabFilters.StateIcon("weird", true));
    }

    // ==================== pipeline_icon ====================

    [Fact]
    public void PipelineIcon_Success()
    {
        Assert.Equal("[ok]", GlabFilters.PipelineIcon("success", false));
        Assert.Equal("+", GlabFilters.PipelineIcon("success", true));
    }

    [Fact]
    public void PipelineIcon_Failed()
    {
        Assert.Equal("[fail]", GlabFilters.PipelineIcon("failed", false));
        Assert.Equal("x", GlabFilters.PipelineIcon("failed", true));
    }

    [Fact]
    public void PipelineIcon_Running()
    {
        Assert.Equal("[run]", GlabFilters.PipelineIcon("running", false));
        Assert.Equal("~", GlabFilters.PipelineIcon("running", true));
    }

    [Fact]
    public void PipelineIcon_PendingSplitsInNonCompactButSharesTildeInCompact()
    {
        // Asymmetry preserved from Rust: compact groups running/pending into "~"; non-compact
        // gives pending its own "[pend]" tag distinct from running's "[run]".
        Assert.Equal("[pend]", GlabFilters.PipelineIcon("pending", false));
        Assert.Equal("~", GlabFilters.PipelineIcon("pending", true));
    }

    [Fact]
    public void PipelineIcon_CanceledAndCancelledShareIcon()
    {
        Assert.Equal("[cancel]", GlabFilters.PipelineIcon("canceled", false));
        Assert.Equal("[cancel]", GlabFilters.PipelineIcon("cancelled", false));
        Assert.Equal("X", GlabFilters.PipelineIcon("canceled", true));
    }

    // ==================== filter_markdown_body ====================

    [Fact]
    public void FilterMarkdownBody_Empty() => Assert.Equal(string.Empty, GlabFilters.FilterMarkdownBody(string.Empty));

    [Fact]
    public void FilterMarkdownBody_HtmlComments()
    {
        var result = GlabFilters.FilterMarkdownBody("Hello\n<!-- comment -->\nWorld");
        Assert.DoesNotContain("<!--", result);
        Assert.Contains("Hello", result);
        Assert.Contains("World", result);
    }

    [Fact]
    public void FilterMarkdownBody_CodeBlockPreserved()
    {
        var result = GlabFilters.FilterMarkdownBody("Text\n```\n<!-- not stripped -->\n```\nAfter");
        Assert.Contains("<!-- not stripped -->", result);
        Assert.Contains("Text", result);
        Assert.Contains("After", result);
    }

    [Fact]
    public void FilterMarkdownBody_BlankLinesCollapse()
    {
        var result = GlabFilters.FilterMarkdownBody("Line 1\n\n\n\n\nLine 2");
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Contains("Line 1", result);
        Assert.Contains("Line 2", result);
    }

    [Fact]
    public void FilterMarkdownBody_BadgesRemoved()
    {
        const string input =
            "# Title\n[![CI](https://img.shields.io/badge.svg)](https://github.com/actions)\nText";
        var result = GlabFilters.FilterMarkdownBody(input);
        Assert.DoesNotContain("shields.io", result);
        Assert.Contains("# Title", result);
        Assert.Contains("Text", result);
    }

    [Fact]
    public void FilterMarkdownBody_MeaningfulContentPreserved()
    {
        const string input = "## Summary\n- Item 1\n- Item 2\n\n[Link](https://example.com)";
        var result = GlabFilters.FilterMarkdownBody(input);
        Assert.Contains("## Summary", result);
        Assert.Contains("- Item 1", result);
        Assert.Contains("[Link](https://example.com)", result);
    }

    // ==================== format_mr_list (fixture) ====================

    [Fact]
    public void MrList_TokenSavings()
    {
        var input = ReadFixture("glab_mr_list_raw.json");
        using var doc = JsonDocument.Parse(input);
        var output = GlabFilters.FormatMrList(doc.RootElement, false);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"MR list: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void MrList_Format()
    {
        var input = ReadFixture("glab_mr_list_raw.json");
        using var doc = JsonDocument.Parse(input);
        var output = GlabFilters.FormatMrList(doc.RootElement, false);
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
        var output = GlabFilters.FormatMrList(doc.RootElement, true);
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
        var output = GlabFilters.FormatIssueList(doc.RootElement, false);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"Issue list: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void IssueList_Format()
    {
        var input = ReadFixture("glab_issue_list_raw.json");
        using var doc = JsonDocument.Parse(input);
        var output = GlabFilters.FormatIssueList(doc.RootElement, false);
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
        Assert.Equal(string.Empty, GlabFilters.FormatMrList(doc.RootElement, false));
    }

    [Fact]
    public void FormatIssueList_NonArrayReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Equal(string.Empty, GlabFilters.FormatIssueList(doc.RootElement, false));
    }

    // ==================== format_release_list (fixture + inline) ====================

    [Fact]
    public void FormatReleaseList_Fixture()
    {
        var input = ReadFixture("glab_release_list_raw.txt");
        var output = GlabFilters.FormatReleaseList(input);
        Assert.NotNull(output);
        Assert.StartsWith("Releases\n", output);
        Assert.Contains("v3.2.1", output);
        Assert.Contains("about 2 days ago", output);
    }

    [Fact]
    public void FormatReleaseList_TokenSavings()
    {
        var input = ReadFixture("glab_release_list_raw.txt");
        var output = GlabFilters.FormatReleaseList(input);
        Assert.NotNull(output);

        var savings = 100.0 - (CountTokens(output!) / (double)CountTokens(input) * 100.0);
        // Release list text is already compact (tab-separated); savings are modest.
        Assert.True(savings >= 20.0, $"Release list: expected >=20% savings, got {savings:F1}%");
    }

    [Fact]
    public void FormatReleaseList_Empty()
    {
        const string input = "No releases available on owner/repo.\nName\tTag\tCreated\n";
        Assert.Null(GlabFilters.FormatReleaseList(input));
    }

    [Fact]
    public void FormatReleaseList_NameDiffersFromTag()
    {
        const string input = "Showing 1 releases\n\nName\tTag\tCreated\nMy Release\tv1.0.0\t2 days ago\n";
        var output = GlabFilters.FormatReleaseList(input);
        Assert.NotNull(output);
        Assert.Contains("My Release [v1.0.0]", output);
    }

    // ==================== filter_ci_trace (fixture) ====================

    [Fact]
    public void FilterCiTrace_StripsBoilerplate()
    {
        var input = ReadFixture("glab_ci_trace_raw.txt");
        var output = GlabFilters.FilterCiTrace(input);

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
        var output = GlabFilters.FilterCiTrace(input);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 30.0, $"CI trace: expected >=30% savings, got {savings:F1}%");
    }

    // ==================== filter_release_view (fixture + inline) ====================

    [Fact]
    public void FilterReleaseView_StripsSources()
    {
        var input = ReadFixture("glab_release_view_raw.txt");
        var output = GlabFilters.FilterReleaseView(input);

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
        var output = GlabFilters.FilterReleaseView(input);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 20.0, $"Release view: expected >=20% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterReleaseView_NoSourcesSection()
    {
        const string input = "# Release 1.0\n\nJust a simple changelog entry.\n";
        var output = GlabFilters.FilterReleaseView(input);
        Assert.Contains("Release 1.0", output);
        Assert.Contains("changelog entry", output);
    }

    // ==================== further edge cases ====================

    [Fact]
    public void FormatMrList_EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No Merge Requests\n", GlabFilters.FormatMrList(doc.RootElement, false));
    }

    [Fact]
    public void FormatMrList_EmptyArrayUltraCompact()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No MRs\n", GlabFilters.FormatMrList(doc.RootElement, true));
    }

    [Fact]
    public void FormatIssueList_EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No Issues\n", GlabFilters.FormatIssueList(doc.RootElement, false));
    }

    [Fact]
    public void FormatCiList_EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Equal("No Pipelines\n", GlabFilters.FormatCiList(doc.RootElement, false));
    }

    [Fact]
    public void FormatMrView_NullNestedFields()
    {
        // Defensive: if the GitLab API omits or nulls out nested fields, formatters must render
        // placeholders without throwing.
        const string json =
            """{"iid":42,"title":"Edge","state":"opened","author":null,"web_url":"","merge_status":"unknown","description":null}""";
        using var doc = JsonDocument.Parse(json);
        var output = GlabFilters.FormatMrView(doc.RootElement, false);
        Assert.Contains("MR !42: Edge", output);
        Assert.Contains("???", output); // author fallback
    }

    [Fact]
    public void FormatIssueView_MissingDescription()
    {
        const string json =
            """{"iid":10,"title":"X","state":"closed","author":{"username":"u"},"web_url":"http://e","description":null}""";
        using var doc = JsonDocument.Parse(json);
        var output = GlabFilters.FormatIssueView(doc.RootElement);
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
        var output = GlabFilters.FormatCiStatus(raw, false);
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
        var output = GlabFilters.FormatMrView(doc.RootElement, false);
        Assert.Contains("feat/widget -> main", output);
    }

    [Fact]
    public void FormatMrView_Labels()
    {
        using var doc = JsonDocument.Parse(MrViewFull);
        var output = GlabFilters.FormatMrView(doc.RootElement, false);
        Assert.Contains("Labels: enhancement, cli", output);
    }

    [Fact]
    public void FormatMrView_Reviewers()
    {
        using var doc = JsonDocument.Parse(MrViewFull);
        var output = GlabFilters.FormatMrView(doc.RootElement, false);
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
        var output = GlabFilters.FormatMrView(doc.RootElement, false);
        Assert.DoesNotContain("Labels:", output);
        Assert.DoesNotContain("Reviewers:", output);
        // Branches line still present.
        Assert.Contains("a -> b", output);
    }

    [Fact]
    public void FormatMrView_MergeableTextTag()
    {
        using var doc = JsonDocument.Parse(MrViewFull);
        var output = GlabFilters.FormatMrView(doc.RootElement, false);
        // merge_status="can_be_merged" -> "[ok]" (text tag, no emoji).
        Assert.Contains("opened | [ok]", output);
        // And no emoji anywhere in the rendered output.
        Assert.DoesNotContain('✅', output);
        Assert.DoesNotContain('❌', output);
        Assert.DoesNotContain('✓', output);
        Assert.DoesNotContain('✗', output);
    }
}
