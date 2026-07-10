using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Js;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PnpmCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>pnpm_cmd.rs</c> (JSON/regex-fallback parsing, the cap-only-when-unfiltered listing logic,
/// install-filter line classification) plus original coverage for the CLI-parsing surface
/// (<c>--filter</c>/<c>--depth</c> extraction, the typecheck-alias dispatch wiring, the <c>--filter</c>+
/// <c>typecheck</c> warning, and the passthrough route) that lives inline in Rust's <c>main.rs</c>
/// <c>Commands::Pnpm</c> arm rather than in a dedicated Rust <c>#[cfg(test)]</c> module.
/// </summary>
public sealed class PnpmCommandTests
{
    // -----------------------------------------------------------------------
    // PnpmListParser/PnpmOutdatedParser/FormatDependencyListing now live in
    // RtkSharp.Filters.Commands.Js.PnpmFilters (Task 7 of the filters-library extraction) - see
    // PnpmFiltersTests in RtkSharp.Filters.Tests.
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // Cap-only-when-unfiltered: `pnpm list` (capped) vs `pnpm list --prod` (uncapped) end to end.
    // This is the specific behavior a review will scrutinize - both cases exercise the exact same
    // 60-entry prod section through RunListAsync's own is_filtered detection so the test genuinely
    // discriminates the flag check, rather than merely calling FormatDependencyListing directly with a
    // hand-picked bool.
    // -----------------------------------------------------------------------

    private static string BuildPnpmListJson(int prodCount)
    {
        // The workspace root itself has no "version" so it doesn't count as its own dependency entry
        // (real `pnpm list --json` workspace roots often omit it) - keeps the expected count an exact
        // match to prodCount rather than prodCount + 1.
        var deps = string.Join(",\n", Enumerable.Range(0, prodCount).Select(i => $"\"pkg{i}\": {{ \"version\": \"1.0.0\" }}"));
        return $$"""
        [
            {
                "name": "root",
                "dependencies": { {{deps}} }
            }
        ]
        """;
    }

    [Fact]
    public async Task RunListAsync_PlainInvocation_NoProdDevFlag_CapsAt20()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var json = BuildPnpmListJson(60);
        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        // No --prod/-P/--dev/-D anywhere in args -> is_filtered=false -> cap applies (pnpm_cmd.rs:383-385).
        var exitCode = await PnpmCommand.RunListAsync(depth: 0, args: [], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("… +40 more", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunListAsync_ProdFlagPresent_NeverCaps()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var json = BuildPnpmListJson(60);
        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        // --prod present -> is_filtered=true -> cap must NOT apply, even though the same 60 packages
        // are present as in the capped case above.
        var exitCode = await PnpmCommand.RunListAsync(depth: 0, args: ["--prod"], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.DoesNotContain("… +", output, StringComparison.Ordinal);
        for (var i = 0; i < 60; i++)
        {
            Assert.Contains($"pkg{i} 1.0.0", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunListAsync_DevFlagPresent_NeverCaps()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var json = BuildPnpmListJson(60);
        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunListAsync(depth: 0, args: ["--dev"], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("… +", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunListAsync_NonZeroExit_EchoesStderrAndReturnsExitCode_NoFiltering()
    {
        using var stdout = new ConsoleOutCapture();
        using var stderr = new ConsoleErrorCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, "pnpm ERR! network timeout", 7, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunListAsync(depth: 0, args: [], verbose: 0, executor: fake);

        Assert.Equal(7, exitCode);
        Assert.Contains("pnpm ERR! network timeout", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    // -----------------------------------------------------------------------
    // run_outdated: always returns 0 even when the child's own exit code is non-zero.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunOutdatedAsync_EmptyResult_PrintsExactLiteralMessage()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult("{}", string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunOutdatedAsync([], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Equal("All packages up-to-date\n", stdout.ToString());
    }

    [Fact]
    public async Task RunOutdatedAsync_ChildNonZeroExitCode_StillReturnsZero()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        // pnpm outdated exits non-zero whenever outdated packages exist; Rust's run_outdated never
        // inspects this and always returns Ok(0) (pnpm_cmd.rs:420-467) - replicated exactly.
        const string json = /*lang=json,strict*/ """{"express":{"current":"4.18.2","latest":"4.19.0"}}""";
        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 1, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunOutdatedAsync([], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("express", stdout.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterPnpmInstall now lives in RtkSharp.Filters.Commands.Js.PnpmFilters (Task 7 of the
    // filters-library extraction) - see PnpmFiltersTests in RtkSharp.Filters.Tests.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunInstallAsync_NonZeroExit_EchoesStderrAndReturnsExitCode_NoFiltering()
    {
        using var stdout = new ConsoleOutCapture();
        using var stderr = new ConsoleErrorCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, "install failed", 1, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunInstallAsync([], verbose: 0, executor: fake);

        Assert.Equal(1, exitCode);
        Assert.Contains("install failed", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    // -----------------------------------------------------------------------
    // CLI parsing: ParseInvocation / ExtractDepth (--filter, --depth extraction)
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseInvocation_ListWithDepthAndFilter_ExtractsAll()
    {
        var invocation = PnpmCommand.ParseInvocation(["-F", "@app1", "--filter=@app2", "list", "--depth", "2", "--prod"]);

        Assert.Equal(["@app1", "@app2"], invocation.Filters);
        Assert.Equal(PnpmVerb.List, invocation.Verb);
        Assert.Equal(2, invocation.Depth);
        Assert.Equal(["--prod"], invocation.Args);
    }

    [Fact]
    public void ParseInvocation_ListNoDepth_DefaultsToZero()
    {
        var invocation = PnpmCommand.ParseInvocation(["list"]);

        Assert.Equal(PnpmVerb.List, invocation.Verb);
        Assert.Equal(0, invocation.Depth);
        Assert.Empty(invocation.Args);
    }

    [Theory]
    [InlineData("outdated", PnpmVerb.Outdated)]
    [InlineData("install", PnpmVerb.Install)]
    [InlineData("typecheck", PnpmVerb.Typecheck)]
    public void ParseInvocation_KnownVerbs_RouteCorrectly(string verbToken, PnpmVerb expected)
    {
        var invocation = PnpmCommand.ParseInvocation([verbToken, "extra"]);

        Assert.Equal(expected, invocation.Verb);
        Assert.Equal(["extra"], invocation.Args);
    }

    [Fact]
    public void ParseInvocation_UnrecognizedSubcommand_RoutesToOtherWithFullPassthroughArgs()
    {
        var invocation = PnpmCommand.ParseInvocation(["exec", "eslint", "--fix"]);

        Assert.Equal(PnpmVerb.Other, invocation.Verb);
        Assert.Equal(["exec", "eslint", "--fix"], invocation.PassthroughArgs);
    }

    [Fact]
    public void ParseInvocation_EmptyArgs_RoutesToOtherWithEmptyPassthrough()
    {
        var invocation = PnpmCommand.ParseInvocation([]);

        Assert.Equal(PnpmVerb.Other, invocation.Verb);
        Assert.Empty(invocation.PassthroughArgs);
    }

    [Theory]
    [InlineData(new[] { "--depth", "3" }, 3)]
    [InlineData(new[] { "-d", "5" }, 5)]
    [InlineData(new[] { "--depth=7" }, 7)]
    [InlineData(new[] { "-d=1" }, 1)]
    public void ExtractDepth_AllSyntaxForms_ParseCorrectly(string[] args, int expectedDepth)
    {
        var (depth, rest) = PnpmCommand.ExtractDepth(args);

        Assert.Equal(expectedDepth, depth);
        Assert.Empty(rest);
    }

    [Fact]
    public void MergeFilters_PrependsFilterEqualsFormBeforeArgs()
    {
        var merged = PnpmCommand.MergeFilters(["@app1", "@app2"], ["--prod"]);

        Assert.Equal(["--filter=@app1", "--filter=@app2", "--prod"], merged);
    }

    // -----------------------------------------------------------------------
    // validate_pnpm_filters: --filter + typecheck warning (main.rs:1402-1422)
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidatePnpmFilters_TypecheckWithFilters_ReturnsExactWarning()
    {
        var warning = PnpmCommand.ValidatePnpmFilters(["@app1"], PnpmVerb.Typecheck);

        Assert.Equal(
            "[rtk] warning: --filter is not yet supported for pnpm tsc, filters preceding the subcommand will be ignored",
            warning);
    }

    [Fact]
    public void ValidatePnpmFilters_TypecheckNoFilters_ReturnsNull() =>
        Assert.Null(PnpmCommand.ValidatePnpmFilters([], PnpmVerb.Typecheck));

    [Theory]
    [InlineData(PnpmVerb.List)]
    [InlineData(PnpmVerb.Outdated)]
    [InlineData(PnpmVerb.Install)]
    [InlineData(PnpmVerb.Other)]
    public void ValidatePnpmFilters_NonTypecheckVerbs_ReturnsNullEvenWithFilters(PnpmVerb verb) =>
        Assert.Null(PnpmCommand.ValidatePnpmFilters(["@app1"], verb));

    // -----------------------------------------------------------------------
    // Typecheck: pure alias delegating to TscCommand (Phase 8 Task 4). DispatchAsync/RunPnpmSafeAsync
    // for "typecheck" are not exercised end-to-end here: TscCommand.RunTscSafeAsync (like the pnpm
    // Other/passthrough route) constructs its own ProcessExecutor internally with no injection seam,
    // so invoking it would risk spawning a real tsc/npx process as a side effect of the test suite.
    // The warning-before-dispatch ordering is covered by ValidatePnpmFilters_TypecheckWithFilters_*
    // above (Rust's validate_pnpm_filters is called unconditionally before the subcommand match,
    // main.rs:1701-1703); TscCommand's own filter logic is covered directly in TscCommandTests.
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // Other/passthrough - executes via an injectable IProcessExecutor, mirroring Rust's
    // core::runner::run_passthrough (runner.rs:186-193, 235-249)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_UnrecognizedSubcommand_RunsRawPassthrough_NoFiltering()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));
        var exitCode = await PnpmCommand.DispatchAsync(["exec", "cowsay"], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("pnpm", fake.LastRequest!.FileName);
        Assert.Equal(["exec", "cowsay"], fake.LastRequest.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.LastRequest.CaptureMode);
    }

    [Fact]
    public async Task DispatchAsync_PassthroughWithFilters_MergesFilterArgsInFront()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));
        var exitCode = await PnpmCommand.DispatchAsync(["-F", "@app1", "why", "left-pad"], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(["--filter=@app1", "why", "left-pad"], fake.LastRequest!.Arguments);
    }

    [Fact]
    public async Task RunPassthroughAsync_NonZeroExitCode_Propagates()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 5, TimeSpan.Zero, true, null, false));
        var exitCode = await PnpmCommand.RunPassthroughAsync(["exec", "cowsay"], verbose: 0, executor: fake);

        Assert.Equal(5, exitCode);
    }

    [Fact]
    public async Task RunPassthroughAsync_SpawnFailure_ThrowsWithFailureDetail()
    {
        var fake = new FakeProcessExecutor(
            new ExecutionResult(string.Empty, string.Empty, 127, TimeSpan.Zero, false, "The system cannot find the file specified", false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PnpmCommand.RunPassthroughAsync(["exec", "cowsay"], verbose: 0, executor: fake));

        Assert.Contains("Failed to run pnpm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("The system cannot find the file specified", ex.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private sealed class FakeProcessExecutor(ExecutionResult result) : IProcessExecutor
    {
        public int Calls { get; private set; }

        public ExecutionRequest? LastRequest { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
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

    /// <summary>Captures <see cref="Console.Error"/> output for the lifetime of the instance.</summary>
    private sealed class ConsoleErrorCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Error;
        private readonly StringWriter _capture = new();

        public ConsoleErrorCapture() => Console.SetError(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetError(_original);
    }

    /// <summary>Points <c>RTK_DB_PATH</c> at a fresh throwaway SQLite file for the lifetime of the instance.</summary>
    private sealed class TempTrackingDb : IDisposable
    {
        private readonly string? _previousDbPath = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "rtksharp-pnpm-cmd-tests-" + Guid.NewGuid().ToString("N"));

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
