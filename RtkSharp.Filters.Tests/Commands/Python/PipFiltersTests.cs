using System;
using RtkSharp.Filters.Commands.Python;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Python;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/python/pip_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (<c>filter_pip_list</c>/<c>filter_pip_outdated</c>). Moved from
/// <c>RtkSharp.Tests.Commands.Python.PipCommandTests</c> when the underlying pure methods moved from
/// <c>RtkSharp.Commands.Python.PipFilters</c> to <see cref="PipFilters"/> (Task 10 of the
/// filters-library extraction). Dispatch-level coverage for <c>rtk pip</c>'s subcommand routing
/// (list/outdated/passthrough) and the <c>pip</c>-missing/<c>uv</c>-fallback branch was not moved —
/// it remains in the original <c>PipCommandTests</c> alongside <c>PipCommand</c>, which is
/// injectable and thus directly testable at the dispatch level (unlike
/// <c>RuffCommand</c>/<c>PytestCommand</c>/<c>MypyCommand</c>).
/// </summary>
public sealed class PipFiltersTests
{
    // ===================== filter_pip_list =====================

    [Fact]
    public void FilterPipList_ReportsCountAndPackages()
    {
        const string output = """
            [
              {"name": "requests", "version": "2.31.0"},
              {"name": "pytest", "version": "7.4.0"},
              {"name": "rich", "version": "13.0.0"}
            ]
            """;

        var result = PipFilters.FilterPipList(output);
        Assert.Contains("3 packages", result, StringComparison.Ordinal);
        Assert.Contains("requests", result, StringComparison.Ordinal);
        Assert.Contains("2.31.0", result, StringComparison.Ordinal);
        Assert.Contains("pytest", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPipList_Empty_ReportsNoPackagesInstalled()
    {
        const string output = "[]";
        var result = PipFilters.FilterPipList(output);
        Assert.Contains("No packages installed", result, StringComparison.Ordinal);
    }

    // ===================== filter_pip_outdated =====================

    [Fact]
    public void FilterPipOutdated_None_ReportsAllUpToDate()
    {
        const string output = "[]";
        var result = PipFilters.FilterPipOutdated(output);
        Assert.Contains("All packages up to date", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPipOutdated_Some_ShowsVersionArrow()
    {
        const string output = """
            [
              {"name": "requests", "version": "2.31.0", "latest_version": "2.32.0"},
              {"name": "pytest", "version": "7.4.0", "latest_version": "8.0.0"}
            ]
            """;

        var result = PipFilters.FilterPipOutdated(output);
        Assert.Contains("2 packages", result, StringComparison.Ordinal);
        Assert.Contains("requests", result, StringComparison.Ordinal);
        Assert.Contains("2.31.0 → 2.32.0", result, StringComparison.Ordinal);
        Assert.Contains("pytest", result, StringComparison.Ordinal);
        Assert.Contains("7.4.0 → 8.0.0", result, StringComparison.Ordinal);
    }
}
