using System.Collections.Generic;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="FindFilters.FormatFindResults"/>, the pure directory-grouping/summary
/// logic extracted from <c>RtkSharp.Commands.System.FindCommand.Run</c> (Task 14 of the
/// filters-library extraction, Step 2 — an atypical extraction: the filesystem walk itself stays
/// in <c>FindCommand</c> since it requires real I/O, but the grouping/capping/extension-summary
/// logic over the resulting path list is pure and testable directly against a synthetic path
/// list, without needing a real temp directory tree). These are new tests: no prior test exercised
/// this exact boundary in isolation (the old <c>FindCommandTests.Run_*</c> suite drives the whole
/// walk-then-format pipeline against real temp trees and remains in place, unchanged, in
/// <c>RtkSharp.Tests.Commands.FindCommandTests</c>).
/// </summary>
public sealed class FindFiltersTests
{
    private static FindArgs Args(
        string pattern = "*", string path = ".", int maxResults = 50, int? maxDepth = null,
        string fileType = "f", bool caseInsensitive = false) =>
        new(pattern, path, maxResults, maxDepth, fileType, caseInsensitive);

    [Fact]
    public void FormatFindResults_NoMatches_ReportsZero()
    {
        var result = FindFilters.FormatFindResults([], Args(pattern: "*.xyz"));
        Assert.Equal("0 for '*.xyz'\n", result);
    }

    [Fact]
    public void FormatFindResults_DotPattern_TreatedAsMatchAll()
    {
        var result = FindFilters.FormatFindResults([], Args(pattern: "."));
        Assert.Equal("0 for '*'\n", result);
    }

    [Fact]
    public void FormatFindResults_GroupsByDirectory()
    {
        var paths = new List<string> { "a.txt", "b.rs", "sub/c.rs", "sub/d.md" };
        var result = FindFilters.FormatFindResults(paths, Args());

        Assert.Equal("4F 2D:\n\n./ a.txt b.rs\nsub/ c.rs d.md\n\next: .rs(2) .md(1) .txt(1)\n", result);
    }

    [Fact]
    public void FormatFindResults_SingleExtension_OmitsExtensionSummary()
    {
        var paths = new List<string> { "a.rs", "sub/b.rs" };
        var result = FindFilters.FormatFindResults(paths, Args());

        Assert.DoesNotContain("ext:", result);
    }

    [Fact]
    public void FormatFindResults_MaxResults_CapsAndReportsOverflow()
    {
        var paths = new List<string> { "a.txt", "b.rs", "sub/c.rs", "sub/d.md" };
        var result = FindFilters.FormatFindResults(paths, Args(maxResults: 1));

        Assert.Equal("4F 2D:\n\n./ a.txt\n+3 more\n\next: .rs(2) .md(1) .txt(1)\n", result);
    }

    [Fact]
    public void FormatFindResults_UnsortedInput_IsSortedBeforeGrouping()
    {
        var paths = new List<string> { "z.txt", "a.txt" };
        var result = FindFilters.FormatFindResults(paths, Args());

        Assert.Equal("2F 1D:\n\n./ a.txt z.txt\n", result);
    }

    [Fact]
    public void FormatFindResults_LongDirectoryName_TruncatedInDisplay()
    {
        var longDir = new string('x', 60);
        var paths = new List<string> { $"{longDir}/file.txt" };
        var result = FindFilters.FormatFindResults(paths, Args());

        // Directory display is truncated to "..." + last 47 chars when longer than 50.
        Assert.Contains("...", result);
        Assert.DoesNotContain(longDir + "/", result);
    }
}
