using RtkSharp.Commands.Go;
using Xunit;

namespace RtkSharp.Tests.Commands.Go;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/go/go_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>: the
/// <c>match_go_tool</c>/<c>has_golangci_format_flag</c> dispatch-helper tests. The pure-filter-function
/// suite (<c>filter_go_test_json</c>/<c>filter_go_build</c>/<c>filter_go_vet</c>/
/// <c>compact_package_name</c>) moved to <c>RtkSharp.Filters.Tests.Commands.Go.GoFiltersTests</c> when
/// the underlying pure methods moved from <c>GoFilters</c> to
/// <c>RtkSharp.Filters.Commands.Go.GoFilters</c> (Task 9 of the filters-library extraction). Rust's own
/// test suite for this module never drives the dispatch functions through a live process (see
/// <see cref="GoCommand"/>'s remarks), so neither does this port.
/// </summary>
public sealed class GoCommandTests
{
    // ===================== match_go_tool / has_golangci_format_flag =====================

    [Fact]
    public void MatchGoTool_GolangciLint_MatchesAndReturnsRemainingArgs()
    {
        string[] args = ["tool", "golangci-lint", "run", "./..."];
        var matched = GoCommand.MatchGoTool(args);

        Assert.NotNull(matched);
        Assert.Equal(RtkSharp.Commands.Go.GoTool.GolangciLint, matched!.Value.Tool);
        Assert.Equal(["run", "./..."], matched.Value.ToolArgs);
    }

    [Fact]
    public void MatchGoTool_Bare_MatchesWithEmptyRemainingArgs()
    {
        string[] args = ["tool", "golangci-lint"];
        var matched = GoCommand.MatchGoTool(args);

        Assert.NotNull(matched);
        Assert.Equal(RtkSharp.Commands.Go.GoTool.GolangciLint, matched!.Value.Tool);
        Assert.Empty(matched.Value.ToolArgs);
    }

    [Fact]
    public void MatchGoTool_RejectsUnknownToolsAndShapes()
    {
        Assert.Null(GoCommand.MatchGoTool(["tool", "pprof"]));
        Assert.Null(GoCommand.MatchGoTool(["tool"]));
        Assert.Null(GoCommand.MatchGoTool(["test", "./..."]));
        Assert.Null(GoCommand.MatchGoTool([]));
    }

    [Fact]
    public void HasGolangciFormatFlag_V1_Detected()
    {
        Assert.True(GoCommand.HasGolangciFormatFlag(["--out-format=json"]));
        Assert.True(GoCommand.HasGolangciFormatFlag(["./...", "--out-format", "json"]));
    }

    [Fact]
    public void HasGolangciFormatFlag_V2_Detected()
    {
        Assert.True(GoCommand.HasGolangciFormatFlag(["--output.json.path", "stdout"]));
        Assert.True(GoCommand.HasGolangciFormatFlag(["--output.json.path=stdout"]));
    }

    [Fact]
    public void HasGolangciFormatFlag_Absent_ReturnsFalse()
    {
        Assert.False(GoCommand.HasGolangciFormatFlag(["run", "./..."]));
        Assert.False(GoCommand.HasGolangciFormatFlag([]));
        Assert.False(GoCommand.HasGolangciFormatFlag(["--fix"]));
    }
}
