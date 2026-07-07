using RtkSharp.Commands.Go;
using Xunit;

namespace RtkSharp.Tests.Commands.Go;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/go/golangci_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>:
/// <c>filter_golangci_json</c>, <c>compact_path</c>, <c>parse_major_version</c>,
/// <c>classify_invocation</c>, <c>build_filtered_args</c>, and the v1/v2 source-line/token-savings
/// coverage.
/// </summary>
public sealed class GolangciLintCommandTests
{
    // ===================== filter_golangci_json =====================

    [Fact]
    public void FilterGolangciJson_NoIssues_ReportsNoIssuesFound()
    {
        const string output = """{"Issues":[]}""";
        var result = GolangciLintCommand.FilterGolangciJson(output, 1);

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

        var result = GolangciLintCommand.FilterGolangciJson(output, 1);

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
        Assert.Equal("pkg/handler/server.go", GolangciLintCommand.CompactPath("/Users/foo/project/pkg/handler/server.go"));
        Assert.Equal("cmd/main/main.go", GolangciLintCommand.CompactPath("/home/user/app/cmd/main/main.go"));
        Assert.Equal("internal/config/loader.go", GolangciLintCommand.CompactPath("/project/internal/config/loader.go"));
        Assert.Equal("file.go", GolangciLintCommand.CompactPath("relative/file.go"));
    }

    // ===================== parse_major_version =====================

    [Fact]
    public void ParseMajorVersion_V1Format_ReturnsOne()
    {
        Assert.Equal(1u, GolangciLintCommand.ParseMajorVersion("golangci-lint version 1.59.1"));
    }

    [Fact]
    public void ParseMajorVersion_V2Format_ReturnsTwo()
    {
        Assert.Equal(
            2u,
            GolangciLintCommand.ParseMajorVersion(
                "golangci-lint has version 2.10.0 built with go1.26.0 from 95dcb68a on 2026-02-17T13:05:51Z"));
    }

    [Fact]
    public void ParseMajorVersion_Empty_ReturnsOne()
    {
        Assert.Equal(1u, GolangciLintCommand.ParseMajorVersion(""));
    }

    [Fact]
    public void ParseMajorVersion_Malformed_ReturnsOne()
    {
        Assert.Equal(1u, GolangciLintCommand.ParseMajorVersion("not a version string"));
    }

    // ===================== classify_invocation =====================

    [Fact]
    public void ClassifyInvocation_RunUsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Empty(filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_WithGlobalFlagValue_UsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["--color", "never", "run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Equal(["--color", "never"], filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_WithShortGlobalFlag_UsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["-v", "run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Equal(["-v"], filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_WithInlineValueFlag_UsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["--color=never", "run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Equal(["--color=never"], filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_WithInlineConfigFlag_UsesFilteredPath()
    {
        var result = GolangciLintCommand.ClassifyInvocation(["--config=foo.yml", "run", "./..."]);

        var filtered = Assert.IsType<GolangciInvocation.FilteredRun>(result);
        Assert.Equal(["--config=foo.yml"], filtered.Invocation.GlobalArgs);
        Assert.Equal(["./..."], filtered.Invocation.RunArgs);
    }

    [Fact]
    public void ClassifyInvocation_BareCommand_IsPassthrough()
    {
        Assert.IsType<GolangciInvocation.Passthrough>(GolangciLintCommand.ClassifyInvocation([]));
    }

    [Fact]
    public void ClassifyInvocation_VersionFlag_IsPassthrough()
    {
        Assert.IsType<GolangciInvocation.Passthrough>(GolangciLintCommand.ClassifyInvocation(["--version"]));
    }

    [Fact]
    public void ClassifyInvocation_VersionSubcommand_IsPassthrough()
    {
        Assert.IsType<GolangciInvocation.Passthrough>(GolangciLintCommand.ClassifyInvocation(["version"]));
    }

    // ===================== build_filtered_args =====================

    [Fact]
    public void BuildFilteredArgs_DoesNotDuplicateRun()
    {
        var invocation = new GolangciRunInvocation([], ["./..."]);

        var result = GolangciLintCommand.BuildFilteredArgs(invocation, 2);

        Assert.Equal(["run", "--output.json.path", "stdout", "./..."], result);
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

        var result = GolangciLintCommand.FilterGolangciJson(output, 2);

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

        var result = GolangciLintCommand.FilterGolangciJson(output, 2);

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

        var result = GolangciLintCommand.FilterGolangciJson(output, 1);

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

        var result = GolangciLintCommand.FilterGolangciJson(output, 2);

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

        var result = GolangciLintCommand.FilterGolangciJson(output, 2);

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
        var result = GolangciLintCommand.FilterGolangciJson(output, 2);
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

        var filtered = GolangciLintCommand.FilterGolangciJson(raw, 2);
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
