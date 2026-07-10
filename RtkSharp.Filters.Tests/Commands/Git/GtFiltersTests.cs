using System.Reflection;
using System.Text;
using RtkSharp.Filters.Commands.Git;

namespace RtkSharp.Filters.Tests.Commands.Git;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/git/gt_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>:
/// <c>filter_gt_log_entries</c>, <c>filter_gt_submit</c>, <c>filter_gt_sync</c>,
/// <c>filter_gt_restack</c>, <c>filter_gt_create</c>, <c>is_graph_node</c>, and
/// <c>extract_branch_name</c>, including the token-savings assertions. Moved from
/// <c>RtkSharp.Tests.Commands.Git.GtCommandTests</c> when the underlying methods moved from
/// <c>RtkSharp.Commands.Git.GtCommand</c> to <see cref="GtFilters"/> (Task 4 of the filters-library
/// extraction). <c>GtCommandTests.cs</c> contained no other tests (no dispatch/<c>RunAsync</c>
/// coverage exists for <c>gt</c> yet), so it was deleted rather than left as an empty class.
/// </summary>
/// <remarks>
/// <c>FilterGtLogEntries</c>/<c>FilterGtSubmit</c>/<c>FilterGtSync</c>/<c>FilterGtRestack</c>/
/// <c>FilterGtCreate</c> are <c>public</c> on <see cref="GtFilters"/> (this extraction's public
/// interface), so they're called directly. <c>IsGraphNode</c> and <c>ExtractBranchName</c> stayed
/// <c>private</c> on <see cref="GtFilters"/> — mirroring Rust's private <c>fn</c>s and the pre-move
/// <c>GtCommand</c> visibility — so they're still reached via reflection, matching the original
/// test's documented rationale ("rather than widening the production API's visibility just to satisfy
/// the test project"), just re-targeted at <c>RtkSharp.Filters.Commands.Git.GtFilters, RtkSharp.Filters</c>.
/// </remarks>
public sealed class GtFiltersTests
{
    private static readonly Type GtFiltersType =
        Type.GetType("RtkSharp.Filters.Commands.Git.GtFilters, RtkSharp.Filters")
        ?? throw new InvalidOperationException("RtkSharp.Filters.Commands.Git.GtFilters type not found.");

    private static object? InvokeMember(string methodName, params object[] args)
    {
        var method = GtFiltersType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException($"GtFilters.{methodName} not found.");
        return method.Invoke(null, args);
    }

    private static bool IsGraphNode(string line) => (bool)InvokeMember("IsGraphNode", line)!;

    private static string ExtractBranchName(string line) => (string)InvokeMember("ExtractBranchName", line)!;

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ===================== filter_gt_log_entries =====================

    [Fact]
    public void FilterGtLogEntries_ExactFormat()
    {
        const string input = "◉  abc1234 feat/add-auth 2d ago\n" +
            "│  feat(auth): add login endpoint\n" +
            "│\n" +
            "◉  def5678 feat/add-db 3d ago user@example.com\n" +
            "│  feat(db): add migration system\n" +
            "│\n" +
            "◉  ghi9012 main 5d ago admin@corp.io\n" +
            "│  chore: update dependencies\n" +
            "~\n";

        var output = GtFilters.FilterGtLogEntries(input);

        const string expected = "◉  abc1234 feat/add-auth 2d ago\n" +
            "│  feat(auth): add login endpoint\n" +
            "│\n" +
            "◉  def5678 feat/add-db 3d ago\n" +
            "│  feat(db): add migration system\n" +
            "│\n" +
            "◉  ghi9012 main 5d ago\n" +
            "│  chore: update dependencies\n" +
            "~";

        Assert.Equal(expected, output);
    }

    [Fact]
    public void FilterGtLogEntries_Truncation()
    {
        var input = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            input.Append($"◉  hash{i:D2} branch-{i} 1d ago\n│  commit message {i}\n│\n");
        }

        input.Append("~\n");

