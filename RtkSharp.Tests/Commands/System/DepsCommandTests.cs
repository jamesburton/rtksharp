using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RtkSharp.Commands.System;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="DepsCommand"/>, the <c>rtk deps</c> multi-ecosystem manifest summarizer.
/// The pure <c>Summarize*</c> helpers moved to <c>RtkSharp.Filters.Commands.System.DepsFilters</c>
/// - see <c>RtkSharp.Filters.Tests.Commands.System.DepsFiltersTests</c>. This file covers
/// <see cref="DepsCommand.ParseArgs"/>, the RTK_META_COMMANDS parse-failure contract, and the
/// directory-scanning <c>RunCore</c> integration flow.
/// </summary>
public sealed class DepsCommandTests
{
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
