using System;
using System.IO;
using RtkSharp.Commands.System;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="FormatCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>format_cmd.rs</c> (<c>test_detect_formatter_from_explicit_arg</c>,
/// <c>test_detect_formatter_from_pyproject_black</c>, <c>test_detect_formatter_from_pyproject_ruff</c>,
/// <c>test_detect_formatter_from_package_json</c>). The pure <c>FilterBlackOutput</c>/<c>CompactPath</c>
/// tests moved to <c>RtkSharp.Filters.Commands.System.FormatFilters</c> - see
/// <c>RtkSharp.Filters.Tests.Commands.System.FormatFiltersTests</c>. <c>RunCoreAsync</c> is not
/// exercised end-to-end with a real formatter process (that would risk spawning real
/// prettier/black/ruff/biome binaries as a side effect of the test suite); its pure sub-component
/// <see cref="FormatCommand.DetectFormatterInDir"/> is tested directly instead, matching Rust's own
/// test scope (<c>format_cmd.rs</c>'s test module never spawns a real formatter either).
/// </summary>
public sealed class FormatCommandTests
{
    // -----------------------------------------------------------------------
    // DetectFormatterInDir - ports format_cmd.rs's test_detect_formatter_from_explicit_arg
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectFormatterInDir_ExplicitBlackArg_ReturnsBlack()
    {
        var formatter = FormatCommand.DetectFormatterInDir(["black", "--check"], ".");
        Assert.Equal("black", formatter);
    }

    [Fact]
    public void DetectFormatterInDir_ExplicitPrettierArg_ReturnsPrettier()
    {
        var formatter = FormatCommand.DetectFormatterInDir(["prettier", "."], ".");
        Assert.Equal("prettier", formatter);
    }

    [Fact]
    public void DetectFormatterInDir_ExplicitRuffArg_ReturnsRuff()
    {
        var formatter = FormatCommand.DetectFormatterInDir(["ruff", "format"], ".");
        Assert.Equal("ruff", formatter);
    }

    // -----------------------------------------------------------------------
    // DetectFormatterInDir - ports format_cmd.rs's test_detect_formatter_from_pyproject_black
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectFormatterInDir_PyprojectWithToolBlackSection_ReturnsBlack()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "pyproject.toml"), "[tool.black]\nline-length = 88\n");

            var formatter = FormatCommand.DetectFormatterInDir([], tempDir.FullName);

            Assert.Equal("black", formatter);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // DetectFormatterInDir - ports format_cmd.rs's test_detect_formatter_from_pyproject_ruff
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectFormatterInDir_PyprojectWithToolRuffFormatSection_ReturnsRuff()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "pyproject.toml"), "[tool.ruff.format]\nindent-width = 4\n");

            var formatter = FormatCommand.DetectFormatterInDir([], tempDir.FullName);

            Assert.Equal("ruff", formatter);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // DetectFormatterInDir - ports format_cmd.rs's test_detect_formatter_from_package_json
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectFormatterInDir_PackageJsonPresent_ReturnsPrettier()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "package.json"), "{\"name\": \"test\"}\n");

            var formatter = FormatCommand.DetectFormatterInDir([], tempDir.FullName);

            Assert.Equal("prettier", formatter);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // DetectFormatterInDir - fallback when nothing matches
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectFormatterInDir_NoMarkersPresent_FallsBackToRuff()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var formatter = FormatCommand.DetectFormatterInDir([], tempDir.FullName);

            Assert.Equal("ruff", formatter);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
