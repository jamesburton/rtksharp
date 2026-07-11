using System;
using System.Text;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="DepsFilters"/>, the pure per-ecosystem manifest summarizers behind
/// <c>rtk deps</c>. Moved from <c>RtkSharp.Tests.Commands.System.DepsCommandTests</c> when the
/// five <c>Summarize*</c> helpers moved from <c>RtkSharp.Commands.System.DepsCommand</c> to
/// <see cref="DepsFilters"/> (Task 14 of the filters-library extraction). Faithful-port target:
/// Rust <c>src/cmds/system/deps.rs</c>, which has NO <c>#[cfg(test)]</c> module of its own — so
/// there are no oracle tests to port verbatim here; this file exercises each <c>Summarize*</c>
/// helper directly against representative manifest fixtures, mirroring the shapes the Rust
/// regexes/parsers are documented to produce. Argument parsing and the directory-scanning
/// <c>RunCore</c> flow are not pure and remain in <c>DepsCommand</c> alongside its own tests.
/// </summary>
public sealed class DepsFiltersTests
{
    // --- SummarizeCargo ---

    [Fact]
    public void SummarizeCargo_DependenciesAndDevDependencies_Counted()
    {
        const string cargoToml = """
            [package]
            name = "demo"

            [dependencies]
            anyhow = "1.0"
            regex = { version = "1.10", features = ["std"] }

            [dev-dependencies]
            insta = "1.34"
            """;

        var result = DepsFilters.SummarizeCargo(cargoToml);

        Assert.Contains("Dependencies (2):", result, StringComparison.Ordinal);
        Assert.Contains("anyhow (1.0)", result, StringComparison.Ordinal);
        Assert.Contains("regex (1.10)", result, StringComparison.Ordinal);
        Assert.Contains("Dev (1):", result, StringComparison.Ordinal);
        Assert.Contains("insta (1.34)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeCargo_MoreThanCap_ShowsOverflowMarker()
    {
        var sb = new StringBuilder("[dependencies]\n");
        for (var i = 0; i < 15; i++)
        {
            sb.Append($"dep{i} = \"1.0\"\n");
        }

        var result = DepsFilters.SummarizeCargo(sb.ToString());
        Assert.Contains("Dependencies (15):", result, StringComparison.Ordinal);
        Assert.Contains("... +5 more", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeCargo_NoSections_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DepsFilters.SummarizeCargo("# just a comment\n"));
    }

    // --- SummarizePackageJson ---

    [Fact]
    public void SummarizePackageJson_NameVersionAndDeps()
    {
        const string packageJson = """
            {
              "name": "demo-app",
              "version": "2.1.0",
              "dependencies": { "react": "^18.0.0", "lodash": "^4.17.21" },
              "devDependencies": { "vitest": "^1.0.0" }
            }
            """;

        var result = DepsFilters.SummarizePackageJson(packageJson);

        Assert.Contains("demo-app @ 2.1.0", result, StringComparison.Ordinal);
        Assert.Contains("Dependencies (2):", result, StringComparison.Ordinal);
        Assert.Contains("react (^18.0.0)", result, StringComparison.Ordinal);
        Assert.Contains("Dev Dependencies (1):", result, StringComparison.Ordinal);
        Assert.Contains("vitest", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizePackageJson_NoNameField_OmitsHeaderLine()
    {
        var result = DepsFilters.SummarizePackageJson("""{"dependencies": {"a": "1.0"}}""");
        Assert.DoesNotContain(" @ ", result, StringComparison.Ordinal);
        Assert.Contains("Dependencies (1):", result, StringComparison.Ordinal);
    }

    // --- SummarizeRequirements ---

    [Fact]
    public void SummarizeRequirements_SkipsBlankLinesAndComments()
    {
        const string requirements = """
            # a comment
            requests==2.31.0

            flask>=2.0
            """;

        var result = DepsFilters.SummarizeRequirements(requirements);

        Assert.Contains("Packages (2):", result, StringComparison.Ordinal);
        Assert.Contains("requests==2.31.0", result, StringComparison.Ordinal);
        Assert.Contains("flask>=2.0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeRequirements_BareNameNoVersion_Included()
    {
        var result = DepsFilters.SummarizeRequirements("numpy\n");
        Assert.Contains("numpy", result, StringComparison.Ordinal);
    }

    // --- SummarizePyproject ---

    [Fact]
    public void SummarizePyproject_ExtractsDependenciesArray()
    {
        const string pyproject = """
            [project]
            name = "demo"
            dependencies = [
                "requests>=2.0",
                "click",
            ]
            """;

        var result = DepsFilters.SummarizePyproject(pyproject);

        Assert.Contains("Dependencies (2):", result, StringComparison.Ordinal);
        Assert.Contains("requests>=2.0", result, StringComparison.Ordinal);
        Assert.Contains("click", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizePyproject_NoDependenciesArray_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DepsFilters.SummarizePyproject("[project]\nname = \"demo\"\n"));
    }

    // --- SummarizeGoMod ---

    [Fact]
    public void SummarizeGoMod_ModuleVersionAndRequireBlock()
    {
        const string goMod = """
            module github.com/example/demo

            go 1.22

            require (
                github.com/pkg/errors v0.9.1
                github.com/stretchr/testify v1.9.0
            )
            """;

        var result = DepsFilters.SummarizeGoMod(goMod);

        Assert.Contains("github.com/example/demo (go 1.22)", result, StringComparison.Ordinal);
        Assert.Contains("Dependencies (2):", result, StringComparison.Ordinal);
        Assert.Contains("github.com/pkg/errors v0.9.1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeGoMod_SingleLineRequire_Included()
    {
        const string goMod = """
            module demo

            require github.com/pkg/errors v0.9.1
            """;

        var result = DepsFilters.SummarizeGoMod(goMod);
        Assert.Contains("github.com/pkg/errors v0.9.1", result, StringComparison.Ordinal);
    }
}
