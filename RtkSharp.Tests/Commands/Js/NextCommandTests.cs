using System;
using RtkSharp.Commands.Js;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="NextCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>next_cmd.rs</c> (<c>test_filter_next_build</c>, <c>test_extract_time</c>). <c>ExecuteAsync</c>/
/// <c>RunNextSafeAsync</c> are not exercised end-to-end: they construct their own process invocation via
/// <see cref="RtkSharp.Execution.CommandRunner"/> with no injection seam, so invoking them would risk
/// spawning a real <c>next</c>/<c>npx</c> process as a side effect of the test suite (same convention
/// <c>TscCommandTests</c> follows for its own <c>CommandRunner</c>-based route).
/// </summary>
public sealed class NextCommandTests
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

        var result = NextCommand.FilterNextBuild(output);

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
        Assert.Equal("34.2s", NextCommand.ExtractTime("Built in 34.2s"));

    [Fact]
    public void ExtractTime_MillisecondsSuffix_ReturnsNumberAndUnit() =>
        Assert.Equal("1250ms", NextCommand.ExtractTime("Compiled in 1250ms"));

    [Fact]
    public void ExtractTime_NoTimeToken_ReturnsNull() =>
        Assert.Null(NextCommand.ExtractTime("No time here"));

    // -----------------------------------------------------------------------
    // ToolExists - pure PathResolver-based check
    // -----------------------------------------------------------------------

    [Fact]
    public void ToolExists_UnresolvableName_ReturnsFalse() =>
        Assert.False(NextCommand.ToolExists("definitely-not-a-real-binary-xyz123"));
}
