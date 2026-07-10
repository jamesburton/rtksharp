using RtkSharp.Filters.Commands.Ruby;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Ruby;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/ruby/rspec_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// subset covering <c>filter_rspec_output</c>, <c>filter_rspec_text</c>, and <c>strip_noise</c>. The
/// <c>has_format</c> flag-detection tests remain in
/// <c>RtkSharp.Tests.Commands.Ruby.RspecCommandTests</c> alongside <c>RspecCommand.RunAsync</c>'s
/// dispatch logic.
/// </summary>
public sealed class RspecFiltersTests
{
    private static string FilterRspecOutput(string output) => RspecFilters.FilterRspecOutput(output);

    private static string FilterRspecText(string output) => RspecFilters.FilterRspecText(output);

    private static string StripNoise(string output) => RspecFilters.StripNoise(output);

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string AllPassJson() => """
        {
          "version": "3.12.0",
          "examples": [
            {
              "id": "./spec/models/user_spec.rb[1:1]",
              "description": "is valid with valid attributes",
              "full_description": "User is valid with valid attributes",
              "status": "passed",
              "file_path": "./spec/models/user_spec.rb",
              "line_number": 5,
              "run_time": 0.001234,
              "pending_message": null,
              "exception": null
            },
            {
              "id": "./spec/models/user_spec.rb[1:2]",
              "description": "validates email format",
              "full_description": "User validates email format",
              "status": "passed",
              "file_path": "./spec/models/user_spec.rb",
              "line_number": 12,
              "run_time": 0.0008,
              "pending_message": null,
              "exception": null
            }
          ],
          "summary": {
            "duration": 0.015,
            "example_count": 2,
            "failure_count": 0,
            "pending_count": 0,
            "errors_outside_of_examples_count": 0
          },
          "summary_line": "2 examples, 0 failures"
        }
        """;

    private static string WithFailuresJson() => """
        {
          "version": "3.12.0",
          "examples": [
            {
              "id": "./spec/models/user_spec.rb[1:1]",
              "description": "is valid",
              "full_description": "User is valid",
              "status": "passed",
              "file_path": "./spec/models/user_spec.rb",
              "line_number": 5,
              "run_time": 0.001,
              "pending_message": null,
              "exception": null
            },
            {
              "id": "./spec/models/user_spec.rb[1:2]",
              "description": "saves to database",
              "full_description": "User saves to database",
              "status": "failed",
              "file_path": "./spec/models/user_spec.rb",
              "line_number": 10,
              "run_time": 0.002,
              "pending_message": null,
              "exception": {
                "class": "RSpec::Expectations::ExpectationNotMetError",
                "message": "expected true but got false",
                "backtrace": [
                  "/usr/local/lib/ruby/gems/3.2.0/gems/rspec-expectations-3.12.0/lib/rspec/expectations/fail_with.rb:37:in `fail_with'",
                  "./spec/models/user_spec.rb:11:in `block (2 levels) in <top (required)>'"
                ]
              }
            }
          ],
          "summary": {
            "duration": 0.123,
            "example_count": 2,
            "failure_count": 1,
            "pending_count": 0,
            "errors_outside_of_examples_count": 0
          },
          "summary_line": "2 examples, 1 failure"
        }
        """;

    private static string WithPendingJson() => """
        {
          "version": "3.12.0",
          "examples": [
            {
              "id": "./spec/models/post_spec.rb[1:1]",
              "description": "creates a post",
              "full_description": "Post creates a post",
              "status": "passed",
              "file_path": "./spec/models/post_spec.rb",
              "line_number": 4,
              "run_time": 0.002,
              "pending_message": null,
              "exception": null
            },
            {
              "id": "./spec/models/post_spec.rb[1:2]",
              "description": "validates title",
              "full_description": "Post validates title",
              "status": "pending",
              "file_path": "./spec/models/post_spec.rb",
              "line_number": 8,
              "run_time": 0.0,
              "pending_message": "Not yet implemented",
              "exception": null
            }
          ],
          "summary": {
            "duration": 0.05,
            "example_count": 2,
            "failure_count": 0,
            "pending_count": 1,
            "errors_outside_of_examples_count": 0
          },
          "summary_line": "2 examples, 0 failures, 1 pending"
        }
        """;

