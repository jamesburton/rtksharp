using System;
using RtkSharp.Commands.Python;
using Xunit;

namespace RtkSharp.Tests.Commands.Python;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/python/ruff_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (<c>filter_ruff_check_json</c>/<c>filter_ruff_format</c>/<c>compact_path</c>). Rust's own oracle
/// never unit-tests <c>ruff_cmd::run</c> itself (it spawns a real subprocess), so — mirroring
/// <c>CargoCommandTests</c>'s precedent for cargo's non-injectable buffered subcommands
/// (clippy/install/nextest) — no dispatch-level test is added here either; only the pure filter
/// functions are covered.
/// </summary>
public sealed class RuffCommandTests
{
    // ===================== filter_ruff_check_json =====================

    [Fact]
    public void FilterRuffCheckJson_NoIssues_ReturnsNoIssuesFound()
    {
        const string output = "[]";
        var result = RuffFilters.FilterRuffCheckJson(output);
        Assert.Contains("Ruff", result, StringComparison.Ordinal);
        Assert.Contains("No issues found", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRuffCheckJson_WithIssues_GroupsByRuleAndFile()
    {
        const string output = """
            [
              {
                "code": "F401",
                "message": "`os` imported but unused",
                "location": {"row": 1, "column": 8},
                "end_location": {"row": 1, "column": 10},
                "filename": "src/main.py",
                "fix": {"applicability": "safe"}
              },
              {
                "code": "F401",
                "message": "`sys` imported but unused",
                "location": {"row": 2, "column": 8},
                "end_location": {"row": 2, "column": 11},
                "filename": "src/main.py",
                "fix": null
              },
              {
                "code": "E501",
                "message": "Line too long (100 > 88 characters)",
                "location": {"row": 10, "column": 89},
                "end_location": {"row": 10, "column": 100},
                "filename": "src/utils.py",
                "fix": null
              }
            ]
            """;

        var result = RuffFilters.FilterRuffCheckJson(output);

        Assert.Contains("3 issues", result, StringComparison.Ordinal);
        Assert.Contains("2 files", result, StringComparison.Ordinal);
        Assert.Contains("1 fixable", result, StringComparison.Ordinal);
        Assert.Contains("F401", result, StringComparison.Ordinal);
        Assert.Contains("E501", result, StringComparison.Ordinal);
        Assert.Contains("main.py", result, StringComparison.Ordinal);
        Assert.Contains("utils.py", result, StringComparison.Ordinal);
        Assert.Contains("Violations:", result, StringComparison.Ordinal);
        Assert.Contains("1:8", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRuffFormat_AllFormatted_ReturnsAllFilesFormatted()
    {
        const string output = "5 files left unchanged";
        var result = RuffFilters.FilterRuffFormat(output);
        Assert.Contains("Ruff format", result, StringComparison.Ordinal);
        Assert.Contains("All files formatted correctly", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRuffFormat_NeedsFormatting_ListsFiles()
    {
        const string output =
            "Would reformat: src/main.py\n" +
            "Would reformat: tests/test_utils.py\n" +
            "2 files would be reformatted, 3 files left unchanged";

        var result = RuffFilters.FilterRuffFormat(output);

        Assert.Contains("2 files need formatting", result, StringComparison.Ordinal);
        Assert.Contains("main.py", result, StringComparison.Ordinal);
        Assert.Contains("test_utils.py", result, StringComparison.Ordinal);
        Assert.Contains("3 files already formatted", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRuffCheckJson_CapsViolationsAndEmitsHint()
    {
        // Mirror ruff's pretty-printed JSON shape so the input-vs-output comparison reflects what a
        // real `ruff check --output-format=json` emits.
        var diags = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            diags.Add(
                "  {\n" +
                "    \"code\": \"F401\",\n" +
                $"    \"message\": \"`module_{i}` imported but unused\",\n" +
                $"    \"location\": {{\"row\": {i}, \"column\": 4}},\n" +
                $"    \"end_location\": {{\"row\": {i}, \"column\": 20}},\n" +
                $"    \"filename\": \"/Users/dev/project/src/feature_{i}.py\",\n" +
                "    \"fix\": null\n" +
                "  }"
            );
        }

        var json = $"[\n{string.Join(",\n", diags)}\n]";
        var result = RuffFilters.FilterRuffCheckJson(json);

        var inSection = result.Contains("Violations:", StringComparison.Ordinal)
            ? result[(result.IndexOf("Violations:", StringComparison.Ordinal) + "Violations:".Length)..]
            : "";
        var listed = inSection.Split('\n').Count(l => l.Trim().StartsWith("src/", StringComparison.Ordinal));

        Assert.True(listed <= 50, $"violations cap not enforced: got {listed}");
        Assert.Contains("… +150 more", result, StringComparison.Ordinal);

        var rawTokens = json.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var outTokens = result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var savings = 100.0 - (double)outTokens / rawTokens * 100.0;

        Assert.True(savings >= 60.0, $"token savings dropped below 60%: {savings:F1}%");
    }

    // ===================== compact_path =====================

    [Fact]
    public void CompactPath_UnixSrcPath_ReturnsSrcRelative()
    {
        Assert.Equal("src/main.py", RuffFilters.CompactPath("/Users/foo/project/src/main.py"));
    }

    [Fact]
    public void CompactPath_UnixLibPath_ReturnsLibRelative()
    {
        Assert.Equal("lib/utils.py", RuffFilters.CompactPath("/home/user/app/lib/utils.py"));
    }

    [Fact]
    public void CompactPath_WindowsTestsPath_ReturnsTestsRelative()
    {
        Assert.Equal("tests/test.py", RuffFilters.CompactPath("C:\\Users\\foo\\project\\tests\\test.py"));
    }

    [Fact]
    public void CompactPath_RelativePath_ReturnsFileName()
    {
        Assert.Equal("file.py", RuffFilters.CompactPath("relative/file.py"));
    }
}
