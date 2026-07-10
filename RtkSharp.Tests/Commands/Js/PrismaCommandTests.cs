using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.Js;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PrismaCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module
/// in <c>prisma_cmd.rs</c> (<c>test_filter_generate</c>, <c>test_filter_migrate_dev</c>,
/// <c>test_extract_number</c>) plus original coverage for the CLI-parsing surface and the shared
/// non-zero-exit failure path, and EXPLICIT discriminating regression tests for the three baked-in
/// Rust-source quirks disclosed in <see cref="PrismaCommand"/>'s class remarks (unused
/// <c>output_path</c>, the literal-<c>"202"</c>-substring migration-name search, and the
/// always-hardcoded-zero pending count). None of these three quirks is "fixed" here - they are
/// captured as intentional-quirk regression tests per the phase brief.
/// </summary>
public sealed class PrismaCommandTests
{
    // -----------------------------------------------------------------------
    // FilterPrismaGenerate/FilterMigrateDev/FilterMigrateStatus/FilterMigrateDeploy/FilterDbPush/
    // ExtractNumber now live in RtkSharp.Filters.Commands.Js.PrismaFilters (Task 7 of the
    // filters-library extraction) - see PrismaFiltersTests in RtkSharp.Filters.Tests.
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // create_prisma_command - global prisma if present, else npx prisma
    // -----------------------------------------------------------------------

    [Fact]
    public void ToolExists_UnresolvableToolName_ReturnsFalse()
    {
        Assert.False(PrismaCommand.ToolExists("definitely-not-a-real-binary-xyz123"));
    }

    // -----------------------------------------------------------------------
    // Shared non-zero-exit failure path - proves it is genuinely shared across subcommands,
    // not independently reimplemented per subcommand.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunGenerateAsync_NonZeroExit_EchoesRawToStderrVerbatim_NoFiltering_ReturnsExitCode()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();
        using var stderr = new ConsoleErrorCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(
            "some raw stdout\n", "Error: P1001 cannot reach database\n", 1, TimeSpan.Zero, true, null, false));

        var exitCode = await PrismaCommand.RunGenerateAsync([], verbose: 0, executor: fake);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("some raw stdout", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Error: P1001 cannot reach database", stderr.ToString(), StringComparison.Ordinal);

        // Not filtered: the raw, unstripped error text appears verbatim, proving no filter function
        // ran on the failure path.
        Assert.DoesNotContain("Prisma Client generated", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunDbPushAsync_NonZeroExit_EchoesRawToStderrVerbatim_NoFiltering_ReturnsExitCode()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();
        using var stderr = new ConsoleErrorCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(
            string.Empty, "Error: schema validation failed\n", 3, TimeSpan.Zero, true, null, false));

        var exitCode = await PrismaCommand.RunDbPushAsync([], verbose: 0, executor: fake);

        Assert.Equal(3, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Error: schema validation failed", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Schema pushed to database", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunMigrateStatusAsync_NonZeroExit_EchoesRawToStderrVerbatim_NoFiltering_ReturnsExitCode()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();
        using var stderr = new ConsoleErrorCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(
            string.Empty, "Error: could not connect\n", 2, TimeSpan.Zero, true, null, false));

        var exitCode = await PrismaCommand.RunMigrateStatusAsync([], verbose: 0, executor: fake);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Error: could not connect", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Migrations:", stderr.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Success paths - happy-path filtering wired through the subcommand entry points.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunGenerateAsync_Success_FiltersAndTracks()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(
            "✔ Generated Prisma Client to ./node_modules/@prisma/client in 100ms\n42 models, 3 enums, 10 types generated\n",
            string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PrismaCommand.RunGenerateAsync([], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("Prisma Client generated", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Output: node_modules/@prisma/client", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunMigrateDevAsync_Success_PassesNameFlagAndFilters()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(
            "Applying migration 20260101000000_add_x\n✓ Migration applied\n",
            string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PrismaCommand.RunMigrateDevAsync("add_x", [], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("20260101000000_add_x", stdout.ToString(), StringComparison.Ordinal);

        // Only assert the subcommand-specific tail: the leading executable/base-args prefix depends
        // on whether "prisma" happens to be directly resolvable on the test machine's PATH
        // (create_prisma_command, like Rust's own tool_exists-based resolution, is not injectable/
        // deterministic - Rust's own test suite does not test this resolution directly either).
        Assert.Equal(new[] { "migrate", "dev", "--name", "add_x" }, fake.LastRequest?.Arguments.TakeLast(4).ToArray());
    }

    [Fact]
    public async Task RunDbPushAsync_Success_FiltersAndTracks()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(
            "CREATE TABLE \"A\" (id TEXT);\n", string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PrismaCommand.RunDbPushAsync([], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("Schema pushed to database", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(new[] { "db", "push" }, fake.LastRequest?.Arguments.TakeLast(2).ToArray());
    }

    // -----------------------------------------------------------------------
    // CLI dispatch surface
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractMigrationName_LongForm_ExtractsAndStripsFlag()
    {
        var (name, rest) = PrismaCommand.ExtractMigrationName(["--name", "add_sessions", "--create-only"]);

        Assert.Equal("add_sessions", name);
        Assert.Equal(new[] { "--create-only" }, rest);
    }

    [Fact]
    public void ExtractMigrationName_ShortFormEquals_ExtractsAndStripsFlag()
    {
        var (name, rest) = PrismaCommand.ExtractMigrationName(["-n=quick_fix"]);

        Assert.Equal("quick_fix", name);
        Assert.Empty(rest);
    }

    [Fact]
    public void ExtractMigrationName_Absent_ReturnsNullAndAllArgsUntouched()
    {
        var (name, rest) = PrismaCommand.ExtractMigrationName(["--create-only"]);

        Assert.Null(name);
        Assert.Equal(new[] { "--create-only" }, rest);
    }

    [Fact]
    public async Task DispatchAsync_EmptyArgs_ThrowsCommandArgumentParseException()
    {
        // `prisma`'s subcommand shape mirrors clap's PrismaCommands enum (closed set) — a missing
        // subcommand is a clap-layer parse failure. `prisma` is Rust-classified PASSTHROUGH, not
        // RTK_META_COMMANDS, so the real oracle falls back to a raw PATH-exec attempt of "prisma"
        // (exit 127 when not installed, verified against target/release/rtk.exe) rather than a
        // clean clap-style exit 2 — this now throws so RtkProgram's dispatch layer can re-route.
        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => PrismaCommand.DispatchAsync([], verbose: 0, executor: null));
        Assert.Contains("requires a subcommand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_UnknownSubcommand_ThrowsCommandArgumentParseException()
    {
        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => PrismaCommand.DispatchAsync(["frobnicate"], verbose: 0, executor: null));
        Assert.Contains("unknown prisma subcommand 'frobnicate'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_MigrateEmptyArgs_ThrowsCommandArgumentParseException()
    {
        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => PrismaCommand.DispatchAsync(["migrate"], verbose: 0, executor: null));
        Assert.Contains("prisma migrate requires a subcommand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_MigrateUnknownSubcommand_ThrowsCommandArgumentParseException()
    {
        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => PrismaCommand.DispatchAsync(["migrate", "frobnicate"], verbose: 0, executor: null));
        Assert.Contains("unknown prisma migrate subcommand 'frobnicate'", ex.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Helpers (mirrors PnpmCommandTests' fakes/capture harness exactly)
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
            Path.Combine(Path.GetTempPath(), "rtksharp-prisma-cmd-tests-" + Guid.NewGuid().ToString("N"));

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