    private static string LargeSuiteJson() => """
        {
          "version": "3.12.0",
          "examples": [
            {"id":"1","description":"test1","full_description":"Suite test1","status":"passed","file_path":"./spec/a_spec.rb","line_number":1,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"2","description":"test2","full_description":"Suite test2","status":"passed","file_path":"./spec/a_spec.rb","line_number":2,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"3","description":"test3","full_description":"Suite test3","status":"passed","file_path":"./spec/a_spec.rb","line_number":3,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"4","description":"test4","full_description":"Suite test4","status":"passed","file_path":"./spec/a_spec.rb","line_number":4,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"5","description":"test5","full_description":"Suite test5","status":"passed","file_path":"./spec/a_spec.rb","line_number":5,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"6","description":"test6","full_description":"Suite test6","status":"passed","file_path":"./spec/a_spec.rb","line_number":6,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"7","description":"test7","full_description":"Suite test7","status":"passed","file_path":"./spec/a_spec.rb","line_number":7,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"8","description":"test8","full_description":"Suite test8","status":"passed","file_path":"./spec/a_spec.rb","line_number":8,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"9","description":"test9","full_description":"Suite test9","status":"passed","file_path":"./spec/a_spec.rb","line_number":9,"run_time":0.01,"pending_message":null,"exception":null},
            {"id":"10","description":"test10","full_description":"Suite test10","status":"passed","file_path":"./spec/a_spec.rb","line_number":10,"run_time":0.01,"pending_message":null,"exception":null}
          ],
          "summary": {
            "duration": 1.234,
            "example_count": 10,
            "failure_count": 0,
            "pending_count": 0,
            "errors_outside_of_examples_count": 0
          },
          "summary_line": "10 examples, 0 failures"
        }
        """;

    [Fact]
    public void FilterRspecAllPass()
    {
        var result = FilterRspecOutput(AllPassJson());
        Assert.StartsWith("✓ RSpec:", result, StringComparison.Ordinal);
        Assert.Contains("2 passed", result, StringComparison.Ordinal);
        Assert.True(result.Contains("0.01s", StringComparison.Ordinal) || result.Contains("0.02s", StringComparison.Ordinal));
    }

