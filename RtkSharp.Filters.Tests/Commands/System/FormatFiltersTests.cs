using System;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="FormatFilters"/>: the <c>black</c>-output condensation and its
/// path-shortening helper. Moved from <c>RtkSharp.Tests.Commands.System.FormatCommandTests</c>
/// when <c>FilterBlackOutput</c>/<c>CompactPath</c> moved from
/// <c>RtkSharp.Commands.System.FormatCommand</c> to <see cref="FormatFilters"/> (Task 14 of the
/// filters-library extraction). Formatter auto-detection and process execution are not pure and
/// remain in <c>FormatCommand</c> alongside its own tests.
/// </summary>
public sealed class FormatFiltersTests
{
    // -----------------------------------------------------------------------
    // FilterBlackOutput - ports format_cmd.rs's test_filter_black_all_formatted
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterBlackOutput_AllDoneAndUnchangedCount_ReportsAllFormatted()
    {
        var output = "All done! ✨ 🍰 ✨\n5 files left unchanged.";

        var result = FormatFilters.FilterBlackOutput(output);

        Assert.Contains("Format (black)", result, StringComparison.Ordinal);
        Assert.Contains("All files formatted", result, StringComparison.Ordinal);
        Assert.Contains("5 files checked", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterBlackOutput - ports format_cmd.rs's test_filter_black_needs_formatting
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterBlackOutput_WouldReformatLines_ListsFilesAndUnchangedCount()
    {
        var output = "would reformat: src/main.py\n" +
            "would reformat: tests/test_utils.py\n" +
            "Oh no! 💥 💔 💥\n" +
            "2 files would be reformatted, 3 files would be left unchanged.";

        var result = FormatFilters.FilterBlackOutput(output);

        Assert.Contains("2 files need formatting", result, StringComparison.Ordinal);
        Assert.Contains("main.py", result, StringComparison.Ordinal);
        Assert.Contains("test_utils.py", result, StringComparison.Ordinal);
        Assert.Contains("3 files already formatted", result, StringComparison.Ordinal);
        Assert.Contains("Run `black .`", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // CompactPath - ports format_cmd.rs's test_compact_path
    // -----------------------------------------------------------------------

    [Fact]
    public void CompactPath_UnixStylePathUnderSrc_ReturnsSrcRelative() =>
        Assert.Equal("src/main.py", FormatFilters.CompactPath("/Users/foo/project/src/main.py"));

    [Fact]
    public void CompactPath_UnixStylePathUnderLib_ReturnsLibRelative() =>
        Assert.Equal("lib/utils.py", FormatFilters.CompactPath("/home/user/app/lib/utils.py"));

    [Fact]
    public void CompactPath_WindowsStylePathUnderTests_ReturnsTestsRelative() =>
        Assert.Equal("tests/test.py", FormatFilters.CompactPath("C:\\Users\\foo\\project\\tests\\test.py"));

    [Fact]
    public void CompactPath_RelativePathWithNoMarkerSegment_ReturnsFileName() =>
        Assert.Equal("file.py", FormatFilters.CompactPath("relative/file.py"));
}
