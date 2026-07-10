using RtkSharp.Filters.Commands.Go;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Go;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/go/go_cmd.rs</c>'s and <c>src/cmds/go/golangci_cmd.rs</c>'s
/// <c>#[cfg(test)] mod tests</c>: the <c>filter_go_test_json</c>/<c>filter_go_build</c>/
/// <c>filter_go_vet</c>/<c>compact_package_name</c>/<c>filter_golangci_json</c>/<c>compact_path</c>
/// pure-function suite. Moved from <c>RtkSharp.Tests.Commands.Go.{GoCommandTests,
/// GolangciLintCommandTests}</c> when the underlying pure methods moved from
/// <c>RtkSharp.Commands.Go.{GoFilters,GolangciLintCommand}</c> to <see cref="GoFilters"/> (Task 9 of
/// the filters-library extraction). Dispatch-helper tests (<c>match_go_tool</c>,
/// <c>has_golangci_format_flag</c>, <c>classify_invocation</c>, <c>build_filtered_args</c>,
/// <c>parse_major_version</c>) were not moved — they remain in the original
/// <c>GoCommandTests</c>/<c>GolangciLintCommandTests</c> alongside <c>GoCommand</c>/
/// <c>GolangciLintCommand</c>.
/// </summary>
public sealed class GoFiltersTests
{
    // ===================== filter_go_test_json =====================

