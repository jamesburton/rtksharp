using RtkSharp.Filters.Commands.Ruby;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Ruby;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/ruby/rubocop_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (18 tests covering <c>filter_rubocop_json</c>, <c>filter_rubocop_text</c>,
/// <c>compact_ruby_path</c>, and <c>severity_rank</c>).
/// </summary>
public sealed class RubocopFiltersTests
{
    private static string FilterRubocopJson(string output) => RubocopFilters.FilterRubocopJson(output);

    private static string FilterRubocopText(string output) => RubocopFilters.FilterRubocopText(output);

    private static string CompactRubyPath(string path) => RubocopFilters.CompactRubyPath(path);

    private static int SeverityRank(string severity) => RubocopFilters.SeverityRank(severity);

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string NoOffensesJson() => """
        {
          "metadata": {"rubocop_version": "1.60.0"},
          "files": [],
          "summary": {
            "offense_count": 0,
            "target_file_count": 0,
            "inspected_file_count": 15
          }
        }
        """;

    private static string WithOffensesJson() => """
        {
          "metadata": {"rubocop_version": "1.60.0"},
          "files": [
            {
              "path": "app/models/user.rb",
              "offenses": [
                {
                  "severity": "convention",
                  "message": "Trailing whitespace detected.",
                  "cop_name": "Layout/TrailingWhitespace",
                  "correctable": true,
                  "location": {"start_line": 10, "start_column": 5, "last_line": 10, "last_column": 8, "length": 3, "line": 10, "column": 5}
                },
                {
                  "severity": "convention",
                  "message": "Missing frozen string literal comment.",
                  "cop_name": "Style/FrozenStringLiteralComment",
                  "correctable": true,
                  "location": {"start_line": 1, "start_column": 1, "last_line": 1, "last_column": 1, "length": 1, "line": 1, "column": 1}
                },
                {
                  "severity": "warning",
                  "message": "Useless assignment to variable - `x`.",
                  "cop_name": "Lint/UselessAssignment",
                  "correctable": false,
                  "location": {"start_line": 25, "start_column": 5, "last_line": 25, "last_column": 6, "length": 1, "line": 25, "column": 5}
                }
              ]
            },
            {
              "path": "app/controllers/users_controller.rb",
              "offenses": [
                {
                  "severity": "convention",
                  "message": "Trailing whitespace detected.",
                  "cop_name": "Layout/TrailingWhitespace",
                  "correctable": true,
                  "location": {"start_line": 5, "start_column": 20, "last_line": 5, "last_column": 22, "length": 2, "line": 5, "column": 20}
                },
                {
                  "severity": "error",
                  "message": "Syntax error, unexpected end-of-input.",
                  "cop_name": "Lint/Syntax",
                  "correctable": false,
                  "location": {"start_line": 30, "start_column": 1, "last_line": 30, "last_column": 1, "length": 1, "line": 30, "column": 1}
                }
              ]
            }
          ],
          "summary": {
            "offense_count": 5,
            "target_file_count": 2,
            "inspected_file_count": 20
          }
        }
        """;

    [Fact]
    public void FilterRubocopNoOffenses()
    {
        var result = FilterRubocopJson(NoOffensesJson());
        Assert.Equal("ok ✓ rubocop (15 files)", result);
    }

    [Fact]
    public void FilterRubocopWithOffensesPerFile()
    {
        var result = FilterRubocopJson(WithOffensesJson());
        Assert.Contains("5 offenses (20 files)", result, StringComparison.Ordinal);
        Assert.Contains("app/controllers/users_controller.rb", result, StringComparison.Ordinal);
        Assert.Contains("app/models/user.rb", result, StringComparison.Ordinal);
        Assert.Contains(":30 Lint/Syntax — Syntax error", result, StringComparison.Ordinal);
        Assert.Contains(":10 Layout/TrailingWhitespace — Trailing whitespace", result, StringComparison.Ordinal);
        Assert.Contains(":25 Lint/UselessAssignment — Useless assignment", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRubocopSeverityOrdering()
    {
        var result = FilterRubocopJson(WithOffensesJson());
        var ctrlPos = result.IndexOf("users_controller.rb", StringComparison.Ordinal);
        var modelPos = result.IndexOf("app/models/user.rb", StringComparison.Ordinal);
        Assert.True(ctrlPos < modelPos, "Error-file should appear before convention-file");

        var errorPos = result.IndexOf(":30 Lint/Syntax", StringComparison.Ordinal);
        var convPos = result.IndexOf(":5 Layout/TrailingWhitespace", StringComparison.Ordinal);
        Assert.True(errorPos < convPos, "Error offense should appear before convention");
    }

    [Fact]
    public void FilterRubocopWithinFileLineOrdering()
    {
        var result = FilterRubocopJson(WithOffensesJson());
        var warningPos = result.IndexOf(":25 Lint/UselessAssignment", StringComparison.Ordinal);
        var conv1Pos = result.IndexOf(":1 Style/FrozenStringLiteralComment", StringComparison.Ordinal);
        Assert.True(warningPos < conv1Pos, "Warning should come before convention within same file");
    }

    [Fact]
    public void FilterRubocopCorrectableHint()
    {
        var result = FilterRubocopJson(WithOffensesJson());
        Assert.Contains("3 correctable", result, StringComparison.Ordinal);
        Assert.Contains("rubocop -A", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRubocopTextFallback()
    {
        const string text = "Inspecting 10 files\n..........\n\n10 files inspected, no offenses detected";
        var result = FilterRubocopText(text);
        Assert.Equal("ok ✓ rubocop (10 files)", result);
    }

    [Fact]
    public void FilterRubocopTextAutocorrect()
    {
        const string text = "Inspecting 15 files\n...C..CC.......\n\n15 files inspected, 3 offenses detected, 3 offenses autocorrected";
        var result = FilterRubocopText(text);
        Assert.Equal("ok ✓ rubocop -A (15 files, 3 autocorrected)", result);
    }

    [Fact]
    public void FilterRubocopEmptyOutput()
    {
        var result = FilterRubocopJson("");
        Assert.Equal("RuboCop: No output", result);
    }

    [Fact]
    public void FilterRubocopInvalidJsonFallsBack()
    {
        const string garbage = "some ruby warning\n{broken json";
        var result = FilterRubocopJson(garbage);
        Assert.False(string.IsNullOrEmpty(result), "should not throw on invalid JSON");
    }

    [Fact]
    public void CompactRubyPathTest()
    {
        Assert.Equal("app/models/user.rb", CompactRubyPath("/home/user/project/app/models/user.rb"));
        Assert.Equal("app/controllers/users_controller.rb", CompactRubyPath("app/controllers/users_controller.rb"));
        Assert.Equal("spec/models/user_spec.rb", CompactRubyPath("/project/spec/models/user_spec.rb"));
        Assert.Equal("lib/tasks/deploy.rake", CompactRubyPath("lib/tasks/deploy.rake"));
    }

    [Fact]
    public void FilterRubocopCapsOffensesPerFile()
    {
        const string json = """
            {
              "metadata": {"rubocop_version": "1.60.0"},
              "files": [
                {
                  "path": "app/models/big.rb",
                  "offenses": [
                    {"severity": "convention", "message": "msg1", "cop_name": "Cop/A", "correctable": false, "location": {"start_line": 1, "start_column": 1}},
                    {"severity": "convention", "message": "msg2", "cop_name": "Cop/B", "correctable": false, "location": {"start_line": 2, "start_column": 1}},
                    {"severity": "convention", "message": "msg3", "cop_name": "Cop/C", "correctable": false, "location": {"start_line": 3, "start_column": 1}},
                    {"severity": "convention", "message": "msg4", "cop_name": "Cop/D", "correctable": false, "location": {"start_line": 4, "start_column": 1}},
                    {"severity": "convention", "message": "msg5", "cop_name": "Cop/E", "correctable": false, "location": {"start_line": 5, "start_column": 1}},
                    {"severity": "convention", "message": "msg6", "cop_name": "Cop/F", "correctable": false, "location": {"start_line": 6, "start_column": 1}},
                    {"severity": "convention", "message": "msg7", "cop_name": "Cop/G", "correctable": false, "location": {"start_line": 7, "start_column": 1}}
                  ]
                }
              ],
              "summary": {"offense_count": 7, "target_file_count": 1, "inspected_file_count": 5}
            }
            """;
        var result = FilterRubocopJson(json);
        Assert.Contains(":5 Cop/E", result, StringComparison.Ordinal);
        Assert.DoesNotContain(":6 Cop/F", result, StringComparison.Ordinal);
        Assert.Contains("… +2 more", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRubocopTextBundlerError()
    {
        const string text = "Bundler::GemNotFound: Could not find gem 'rubocop' in any sources.";
        var result = FilterRubocopText(text);
        Assert.StartsWith("RuboCop error:", result, StringComparison.Ordinal);
        Assert.Contains("GemNotFound", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRubocopTextLoadError()
    {
        const string text = "/usr/lib/ruby/3.2.0/rubygems.rb:250: cannot load such file -- rubocop (LoadError)";
        var result = FilterRubocopText(text);
        Assert.StartsWith("RuboCop error:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRubocopTextWithOffenses()
    {
        const string text = "Inspecting 5 files\n..C..\n\n5 files inspected, 1 offense detected";
        var result = FilterRubocopText(text);
        Assert.Equal("RuboCop: 5 files inspected, 1 offense detected", result);
    }

    [Fact]
    public void SeverityRankTest()
    {
        Assert.True(SeverityRank("error") < SeverityRank("warning"));
        Assert.True(SeverityRank("warning") < SeverityRank("convention"));
        Assert.True(SeverityRank("fatal") < SeverityRank("warning"));
    }

    [Fact]
    public void TokenSavings()
    {
        var input = WithOffensesJson();
        var output = FilterRubocopJson(input);

        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"RuboCop: expected >=60% savings, got {savings:F1}% (in={inputTokens}, out={outputTokens})");
    }

    [Fact]
    public void FilterRubocopJsonWithAnsiPrefix()
    {
        const string input = "\x1b[33mWarning: something\x1b[0m\n{\"broken\": true}";
        var result = FilterRubocopJson(input);
        Assert.False(string.IsNullOrEmpty(result), "should not throw on ANSI-prefixed JSON");
    }

    [Fact]
    public void FilterRubocopCapsAtTenFiles()
    {
        var filesJson = new List<string>();
        for (var i = 1; i <= 12; i++)
        {
            filesJson.Add(
                "{\"path\": \"app/models/model_" + i + ".rb\", \"offenses\": [{\"severity\": \"convention\", \"message\": \"msg" + i
                + "\", \"cop_name\": \"Cop/X" + i + "\", \"correctable\": false, \"location\": {\"start_line\": 1, \"start_column\": 1}}]}");
        }

        var json = "{\"metadata\": {\"rubocop_version\": \"1.60.0\"}, \"files\": [" + string.Join(",", filesJson)
            + "], \"summary\": {\"offense_count\": 12, \"target_file_count\": 12, \"inspected_file_count\": 12}}";
        var result = FilterRubocopJson(json);
        Assert.Contains("… +2 more files", result, StringComparison.Ordinal);
    }
}
