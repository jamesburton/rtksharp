using RtkSharp.Parser;
using Xunit;

namespace RtkSharp.Tests.Parser;

/// <summary>
/// Tests for <see cref="DependencyState"/>'s <see cref="ITokenFormatter"/> rendering. Ports Rust's
/// <c>test_dependency_state_plain_listing_shows_packages</c> (<c>src/parser/formatter.rs:243-260</c>),
/// plus new coverage for the outdated-packages path and each <see cref="FormatMode"/>.
/// </summary>
public sealed class DependencyStateTests
{
    private static Dependency MakeDep(string name, string version, string? latest = null, string? wanted = null, bool dev = false) => new()
    {
        Name = name,
        CurrentVersion = version,
        LatestVersion = latest,
        WantedVersion = wanted,
        DevDependency = dev,
    };

    [Fact]
    public void FormatCompact_PlainListing_ShowsPackagesNotUpToDateMessage()
    {
        var state = new DependencyState
        {
            TotalPackages = 2,
            OutdatedCount = 0,
            Dependencies = [MakeDep("react", "18.0.0"), MakeDep("typescript", "5.0.0")],
        };

        var output = state.FormatCompact();

        Assert.Contains("react", output);
        Assert.Contains("typescript", output);
        Assert.DoesNotContain("up-to-date", output);
    }

    [Fact]
    public void FormatCompact_NoDependencies_ReportsUpToDate()
    {
        var state = new DependencyState { TotalPackages = 0, OutdatedCount = 0, Dependencies = [] };
        Assert.Equal("All packages up-to-date", state.FormatCompact());
    }

    [Fact]
    public void FormatCompact_PlainListing_MarksDevDependencies()
    {
        var state = new DependencyState
        {
            TotalPackages = 1,
            OutdatedCount = 0,
            Dependencies = [MakeDep("vitest", "1.0.0", dev: true)],
        };

        Assert.Contains("vitest 1.0.0 (dev)", state.FormatCompact());
    }

    [Fact]
    public void FormatCompact_PlainListing_OverflowsPastCapInventory()
    {
        var deps = Enumerable.Range(1, 55).Select(i => MakeDep($"pkg{i}", "1.0.0")).ToList();
        var state = new DependencyState { TotalPackages = 55, OutdatedCount = 0, Dependencies = deps };

        var output = state.FormatCompact();

        Assert.Contains("... +5 more", output);
        Assert.Contains("pkg50", output);
        Assert.DoesNotContain("pkg51 ", output);
    }

    [Fact]
    public void FormatCompact_OutdatedPackages_ShowsUpgradeArrows()
    {
        var state = new DependencyState
        {
            TotalPackages = 5,
            OutdatedCount = 1,
            Dependencies = [MakeDep("react", "17.0.0", latest: "18.0.0")],
        };

        var output = state.FormatCompact();

        Assert.Contains("1 outdated packages (of 5)", output);
        Assert.Contains("react: 17.0.0 → 18.0.0", output);
    }

    [Fact]
    public void FormatCompact_MoreThanTenOutdated_ShowsOverflowMarker()
    {
        var deps = Enumerable.Range(1, 12).Select(i => MakeDep($"pkg{i}", "1.0.0", latest: "2.0.0")).ToList();
        var state = new DependencyState { TotalPackages = 20, OutdatedCount = 12, Dependencies = deps };

        Assert.Contains("... +2 more", state.FormatCompact());
    }

    [Fact]
    public void FormatVerbose_ListsOutdatedWithWantedVersion()
    {
        var state = new DependencyState
        {
            TotalPackages = 5,
            OutdatedCount = 1,
            Dependencies = [MakeDep("react", "17.0.0", latest: "18.0.0", wanted: "17.5.0", dev: true)],
        };

        var output = state.FormatVerbose();

        Assert.Contains("Total packages: 5 (1 outdated)", output);
        Assert.Contains("react: 17.0.0 → 18.0.0 (dev)", output);
        Assert.Contains("(wanted: 17.5.0)", output);
    }

    [Fact]
    public void FormatVerbose_NoOutdated_OmitsOutdatedSection()
    {
        var state = new DependencyState { TotalPackages = 3, OutdatedCount = 0, Dependencies = [] };
        Assert.DoesNotContain("Outdated packages:", state.FormatVerbose());
    }

    [Fact]
    public void FormatUltra_RendersSymbolicSummary()
    {
        var state = new DependencyState { TotalPackages = 42, OutdatedCount = 3, Dependencies = [] };
        Assert.Equal("pkg:42 ^3", state.FormatUltra());
    }

    [Theory]
    [InlineData(FormatMode.Compact)]
    [InlineData(FormatMode.Verbose)]
    [InlineData(FormatMode.Ultra)]
    public void Format_DispatchesToCorrectPerModeRenderer(FormatMode mode)
    {
        var state = new DependencyState
        {
            TotalPackages = 5,
            OutdatedCount = 1,
            Dependencies = [MakeDep("react", "17.0.0", latest: "18.0.0")],
        };

        var expected = mode switch
        {
            FormatMode.Compact => state.FormatCompact(),
            FormatMode.Verbose => state.FormatVerbose(),
            FormatMode.Ultra => state.FormatUltra(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        Assert.Equal(expected, ((ITokenFormatter)state).Format(mode));
    }
}
