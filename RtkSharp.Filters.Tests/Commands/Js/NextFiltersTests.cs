using System;
using RtkSharp.Filters.Commands.Js;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="NextFilters"/>, moved from <c>RtkSharp.Tests.Commands.Js.NextCommandTests</c>
/// (Task 7 of the filters-library extraction) — ported directly from Rust's own <c>#[cfg(test)]</c>
/// module in <c>next_cmd.rs</c> (<c>test_filter_next_build</c>, <c>test_extract_time</c>).
/// </summary>
public sealed class NextFiltersTests
{
    // -----------------------------------------------------------------------
    // FilterNextBuild - ports next_cmd.rs's test_filter_next_build
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterNextBuild_ContainsBuildHeaderAndRoutes_ExcludesVerboseLogs()
    {
        var output = "\n" +
            "   ▲ Next.js 15.2.0\n" +
            "\n" +
            "   Creating an optimized production build ...\n" +
            "✓ Compiled successfully\n" +
            "✓ Linting and checking validity of types\n" +
            "✓ Collecting page data\n" +
            "○ /                            1.2 kB        132 kB\n" +
            "● /dashboard                   2.5 kB        156 kB\n" +
            "○ /api/auth                    0.5 kB         89 kB\n" +
            "\n" +
            "Route (app)                    Size     First Load JS\n" +
            "┌ ○ /                          1.2 kB        132 kB\n" +
            "├ ● /dashboard                 2.5 kB        156 kB\n" +
            "└ ○ /api/auth                  0.5 kB         89 kB\n" +
            "\n" +
            "○  (Static)  prerendered as static content\n" +
            "●  (SSG)     prerendered as static HTML\n" +
            "λ  (Server)  server-side renders at runtime\n" +
            "\n" +
            "✓ Built in 34.2s\n";

        var result = NextFilters.FilterNextBuild(output);

        Assert.Contains("Next.js Build", result, StringComparison.Ordinal);
        Assert.Contains("routes", result, StringComparison.Ordinal);

        // Should filter verbose logs.
        Assert.DoesNotContain("Creating an optimized", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // ExtractTime - ports next_cmd.rs's test_extract_time
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractTime_SecondsSuffix_ReturnsNumberAndUnit() =>
        Assert.Equal("34.2s", NextFilters.ExtractTime("Built in 34.2s"));

    [Fact]
    public void ExtractTime_MillisecondsSuffix_ReturnsNumberAndUnit() =>
        Assert.Equal("1250ms", NextFilters.ExtractTime("Compiled in 1250ms"));

    [Fact]
    public void ExtractTime_NoTimeToken_ReturnsNull() =>
        Assert.Null(NextFilters.ExtractTime("No time here"));
}