    [Fact]
    public void FilterRspecWithFailures()
    {
        var result = FilterRspecOutput(WithFailuresJson());
        Assert.Contains("1 passed, 1 failed", result, StringComparison.Ordinal);
        Assert.Contains("✗ User saves to database", result, StringComparison.Ordinal);
        Assert.Contains("user_spec.rb:10", result, StringComparison.Ordinal);
        Assert.Contains("ExpectationNotMetError", result, StringComparison.Ordinal);
        Assert.Contains("expected true but got false", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRspecWithPending()
    {
        var result = FilterRspecOutput(WithPendingJson());
        Assert.StartsWith("✓ RSpec:", result, StringComparison.Ordinal);
        Assert.Contains("1 passed", result, StringComparison.Ordinal);
        Assert.Contains("1 pending", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRspecEmptyOutput()
    {
        var result = FilterRspecOutput("");
        Assert.Equal("RSpec: No output", result);
    }

    [Fact]
    public void FilterRspecNoExamples()
    {
        const string json = """
            {
              "version": "3.12.0",
              "examples": [],
              "summary": {
                "duration": 0.001,
                "example_count": 0,
                "failure_count": 0,
                "pending_count": 0,
                "errors_outside_of_examples_count": 0
              }
            }
            """;
        var result = FilterRspecOutput(json);
        Assert.Equal("RSpec: No examples found", result);
    }

    [Fact]
    public void FilterRspecErrorsOutsideExamples()
    {
        const string json = """
            {
              "version": "3.12.0",
              "examples": [],
              "summary": {
                "duration": 0.01,
                "example_count": 0,
                "failure_count": 0,
                "pending_count": 0,
                "errors_outside_of_examples_count": 1
              }
            }
            """;
        var result = FilterRspecOutput(json);
        Assert.DoesNotContain("No examples found", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRspecTextFallback()
    {
        const string text = "\n..F.\n\nFailures:\n\n" +
            "  1) User is valid\n" +
            "     Failure/Error: expect(user).to be_valid\n" +
            "       expected true got false\n" +
            "     # ./spec/models/user_spec.rb:5\n\n" +
            "4 examples, 1 failure\n";
        var result = FilterRspecOutput(text);
        Assert.Contains("RSpec:", result, StringComparison.Ordinal);
        Assert.Contains("4 examples, 1 failure", result, StringComparison.Ordinal);
        Assert.Contains("✗", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRspecTextFallbackExtractsFailures()
    {
        const string text = "Randomized with seed 12345\n..F...E..\n\nFailures:\n\n" +
            "  1) User#full_name returns first and last name\n" +
            "     Failure/Error: expect(user.full_name).to eq(\"John Doe\")\n" +
            "       expected: \"John Doe\"\n" +
            "            got: \"John D.\"\n" +
            "     # /usr/local/lib/ruby/gems/3.2.0/gems/rspec-expectations-3.12.0/lib/rspec/expectations/fail_with.rb:37\n" +
            "     # ./spec/models/user_spec.rb:15\n\n" +
            "  2) Api::Controller#index fails\n" +
            "     Failure/Error: get :index\n" +
            "       expected 200 got 500\n" +
            "     # ./spec/controllers/api_spec.rb:42\n\n" +
            "9 examples, 2 failures\n";
        var result = FilterRspecText(text);
        Assert.Contains("2 failures", result, StringComparison.Ordinal);
        Assert.Contains("✗", result, StringComparison.Ordinal);
        Assert.Contains("spec/models/user_spec.rb:15", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRspecBacktraceFiltersGems()
    {
        var result = FilterRspecOutput(WithFailuresJson());
        Assert.Contains("user_spec.rb:11", result, StringComparison.Ordinal);
        Assert.DoesNotContain("gems/rspec-expectations", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRspecExceptionClassShortened()
    {
        var result = FilterRspecOutput(WithFailuresJson());
        Assert.Contains("ExpectationNotMetError", result, StringComparison.Ordinal);
        Assert.DoesNotContain("RSpec::Expectations::ExpectationNotMetError", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRspecManyFailuresCapsAtFive()
    {
        const string json = """
            {
              "version": "3.12.0",
              "examples": [
                {"id":"1","description":"test 1","full_description":"A test 1","status":"failed","file_path":"./spec/a_spec.rb","line_number":5,"run_time":0.001,"pending_message":null,"exception":{"class":"RuntimeError","message":"boom 1","backtrace":["./spec/a_spec.rb:6:in `block'"]}},
                {"id":"2","description":"test 2","full_description":"A test 2","status":"failed","file_path":"./spec/a_spec.rb","line_number":10,"run_time":0.001,"pending_message":null,"exception":{"class":"RuntimeError","message":"boom 2","backtrace":["./spec/a_spec.rb:11:in `block'"]}},
                {"id":"3","description":"test 3","full_description":"A test 3","status":"failed","file_path":"./spec/a_spec.rb","line_number":15,"run_time":0.001,"pending_message":null,"exception":{"class":"RuntimeError","message":"boom 3","backtrace":["./spec/a_spec.rb:16:in `block'"]}},
                {"id":"4","description":"test 4","full_description":"A test 4","status":"failed","file_path":"./spec/a_spec.rb","line_number":20,"run_time":0.001,"pending_message":null,"exception":{"class":"RuntimeError","message":"boom 4","backtrace":["./spec/a_spec.rb:21:in `block'"]}},
                {"id":"5","description":"test 5","full_description":"A test 5","status":"failed","file_path":"./spec/a_spec.rb","line_number":25,"run_time":0.001,"pending_message":null,"exception":{"class":"RuntimeError","message":"boom 5","backtrace":["./spec/a_spec.rb:26:in `block'"]}},
                {"id":"6","description":"test 6","full_description":"A test 6","status":"failed","file_path":"./spec/a_spec.rb","line_number":30,"run_time":0.001,"pending_message":null,"exception":{"class":"RuntimeError","message":"boom 6","backtrace":["./spec/a_spec.rb:31:in `block'"]}}
              ],
              "summary": {
                "duration": 0.05,
                "example_count": 6,
                "failure_count": 6,
                "pending_count": 0,
                "errors_outside_of_examples_count": 0
              },
              "summary_line": "6 examples, 6 failures"
            }
            """;
        var result = FilterRspecOutput(json);
        Assert.Contains("1. ✗", result, StringComparison.Ordinal);
        Assert.Contains("5. ✗", result, StringComparison.Ordinal);
        Assert.DoesNotContain("6. ✗", result, StringComparison.Ordinal);
        Assert.Contains("+1 more", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterRspecTextFallbackNoSummary()
    {
        const string text = "some output\nwithout a summary line";
        var result = FilterRspecOutput(text);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void FilterRspecInvalidJsonFallsBack()
    {
        const string garbage = "not json at all { broken";
        var result = FilterRspecOutput(garbage);
        Assert.False(string.IsNullOrEmpty(result), "should not throw on invalid JSON");
    }

    // ── Noise stripping tests ─────────────────────────────────────────────────

    [Fact]
    public void StripNoiseSpring()
    {
        const string input = "Running via Spring preloader in process 12345\n...\n3 examples, 0 failures";
        var result = StripNoise(input);
        Assert.DoesNotContain("Spring", result, StringComparison.Ordinal);
        Assert.Contains("3 examples", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripNoiseSimplecov()
    {
        const string input = "...\n\nCoverage report generated for RSpec to /app/coverage.\n142 / 200 LOC (71.0%) covered.\n\n3 examples, 0 failures";
        var result = StripNoise(input);
        Assert.DoesNotContain("Coverage report", result, StringComparison.Ordinal);
        Assert.DoesNotContain("LOC", result, StringComparison.Ordinal);
        Assert.Contains("3 examples", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripNoiseDeprecation()
    {
        const string input = "DEPRECATION WARNING: Using `return` in before callbacks is deprecated.\n...\n3 examples, 0 failures";
        var result = StripNoise(input);
        Assert.DoesNotContain("DEPRECATION", result, StringComparison.Ordinal);
        Assert.Contains("3 examples", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripNoiseFinishedIn()
    {
        const string input = "...\nFinished in 12.34 seconds (files took 3.21 seconds to load)\n3 examples, 0 failures";
        var result = StripNoise(input);
        Assert.DoesNotContain("Finished in 12.34", result, StringComparison.Ordinal);
        Assert.Contains("3 examples", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripNoiseCapybaraScreenshot()
    {
        const string input = "...\n     saved screenshot to /tmp/capybara/screenshots/2026_failed.png\n3 examples, 1 failure";
        var result = StripNoise(input);
        Assert.Contains("[screenshot:", result, StringComparison.Ordinal);
        Assert.Contains("failed.png", result, StringComparison.Ordinal);
        Assert.DoesNotContain("saved screenshot to", result, StringComparison.Ordinal);
    }

    // ── Token savings tests ───────────────────────────────────────────────────

    [Fact]
    public void TokenSavingsAllPass()
    {
        var input = LargeSuiteJson();
        var output = FilterRspecOutput(input);

        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"RSpec all-pass: expected >=60% savings, got {savings:F1}% (in={inputTokens}, out={outputTokens})");
    }

    [Fact]
    public void TokenSavingsWithFailures()
    {
        var input = WithFailuresJson();
        var output = FilterRspecOutput(input);

        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 60.0, $"RSpec failures: expected >=60% savings, got {savings:F1}% (in={inputTokens}, out={outputTokens})");
    }

    [Fact]
    public void TokenSavingsTextFallback()
    {
        const string input = "Running via Spring preloader in process 12345\n" +
            "Randomized with seed 54321\n" +
            "..F...E..F..\n\n" +
            "Failures:\n\n" +
            "  1) User#full_name returns first and last name\n" +
            "     Failure/Error: expect(user.full_name).to eq(\"John Doe\")\n" +
            "       expected: \"John Doe\"\n" +
            "            got: \"John D.\"\n" +
            "     # /usr/local/lib/ruby/gems/3.2.0/gems/rspec-expectations-3.12.0/lib/rspec/expectations/fail_with.rb:37\n" +
            "     # ./spec/models/user_spec.rb:15\n" +
            "     # /usr/local/lib/ruby/gems/3.2.0/gems/rspec-core-3.12.0/lib/rspec/core/example.rb:258\n\n" +
            "  2) Api::Controller#index returns success\n" +
            "     Failure/Error: get :index\n" +
            "       expected 200 got 500\n" +
            "     # /usr/local/lib/ruby/gems/3.2.0/gems/rspec-expectations-3.12.0/lib/rspec/expectations/fail_with.rb:37\n" +
            "     # ./spec/controllers/api_spec.rb:42\n" +
            "     # /usr/local/lib/ruby/gems/3.2.0/gems/rspec-core-3.12.0/lib/rspec/core/example.rb:258\n\n" +
            "Failed examples:\n\n" +
            "rspec ./spec/models/user_spec.rb:15 # User#full_name returns first and last name\n" +
            "rspec ./spec/controllers/api_spec.rb:42 # Api::Controller#index returns success\n\n" +
            "12 examples, 2 failures\n\n" +
            "Coverage report generated for RSpec to /app/coverage.\n" +
            "142 / 200 LOC (71.0%) covered.\n";

        var output = FilterRspecText(input);

        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(output);
        var savings = 100.0 - (outputTokens / (double)inputTokens * 100.0);

        Assert.True(savings >= 30.0, $"RSpec text fallback: expected >=30% savings, got {savings:F1}% (in={inputTokens}, out={outputTokens})");
    }

    // ── ANSI handling tests ───────────────────────────────────────────────────

    [Fact]
    public void FilterRspecAnsiWrappedJson()
    {
        const string input = "\x1b[32m{\"version\":\"3.12.0\"\x1b[0m broken json";
        var result = FilterRspecOutput(input);
        Assert.False(string.IsNullOrEmpty(result), "should not throw on ANSI-wrapped JSON");
    }

    // ── Text fallback >5 failures truncation (Issue 9) ────────────────────────

    [Fact]
    public void FilterRspecTextManyFailuresCapsAtFive()
    {
        const string text = "Randomized with seed 12345\n" +
            ".......FFFFFFF\n\n" +
            "Failures:\n\n" +
            "  1) User#full_name fails\n" +
            "     Failure/Error: expect(true).to eq(false)\n" +
            "     # ./spec/models/user_spec.rb:5\n\n" +
            "  2) Post#title fails\n" +
            "     Failure/Error: expect(true).to eq(false)\n" +
            "     # ./spec/models/post_spec.rb:10\n\n" +
            "  3) Comment#body fails\n" +
            "     Failure/Error: expect(true).to eq(false)\n" +
            "     # ./spec/models/comment_spec.rb:15\n\n" +
            "  4) Session#token fails\n" +
            "     Failure/Error: expect(true).to eq(false)\n" +
            "     # ./spec/models/session_spec.rb:20\n\n" +
            "  5) Profile#avatar fails\n" +
            "     Failure/Error: expect(true).to eq(false)\n" +
            "     # ./spec/models/profile_spec.rb:25\n\n" +
            "  6) Team#members fails\n" +
            "     Failure/Error: expect(true).to eq(false)\n" +
            "     # ./spec/models/team_spec.rb:30\n\n" +
            "  7) Role#permissions fails\n" +
            "     Failure/Error: expect(true).to eq(false)\n" +
            "     # ./spec/models/role_spec.rb:35\n\n" +
            "14 examples, 7 failures\n";

        var result = FilterRspecText(text);
        Assert.Contains("1. ✗", result, StringComparison.Ordinal);
        Assert.Contains("5. ✗", result, StringComparison.Ordinal);
        Assert.DoesNotContain("6. ✗", result, StringComparison.Ordinal);
        Assert.Contains("+2 more", result, StringComparison.Ordinal);
    }

    // ── Header -> FailedExamples transition (Issue 13) ────────────────────────

    [Fact]
    public void FilterRspecTextHeaderToFailedExamples()
    {
        const string text = "..F..\n\nFailed examples:\n\n" +
            "rspec ./spec/models/user_spec.rb:5 # User is valid\n\n" +
            "5 examples, 1 failure\n";
        var result = FilterRspecText(text);
        Assert.Contains("5 examples, 1 failure", result, StringComparison.Ordinal);
        Assert.Contains("RSpec:", result, StringComparison.Ordinal);
    }
}
