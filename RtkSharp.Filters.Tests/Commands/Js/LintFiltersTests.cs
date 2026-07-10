using System;
using System.Linq;
using RtkSharp.Filters.Commands.Js;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="LintFilters"/>, moved from <c>RtkSharp.Tests.Commands.Js.LintCommandTests</c>
/// (Task 7 of the filters-library extraction) — a faithful, test-for-test port of Rust
/// <c>src/cmds/js/lint_cmd.rs</c>'s own <c>#[cfg(test)] mod tests</c> for the <c>eslint</c>/
/// <c>pylint</c>/generic-fallback paths.
/// </summary>
public sealed class LintFiltersTests
{
    // ===================== FilterEslintJson (lint_cmd.rs's own mod tests) =====================

    [Fact]
    public void FilterEslintJson_GroupedByRuleAndFile()
    {
        const string json = """
            [
                {
                    "filePath": "/Users/test/project/src/utils.ts",
                    "messages": [
                        {"ruleId": "prefer-const", "severity": 1, "message": "Use const instead of let", "line": 10, "column": 5},
                        {"ruleId": "prefer-const", "severity": 1, "message": "Use const instead of let", "line": 15, "column": 5}
                    ],
                    "errorCount": 0,
                    "warningCount": 2
                },
                {
                    "filePath": "/Users/test/project/src/api.ts",
                    "messages": [
                        {"ruleId": "@typescript-eslint/no-unused-vars", "severity": 2, "message": "Variable x is unused", "line": 20, "column": 10}
                    ],
                    "errorCount": 1,
                    "warningCount": 0
                }
            ]
            """;

        var result = LintFilters.FilterEslintJson(json);

        Assert.Contains("ESLint:", result, StringComparison.Ordinal);
        Assert.Contains("prefer-const", result, StringComparison.Ordinal);
        Assert.Contains("no-unused-vars", result, StringComparison.Ordinal);
        Assert.Contains("src/utils.ts", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterEslintJson_NoIssues()
    {
        const string json = """[{"filePath":"/a.ts","messages":[],"errorCount":0,"warningCount":0}]""";

        Assert.Equal("ESLint: No issues found", LintFilters.FilterEslintJson(json));
    }

    [Fact]
    public void FilterEslintJson_InvalidJson_FallsBackToTruncatedRaw()
    {
        const string notJson = "eslint: command not found";

        var result = LintFilters.FilterEslintJson(notJson);

        Assert.StartsWith("ESLint output (JSON parse failed:", result, StringComparison.Ordinal);
        Assert.Contains(notJson, result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterEslintJson_MoreThanTenRules_CapsAtTopTen()
    {
        var sb = new global::System.Text.StringBuilder("[");
        for (var i = 0; i < 12; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($$"""{"filePath":"/f{{i}}.ts","messages":[{"ruleId":"rule-{{i}}","severity":1,"message":"m","line":1,"column":1}],"errorCount":0,"warningCount":1}""");
        }

        sb.Append(']');

        var result = LintFilters.FilterEslintJson(sb.ToString());

        // Isolate just the "Top rules:" section — each of the 10 shown files also repeats its own
        // rule name in its per-file breakdown underneath "Top files:", which would otherwise
        // contaminate a naive whole-output count of "rule-" lines.
        var lines = result.Split('\n');
        var topRulesStart = Array.IndexOf(lines, "Top rules:") + 1;
        var topRulesEnd = Array.IndexOf(lines, "Top files:", topRulesStart);
        var topRulesSection = lines[topRulesStart..topRulesEnd];

        Assert.Equal(10, topRulesSection.Count(l => l.TrimStart().StartsWith("rule-", StringComparison.Ordinal)));
    }

    [Fact]
    public void FilterEslintJson_MoreThanTenFiles_ShowsOverflowHint()
    {
        var sb = new global::System.Text.StringBuilder("[");
        for (var i = 0; i < 12; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($$"""{"filePath":"/f{{i}}.ts","messages":[{"ruleId":"r","severity":1,"message":"m","line":1,"column":1}],"errorCount":0,"warningCount":1}""");
        }

        sb.Append(']');

        var result = LintFilters.FilterEslintJson(sb.ToString());

        Assert.Contains("+2 more files", result, StringComparison.Ordinal);
    }

    // ===================== CompactPath (lint_cmd.rs's own mod tests) =====================

    [Theory]
    [InlineData("/Users/foo/project/src/utils.ts", "src/utils.ts")]
    [InlineData(@"C:\Users\project\src\api.ts", "src/api.ts")]
    [InlineData("simple.ts", "simple.ts")]
    public void CompactPath_MatchesRustOracle(string input, string expected)
    {
        Assert.Equal(expected, LintFilters.CompactPath(input));
    }

    [Fact]
    public void CompactPath_LibSegment_KeptFromLibOnward()
    {
        Assert.Equal("lib/helpers.js", LintFilters.CompactPath("/home/user/project/lib/helpers.js"));
    }

    // ===================== FilterPylintJson (lint_cmd.rs's own mod tests) =====================

    [Fact]
    public void FilterPylintJson_NoIssues()
    {
        var result = LintFilters.FilterPylintJson("[]");

        Assert.Contains("Pylint", result, StringComparison.Ordinal);
        Assert.Contains("No issues found", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPylintJson_WithIssues_GroupedBySymbolAndFile()
    {
        const string json = """
            [
                {"type":"warning","module":"main","obj":"","line":10,"column":0,"path":"src/main.py","symbol":"unused-variable","message":"Unused variable 'x'","message-id":"W0612"},
                {"type":"warning","module":"main","obj":"foo","line":15,"column":4,"path":"src/main.py","symbol":"unused-variable","message":"Unused variable 'y'","message-id":"W0612"},
                {"type":"error","module":"utils","obj":"bar","line":20,"column":0,"path":"src/utils.py","symbol":"undefined-variable","message":"Undefined variable 'z'","message-id":"E0602"}
            ]
            """;

        var result = LintFilters.FilterPylintJson(json);

        Assert.Contains("3 issues", result, StringComparison.Ordinal);
        Assert.Contains("2 files", result, StringComparison.Ordinal);
        Assert.Contains("1 errors, 2 warnings", result, StringComparison.Ordinal);
        Assert.Contains("unused-variable (W0612)", result, StringComparison.Ordinal);
        Assert.Contains("undefined-variable (E0602)", result, StringComparison.Ordinal);
        Assert.Contains("main.py", result, StringComparison.Ordinal);
        Assert.Contains("utils.py", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPylintJson_InvalidJson_FallsBackToTruncatedRaw()
    {
        const string notJson = "pylint: command not found";

        var result = LintFilters.FilterPylintJson(notJson);

        Assert.StartsWith("Pylint output (JSON parse failed:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPylintJson_MoreThanTenFiles_ShowsOverflowHint()
    {
        var sb = new global::System.Text.StringBuilder("[");
        for (var i = 0; i < 12; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($$"""{"type":"warning","module":"m","obj":"","line":1,"column":0,"path":"f{{i}}.py","symbol":"s","message":"m","message-id":"W0001"}""");
        }

        sb.Append(']');

        var result = LintFilters.FilterPylintJson(sb.ToString());

        Assert.Contains("+2 more files", result, StringComparison.Ordinal);
    }

    // ===================== FilterGenericLint (not in Rust's mod tests, but exercised by run()) =====================

    [Fact]
    public void FilterGenericLint_NoIssues()
    {
        Assert.Equal("Lint: No issues found", LintFilters.FilterGenericLint("all good\nno problems"));
    }

    [Fact]
    public void FilterGenericLint_CountsErrorsAndWarnings()
    {
        var output = "file.js:1: warning: unused var\nfile.js:2: error: syntax error\n0 errors found elsewhere";

        var result = LintFilters.FilterGenericLint(output);

        Assert.Contains("Lint: 1 errors, 1 warnings", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGenericLint_ZeroErrorLine_NotCountedAsError()
    {
        // "0 error" is an explicit exclusion in the Rust source (lint_cmd.rs:460) so a clean "0
        // errors" summary line from the underlying tool doesn't inflate the error count.
        var result = LintFilters.FilterGenericLint("Done. 0 error(s) found.");

        Assert.Equal("Lint: No issues found", result);
    }

    [Fact]
    public void FilterGenericLint_MoreThanCapErrors_ShowsOverflowHint()
    {
        var lines = Enumerable.Range(0, 25).Select(i => $"file{i}.js: error: problem {i}");
        var output = string.Join('\n', lines);

        var result = LintFilters.FilterGenericLint(output);

        Assert.Contains("+5 more issues", result, StringComparison.Ordinal);
    }
}