        var output = GtFilters.FilterGtLogEntries(input.ToString());
        Assert.Contains("... +", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGtLogEntries_Empty()
    {
        Assert.Equal(string.Empty, GtFilters.FilterGtLogEntries(""));
        Assert.Equal(string.Empty, GtFilters.FilterGtLogEntries("  "));
    }

    [Fact]
    public void FilterGtLogEntries_TokenSavings()
    {
        var input = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            input.Append(
                $"◉  hash{i:D2}abc feat/feature-{i} {i + 1}d ago developer{i}@longcompany.example.com\n" +
                $"│  feat(module-{i}): implement feature {i} with detailed description of changes\n│\n");
        }

        input.Append("~\n");

        var output = GtFilters.FilterGtLogEntries(input.ToString());
        var inputTokens = CountTokens(input.ToString());
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"gt log filter: expected >=60% savings, got {savings:F1}% ({inputTokens} -> {outputTokens} tokens)");
    }

    [Fact]
    public void FilterGtLogEntries_Long()
    {
        const string input = "◉  abc1234 feat/add-auth\n" +
            "│  Author: Dev User <dev@example.com>\n" +
            "│  Date: 2026-02-25 10:30:00 -0800\n" +
            "│\n" +
            "│  feat(auth): add login endpoint with OAuth2 support\n" +
            "│  and session management for web clients\n" +
            "│\n" +
            "◉  def5678 feat/add-db\n" +
            "│  Author: Other Dev <other@example.com>\n" +
            "│  Date: 2026-02-24 14:00:00 -0800\n" +
            "│\n" +
            "│  feat(db): add migration system\n" +
            "~\n";

        var output = GtFilters.FilterGtLogEntries(input);

        Assert.Contains("abc1234", output, StringComparison.Ordinal);
        Assert.DoesNotContain("dev@example.com", output, StringComparison.Ordinal);
        Assert.DoesNotContain("other@example.com", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGtLogEntries_PreStrippedInput()
    {
        const string input = "◉  abc1234 feat/x 1d ago user@test.com\n│  message\n~\n";
        var output = GtFilters.FilterGtLogEntries(input);

        Assert.Contains("abc1234", output, StringComparison.Ordinal);
        Assert.DoesNotContain("user@test.com", output, StringComparison.Ordinal);
    }

    // ===================== filter_gt_submit =====================

    [Fact]
    public void FilterGtSubmit_ExactFormat()
    {
        const string input = "Pushed branch feat/add-auth\n" +
            "Created pull request #42 for feat/add-auth\n" +
            "Pushed branch feat/add-db\n" +
            "Updated pull request #40 for feat/add-db\n";

        var output = GtFilters.FilterGtSubmit(input);

        const string expected = "pushed feat/add-auth, feat/add-db\n" +
            "created PR #42 feat/add-auth\n" +
            "updated PR #40 feat/add-db";

        Assert.Equal(expected, output);
    }

    [Fact]
    public void FilterGtSubmit_Empty()
    {
        Assert.Equal(string.Empty, GtFilters.FilterGtSubmit(""));
    }

    [Fact]
    public void FilterGtSubmit_WithUrls()
    {
        const string input = "Created pull request #42 for feat/add-auth: https://github.com/org/repo/pull/42\n";
        var output = GtFilters.FilterGtSubmit(input);

        Assert.Contains("PR #42", output, StringComparison.Ordinal);
        Assert.Contains("feat/add-auth", output, StringComparison.Ordinal);
        Assert.Contains("https://github.com/org/repo/pull/42", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGtSubmit_TokenSavings()
    {
        const string input = "\n" +
            "  ✅  Pushing to remote...\n" +
            "  Enumerating objects: 15, done.\n" +
            "  Counting objects: 100% (15/15), done.\n" +
            "  Delta compression using up to 10 threads\n" +
            "  Compressing objects: 100% (8/8), done.\n" +
            "  Writing objects: 100% (10/10), 2.50 KiB | 2.50 MiB/s, done.\n" +
            "  Total 10 (delta 5), reused 0 (delta 0), pack-reused 0\n" +
            "  Pushed branch feat/add-auth to origin\n" +
            "  Creating pull request for feat/add-auth...\n" +
            "  Created pull request #42 for feat/add-auth: https://github.com/org/repo/pull/42\n" +
            "  ✅  Pushing to remote...\n" +
            "  Enumerating objects: 8, done.\n" +
            "  Counting objects: 100% (8/8), done.\n" +
            "  Delta compression using up to 10 threads\n" +
            "  Compressing objects: 100% (4/4), done.\n" +
            "  Writing objects: 100% (5/5), 1.20 KiB | 1.20 MiB/s, done.\n" +
            "  Total 5 (delta 3), reused 0 (delta 0), pack-reused 0\n" +
            "  Pushed branch feat/add-db to origin\n" +
            "  Updating pull request for feat/add-db...\n" +
            "  Updated pull request #40 for feat/add-db: https://github.com/org/repo/pull/40\n" +
            "  ✅  Pushing to remote...\n" +
            "  Enumerating objects: 5, done.\n" +
            "  Counting objects: 100% (5/5), done.\n" +
            "  Delta compression using up to 10 threads\n" +
            "  Compressing objects: 100% (3/3), done.\n" +
            "  Writing objects: 100% (3/3), 890 bytes | 890.00 KiB/s, done.\n" +
            "  Total 3 (delta 2), reused 0 (delta 0), pack-reused 0\n" +
            "  Pushed branch fix/parsing to origin\n" +
            "  All branches submitted successfully!\n";

        var output = GtFilters.FilterGtSubmit(input);
        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"gt submit filter: expected >=60% savings, got {savings:F1}% ({inputTokens} -> {outputTokens} tokens)");
    }

    // ===================== filter_gt_sync =====================

    [Fact]
    public void FilterGtSync_ExactFormat()
    {
        const string input = "Synced with remote\n" +
            "Deleted branch feat/merged-feature\n" +
            "Deleted branch fix/old-hotfix\n";

        var output = GtFilters.FilterGtSync(input);

        Assert.Equal("ok sync: 1 synced, 2 deleted (feat/merged-feature, fix/old-hotfix)", output);
    }

    [Fact]
    public void FilterGtSync_Basic()
    {
        const string input = "Synced with remote\n" +
            "Deleted branch feat/merged-feature\n" +
            "Deleted branch fix/old-hotfix\n";

        var output = GtFilters.FilterGtSync(input);

        Assert.Contains("ok sync", output, StringComparison.Ordinal);
        Assert.Contains("synced", output, StringComparison.Ordinal);
        Assert.Contains("deleted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGtSync_Empty()
    {
        Assert.Equal(string.Empty, GtFilters.FilterGtSync(""));
    }

    [Fact]
    public void FilterGtSync_NoDeletes()
    {
        const string input = "Synced with remote\n";
        var output = GtFilters.FilterGtSync(input);

        Assert.Contains("ok sync", output, StringComparison.Ordinal);
        Assert.Contains("synced", output, StringComparison.Ordinal);
        Assert.DoesNotContain("deleted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGtSync_TokenSavings()
    {
        const string input = "\n" +
            "  ✅ Syncing with remote...\n" +
            "  Pulling latest changes from main...\n" +
            "  Successfully pulled 5 new commits\n" +
            "  Synced branch feat/add-auth with remote\n" +
            "  Synced branch feat/add-db with remote\n" +
            "  Branch feat/merged-feature has been merged\n" +
            "  Deleted branch feat/merged-feature\n" +
            "  Branch fix/old-hotfix has been merged\n" +
            "  Deleted branch fix/old-hotfix\n" +
            "  All branches synced!\n";

        var output = GtFilters.FilterGtSync(input);
        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"gt sync filter: expected >=60% savings, got {savings:F1}% ({inputTokens} -> {outputTokens} tokens)");
    }

    // ===================== filter_gt_restack =====================

    [Fact]
    public void FilterGtRestack_ExactFormat()
    {
        const string input = "Restacked branch feat/add-auth on main\n" +
            "Restacked branch feat/add-db on feat/add-auth\n" +
            "Restacked branch fix/parsing on feat/add-db\n";

        var output = GtFilters.FilterGtRestack(input);

        Assert.Equal("ok restacked 3 branches", output);
    }

    [Fact]
    public void FilterGtRestack_Basic()
    {
        const string input = "Restacked branch feat/add-auth on main\n" +
            "Restacked branch feat/add-db on feat/add-auth\n" +
            "Restacked branch fix/parsing on feat/add-db\n";

        var output = GtFilters.FilterGtRestack(input);

        Assert.Contains("ok restacked", output, StringComparison.Ordinal);
        Assert.Contains("3 branches", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGtRestack_Empty()
    {
        Assert.Equal(string.Empty, GtFilters.FilterGtRestack(""));
    }

    [Fact]
    public void FilterGtRestack_TokenSavings()
    {
        const string input = "\n" +
            "  ✅ Restacking branches...\n" +
            "  Restacked branch feat/add-auth on top of main\n" +
            "  Successfully rebased feat/add-auth (3 commits)\n" +
            "  Restacked branch feat/add-db on top of feat/add-auth\n" +
            "  Successfully rebased feat/add-db (2 commits)\n" +
            "  Restacked branch fix/parsing on top of feat/add-db\n" +
            "  Successfully rebased fix/parsing (1 commit)\n" +
            "  All branches restacked!\n";

        var output = GtFilters.FilterGtRestack(input);
        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"gt restack filter: expected >=60% savings, got {savings:F1}%");
    }

    // ===================== filter_gt_create =====================

    [Fact]
    public void FilterGtCreate_ExactFormat()
    {
        const string input = "Created branch feat/new-feature\n";
        var output = GtFilters.FilterGtCreate(input);

        Assert.Equal("ok created feat/new-feature", output);
    }

    [Fact]
    public void FilterGtCreate_Empty()
    {
        Assert.Equal(string.Empty, GtFilters.FilterGtCreate(""));
    }

    [Fact]
    public void FilterGtCreate_NoBranchName()
    {
        const string input = "Some unexpected output\n";
        var output = GtFilters.FilterGtCreate(input);

        Assert.StartsWith("ok created", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGtCreate_TokenSavings()
    {
        const string input = "\n" +
            "  ✅ Creating new branch...\n" +
            "  Checking out from feat/add-auth...\n" +
            "  Created branch feat/new-feature from feat/add-auth\n" +
            "  Tracking branch set up to follow feat/add-auth\n" +
            "  Branch feat/new-feature is ready for development\n";

        var output = GtFilters.FilterGtCreate(input);
        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"gt create filter: expected >=60% savings, got {savings:F1}% ({inputTokens} -> {outputTokens} tokens)");
    }

    // ===================== is_graph_node =====================

    [Fact]
    public void IsGraphNode_RecognizesAllMarkers()
    {
        Assert.True(IsGraphNode("◉  abc1234 main"));
        Assert.True(IsGraphNode("○  def5678 feat/x"));
        Assert.True(IsGraphNode("@  ghi9012 (current)"));
        Assert.True(IsGraphNode("*  jkl3456 branch"));
        Assert.True(IsGraphNode("│ ◉  nested node"));
        Assert.False(IsGraphNode("│  just a message line"));
        Assert.False(IsGraphNode("~"));
    }

    // ===================== extract_branch_name =====================

    [Fact]
    public void ExtractBranchName_ParsesCreatedPushedAndSpecialChars()
    {
        Assert.Equal("feat/new-feature", ExtractBranchName("Created branch feat/new-feature"));
        Assert.Equal("fix/bug-123", ExtractBranchName("Pushed branch fix/bug-123"));
        Assert.Equal("feat/auth+session", ExtractBranchName("Pushed branch feat/auth+session"));
        Assert.Equal("user@fix", ExtractBranchName("Created branch user@fix"));
        Assert.Equal(string.Empty, ExtractBranchName("no branch here"));
    }
}
