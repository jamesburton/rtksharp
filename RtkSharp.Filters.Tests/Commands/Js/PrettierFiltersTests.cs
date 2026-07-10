using System;
using System.Text;
using RtkSharp.Filters.Commands.Js;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PrettierFilters"/>, moved from
/// <c>RtkSharp.Tests.Commands.Js.PrettierCommandTests</c> (Task 7 of the filters-library extraction) —
/// ported directly from Rust's own <c>#[cfg(test)]</c> module in <c>prettier_cmd.rs</c>
/// (<c>test_filter_all_formatted</c>, <c>test_filter_files_need_formatting</c>,
/// <c>test_filter_many_files</c>, <c>test_filter_empty_output</c>,
/// <c>test_filter_whitespace_only_output</c>).
/// </summary>
public sealed class PrettierFiltersTests
{
    // -----------------------------------------------------------------------
    // FilterPrettierOutput - ports prettier_cmd.rs's test_filter_all_formatted
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterPrettierOutput_AllMatchedFilesMessage_ReportsAllFormattedCorrectly()
    {
        var output = "\nChecking formatting...\nAll matched files use Prettier code style!\n        ";

        var result = PrettierFilters.FilterPrettierOutput(output);

        Assert.Contains("Prettier", result, StringComparison.Ordinal);
        Assert.Contains("All files formatted correctly", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterPrettierOutput - ports prettier_cmd.rs's test_filter_files_need_formatting
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterPrettierOutput_FilesNeedFormatting_ListsThem()
    {
        var output = "\n" +
            "Checking formatting...\n" +
            "src/components/ui/button.tsx\n" +
            "src/lib/auth/session.ts\n" +
            "src/pages/dashboard.tsx\n" +
            "Code style issues found in the above file(s). Forgot to run Prettier?\n        ";

        var result = PrettierFilters.FilterPrettierOutput(output);

        Assert.Contains("3 files need formatting", result, StringComparison.Ordinal);
        Assert.Contains("button.tsx", result, StringComparison.Ordinal);
        Assert.Contains("session.ts", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterPrettierOutput - ports prettier_cmd.rs's test_filter_many_files
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterPrettierOutput_MoreThanTenFiles_CapsAndReportsOverflow()
    {
        var sb = new StringBuilder("Checking formatting...\n");
        for (var i = 0; i < 15; i++)
        {
            sb.Append($"src/file{i}.ts\n");
        }

        var result = PrettierFilters.FilterPrettierOutput(sb.ToString());

        Assert.Contains("15 files need formatting", result, StringComparison.Ordinal);
        Assert.Contains("... +5 more files", result, StringComparison.Ordinal);
    }

    // --- #221: empty output should not say "All files formatted" ---

    // -----------------------------------------------------------------------
    // FilterPrettierOutput - ports prettier_cmd.rs's test_filter_empty_output
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterPrettierOutput_EmptyOutput_ReturnsErrorNotAllFormatted()
    {
        var result = PrettierFilters.FilterPrettierOutput("");

        Assert.Contains("Error", result, StringComparison.Ordinal);
        Assert.DoesNotContain("All files formatted", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterPrettierOutput - ports prettier_cmd.rs's test_filter_whitespace_only_output
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterPrettierOutput_WhitespaceOnlyOutput_ReturnsErrorNotAllFormatted()
    {
        var result = PrettierFilters.FilterPrettierOutput("   \n\n  ");

        Assert.Contains("Error", result, StringComparison.Ordinal);
        Assert.DoesNotContain("All files formatted", result, StringComparison.Ordinal);
    }
}
