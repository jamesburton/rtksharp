using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Js;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Covers <see cref="LintCommand"/>, a faithful, test-for-test port of Rust
/// <c>src/cmds/js/lint_cmd.rs</c>'s own <c>#[cfg(test)] mod tests</c> for the <c>eslint</c>/
/// <c>pylint</c>/generic-fallback paths (all pure-function tests upstream too — no real linter
/// process is invoked, matching the Rust source's own testing convention).
/// </summary>
public sealed class LintCommandTests
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

        var result = LintCommand.FilterEslintJson(json);

        Assert.Contains("ESLint:", result, StringComparison.Ordinal);
        Assert.Contains("prefer-const", result, StringComparison.Ordinal);
        Assert.Contains("no-unused-vars", result, StringComparison.Ordinal);
        Assert.Contains("src/utils.ts", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterEslintJson_NoIssues()
    {
        const string json = """[{"filePath":"/a.ts","messages":[],"errorCount":0,"warningCount":0}]""";

        Assert.Equal("ESLint: No issues found", LintCommand.FilterEslintJson(json));
    }

    [Fact]
    public void FilterEslintJson_InvalidJson_FallsBackToTruncatedRaw()
    {
        const string notJson = "eslint: command not found";

        var result = LintCommand.FilterEslintJson(notJson);

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

        var result = LintCommand.FilterEslintJson(sb.ToString());

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

        var result = LintCommand.FilterEslintJson(sb.ToString());

        Assert.Contains("+2 more files", result, StringComparison.Ordinal);
    }

    // ===================== CompactPath (lint_cmd.rs's own mod tests) =====================

    [Theory]
    [InlineData("/Users/foo/project/src/utils.ts", "src/utils.ts")]
    [InlineData(@"C:\Users\project\src\api.ts", "src/api.ts")]
    [InlineData("simple.ts", "simple.ts")]
    public void CompactPath_MatchesRustOracle(string input, string expected)
    {
        Assert.Equal(expected, LintCommand.CompactPath(input));
    }

    [Fact]
    public void CompactPath_LibSegment_KeptFromLibOnward()
    {
        Assert.Equal("lib/helpers.js", LintCommand.CompactPath("/home/user/project/lib/helpers.js"));
    }

    // ===================== FilterPylintJson (lint_cmd.rs's own mod tests) =====================

    [Fact]
    public void FilterPylintJson_NoIssues()
    {
        var result = LintCommand.FilterPylintJson("[]");

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

        var result = LintCommand.FilterPylintJson(json);

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

        var result = LintCommand.FilterPylintJson(notJson);

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

        var result = LintCommand.FilterPylintJson(sb.ToString());

        Assert.Contains("+2 more files", result, StringComparison.Ordinal);
    }

    // ===================== StripPmPrefix (lint_cmd.rs's own mod tests) =====================

    [Fact]
    public void StripPmPrefix_Npx()
    {
        Assert.Equal(1, LintCommand.StripPmPrefix(["npx", "eslint", "src/"]));
    }

    [Fact]
    public void StripPmPrefix_Bunx()
    {
        Assert.Equal(1, LintCommand.StripPmPrefix(["bunx", "eslint", "."]));
    }

    [Fact]
    public void StripPmPrefix_PnpmExec()
    {
        Assert.Equal(2, LintCommand.StripPmPrefix(["pnpm", "exec", "eslint"]));
    }

    [Fact]
    public void StripPmPrefix_None()
    {
        Assert.Equal(0, LintCommand.StripPmPrefix(["eslint", "src/"]));
    }

    [Fact]
    public void StripPmPrefix_Empty()
    {
        Assert.Equal(0, LintCommand.StripPmPrefix([]));
    }

    // ===================== DetectLinter (lint_cmd.rs's own mod tests) =====================

    [Fact]
    public void DetectLinter_ExplicitEslint()
    {
        var (linter, isExplicit) = LintCommand.DetectLinter(["eslint", "src/"]);

        Assert.Equal("eslint", linter);
        Assert.True(isExplicit);
    }

    [Fact]
    public void DetectLinter_DefaultsOnPath()
    {
        var (linter, isExplicit) = LintCommand.DetectLinter(["src/"]);

        Assert.Equal("eslint", linter);
        Assert.False(isExplicit);
    }

    [Fact]
    public void DetectLinter_DefaultsOnFlag()
    {
        var (linter, isExplicit) = LintCommand.DetectLinter(["--max-warnings=0"]);

        Assert.Equal("eslint", linter);
        Assert.False(isExplicit);
    }

    [Fact]
    public void DetectLinter_AfterNpxStrip()
    {
        string[] fullArgs = ["npx", "eslint", "src/"];
        var skip = LintCommand.StripPmPrefix(fullArgs);
        var (linter, _) = LintCommand.DetectLinter(fullArgs[skip..]);

        Assert.Equal("eslint", linter);
    }

    [Fact]
    public void DetectLinter_AfterPnpmExecStrip_Biome()
    {
        string[] fullArgs = ["pnpm", "exec", "biome", "check"];
        var skip = LintCommand.StripPmPrefix(fullArgs);
        var (linter, _) = LintCommand.DetectLinter(fullArgs[skip..]);

        Assert.Equal("biome", linter);
    }

    // ===================== IsPythonLinter (lint_cmd.rs's own mod tests) =====================

    [Theory]
    [InlineData("ruff", true)]
    [InlineData("pylint", true)]
    [InlineData("mypy", true)]
    [InlineData("flake8", true)]
    [InlineData("eslint", false)]
    [InlineData("biome", false)]
    [InlineData("unknown", false)]
    public void IsPythonLinter_MatchesRustOracle(string linter, bool expected)
    {
        Assert.Equal(expected, LintCommand.IsPythonLinter(linter));
    }

    // ===================== FilterGenericLint (not in Rust's mod tests, but exercised by run()) =====================

    [Fact]
    public void FilterGenericLint_NoIssues()
    {
        Assert.Equal("Lint: No issues found", LintCommand.FilterGenericLint("all good\nno problems"));
    }

    [Fact]
    public void FilterGenericLint_CountsErrorsAndWarnings()
    {
        var output = "file.js:1: warning: unused var\nfile.js:2: error: syntax error\n0 errors found elsewhere";

        var result = LintCommand.FilterGenericLint(output);

        Assert.Contains("Lint: 1 errors, 1 warnings", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterGenericLint_ZeroErrorLine_NotCountedAsError()
    {
        // "0 error" is an explicit exclusion in the Rust source (lint_cmd.rs:460) so a clean "0
        // errors" summary line from the underlying tool doesn't inflate the error count.
        var result = LintCommand.FilterGenericLint("Done. 0 error(s) found.");

        Assert.Equal("Lint: No issues found", result);
    }

    [Fact]
    public void FilterGenericLint_MoreThanCapErrors_ShowsOverflowHint()
    {
        var lines = Enumerable.Range(0, 25).Select(i => $"file{i}.js: error: problem {i}");
        var output = string.Join('\n', lines);

        var result = LintCommand.FilterGenericLint(output);

        Assert.Contains("+5 more issues", result, StringComparison.Ordinal);
    }

    // ===================== RunCoreAsync: dispatch flow via a recording/responding fake executor =====================

    [Fact]
    public async Task RunCoreAsync_NoArgs_DefaultsToEslintWithJsonFlagAndCurrentDirPath()
    {
        var executor = new RecordingExecutor(_ => Ok("[]", ""));

        var exit = await LintCommand.RunCoreAsync([], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Contains("-f", request.Arguments);
        Assert.Contains("json", request.Arguments);
        Assert.Contains(".", request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_ExplicitEslintWithPath_DoesNotAppendDefaultPath()
    {
        var executor = new RecordingExecutor(_ => Ok("[]", ""));

        await LintCommand.RunCoreAsync(["eslint", "src/"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Contains("src/", request.Arguments);
        Assert.DoesNotContain(".", request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_NonZeroExitBelow128_ReturnsExitCodeAfterFiltering()
    {
        var executor = new RecordingExecutor(_ => new ExecutionResult(
            "[]", "", ExitCode: 1, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false));

        var exit = await LintCommand.RunCoreAsync([], verbose: 0, executor);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task RunCoreAsync_AbnormalExitAbove128_PrintsWarningAndReturnsExitCodeWithoutFiltering()
    {
        var executor = new RecordingExecutor(_ => new ExecutionResult(
            "garbage", "OOM killed", ExitCode: 137, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false));
        var stderr = new global::System.IO.StringWriter { NewLine = "\n" };
        var previousError = Console.Error;
        Console.SetError(stderr);

        int exit;
        try
        {
            exit = await LintCommand.RunCoreAsync([], verbose: 0, executor);
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Equal(137, exit);
        Assert.Contains("terminated abnormally", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("OOM killed", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_ProcessFailsToStart_ThrowsWithInstallHint()
    {
        var executor = new RecordingExecutor(_ => new ExecutionResult(
            "", "", ExitCode: 127, TimedDuration: TimeSpan.Zero, WasStarted: false, Failure: "not found", TimedOut: false));

        var ex = await Assert.ThrowsAsync<global::System.IO.IOException>(() => LintCommand.RunCoreAsync([], verbose: 0, executor));

        Assert.Contains("Is it installed?", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_PylintLinter_DispatchesToFilterPylintJson()
    {
        const string json = """[{"type":"error","module":"m","obj":"","line":1,"column":0,"path":"a.py","symbol":"bad","message":"x","message-id":"E001"}]""";
        var executor = new RecordingExecutor(_ => Ok(json, ""));

        var stdout = await CaptureStdoutAsync(() => LintCommand.RunCoreAsync(["pylint"], verbose: 0, executor));

        Assert.Contains("Pylint:", stdout, StringComparison.Ordinal);
        var request = Assert.Single(executor.Requests);
        Assert.Contains("--output-format=json2", request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_UnknownLinter_UsesGenericFallback()
    {
        var executor = new RecordingExecutor(_ => Ok("file.js: warning: something\n", ""));

        var stdout = await CaptureStdoutAsync(() => LintCommand.RunCoreAsync(["biome", "check"], verbose: 0, executor));

        Assert.Contains("Lint:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_RuffLinter_ForcesJsonOutputAndPassesThroughUnfiltered()
    {
        // Disclosed scope cut: ruff/mypy delegate to unported Python modules in Rust, so this port
        // passes the raw combined output through rather than fabricating a grouped format.
        const string rawJson = """{"some":"ruff json we don't parse"}""";
        var executor = new RecordingExecutor(_ => Ok(rawJson, ""));

        var stdout = await CaptureStdoutAsync(() => LintCommand.RunCoreAsync(["ruff"], verbose: 0, executor));

        // Exact-equals (not just Contains) rules out an accidental route through FilterEslintJson's
        // JSON-parse-failure fallback, which would also echo this raw body but wrapped in an
        // "... output (JSON parse failed: ...)" prefix this fixture would satisfy a mere Contains check for.
        Assert.Equal(rawJson, stdout.Trim());
        Assert.DoesNotContain("JSON parse failed", stdout, StringComparison.Ordinal);
        var request = Assert.Single(executor.Requests);
        Assert.Contains("check", request.Arguments);
        Assert.Contains("--output-format=json", request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_RuffWithExplicitTwoTokenOutputFormat_DropsFlagButLeavesStrayValue()
    {
        // Genuine, oracle-verified Rust quirk (lint_cmd.rs:143-145), not a guess: the "skip
        // --output-format if we already added it" loop skips ANY "--output-format"-prefixed token
        // unconditionally, even when this run never injected one (because the user already supplied
        // one, so the earlier `!effective_args.contains(...)` injection guard was false). A two-token
        // "--output-format text" therefore has ONLY its flag half dropped — "text" survives as a
        // stray positional with no flag before it, and ruff receives neither the user's chosen format
        // nor RTK's own forced "json". Confirmed against target/release/rtk.exe directly
        // (`rtk lint ruff --output-format text` invokes the child as `ruff text`, nothing else).
        var executor = new RecordingExecutor(_ => Ok("no issues", ""));

        await LintCommand.RunCoreAsync(["ruff", "--output-format", "text"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["text"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_RuffWithExplicitEqualsFormOutputFormat_StillInjectsJson()
    {
        // The injection guard (lint_cmd.rs:112) checks for the bare "--output-format" token via
        // Vec::contains, which does NOT match an "=" form like "--output-format=text" — so this input
        // still triggers RTK's own "check --output-format=json" injection, and the user's
        // "--output-format=text" token (which itself starts with "--output-format") is then also
        // dropped by the same unconditional skip loop as the previous test.
        var executor = new RecordingExecutor(_ => Ok("no issues", ""));

        await LintCommand.RunCoreAsync(["ruff", "--output-format=text"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Contains("check", request.Arguments);
        Assert.Contains("--output-format=json", request.Arguments);
        Assert.DoesNotContain("--output-format=text", request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_PylintWithExplicitEqualsFormOutputFormat_StillInjectsJson2AndDropsUsersFlag()
    {
        // Same oracle-verified quirk as the ruff test above: the injection guard checks for the bare
        // "--output-format" token (Vec::contains), which does NOT match the "=" form, so RTK's own
        // "--output-format=json2" is injected anyway; the user's own "--output-format=text" token is
        // then dropped by the unconditional "starts with --output-format" skip loop, and since no
        // other user arg remains, the "." default-path is appended too. Confirmed directly against
        // target/release/rtk.exe: `rtk lint pylint --output-format=text` invokes the child as
        // `pylint --output-format=json2 .`
        var executor = new RecordingExecutor(_ => Ok("no issues", ""));

        await LintCommand.RunCoreAsync(["pylint", "--output-format=text"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["--output-format=json2", "."], request.Arguments);
    }

    private static ExecutionResult Ok(string stdout, string stderr) =>
        new(stdout, stderr, ExitCode: 0, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

    private static async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var previous = Console.Out;
        var writer = new global::System.IO.StringWriter { NewLine = "\n" };
        Console.SetOut(writer);
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private sealed class RecordingExecutor(Func<ExecutionRequest, ExecutionResult> responder) : IProcessExecutor
    {
        public List<ExecutionRequest> Requests { get; } = [];

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(responder(request));
        }
    }
}
