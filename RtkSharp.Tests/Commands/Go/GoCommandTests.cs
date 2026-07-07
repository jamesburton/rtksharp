using RtkSharp.Commands.Go;
using Xunit;

namespace RtkSharp.Tests.Commands.Go;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/go/go_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>: the
/// <c>filter_go_test_json</c>/<c>filter_go_build</c>/<c>filter_go_vet</c>/<c>compact_package_name</c>
/// pure-function suite, plus the <c>match_go_tool</c>/<c>has_golangci_format_flag</c> dispatch-helper
/// tests. Rust's own test suite for this module never drives the dispatch functions through a live
/// process (see <see cref="GoCommand"/>'s remarks), so neither does this port.
/// </summary>
public sealed class GoCommandTests
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

    // ===================== match_go_tool / has_golangci_format_flag =====================

    [Fact]
    public void MatchGoTool_GolangciLint_MatchesAndReturnsRemainingArgs()
    {
        string[] args = ["tool", "golangci-lint", "run", "./..."];
        var matched = GoCommand.MatchGoTool(args);

        Assert.NotNull(matched);
        Assert.Equal(RtkSharp.Commands.Go.GoTool.GolangciLint, matched!.Value.Tool);
        Assert.Equal(["run", "./..."], matched.Value.ToolArgs);
    }

    [Fact]
    public void MatchGoTool_Bare_MatchesWithEmptyRemainingArgs()
    {
        string[] args = ["tool", "golangci-lint"];
        var matched = GoCommand.MatchGoTool(args);

        Assert.NotNull(matched);
        Assert.Equal(RtkSharp.Commands.Go.GoTool.GolangciLint, matched!.Value.Tool);
        Assert.Empty(matched.Value.ToolArgs);
    }

    [Fact]
    public void MatchGoTool_RejectsUnknownToolsAndShapes()
    {
        Assert.Null(GoCommand.MatchGoTool(["tool", "pprof"]));
        Assert.Null(GoCommand.MatchGoTool(["tool"]));
        Assert.Null(GoCommand.MatchGoTool(["test", "./..."]));
        Assert.Null(GoCommand.MatchGoTool([]));
    }

    [Fact]
    public void HasGolangciFormatFlag_V1_Detected()
    {
        Assert.True(GoCommand.HasGolangciFormatFlag(["--out-format=json"]));
        Assert.True(GoCommand.HasGolangciFormatFlag(["./...", "--out-format", "json"]));
    }

    [Fact]
    public void HasGolangciFormatFlag_V2_Detected()
    {
        Assert.True(GoCommand.HasGolangciFormatFlag(["--output.json.path", "stdout"]));
        Assert.True(GoCommand.HasGolangciFormatFlag(["--output.json.path=stdout"]));
    }

    [Fact]
    public void HasGolangciFormatFlag_Absent_ReturnsFalse()
    {
        Assert.False(GoCommand.HasGolangciFormatFlag(["run", "./..."]));
        Assert.False(GoCommand.HasGolangciFormatFlag([]));
        Assert.False(GoCommand.HasGolangciFormatFlag(["--fix"]));
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
}
