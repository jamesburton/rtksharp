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
/// Tests for <see cref="NpmCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>npm_cmd.rs</c> (subcommand-injection logic, <c>filter_npm_output</c>'s exact line-stripping) plus
/// original coverage for the <c>npx</c> routing table that lives inline in Rust's <c>main.rs</c>
/// <c>Commands::Npx</c> arm (no dedicated Rust <c>#[cfg(test)]</c> module exists for that dispatch,
/// same situation <c>ProxyCommandTests</c> documents for <c>Commands::Proxy</c>).
/// </summary>
public sealed class NpmCommandTests
{
    // -----------------------------------------------------------------------
    // NeedsRunInjection / BuildEffectiveArgs - ports npm_cmd.rs's test_npm_subcommand_routing
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("install")]
    [InlineData("ci")]
    [InlineData("test")]
    [InlineData("start")]
    [InlineData("publish")]
    [InlineData("audit")]
    [InlineData("whoami")]
    public void NeedsRunInjection_KnownSubcommand_ReturnsFalse(string subcommand) =>
        Assert.False(NpmCommand.NeedsRunInjection([subcommand]));

    [Fact]
    public void NeedsRunInjection_EveryWhitelistEntry_ReturnsFalse()
    {
        // Exhaustive sweep of all 64 entries, mirroring Rust's own `for subcmd in NPM_SUBCOMMANDS` loop.
        foreach (var subcmd in NpmCommand.NpmSubcommands)
        {
            Assert.False(
                NpmCommand.NeedsRunInjection([subcmd]),
                $"'npm {subcmd}' should NOT inject 'run'");
        }
    }

    [Theory]
    [InlineData("build")]
    [InlineData("dev")]
    [InlineData("lint")]
    [InlineData("typecheck")]
    [InlineData("deploy")]
    public void NeedsRunInjection_ScriptName_ReturnsTrue(string script) =>
        Assert.True(NpmCommand.NeedsRunInjection([script]));

    [Fact]
    public void NeedsRunInjection_Flags_ReturnFalse()
    {
        Assert.False(NpmCommand.NeedsRunInjection(["--version"]));
        Assert.False(NpmCommand.NeedsRunInjection(["-h"]));
    }

    [Fact]
    public void NeedsRunInjection_ExplicitRun_ReturnsFalse() =>
        Assert.False(NpmCommand.NeedsRunInjection(["run", "build"]));

    [Fact]
    public void NeedsRunInjection_EmptyArgs_ReturnsTrue()
    {
        // Mirrors Rust exactly: `args.first()` is None, so both `is_run_explicit` and
        // `is_npm_subcommand` are false (the latter via `.unwrap_or(false)` on the None `.map`), and
        // `!false && !false` = true - an empty arg list gets "run" injected in front (npm_cmd.rs:79-83).
        Assert.True(NpmCommand.NeedsRunInjection([]));
    }

    [Fact]
    public void BuildEffectiveArgs_ScriptName_InjectsRunInFront()
    {
        var effective = NpmCommand.BuildEffectiveArgs(["build"]);
        Assert.Equal(["run", "build"], effective);
    }

    [Fact]
    public void BuildEffectiveArgs_KnownSubcommand_LeftUnchanged()
    {
        var effective = NpmCommand.BuildEffectiveArgs(["install"]);
        Assert.Equal(["install"], effective);
    }

    [Fact]
    public void BuildEffectiveArgs_ExplicitRun_LeftUnchanged()
    {
        var effective = NpmCommand.BuildEffectiveArgs(["run", "build"]);
        Assert.Equal(["run", "build"], effective);
    }

    // -----------------------------------------------------------------------
    // FilterNpmOutput - ports npm_cmd.rs's test_filter_npm_output / test_filter_npm_output_empty
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterNpmOutput_StripsBannersWarnNoticeAndBlankLines_KeepsRealOutput()
    {
        var output =
            "\n> project@1.0.0 build\n> next build\n\nnpm WARN deprecated inflight@1.0.6: This module is not supported\nnpm notice\n\n   Creating an optimized production build...\n   ✓ Build completed\n";

        var result = NpmCommand.FilterNpmOutput(output);

        Assert.DoesNotContain("npm WARN", result, StringComparison.Ordinal);
        Assert.DoesNotContain("npm notice", result, StringComparison.Ordinal);
        Assert.DoesNotContain("> project@", result, StringComparison.Ordinal);
        Assert.Contains("Build completed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterNpmOutput_AllFiltered_ReturnsLiteralOk()
    {
        Assert.Equal("ok", NpmCommand.FilterNpmOutput("\n\n\n"));
    }

    [Fact]
    public void FilterNpmOutput_EmptyInput_ReturnsLiteralOk()
    {
        Assert.Equal("ok", NpmCommand.FilterNpmOutput(string.Empty));
    }

    [Fact]
    public void FilterNpmOutput_SpinnerGlyphs_Stripped()
    {
        var output = "kept line here\n⸨ spinning\n⸩ spinning\nanother kept line\n";
        var result = NpmCommand.FilterNpmOutput(output);

        Assert.DoesNotContain('⸨', result);
        Assert.DoesNotContain('⸩', result);
        Assert.Contains("kept line here", result, StringComparison.Ordinal);
        Assert.Contains("another kept line", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterNpmOutput_ShortLineUnderTenChars_Stripped()
    {
        // "..." alone is under 10 chars AND contains "..." -> both conditions of the OR'd third
        // clause are true, so it is dropped; a long real output line survives.
        var output = "...\nthis is a much longer kept output line\n";
        var result = NpmCommand.FilterNpmOutput(output);

        Assert.DoesNotContain("...\n", result, StringComparison.Ordinal);
        Assert.Contains("this is a much longer kept output line", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterNpmOutput_LongLineContainingEllipsis_NotStripped()
    {
        // Rust's `&&` binds tighter than `||`: "contains ... AND under 10 chars" - a long line
        // containing "..." does NOT satisfy the length half, so it must survive.
        var output = "Compiling with options... this line is definitely over ten characters long\n";
        var result = NpmCommand.FilterNpmOutput(output);

        Assert.Contains("Compiling with options...", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // skip_env -> SKIP_ENV_VALIDATION=1 env-var wiring
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildEnvironment_SkipEnvTrue_SetsSkipEnvValidationToOne()
    {
        // CommandRunner.RunFilteredAsync always constructs its own ProcessExecutor internally (no
        // injection seam), so actually spawning "npm install" here to observe the env var would risk
        // running a real npm install as a side effect of the test suite. The env-var-building logic is
        // extracted into BuildEnvironment specifically so this wiring is verifiable without spawning
        // anything, mirroring npm_cmd.rs's `cmd.env("SKIP_ENV_VALIDATION", "1")` (npm_cmd.rs:117-119).
        var environment = NpmCommand.BuildEnvironment(skipEnv: true);

        Assert.NotNull(environment);
        Assert.Equal("1", environment!["SKIP_ENV_VALIDATION"]);
    }

    [Fact]
    public void BuildEnvironment_SkipEnvFalse_ReturnsNull()
    {
        Assert.Null(NpmCommand.BuildEnvironment(skipEnv: false));
    }

    // -----------------------------------------------------------------------
    // npx: empty-args bail (main.rs:2164-2166)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunNpxSafeAsync_EmptyArgs_PrintsExactErrorMessage_ReturnsOne()
    {
        using var console = new ConsoleErrorCapture();

        var exitCode = await NpmCommand.RunNpxSafeAsync([], verbose: 0, skipEnv: false, executor: null);

        Assert.Equal(1, exitCode);
        Assert.Equal("rtk: npx requires a command argument\n", console.ToString());
    }

    [Fact]
    public async Task DispatchNpxAsync_EmptyArgs_ThrowsWithExactMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NpmCommand.DispatchNpxAsync([], verbose: 0, skipEnv: false, executor: null));

        Assert.Equal("npx requires a command argument", ex.Message);
    }

    // -----------------------------------------------------------------------
    // npx: routing table - ResolveNpxRoute (pure, no execution)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("tsc", NpxRouteKind.Tsc)]
    [InlineData("typescript", NpxRouteKind.Tsc)]
    [InlineData("eslint", NpxRouteKind.EslintPassthrough)]
    [InlineData("next", NpxRouteKind.NextPassthrough)]
    [InlineData("prettier", NpxRouteKind.PrettierPassthrough)]
    [InlineData("playwright", NpxRouteKind.Playwright)]
    [InlineData("cowsay", NpxRouteKind.Default)]
    public void ResolveNpxRoute_RecognizedAndUnrecognizedTools_RouteCorrectly(string tool, NpxRouteKind expected)
    {
        var route = NpmCommand.ResolveNpxRoute([tool, "extra-arg"]);
        Assert.Equal(expected, route.Kind);
    }

    [Fact]
    public void ResolveNpxRoute_UnrecognizedTool_PassthroughArgsCarryFullVectorForNpmFilter()
    {
        // The bug fixed by rtk-ai/rtk#815: unknown tools must reach `npx`, carrying every arg through
        // unchanged, not get silently dispatched to `npm`.
        var route = NpmCommand.ResolveNpxRoute(["cowsay", "hello"]);

        Assert.Equal(NpxRouteKind.Default, route.Kind);
        Assert.Equal(["cowsay", "hello"], route.PassthroughArgs);
    }

    [Fact]
    public void ResolveNpxRoute_PrismaGenerate_RoutesToPrismaGenerate()
    {
        var route = NpmCommand.ResolveNpxRoute(["prisma", "generate", "--schema=foo.prisma"]);

        Assert.Equal(NpxRouteKind.PrismaGenerate, route.Kind);
        Assert.Equal(["--schema=foo.prisma"], route.RemainingArgs);
    }

    [Fact]
    public void ResolveNpxRoute_PrismaDbPush_RoutesToPrismaDbPush()
    {
        var route = NpmCommand.ResolveNpxRoute(["prisma", "db", "push", "--force-reset"]);

        Assert.Equal(NpxRouteKind.PrismaDbPush, route.Kind);
        Assert.Equal(["--force-reset"], route.RemainingArgs);
    }

    [Fact]
    public void ResolveNpxRoute_PrismaDbNotPush_PassthroughsRawPrismaDb()
    {
        // "db" alone (no "push") is not the recognized `db push` shortcut - falls to passthrough.
        var route = NpmCommand.ResolveNpxRoute(["prisma", "db", "seed"]);

        Assert.Equal(NpxRouteKind.PrismaPassthrough, route.Kind);
        Assert.Equal(["prisma", "db", "seed"], route.PassthroughArgs);
    }

    [Fact]
    public void ResolveNpxRoute_PrismaUnrecognizedSubcommand_Passthroughs()
    {
        var route = NpmCommand.ResolveNpxRoute(["prisma", "migrate", "status"]);

        Assert.Equal(NpxRouteKind.PrismaPassthrough, route.Kind);
        Assert.Equal(["prisma", "migrate", "status"], route.PassthroughArgs);
    }

    [Fact]
    public void ResolveNpxRoute_BarePrisma_PassthroughsJustPrisma()
    {
        var route = NpmCommand.ResolveNpxRoute(["prisma"]);

        Assert.Equal(NpxRouteKind.PrismaPassthrough, route.Kind);
        Assert.Equal(["prisma"], route.PassthroughArgs);
    }

    // -----------------------------------------------------------------------
    // npx: stub routes (tsc/playwright/prisma) - sane, clearly-marked failure, no execution attempted
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("tsc")]
    [InlineData("typescript")]
    public async Task DispatchNpxAsync_Tsc_ThrowsNotImplementedNamingFutureTask(string tool)
    {
        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => NpmCommand.DispatchNpxAsync([tool, "--noEmit"], verbose: 0, skipEnv: false, executor: null));

        Assert.Contains("tsc", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Phase 8 Task 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchNpxAsync_Playwright_ThrowsNotImplementedNamingFutureTask()
    {
        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => NpmCommand.DispatchNpxAsync(["playwright", "test"], verbose: 0, skipEnv: false, executor: null));

        Assert.Contains("playwright", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Phase 8 Task 6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchNpxAsync_PrismaGenerate_ThrowsNotImplementedNamingFutureTask()
    {
        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => NpmCommand.DispatchNpxAsync(["prisma", "generate"], verbose: 0, skipEnv: false, executor: null));

        Assert.Contains("prisma", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Phase 8 Task 7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchNpxAsync_PrismaDbPush_ThrowsNotImplementedNamingFutureTask()
    {
        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => NpmCommand.DispatchNpxAsync(["prisma", "db", "push"], verbose: 0, skipEnv: false, executor: null));

        Assert.Contains("prisma", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Phase 8 Task 7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunNpxSafeAsync_StubRoute_PrintsRtkPrefixedNotImplementedMessage_ReturnsOne()
    {
        using var console = new ConsoleErrorCapture();

        var exitCode = await NpmCommand.RunNpxSafeAsync(["tsc"], verbose: 0, skipEnv: false, executor: null);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("rtk: npx tsc is not yet implemented", console.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // npx: out-of-scope passthrough routes (eslint/next/prettier) and prisma-passthrough execute via
    // an injectable IProcessExecutor, mirroring Rust's manual TimedExecution branch (main.rs:2187-2211)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("eslint")]
    [InlineData("next")]
    [InlineData("prettier")]
    public async Task DispatchNpxAsync_OutOfScopeTool_RunsRawPassthrough_NoFiltering(string tool)
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult("", "", 0, TimeSpan.Zero, true, null, false));
        var exitCode = await NpmCommand.DispatchNpxAsync(
            [tool, "--fix"], verbose: 0, skipEnv: false, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("npx", fake.LastRequest!.FileName);
        Assert.Equal([tool, "--fix"], fake.LastRequest.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.LastRequest.CaptureMode);
    }

    [Fact]
    public async Task DispatchNpxAsync_PrismaPassthrough_ExecutesNpxWithFullArgsIncludingToolName()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult("", "", 0, TimeSpan.Zero, true, null, false));
        var exitCode = await NpmCommand.DispatchNpxAsync(
            ["prisma", "migrate", "status"], verbose: 0, skipEnv: false, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Equal("npx", fake.LastRequest!.FileName);
        Assert.Equal(["prisma", "migrate", "status"], fake.LastRequest.Arguments);
    }

    [Fact]
    public async Task DispatchNpxAsync_PassthroughRoute_NonZeroExitCode_Propagates()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult("", "", 3, TimeSpan.Zero, true, null, false));
        var exitCode = await NpmCommand.DispatchNpxAsync(
            ["eslint"], verbose: 0, skipEnv: false, executor: fake);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task DispatchNpxAsync_PassthroughRoute_SpawnFailure_ThrowsWithFailureDetail()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(
            new ExecutionResult("", "", 127, TimeSpan.Zero, false, "The system cannot find the file specified", false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NpmCommand.DispatchNpxAsync(["eslint"], verbose: 0, skipEnv: false, executor: fake));

        Assert.Contains("npx eslint", ex.Message, StringComparison.Ordinal);
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
            Path.Combine(Path.GetTempPath(), "rtksharp-npm-cmd-tests-" + Guid.NewGuid().ToString("N"));

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
