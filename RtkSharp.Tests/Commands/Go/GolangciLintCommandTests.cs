using RtkSharp.Commands.Go;
using Xunit;

namespace RtkSharp.Tests.Commands.Go;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/go/golangci_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>:
/// <c>parse_major_version</c>, <c>classify_invocation</c>, and <c>build_filtered_args</c>. The
/// pure-filter-function suite (<c>filter_golangci_json</c>, <c>compact_path</c>, including the v1/v2
/// source-line/token-savings coverage) moved to
/// <c>RtkSharp.Filters.Tests.Commands.Go.GoFiltersTests</c> when the underlying pure methods moved from
/// <c>GolangciLintCommand</c> to <c>RtkSharp.Filters.Commands.Go.GoFilters</c> (Task 9 of the
/// filters-library extraction).
/// </summary>
public sealed class GolangciLintCommandTests
{
    // ===================== parse_major_version =====================

    [Fact]
    public void ParseMajorVersion_V1Format_ReturnsOne()
    {
        Assert.Equal(1u, GolangciLintCommand.ParseMajorVersion("golangci-lint version 1.59.1"));
    }

    [Fact]
    public void ParseMajorVersion_V2Format_ReturnsTwo()
    {
        Assert.Equal(
            2u,
            GolangciLintCommand.ParseMajorVersion(
                "golangci-lint has version 2.10.0 built with go1.26.0 from 95dcb68a on 2026-02-17T13:05:51Z"));
    }

    [Fact]
    public void ParseMajorVersion_Empty_ReturnsOne()
    {
        Assert.Equal(1u, GolangciLintCommand.ParseMajorVersion(""));
    }

    [Fact]
    public void ParseMajorVersion_Malformed_ReturnsOne()
    {
        Assert.Equal(1u, GolangciLintCommand.ParseMajorVersion("not a version string"));
    }

    // ===================== classify_invocation =====================

    [Fact]
    public void ClassifyInvocation_RunUsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Empty(filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_WithGlobalFlagValue_UsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["--color", "never", "run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Equal(["--color", "never"], filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_WithShortGlobalFlag_UsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["-v", "run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Equal(["-v"], filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_WithInlineValueFlag_UsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["--color=never", "run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Equal(["--color=never"], filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_WithInlineConfigFlag_UsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["--config=foo.yml", "run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Equal(["--config=foo.yml"], filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_BareCommand_IsPassthrough()
    {
        Assert.IsType<GolangciInvocation.Passthrough>(GolangciLintCommand.ClassifyInvocation([]));
    }

    [Fact]
    public void ClassifyInvocation_VersionFlag_IsPassthrough()
    {
        Assert.IsType<GolangciInvocation.Passthrough>(GolangciLintCommand.ClassifyInvocation(["--version"]));
    }

    [Fact]
    public void ClassifyInvocation_VersionSubcommand_IsPassthrough()
    {
        Assert.IsType<GolangciInvocation.Passthrough>(GolangciLintCommand.ClassifyInvocation(["version"]));
    }

    // ===================== build_filtered_args =====================

    [Fact]
    public void BuildFilteredArgs_DoesNotDuplicateRun()
    {
        var invocation = new GolangciRunInvocation([], ["./..."]);

        var result = GolangciLintCommand.BuildFilteredArgs(invocation, 2);

        Assert.Equal(["run", "--output.json.path", "stdout", "./..."], result);
    }
}
