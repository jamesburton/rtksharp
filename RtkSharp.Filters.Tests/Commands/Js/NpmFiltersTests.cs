using System;
using RtkSharp.Filters.Commands.Js;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="NpmFilters"/>, moved from <c>RtkSharp.Tests.Commands.Js.NpmCommandTests</c>
/// (Task 7 of the filters-library extraction) — ported directly from Rust's own <c>#[cfg(test)]</c>
/// module in <c>npm_cmd.rs</c> (<c>test_filter_npm_output</c>/<c>test_filter_npm_output_empty</c>).
/// </summary>
public sealed class NpmFiltersTests
{
    [Fact]
    public void FilterNpmOutput_StripsBannersWarnNoticeAndBlankLines_KeepsRealOutput()
    {
        var output =
            "\n> project@1.0.0 build\n> next build\n\nnpm WARN deprecated inflight@1.0.6: This module is not supported\nnpm notice\n\n   Creating an optimized production build...\n   ✓ Build completed\n";

        var result = NpmFilters.FilterNpmOutput(output);

        Assert.DoesNotContain("npm WARN", result, StringComparison.Ordinal);
        Assert.DoesNotContain("npm notice", result, StringComparison.Ordinal);
        Assert.DoesNotContain("> project@", result, StringComparison.Ordinal);
        Assert.Contains("Build completed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterNpmOutput_AllFiltered_ReturnsLiteralOk()
    {
        Assert.Equal("ok", NpmFilters.FilterNpmOutput("\n\n\n"));
    }

    [Fact]
    public void FilterNpmOutput_EmptyInput_ReturnsLiteralOk()
    {
        Assert.Equal("ok", NpmFilters.FilterNpmOutput(string.Empty));
    }

    [Fact]
    public void FilterNpmOutput_SpinnerGlyphs_Stripped()
    {
        var output = "kept line here\n⸨ spinning\n⸩ spinning\nanother kept line\n";
        var result = NpmFilters.FilterNpmOutput(output);

        Assert.DoesNotContain('⸨', result);
        Assert.DoesNotContain('⸩', result);
        Assert.Contains("kept line here", result, StringComparison.Ordinal);
        Assert.Contains("another kept line", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterNpmOutput_ShortLineUnderTenChars_Stripped()
    {
        // "..." alone is under 10 chars AND contains "..." -> both conditions of the OR'd third
        // clause are true, so it is dropped; a long real output line survives.
        var output = "...\nthis is a much longer kept output line\n";
        var result = NpmFilters.FilterNpmOutput(output);

        Assert.DoesNotContain("...\n", result, StringComparison.Ordinal);
        Assert.Contains("this is a much longer kept output line", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterNpmOutput_LongLineContainingEllipsis_NotStripped()
    {
        // Rust's `&&` binds tighter than `||`: "contains ... AND under 10 chars" - a long line
        // containing "..." does NOT satisfy the length half, so it must survive.
        var output = "Compiling with options... this line is definitely over ten characters long\n";
        var result = NpmFilters.FilterNpmOutput(output);

        Assert.Contains("Compiling with options...", result, StringComparison.Ordinal);
    }
}
