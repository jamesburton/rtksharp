using System.Text;
using RtkSharp.Filters.Commands.Git;

namespace RtkSharp.Filters.Tests.Commands.Git;

/// <summary>
/// Tests for <see cref="GitFilters"/>, moved from <c>RtkSharp.Tests.Commands.GitCommandTests</c>
/// (Phase 7a Tasks 1-3) when the pure filter methods were extracted out of
/// <c>RtkSharp.Commands.Git.GitCommand</c> into this library (filters-library plan, Task 4). Only the
/// class qualifier changed (<c>GitCommand.X</c> → <c>GitFilters.X</c>); assertions, fixtures, and
/// comments are unchanged. Filter expectations are oracle-derived: the format-faithful log fixtures
/// below were confirmed byte-identical against the reference <c>rtk.exe</c> for the equivalent live
/// invocations. Integration-style tests that exercise <c>GitCommand.RunAsync</c> (not a moved static
/// method directly) remain in the original <c>GitCommandTests.cs</c>.
/// </summary>
public sealed class GitFiltersTests
{
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
        var filtered = GitFilters.FilterLogOutput(RawLogTwoCommits, limit: 2, userSetLimit: true, userFormat: false);

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

        var filtered = GitFilters.FilterLogOutput(raw, limit: 10, userSetLimit: false, userFormat: false);

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
        var filtered = GitFilters.FilterLogOutput(raw, limit: 2, userSetLimit: true, userFormat: true);
        Assert.Equal("abc1234 first\ndef5678 second\nghi9012 third", filtered);