    [Fact]
    public void FilterGoTestJson_AllPass_ReportsPassCount()
    {
        const string output = """
            {"Time":"2024-01-01T10:00:00Z","Action":"run","Package":"example.com/foo","Test":"TestBar"}
            {"Time":"2024-01-01T10:00:01Z","Action":"output","Package":"example.com/foo","Test":"TestBar","Output":"=== RUN   TestBar\n"}
            {"Time":"2024-01-01T10:00:02Z","Action":"pass","Package":"example.com/foo","Test":"TestBar","Elapsed":0.5}
            {"Time":"2024-01-01T10:00:02Z","Action":"pass","Package":"example.com/foo","Elapsed":0.5}
            """;

        var result = GoFilters.FilterGoTestJson(output);

        Assert.Contains("Go test", result, StringComparison.Ordinal);
        Assert.Contains("1 passed", result, StringComparison.Ordinal);
        Assert.Contains("1 packages", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoTestJson_WithFailures_ShowsFailureDetail()
    {
        const string output = """
            {"Time":"2024-01-01T10:00:00Z","Action":"run","Package":"example.com/foo","Test":"TestFail"}
            {"Time":"2024-01-01T10:00:01Z","Action":"output","Package":"example.com/foo","Test":"TestFail","Output":"=== RUN   TestFail\n"}
            {"Time":"2024-01-01T10:00:02Z","Action":"output","Package":"example.com/foo","Test":"TestFail","Output":"    Error: expected 5, got 3\n"}
            {"Time":"2024-01-01T10:00:03Z","Action":"fail","Package":"example.com/foo","Test":"TestFail","Elapsed":0.5}
            {"Time":"2024-01-01T10:00:03Z","Action":"fail","Package":"example.com/foo","Elapsed":0.5}
            """;

        var result = GoFilters.FilterGoTestJson(output);

        Assert.Contains("1 failed", result, StringComparison.Ordinal);
        Assert.Contains("TestFail", result, StringComparison.Ordinal);
        Assert.Contains("expected 5, got 3", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoTestJson_PreservesFileLocationAndFollowupContext()
    {
        const string output = """
            {"Time":"2024-01-01T10:00:00Z","Action":"run","Package":"example.com/foo","Test":"TestFail"}
            {"Time":"2024-01-01T10:00:01Z","Action":"output","Package":"example.com/foo","Test":"TestFail","Output":"=== RUN   TestFail\n"}
            {"Time":"2024-01-01T10:00:02Z","Action":"output","Package":"example.com/foo","Test":"TestFail","Output":"    foo_test.go:42:\n"}
            {"Time":"2024-01-01T10:00:03Z","Action":"output","Package":"example.com/foo","Test":"TestFail","Output":"        values differ after normalization\n"}
            {"Time":"2024-01-01T10:00:04Z","Action":"fail","Package":"example.com/foo","Test":"TestFail","Elapsed":0.5}
            {"Time":"2024-01-01T10:00:04Z","Action":"fail","Package":"example.com/foo","Elapsed":0.5}
            """;

        var result = GoFilters.FilterGoTestJson(output);

        Assert.Contains("foo_test.go:42:", result, StringComparison.Ordinal);
        Assert.Contains("values differ after normalization", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoTestJson_TimeoutPackageFail_IsReportedAsFailure()
    {
        // When go test times out, the JSON stream has a package-level "fail" with no Test field
        // and no FailedBuild field. This should be reported as a failure, not "No tests found".
        const string output = """
            {"Time":"2024-01-01T10:00:00Z","Action":"start","Package":"example.com/foo"}
            {"Time":"2024-01-01T10:01:03Z","Action":"output","Package":"example.com/foo","Output":"*** Test killed with quit: ran too long (1m3s).\n"}
            {"Time":"2024-01-01T10:01:03Z","Action":"output","Package":"example.com/foo","Output":"FAIL\texample.com/foo\t63.001s\n"}
            {"Time":"2024-01-01T10:01:03Z","Action":"fail","Package":"example.com/foo","Elapsed":63.003}
            """;

        var result = GoFilters.FilterGoTestJson(output);

        Assert.Contains("1 failed", result, StringComparison.Ordinal);
        Assert.DoesNotContain("No tests found", result, StringComparison.Ordinal);
        Assert.Contains("FAIL", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoTestJson_NoDoubleCountOnTestFailure()
    {
        // go test -json always emits a package-level {"action":"fail"} after each test-level
        // failure. The package-level event is a cascade, not an additional failure. The summary
        // header must show "1 failed", not "2 failed".
        const string output = """
            {"Time":"2024-01-01T10:00:00Z","Action":"run","Package":"example.com/foo","Test":"TestFail"}
            {"Time":"2024-01-01T10:00:01Z","Action":"output","Package":"example.com/foo","Test":"TestFail","Output":"=== RUN   TestFail\n"}
            {"Time":"2024-01-01T10:00:02Z","Action":"output","Package":"example.com/foo","Test":"TestFail","Output":"    Error: expected 5, got 3\n"}
            {"Time":"2024-01-01T10:00:03Z","Action":"fail","Package":"example.com/foo","Test":"TestFail","Elapsed":0.5}
            {"Time":"2024-01-01T10:00:03Z","Action":"fail","Package":"example.com/foo","Elapsed":0.5}
            """;

        var result = GoFilters.FilterGoTestJson(output);

        Assert.StartsWith("Go test: 0 passed, 1 failed", result, StringComparison.Ordinal);
        Assert.Contains("TestFail", result, StringComparison.Ordinal);
        Assert.Contains("expected 5, got 3", result, StringComparison.Ordinal);
        // The package must NOT appear twice (once as "[FAIL]" and once with test details).
        Assert.Equal(1, CountOccurrences(result, "foo"));
    }

    [Fact]
    public void FilterGoTestJson_TimeoutWithSignalQuitOutput()
    {
        // Exact reproduction of the scenario from issue #958: the signal: quit line appears as a
        // separate JSON output event.
        const string output = """
            {"Action":"start","Package":"example.com/pkg"}
            {"Action":"output","Package":"example.com/pkg","Output":"*** Test killed with quit: ran too long (1m30s).\n"}
            {"Action":"output","Package":"example.com/pkg","Output":"signal: quit\n"}
            {"Action":"output","Package":"example.com/pkg","Output":"FAIL\texample.com/pkg\t90.000s\n"}
            {"Action":"fail","Package":"example.com/pkg","Elapsed":90.001}
            """;

        var result = GoFilters.FilterGoTestJson(output);

        Assert.StartsWith("Go test: 0 passed, 1 failed", result, StringComparison.Ordinal);
        Assert.DoesNotContain("No tests found", result, StringComparison.Ordinal);
        Assert.Contains("Test killed with quit", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoTestJson_TimeoutWithPassingTestsBeforeKill()
    {
        // Some tests pass before the package times out. Summary should show both pass and fail counts.
        const string output = """
            {"Action":"run","Package":"example.com/foo","Test":"TestFast"}
            {"Action":"pass","Package":"example.com/foo","Test":"TestFast","Elapsed":0.001}
            {"Action":"run","Package":"example.com/foo","Test":"TestHang"}
            {"Action":"output","Package":"example.com/foo","Output":"*** Test killed with quit: ran too long (30s).\n"}
            {"Action":"fail","Package":"example.com/foo","Elapsed":30.001}
            """;

        var result = GoFilters.FilterGoTestJson(output);

        Assert.StartsWith("Go test: 1 passed, 1 failed", result, StringComparison.Ordinal);
        Assert.DoesNotContain("No tests found", result, StringComparison.Ordinal);
        Assert.Contains("Test killed with quit", result, StringComparison.Ordinal);
    }

    // ===================== filter_go_build =====================

    [Fact]
    public void FilterGoBuild_Success_ReportsSuccess()
    {
        var result = GoFilters.FilterGoBuild("");

        Assert.Contains("Go build", result, StringComparison.Ordinal);
        Assert.Contains("Success", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoBuild_Errors_ShowsErrorList()
    {
        const string output = "# example.com/foo\n" +
            "main.go:10:5: undefined: missingFunc\n" +
            "main.go:15:2: cannot use x (type int) as type string";

        var result = GoFilters.FilterGoBuild(output);

        Assert.Contains("2 errors", result, StringComparison.Ordinal);
        Assert.Contains("undefined: missingFunc", result, StringComparison.Ordinal);
        Assert.Contains("cannot use x", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoBuild_IgnoresDownloadLinesWithErrorInPackageNames()
    {
        const string output = "go: downloading github.com/go-errors/errors v1.5.1\n" +
            "go: finding module for package example.com/foo\n" +
            "go: extracting github.com/pkg/errors v0.9.1\n" +
            "go: downloading github.com/pkg/errors v0.9.1\n" +
            "go: downloading github.com/hashicorp/go-multierror v1.1.1\n" +
            "go: downloading golang.org/x/xerrors v0.0.0-20220907171357-04be3eba64a2";

        var result = GoFilters.FilterGoBuild(output);

        Assert.Equal("Go build: Success", result);
    }

    [Theory]
    [InlineData("undefined: missingFunc")]
    [InlineData("cannot find package \"foo/bar\"")]
    [InlineData("found packages a (a.go) and b (b.go) in /tmp/rtk-go-build-probe-mix")]
    [InlineData("imports example.com/cycle/a: import cycle not allowed")]
    [InlineData("package example.com/buildtag: build constraints exclude all Go files in /tmp/rtk-go-build-probe-buildtag")]
    [InlineData("go.mod:3: invalid go version 'not-a-version': must match format 1.23.0")]
    [InlineData("go.work:1: invalid go version 'not-a-version': must match format 1.23.0")]
    [InlineData("go: go.mod file not found in current directory or any parent directory; see 'go help modules'")]
    [InlineData("no Go files in /tmp/example")]
    [InlineData("go: cannot load module missing listed in go.work file: open missing/go.mod: no such file or directory")]
    [InlineData("runtime.main_main·f: function main is undeclared in the main package")]
    [InlineData("main.go:10:5: undefined: missingFunc")]
    [InlineData("error: failed to load module")]
    public void IsGoBuildErrorLine_RecognizesRealCompilerErrors(string line)
    {
        // Exercised indirectly through FilterGoBuild, mirroring Rust's direct
        // is_go_build_error_line(...) assertions (the helper itself is private in both ports).
        var result = GoFilters.FilterGoBuild(line);
        Assert.Contains("1 errors", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("go: downloading github.com/pkg/errors v0.9.1")]
    [InlineData("go: finding module for package example.com/foo")]
    [InlineData("go: extracting github.com/pkg/errors v0.9.1")]
    [InlineData("# example.com/foo")]
    public void IsGoBuildErrorLine_RejectsNoiseLines(string line)
    {
        var result = GoFilters.FilterGoBuild(line);
        Assert.Equal("Go build: Success", result);
    }

    [Fact]
    public void FilterGoBuild_PreservesNonFileErrorShapes()
    {
        const string output = "undefined: missingFunc\n" +
            "cannot find package \"foo/bar\"\n" +
            "found packages a (a.go) and b (b.go) in /tmp/rtk-go-build-probe-mix\n" +
            "imports example.com/cycle/a: import cycle not allowed\n" +
            "package example.com/buildtag: build constraints exclude all Go files in /tmp/rtk-go-build-probe-buildtag\n" +
            "runtime.main_main·f: function main is undeclared in the main package";

        var result = GoFilters.FilterGoBuild(output);

        Assert.Contains("6 errors", result, StringComparison.Ordinal);
        Assert.Contains("undefined: missingFunc", result, StringComparison.Ordinal);
        Assert.Contains("cannot find package \"foo/bar\"", result, StringComparison.Ordinal);
        Assert.Contains("found packages a (a.go) and b (b.go)", result, StringComparison.Ordinal);
        Assert.Contains("import cycle not allowed", result, StringComparison.Ordinal);
        Assert.Contains("build constraints exclude all Go files", result, StringComparison.Ordinal);
        Assert.Contains("function main is undeclared in the main package", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoBuild_PreservesGoConfigParseErrors()
    {
        const string output = "go: errors parsing go.mod:\n" +
            "go.mod:3: invalid go version 'not-a-version': must match format 1.23.0\n" +
            "go: errors parsing go.work:\n" +
            "go.work:1: invalid go version 'not-a-version': must match format 1.23.0";

        var result = GoFilters.FilterGoBuild(output);

        Assert.Contains("2 errors", result, StringComparison.Ordinal);
        Assert.Contains("go.mod:3: invalid go version", result, StringComparison.Ordinal);
        Assert.Contains("go.work:1: invalid go version", result, StringComparison.Ordinal);
        Assert.DoesNotContain("go: errors parsing go.mod:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("go: errors parsing go.work:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoBuild_PreservesModuleRootAndWorkspaceErrors()
    {
        const string output = "go: go.mod file not found in current directory or any parent directory; see 'go help modules'\n" +
            "no Go files in /tmp/example\n" +
            "go: cannot load module missing listed in go.work file: open missing/go.mod: no such file or directory";

        var result = GoFilters.FilterGoBuild(output);

        Assert.Contains("3 errors", result, StringComparison.Ordinal);
        Assert.Contains("go.mod file not found in current directory or any parent directory", result, StringComparison.Ordinal);
        Assert.Contains("no Go files in /tmp/example", result, StringComparison.Ordinal);
        Assert.Contains("go: cannot load module missing listed in go.work file", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoBuild_PreservesPackagePatternErrors()
    {
        const string output = "pattern ./...: directory prefix . does not contain main module or its selected dependencies\n" +
            "pattern ./...: directory prefix . does not contain modules listed in go.work or their selected dependencies";

        var result = GoFilters.FilterGoBuild(output);

        Assert.Contains("2 errors", result, StringComparison.Ordinal);
        Assert.Contains("does not contain main module", result, StringComparison.Ordinal);
        Assert.Contains("does not contain modules listed in go.work", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Success", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoBuildWithExit_NonzeroExit_NeverReportsSuccess()
    {
        const string output = "opaque go build failure from stderr";

        var result = GoFilters.FilterGoBuildWithExit(output, 1);

        Assert.Contains("Go build: failed (exit 1)", result, StringComparison.Ordinal);
        Assert.Contains(output, result, StringComparison.Ordinal);
        Assert.DoesNotContain("Success", result, StringComparison.Ordinal);
    }

    // ===================== filter_go_vet =====================

    [Fact]
    public void FilterGoVet_NoIssues_ReportsNoIssuesFound()
    {
        var result = GoFilters.FilterGoVet("");

        Assert.Contains("Go vet", result, StringComparison.Ordinal);
        Assert.Contains("No issues found", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGoVet_WithIssues_ShowsIssueList()
    {
        const string output = "main.go:42:2: Printf format %d has arg x of wrong type string\n" +
            "utils.go:15:5: unreachable code";

        var result = GoFilters.FilterGoVet(output);

        Assert.Contains("2 issues", result, StringComparison.Ordinal);
        Assert.Contains("Printf format", result, StringComparison.Ordinal);
        Assert.Contains("unreachable code", result, StringComparison.Ordinal);
    }

    // ===================== compact_package_name =====================

    [Theory]
    [InlineData("github.com/user/repo/pkg", "pkg")]
    [InlineData("example.com/foo", "foo")]
    [InlineData("simple", "simple")]
    public void CompactPackageName_StripsModulePrefix(string package, string expected)
    {
        Assert.Equal(expected, GoFilters.CompactPackageName(package));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }

    // ===================== filter_golangci_json =====================

    [Fact]
    public void FilterGolangciJson_NoIssues_ReportsNoIssuesFound()
    {
        const string output = """{"Issues":[]}""";
        var result = GoFilters.FilterGolangciJson(output, 1);

        Assert.Contains("golangci-lint", result, StringComparison.Ordinal);
        Assert.Contains("No issues found", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGolangciJson_WithIssues_GroupsByLinterAndFile()
    {
        const string output = """
            {
              "Issues": [
                {
                  "FromLinter": "errcheck",
                  "Text": "Error return value not checked",
                  "Pos": {"Filename": "main.go", "Line": 42, "Column": 5}
                },
                {
                  "FromLinter": "errcheck",
                  "Text": "Error return value not checked",
                  "Pos": {"Filename": "main.go", "Line": 50, "Column": 10}
                },
                {
                  "FromLinter": "gosimple",
                  "Text": "Should use strings.Contains",
                  "Pos": {"Filename": "utils.go", "Line": 15, "Column": 2}
                }
              ]
            }
            """;

        var result = GoFilters.FilterGolangciJson(output, 1);

        Assert.Contains("3 issues", result, StringComparison.Ordinal);
        Assert.Contains("2 files", result, StringComparison.Ordinal);
        Assert.Contains("errcheck", result, StringComparison.Ordinal);
        Assert.Contains("gosimple", result, StringComparison.Ordinal);
        Assert.Contains("main.go", result, StringComparison.Ordinal);
        Assert.Contains("utils.go", result, StringComparison.Ordinal);
    }

    // ===================== compact_path =====================

    [Fact]
    public void CompactPath_PrefersPkgCmdInternalPrefixes()
    {
        Assert.Equal("pkg/handler/server.go", GoFilters.CompactPath("/Users/foo/project/pkg/handler/server.go"));
        Assert.Equal("cmd/main/main.go", GoFilters.CompactPath("/home/user/app/cmd/main/main.go"));
        Assert.Equal("internal/config/loader.go", GoFilters.CompactPath("/project/internal/config/loader.go"));
        Assert.Equal("file.go", GoFilters.CompactPath("relative/file.go"));
    }

    // ===================== v2 field parsing =====================

    [Fact]
    public void FilterGolangciJson_V2FieldsParseCleanly()
    {
        // v2 JSON includes Severity, SourceLines, Offset — must not throw.
        const string output = """
            {
              "Issues": [
                {
                  "FromLinter": "errcheck",
                  "Text": "Error return value not checked",
                  "Severity": "error",
                  "SourceLines": ["    if err := foo(); err != nil {"],
                  "Pos": {"Filename": "main.go", "Line": 42, "Column": 5, "Offset": 1024}
                }
              ]
            }
            """;

        var result = GoFilters.FilterGolangciJson(output, 2);

        Assert.Contains("errcheck", result, StringComparison.Ordinal);
        Assert.Contains("main.go", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGolangciJson_V2_ShowsSourceLines()
    {
        const string output = """
            {
              "Issues": [
                {
                  "FromLinter": "errcheck",
                  "Text": "Error return value not checked",
                  "Severity": "error",
                  "SourceLines": ["    if err := foo(); err != nil {"],
                  "Pos": {"Filename": "main.go", "Line": 42, "Column": 5, "Offset": 0}
                }
              ]
            }
            """;

        var result = GoFilters.FilterGolangciJson(output, 2);

        Assert.Contains("→", result, StringComparison.Ordinal);
        Assert.Contains("if err := foo()", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGolangciJson_V1_DoesNotShowSourceLines()
    {
        const string output = """
            {
              "Issues": [
                {
                  "FromLinter": "errcheck",
                  "Text": "Error return value not checked",
                  "Severity": "error",
                  "SourceLines": ["    if err := foo(); err != nil {"],
                  "Pos": {"Filename": "main.go", "Line": 42, "Column": 5, "Offset": 0}
                }
              ]
            }
            """;

        var result = GoFilters.FilterGolangciJson(output, 1);

        Assert.DoesNotContain("→", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGolangciJson_V2_EmptySourceLinesGraceful()
    {
        const string output = """
            {
              "Issues": [
                {
                  "FromLinter": "errcheck",
                  "Text": "Error return value not checked",
                  "Severity": "",
                  "SourceLines": [],
                  "Pos": {"Filename": "main.go", "Line": 42, "Column": 5, "Offset": 0}
                }
              ]
            }
            """;

        var result = GoFilters.FilterGolangciJson(output, 2);

        Assert.Contains("errcheck", result, StringComparison.Ordinal);
        Assert.DoesNotContain("→", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGolangciJson_V2_SourceLineTruncatedTo80Chars()
    {
        var longLine = new string('x', 120);
        var output = $$"""
            {
              "Issues": [
                {
                  "FromLinter": "lll",
                  "Text": "line too long",
                  "Severity": "",
                  "SourceLines": ["{{longLine}}"],
                  "Pos": {"Filename": "main.go", "Line": 1, "Column": 1, "Offset": 0}
                }
              ]
            }
            """;

        var result = GoFilters.FilterGolangciJson(output, 2);

        // Content truncated at 80 chars; prefix "      → " = 10 bytes (6 spaces + 3-byte arrow + space).
        // Total line max = 80 + 10 = 90 bytes.
        foreach (var line in result.Split('\n'))
        {
            if (line.TrimStart().StartsWith('→'))
            {
                Assert.True(line.Length <= 90, $"source line too long: {line.Length}");
            }
        }
    }

    [Fact]
    public void FilterGolangciJson_V2_SourceLineTruncatedNonAscii()
    {
        // Japanese characters are 3 bytes each; 30 chars = 90 bytes > 80 bytes naive slice would panic.
        var longLine = new string('日', 30); // 30 chars, 90 bytes in UTF-8
        var output = $$"""
            {
              "Issues": [
                {
                  "FromLinter": "lll",
                  "Text": "line too long",
                  "Severity": "",
                  "SourceLines": ["{{longLine}}"],
                  "Pos": {"Filename": "main.go", "Line": 1, "Column": 1, "Offset": 0}
                }
              ]
            }
            """;

        // Should not throw, and output should be <= 80 chars.
        var result = GoFilters.FilterGolangciJson(output, 2);
        foreach (var line in result.Split('\n'))
        {
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith('→'))
            {
                var content = trimmedStart.TrimStart('→').Trim();
                Assert.True(content.Length <= 80, $"content chars: {content.Length}");
            }
        }
    }

    // ===================== token savings =====================

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public void FilterGolangciJson_V2_TokenSavings()
    {
        var fixturePath = FindRepoFile("tests/fixtures/golangci_v2_json.txt");
        var raw = File.ReadAllText(fixturePath);

        var filtered = GoFilters.FilterGolangciJson(raw, 2);
        var savings = 100.0 - (CountTokens(filtered) / (double)CountTokens(raw) * 100.0);

        Assert.True(savings >= 60.0, $"Expected >=60% token savings, got {savings:F1}%\nFiltered output:\n{filtered}");
    }

    /// <summary>Walks up from the test assembly's directory to find the repo-root-relative fixture path (mirrors <c>include_str!</c>'s compile-time repo-relative resolution in Rust).</summary>
    private static string FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Could not locate fixture: {relativePath}");
    }
}
