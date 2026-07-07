using System;
using System.IO;
using RtkSharp.Commands.System;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="FormatCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>format_cmd.rs</c> (<c>test_detect_formatter_from_explicit_arg</c>,
/// <c>test_detect_formatter_from_pyproject_black</c>, <c>test_detect_formatter_from_pyproject_ruff</c>,
/// <c>test_detect_formatter_from_package_json</c>, <c>test_filter_black_all_formatted</c>,
/// <c>test_filter_black_needs_formatting</c>, <c>test_compact_path</c>). <c>RunCoreAsync</c> is not
/// exercised end-to-end with a real formatter process (that would risk spawning real
/// prettier/black/ruff/biome binaries as a side effect of the test suite); its pure sub-components
/// (<see cref="FormatCommand.DetectFormatterInDir"/>, <see cref="FormatCommand.FilterBlackOutput"/>,
/// <see cref="FormatCommand.CompactPath"/>) are tested directly instead, matching Rust's own test scope
/// (<c>format_cmd.rs</c>'s test module never spawns a real formatter either).
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

    // -----------------------------------------------------------------------
    // FilterBlackOutput - ports format_cmd.rs's test_filter_black_all_formatted
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterBlackOutput_AllDoneAndUnchangedCount_ReportsAllFormatted()
    {
        var output = "All done! ✨ 🍰 ✨\n5 files left unchanged.";

        var result = FormatCommand.FilterBlackOutput(output);

        Assert.Contains("Format (black)", result, StringComparison.Ordinal);
        Assert.Contains("All files formatted", result, StringComparison.Ordinal);
        Assert.Contains("5 files checked", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterBlackOutput - ports format_cmd.rs's test_filter_black_needs_formatting
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterBlackOutput_WouldReformatLines_ListsFilesAndUnchangedCount()
    {
        var output = "would reformat: src/main.py\n" +
            "would reformat: tests/test_utils.py\n" +
            "Oh no! 💥 💔 💥\n" +
            "2 files would be reformatted, 3 files would be left unchanged.";

        var result = FormatCommand.FilterBlackOutput(output);

        Assert.Contains("2 files need formatting", result, StringComparison.Ordinal);
        Assert.Contains("main.py", result, StringComparison.Ordinal);
        Assert.Contains("test_utils.py", result, StringComparison.Ordinal);
        Assert.Contains("3 files already formatted", result, StringComparison.Ordinal);
        Assert.Contains("Run `black .`", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // CompactPath - ports format_cmd.rs's test_compact_path
    // -----------------------------------------------------------------------

    [Fact]
    public void CompactPath_UnixStylePathUnderSrc_ReturnsSrcRelative() =>
        Assert.Equal("src/main.py", FormatCommand.CompactPath("/Users/foo/project/src/main.py"));

    [Fact]
    public void CompactPath_UnixStylePathUnderLib_ReturnsLibRelative() =>
        Assert.Equal("lib/utils.py", FormatCommand.CompactPath("/home/user/app/lib/utils.py"));

    [Fact]
    public void CompactPath_WindowsStylePathUnderTests_ReturnsTestsRelative() =>
        Assert.Equal("tests/test.py", FormatCommand.CompactPath("C:\\Users\\foo\\project\\tests\\test.py"));

    [Fact]
    public void CompactPath_RelativePathWithNoMarkerSegment_ReturnsFileName() =>
        Assert.Equal("file.py", FormatCommand.CompactPath("relative/file.py"));
}
