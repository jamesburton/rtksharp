using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Js;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PlaywrightCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c>
/// module in <c>playwright_cmd.rs</c> (real JSON schema with nested <c>describe</c>-block recursion,
/// the truncated-not-rounded float duration, the minutes-supporting regex fallback, and failure
/// message defaulting) plus original coverage proving the two structural distinctions from
/// <see cref="VitestCommand"/>: no reporter-passthrough escape hatch, and an unconditional tee-hint.
/// </summary>
public sealed class PlaywrightCommandTests
{
    // -----------------------------------------------------------------------
    // PlaywrightParser/RenderOutput now live in RtkSharp.Filters.Commands.Js.PlaywrightFilters (Task 7
    // of the filters-library extraction) - see PlaywrightFiltersTests in RtkSharp.Filters.Tests.
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // End-to-end: no reporter-passthrough escape hatch for `playwright test`.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Proves playwright has NO reporter-passthrough escape hatch (unlike vitest): an explicit
    /// <c>--reporter=X</c> flag passed to <c>rtk playwright test</c> is always stripped/overridden,
    /// never honored - the child always receives <c>--reporter=json</c> and never the user's flag.
    /// </summary>
    [Fact]
    public async Task PlaywrightTest_ExplicitReporterFlag_AlwaysStrippedAndOverridden()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        const string json = /*lang=json,strict*/ """
        {"stats": {"expected": 1, "unexpected": 0, "skipped": 0, "duration": 10.0}, "suites": [], "errors": []}
        """;

        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PlaywrightCommand.RunAsync(["test", "--reporter=list"], verbose: 0, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.LastRequest);

        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("--reporter=json", childArgs);
        Assert.DoesNotContain("--reporter=list", childArgs);

        var printed = stdout.ToString();
        Assert.Contains("PASS (1) FAIL (0)", printed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-<c>test</c> subcommands (e.g. <c>playwright install</c>, <c>playwright show-report</c>)
    /// pass all args through untouched - no <c>--reporter=json</c> injection, no stripping.
    /// </summary>
    [Fact]
    public async Task PlaywrightNonTestSubcommand_PassesArgsThroughUnmodified()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult("Downloading browsers...", string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PlaywrightCommand.RunAsync(["install", "--with-deps", "chromium"], verbose: 0, fake);

        Assert.Equal(0, exitCode);
        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("install", childArgs);
        Assert.Contains("--with-deps", childArgs);
        Assert.Contains("chromium", childArgs);
        Assert.DoesNotContain("--reporter=json", childArgs);
    }

    /// <summary>
    /// End-to-end: a default <c>playwright test</c> invocation forces <c>--reporter=json</c>,
    /// propagates a non-zero exit code, and formats through the shared <c>TestResult</c> formatter.
    /// </summary>
    [Fact]
    public async Task PlaywrightTest_DefaultInvocation_ForcesJsonReporter_ParsesAndPropagatesExitCode()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        const string json = /*lang=json,strict*/ """
        {"stats": {"expected": 2, "unexpected": 1, "skipped": 0, "duration": 500.0}, "suites": [], "errors": []}
        """;

        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 1, TimeSpan.Zero, true, null, false));

        var exitCode = await PlaywrightCommand.RunAsync(["test"], verbose: 0, fake);

        Assert.Equal(1, exitCode);

        var childArgs = fake.LastRequest!.Arguments;
        Assert.Contains("test", childArgs);
        Assert.Contains("--reporter=json", childArgs);

        Assert.Contains("PASS (2) FAIL (1)", stdout.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Test doubles (mirrors VitestCommandTests/PnpmCommandTests conventions exactly).
    // -----------------------------------------------------------------------

    private sealed class FakeProcessExecutor(ExecutionResult result) : IProcessExecutor
    {
        public ExecutionRequest? LastRequest { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Captures <see cref="Console.Out"/> output for the lifetime of the instance.</summary>
    private sealed class ConsoleOutCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Out;
        private readonly StringWriter _capture = new();

        public ConsoleOutCapture() => Console.SetOut(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetOut(_original);
    }

    /// <summary>Points <c>RTK_DB_PATH</c> at a fresh throwaway SQLite file for the lifetime of the instance.</summary>
    private sealed class TempTrackingDb : IDisposable
    {
        private readonly string? _previousDbPath = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "rtksharp-playwright-cmd-tests-" + Guid.NewGuid().ToString("N"));

        public string DbPath { get; }

        public TempTrackingDb()
        {
            Directory.CreateDirectory(_root);
            DbPath = Path.Combine(_root, "history.db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, DbPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, _previousDbPath);
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
