using System;
using RtkSharp.Commands.Python;
using Xunit;

namespace RtkSharp.Tests.Commands.Python;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/python/mypy_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (<c>filter_mypy_output</c>). Rust's own oracle never unit-tests <c>mypy_cmd::run</c> itself (it
/// spawns a real subprocess), so no dispatch-level test is added here either — only the pure filter
/// function is covered, mirroring <c>CargoCommandTests</c>'s precedent for cargo's non-injectable
/// buffered subcommands.
/// </summary>
public sealed class MypyCommandTests
{
    [Fact]
    public void FilterMypyOutput_ErrorsGroupedByFile_MostErrorsFirst()
    {
        const string output = """
            src/server/auth.py:12: error: Incompatible return value type (got "str", expected "int")  [return-value]
            src/server/auth.py:15: error: Argument 1 has incompatible type "int"; expected "str"  [arg-type]
            src/models/user.py:8: error: Name "foo" is not defined  [name-defined]
            src/models/user.py:10: error: Incompatible types in assignment  [assignment]
            src/models/user.py:20: error: Missing return statement  [return]
            Found 5 errors in 2 files (checked 10 source files)

            """;

        var result = MypyFilters.FilterMypyOutput(output);

        Assert.Contains("mypy: 5 errors in 2 files", result, StringComparison.Ordinal);

        // user.py has 3 errors, auth.py has 2 -- user.py should come first
        var userPos = result.IndexOf("user.py", StringComparison.Ordinal);
        var authPos = result.IndexOf("auth.py", StringComparison.Ordinal);
        Assert.True(userPos < authPos, "user.py (3 errors) should appear before auth.py (2 errors)");
        Assert.Contains("user.py (3 errors)", result, StringComparison.Ordinal);
        Assert.Contains("auth.py (2 errors)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMypyOutput_WithColumnNumbers_ParsesLineAndCode()
    {
        const string output = """
            src/api.py:10:5: error: Incompatible return value type  [return-value]

            """;

        var result = MypyFilters.FilterMypyOutput(output);
        Assert.Contains("L10:", result, StringComparison.Ordinal);
        Assert.Contains("[return-value]", result, StringComparison.Ordinal);
        Assert.Contains("Incompatible return value type", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMypyOutput_TopCodesSummary_ShownWhenMultipleDistinctCodes()
    {
        const string output = """
            a.py:1: error: Error one  [return-value]
            a.py:2: error: Error two  [return-value]
            a.py:3: error: Error three  [return-value]
            b.py:1: error: Error four  [name-defined]
            c.py:1: error: Error five  [arg-type]
            Found 5 errors in 3 files

            """;

        var result = MypyFilters.FilterMypyOutput(output);
        Assert.Contains("Top codes:", result, StringComparison.Ordinal);
        Assert.Contains("return-value (3x)", result, StringComparison.Ordinal);
        Assert.Contains("name-defined (1x)", result, StringComparison.Ordinal);
        Assert.Contains("arg-type (1x)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMypyOutput_SingleCode_NoTopCodesSummary()
    {
        const string output = """
            a.py:1: error: Error one  [return-value]
            a.py:2: error: Error two  [return-value]
            b.py:1: error: Error three  [return-value]
            Found 3 errors in 2 files

            """;

        var result = MypyFilters.FilterMypyOutput(output);
        Assert.DoesNotContain("Top codes:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMypyOutput_EveryErrorShown()
    {
        const string output = """
            src/api.py:10: error: Type "str" not assignable to "int"  [assignment]
            src/api.py:20: error: Missing return statement  [return]
            src/api.py:30: error: Name "bar" is not defined  [name-defined]

            """;

        var result = MypyFilters.FilterMypyOutput(output);
        Assert.Contains("Type \"str\" not assignable to \"int\"", result, StringComparison.Ordinal);
        Assert.Contains("Missing return statement", result, StringComparison.Ordinal);
        Assert.Contains("Name \"bar\" is not defined", result, StringComparison.Ordinal);
        Assert.Contains("L10:", result, StringComparison.Ordinal);
        Assert.Contains("L20:", result, StringComparison.Ordinal);
        Assert.Contains("L30:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMypyOutput_NoteContinuation_AttachedToPrecedingError()
    {
        const string output = """
            src/app.py:10: error: Incompatible types in assignment  [assignment]
            src/app.py:10: note: Expected type "int"
            src/app.py:10: note: Got type "str"
            src/app.py:20: error: Missing return statement  [return]

            """;

        var result = MypyFilters.FilterMypyOutput(output);
        Assert.Contains("Incompatible types in assignment", result, StringComparison.Ordinal);
        Assert.Contains("Expected type \"int\"", result, StringComparison.Ordinal);
        Assert.Contains("Got type \"str\"", result, StringComparison.Ordinal);
        Assert.Contains("L10:", result, StringComparison.Ordinal);
        Assert.Contains("L20:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMypyOutput_FilelessErrors_AppearBeforeGroupedErrors()
    {
        const string output = """
            mypy: error: No module named 'nonexistent'
            src/api.py:10: error: Name "foo" is not defined  [name-defined]
            Found 1 error in 1 file

            """;

        var result = MypyFilters.FilterMypyOutput(output);

        // File-less error should appear verbatim before grouped output
        Assert.Contains("mypy: error: No module named 'nonexistent'", result, StringComparison.Ordinal);
        Assert.Contains("api.py (1 error", result, StringComparison.Ordinal);

        var filelessPos = result.IndexOf("No module named", StringComparison.Ordinal);
        var groupedPos = result.IndexOf("api.py", StringComparison.Ordinal);
        Assert.True(filelessPos < groupedPos, "File-less errors should appear before grouped file errors");
    }

    [Fact]
    public void FilterMypyOutput_NoErrors_ReportsNoIssuesFound()
    {
        const string output = "Success: no issues found in 5 source files\n";
        var result = MypyFilters.FilterMypyOutput(output);
        Assert.Equal("mypy: No issues found", result);
    }

    [Fact]
    public void FilterMypyOutput_NoFileLimit_ShowsAllFifteenFiles()
    {
        var output = "";
        for (var i = 1; i <= 15; i++)
        {
            output += $"src/file{i}.py:{i}: error: Error in file {i}.  [assignment]\n";
        }

        output += "Found 15 errors in 15 files\n";

        var result = MypyFilters.FilterMypyOutput(output);
        Assert.Contains("15 errors in 15 files", result, StringComparison.Ordinal);
        for (var i = 1; i <= 15; i++)
        {
            Assert.Contains($"file{i}.py", result, StringComparison.Ordinal);
        }
    }
}
