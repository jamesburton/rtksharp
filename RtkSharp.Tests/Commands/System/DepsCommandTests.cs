using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RtkSharp.Commands.System;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="DepsCommand"/>, the <c>rtk deps</c> multi-ecosystem manifest summarizer.
/// Faithful-port target: Rust <c>src/cmds/system/deps.rs</c>, which has NO <c>#[cfg(test)]</c> module
/// of its own — so there are no oracle tests to port verbatim here. This file instead exercises each
/// <c>Summarize*</c> helper directly against representative manifest fixtures, mirroring the shapes
/// the Rust regexes/parsers are documented to produce, plus <see cref="DepsCommand.ParseArgs"/> and
/// the RTK_META_COMMANDS parse-failure contract.
/// </summary>
public sealed class DepsCommandTests
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

        var result = DepsCommand.SummarizeCargo(cargoToml);

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

        var result = DepsCommand.SummarizeCargo(sb.ToString());
        Assert.Contains("Dependencies (15):", result, StringComparison.Ordinal);
        Assert.Contains("... +5 more", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeCargo_NoSections_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DepsCommand.SummarizeCargo("# just a comment\n"));
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

        var result = DepsCommand.SummarizePackageJson(packageJson);

        Assert.Contains("demo-app @ 2.1.0", result, StringComparison.Ordinal);
        Assert.Contains("Dependencies (2):", result, StringComparison.Ordinal);
        Assert.Contains("react (^18.0.0)", result, StringComparison.Ordinal);
        Assert.Contains("Dev Dependencies (1):", result, StringComparison.Ordinal);
        Assert.Contains("vitest", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizePackageJson_NoNameField_OmitsHeaderLine()
    {
        var result = DepsCommand.SummarizePackageJson("""{"dependencies": {"a": "1.0"}}""");
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

        var result = DepsCommand.SummarizeRequirements(requirements);

        Assert.Contains("Packages (2):", result, StringComparison.Ordinal);
        Assert.Contains("requests==2.31.0", result, StringComparison.Ordinal);
        Assert.Contains("flask>=2.0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeRequirements_BareNameNoVersion_Included()
    {
        var result = DepsCommand.SummarizeRequirements("numpy\n");
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

        var result = DepsCommand.SummarizePyproject(pyproject);

        Assert.Contains("Dependencies (2):", result, StringComparison.Ordinal);
        Assert.Contains("requests>=2.0", result, StringComparison.Ordinal);
        Assert.Contains("click", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizePyproject_NoDependenciesArray_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DepsCommand.SummarizePyproject("[project]\nname = \"demo\"\n"));
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

        var result = DepsCommand.SummarizeGoMod(goMod);

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

        var result = DepsCommand.SummarizeGoMod(goMod);
        Assert.Contains("github.com/pkg/errors v0.9.1", result, StringComparison.Ordinal);
    }

    // --- ParseArgs / RTK_META_COMMANDS parse-failure contract ---

    [Fact]
    public void ParseArgs_NoArgs_DefaultsToCurrentDirectory()
    {
        Assert.Equal(".", DepsCommand.ParseArgs([]).Path);
    }

    [Fact]
    public void ParseArgs_ExplicitPath_Used()
    {
        Assert.Equal("some/dir", DepsCommand.ParseArgs(["some/dir"]).Path);
    }

    [Fact]
    public void ParseArgs_ExtraPositional_ThrowsDepsArgsException()
    {
        Assert.Throws<DepsArgsException>(() => DepsCommand.ParseArgs(["dir1", "dir2"]));
    }

    [Fact]
    public async Task RunAsync_ParseFailure_ReturnsExitCode2()
    {
        var exitCode = await DepsCommand.RunAsync(["dir1", "dir2"]);
        Assert.Equal(2, exitCode);
    }

    // --- RunCore integration ---

    [Fact]
    public void RunCore_NoManifestFiles_ReportsNoneFound()
    {
        var tempDir = Directory.CreateTempSubdirectory("rtk-deps-test-");
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = DepsCommand.RunCore(tempDir.FullName, 0, stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("No dependency files found", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RunCore_CargoTomlPresent_SummarizesRust()
    {
        var tempDir = Directory.CreateTempSubdirectory("rtk-deps-test-");
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "Cargo.toml"), "[dependencies]\nanyhow = \"1.0\"\n");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = DepsCommand.RunCore(tempDir.FullName, 0, stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Rust (Cargo.toml):", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("anyhow (1.0)", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
