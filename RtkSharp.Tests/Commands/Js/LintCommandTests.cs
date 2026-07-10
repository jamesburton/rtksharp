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
    // ===================== FilterEslintJson/FilterPylintJson/CompactPath now live in
    // RtkSharp.Filters.Commands.Js.LintFilters (Task 7 of the filters-library extraction) - see
    // LintFiltersTests in RtkSharp.Filters.Tests. =====================

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

    // ===================== FilterGenericLint now lives in
    // RtkSharp.Filters.Commands.Js.LintFilters (Task 7 of the filters-library extraction) - see
    // LintFiltersTests in RtkSharp.Filters.Tests. =====================

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