        var capped = GitFilters.FilterLogOutput(raw, limit: 2, userSetLimit: false, userFormat: true);
        Assert.Equal("abc1234 first\ndef5678 second", capped);
    }

    [Fact]
    public void TruncateLine_ShortUnchanged_LongGetsEllipsis()
    {
        Assert.Equal("hello", GitFilters.TruncateLine("hello", 80));

        var truncated = GitFilters.TruncateLine(new string('a', 100), 80);
        Assert.Equal(80, truncated.Length);
        Assert.EndsWith("...", truncated);
    }

    [Fact]
    public void TruncateLine_CountsUnicodeScalars_NotUtf16()
    {
        // 10 astral emoji (each 2 UTF-16 units) — width 8 must slice on scalar boundaries.
        var line = string.Concat(Enumerable.Repeat("\U0001F600", 10));
        var truncated = GitFilters.TruncateLine(line, 8);
        Assert.EndsWith("...", truncated);
        // 5 kept emoji (width-3) + "..." → never a split surrogate.
        Assert.Equal(5, truncated[..^3].EnumerateRunes().Count());
    }

    // ==================== status filter ====================

    [Fact]
    public void FormatStatusOutput_RewritesBranchHeader()
    {
        var porcelain = "## port/dotnet-phase7a\n?? .docs/\n?? docs/PLANS.md\n";
        var formatted = GitFilters.FormatStatusOutput(porcelain);
        Assert.Equal("* port/dotnet-phase7a\n?? .docs/\n?? docs/PLANS.md", formatted);
    }

    [Fact]
    public void FormatStatusOutput_BranchOnly_AppendsCleanMarker() =>
        Assert.Equal("* main\nclean — nothing to commit", GitFilters.FormatStatusOutput("## main\n"));

    [Fact]
    public void FormatStatusOutput_Empty_CleanWorkingTree() =>
        Assert.Equal("Clean working tree", GitFilters.FormatStatusOutput(""));

    [Fact]
    public void FormatStatusOutputDetached_SubstitutesRef()
    {
        var porcelain = "## HEAD (no branch)\n M src/main.rs\n";
        var formatted = GitFilters.FormatStatusOutputDetached(porcelain, "HEAD detached at abc1234");
        Assert.Equal("* HEAD detached at abc1234\n M src/main.rs", formatted);
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
        var filtered = GitFilters.FilterStatusWithArgs(raw);
        Assert.Equal("On branch main\nUntracked files:\n\tnewfile.txt", filtered);
    }

    [Fact]
    public void FilterStatusWithArgs_CleanTree_ShortCircuits()
    {
        var raw = "On branch main\nnothing to commit, working tree clean\n";
        Assert.Equal("On branch main\nnothing to commit, working tree clean", GitFilters.FilterStatusWithArgs(raw));
    }

    [Fact]
    public void FilterStatusWithArgs_AllFiltered_ReturnsOk() =>
        Assert.Equal("ok", GitFilters.FilterStatusWithArgs("\n  (use \"git add foo\")\n"));

    // ==================== compact diff (Task 2) ====================

    // Real `git diff 01d5d1f 4e045a7 -- .gitignore` for this repo (deterministic — a diff carries no
    // timestamps). Captured raw; the expected compaction below was confirmed byte-identical against
    // `rtk.exe git diff` for the equivalent live invocation.
    private const string RawSingleFileDiff =
        "diff --git a/.gitignore b/.gitignore\n"
        + "index 947ca4f..a8be435 100644\n"
        + "--- a/.gitignore\n"
        + "+++ b/.gitignore\n"
        + "@@ -1,6 +1,9 @@\n"
        + " # Build\n"
        + " /target\n"
        + " \n"
        + "+# .NET pack/publish output (RtkSharp port)\n"
        + "+.artifacts/\n"
        + "+\n"
        + " # Environment & Secrets\n"
        + " .env\n"
        + " .env.*\n";

    [Fact]
    public void CompactDiff_SingleFile_MatchesOracleShape()
    {
        var compacted = GitFilters.CompactDiff(RawSingleFileDiff, 500);

        // Leading "\n" (blank line before the filename), hunk header, +/- lines, context kept only
        // after the first change, and a trailing per-file "+A -R" tally.
        var expected =
            "\n.gitignore\n"
            + "  @@ -1,6 +1,9 @@\n"
            + "  +# .NET pack/publish output (RtkSharp port)\n"
            + "  +.artifacts/\n"
            + "  +\n"
            + "   # Environment & Secrets\n"
            + "   .env\n"
            + "   .env.*\n"
            + "  +3 -0";
        Assert.Equal(expected, compacted);
    }

    [Fact]
    public void CompactDiff_DropsLeadingContextBeforeFirstChange()
    {
        // The three context lines before the first "+" (# Build, /target, blank) must not appear:
        // context is only retained once a change has been shown in the hunk.
        var compacted = GitFilters.CompactDiff(RawSingleFileDiff, 500);
        Assert.DoesNotContain("# Build", compacted);
        Assert.DoesNotContain("/target", compacted);
    }

    [Fact]
    public void CompactDiff_HunkOverflow_TruncatesAndAppendsFooter()
    {
        // 150 added lines in one hunk; only the first 100 are shown, the remaining 50 counted.
        var sb = new StringBuilder("diff --git a/big.txt b/big.txt\n@@ -0,0 +1,150 @@\n");
        for (var i = 0; i < 150; i++)
        {
            sb.Append("+line ").Append(i).Append('\n');
        }

        var compacted = GitFilters.CompactDiff(sb.ToString(), 500);

        Assert.Contains("+line 99", compacted);
        Assert.DoesNotContain("+line 100", compacted);
        Assert.Contains("  ... (50 lines truncated)", compacted);
        Assert.Contains("[full diff: rtk git diff --no-compact]", compacted);
        Assert.Contains("  +150 -0", compacted);
    }

    [Fact]
    public void CompactDiff_MaxLines_StopsEmittingAndFlagsTruncation()
    {
        var sb = new StringBuilder("diff --git a/big.txt b/big.txt\n@@ -0,0 +1,20 @@\n");
        for (var i = 0; i < 20; i++)
        {
            sb.Append("+line ").Append(i).Append('\n');
        }

        // maxLines=5 → the overall budget trips before the hunk is exhausted.
        var compacted = GitFilters.CompactDiff(sb.ToString(), 5);
        Assert.Contains("... (more changes truncated)", compacted);
        Assert.Contains("[full diff: rtk git diff --no-compact]", compacted);
    }

    [Fact]
    public void CompactDiff_MissingBSlash_FallsBackToUnknownFile()
    {
        // A malformed header with no " b/" segment maps the file name to "unknown".
        var diff = "diff --git weird\n@@ -1 +1 @@\n-old\n+new\n";
        var compacted = GitFilters.CompactDiff(diff, 500);
        Assert.Contains("\nunknown", compacted);
    }

    // ==================== add (Task 3) ====================

    [Theory]
    [InlineData(" README.md | 1 +\n a.txt     | 3 +++\n 2 files changed, 4 insertions(+)\n 2 files changed, 4 insertions(+)\n", "ok 2 files changed, 4 insertions(+)")]
    [InlineData(" a.txt | 1 +\n 1 file changed, 1 insertion(+)\n", "ok 1 file changed, 1 insertion(+)")]
    [InlineData("", "")]
    [InlineData("   \n  \n", "")]
    public void FormatAddSummary_MatchesOracle(string cachedStat, string expected) =>
        Assert.Equal(expected, GitFilters.FormatAddSummary(cachedStat));

    // ==================== commit (Task 3) ====================

    [Theory]
    [InlineData("[main (root-commit) 72749d8] initial commit", "ok 72749d8")]
    [InlineData("[main e8f957d] second commit", "ok e8f957d")]
    [InlineData("[feature/x abcdef1234567] msg", "ok abcdef1")]
    [InlineData("no bracket here", "ok")]
    [InlineData("[main abc] short hash", "ok")]
    public void ParseCommitOutput_MatchesRust(string line, string expected) =>
        Assert.Equal(expected, GitFilters.ParseCommitOutput(line));

    // ==================== push (Task 3) ====================

    [Fact]
    public void FilterPushOutput_NewBranch_KeepsRefsAndAppendsOkRef()
    {
        const string stderr =
            "To ../gitbare\n"
            + " * [new branch]      main -> main\n"
            + "branch 'main' set up to track 'origin/main'.\n";
        var filtered = GitFilters.FilterPushOutput(string.Empty, stderr, 0);
        Assert.Equal(
            "To ../gitbare\n"
            + " * [new branch]      main -> main\n"
            + "branch 'main' set up to track 'origin/main'.\n"
            + "ok main\n",
            filtered);
    }

    [Fact]
    public void FilterPushOutput_UpToDate_ReportsUpToDate() =>
        Assert.Equal(
            "Everything up-to-date\nok (up-to-date)\n",
            GitFilters.FilterPushOutput(string.Empty, "Everything up-to-date\n", 0));

    [Fact]
    public void FilterPushOutput_StripsProgressNoise()
    {
        const string stderr =
            "Enumerating objects: 5, done.\n"
            + "Counting objects: 100% (5/5), done.\n"
            + "Writing objects: 100% (3/3), 300 bytes | 300.00 KiB/s, done.\n"
            + "Total 3 (delta 0), reused 0 (delta 0)\n"
            + "To origin\n"
            + "   abc1234..def5678  main -> main\n";
        var filtered = GitFilters.FilterPushOutput(string.Empty, stderr, 0);
        Assert.DoesNotContain("Enumerating", filtered);
        Assert.DoesNotContain("Counting", filtered);
        Assert.DoesNotContain("Writing objects", filtered);
        Assert.DoesNotContain("Total 3", filtered);
        Assert.Equal("To origin\n   abc1234..def5678  main -> main\nok main\n", filtered);
    }

    [Fact]
    public void FilterPushOutput_NonZeroExit_OmitsSummary() =>
        Assert.Equal(
            "error: failed to push\n",
            GitFilters.FilterPushOutput(string.Empty, "error: failed to push\n", 1));

    // ==================== pull (Task 3) ====================

    [Theory]
    [InlineData("Already up to date.\n", "ok (up-to-date)")]
    [InlineData("Already up-to-date.\n", "ok (up-to-date)")]
    [InlineData("Updating a..b\nFast-forward\n 3 files changed, 10 insertions(+), 2 deletions(-)\n", "ok 3 files +10 -2")]
    [InlineData("Updating a..b\n 1 file changed, 5 insertions(+)\n", "ok 1 files +5 -0")]
    [InlineData("Merge made by the 'ort' strategy.\n", "ok")]
    public void FormatPullSummary_MatchesRust(string stdout, string expected) =>
        Assert.Equal(expected, GitFilters.FormatPullSummary(stdout));

    // ==================== branch (Task 3) ====================

    [Fact]
    public void FilterBranchOutput_CurrentAndLocal_NoRemoteOnly() =>
        Assert.Equal(
            "* main\n  develop",
            GitFilters.FilterBranchOutput("  develop\n* main\n  remotes/origin/HEAD -> origin/main\n  remotes/origin/main\n"));

    [Fact]
    public void FilterBranchOutput_RemoteOnly_Sectioned() =>
        Assert.Equal(
            "* main\n  remote-only (1):\n    feature-x",
            GitFilters.FilterBranchOutput("* main\n  remotes/origin/feature-x\n"));

    [Fact]
    public void FilterBranchOutput_RemoteOnly_CapsAtTenWithOverflow()
    {
        var sb = new StringBuilder("* main\n");
        for (var i = 0; i < 13; i++)
        {
            sb.Append("  remotes/origin/b").Append(i).Append('\n');
        }

        var filtered = GitFilters.FilterBranchOutput(sb.ToString());
        Assert.Contains("  remote-only (13):", filtered);
        Assert.Contains("    b0", filtered);
        Assert.Contains("    b9", filtered);
        Assert.DoesNotContain("    b10", filtered);
        Assert.Contains("    ... +3 more", filtered);
    }

    // ==================== fetch (Task 3) ====================

    [Theory]
    [InlineData("", "ok fetched")]
    [InlineData("From origin\n   a..b  main -> origin/main\n", "ok fetched (1 new refs)")]
    [InlineData("From origin\n * [new tag]         v1.0 -> v1.0\n", "ok fetched (1 new refs)")]
    public void FormatFetchSummary_MatchesRust(string stderr, string expected) =>
        Assert.Equal(expected, GitFilters.FormatFetchSummary(stderr));

    // ==================== stash (Task 3) ====================

    [Theory]
    [InlineData(null, "Saved working directory ...", "", "ok stashed")]
    [InlineData("push", "Saved working directory ...", "", "ok stashed")]
    [InlineData("save", "Saved working directory ...", "", "ok stashed")]
    [InlineData(null, "", "No local changes to save\n", "No local changes to save")]
    [InlineData("pop", "Dropped refs/stash@{0}", "", "ok stash pop")]
    [InlineData("apply", "", "", "ok stash apply")]
    [InlineData("drop", "Dropped ...", "", "ok stash drop")]
    public void FormatStashMessage_MatchesRust(string? sub, string stdout, string stderr, string expected) =>
        Assert.Equal(expected, GitFilters.FormatStashMessage(sub, stdout, stderr));

    [Theory]
    [InlineData("stash@{0}: WIP on main: b375c31 second\n", "stash@{0}: b375c31 second")]
    [InlineData("stash@{0}: On main: wip changes\n", "stash@{0}: wip changes")]
    [InlineData("no-colon-space-here\n", "no-colon-space-here")]
    public void FilterStashList_MatchesRust(string output, string expected) =>
        Assert.Equal(expected, GitFilters.FilterStashList(output));

    // ==================== worktree (Task 3) ====================

    [Fact]
    public void FilterWorktreeList_NormalizesColumns_NonHomePathUnchanged() =>
        Assert.Equal(
            "/some/path/repo abc1234 [main]",
            GitFilters.FilterWorktreeList("/some/path/repo  abc1234 [main]\n"));

    [Fact]
    public void FilterWorktreeList_HomePrefix_RewrittenToTilde()
    {
        // Construct a path under the real home directory (same separators) to exercise the ~ rewrite.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var line = home + "/wt abc1234 [feature]";
        Assert.Equal("~/wt abc1234 [feature]", GitFilters.FilterWorktreeList(line));
    }

    [Fact]
    public void FilterWorktreeList_ShortLine_PassedThrough() =>
        Assert.Equal("/bare/repo (bare)", GitFilters.FilterWorktreeList("/bare/repo (bare)\n"));
}
