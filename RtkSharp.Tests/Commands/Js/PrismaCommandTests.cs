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
    // filter_prisma_generate - ports prisma_cmd.rs's test_filter_generate
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterPrismaGenerate_StripsBannerAndImportNoise_ReportsHeader()
    {
        const string output = """

        Prisma schema loaded from prisma/schema.prisma

        ✔ Generated Prisma Client (v5.7.0) to ./node_modules/@prisma/client in 234ms

        Start by importing your Prisma Client:

        import { PrismaClient } from '@prisma/client'

        42 models, 18 enums, 890 types generated

        """;

        var result = PrismaCommand.FilterPrismaGenerate(output);

        Assert.Contains("Prisma Client generated", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Prisma schema loaded", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Start by importing", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPrismaGenerate_StripsBoxDrawingAndBlockGlyphLines()
    {
        const string output = "█▀▄ ascii art banner\n┌───┐\n│ box │\n└───┘\nPrisma Client generated fine\n";

        var result = PrismaCommand.FilterPrismaGenerate(output);

        Assert.DoesNotContain("ascii art banner", result, StringComparison.Ordinal);
        Assert.DoesNotContain("box", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression test for compatibility-ledger quirk #1 (class remarks item 1): the detected
    /// <c>@prisma</c> output-path line is NEVER used for its actual content - only whether one was
    /// found gates the hardcoded literal string. Feeds a line with a genuinely different detected
    /// path and confirms the hardcoded string still appears, not the real detected text.
    /// </summary>
    [Fact]
    public void FilterPrismaGenerate_OutputPathLine_AlwaysPrintsHardcodedPath_NeverTheDetectedText()
    {
        const string output = "✔ Generated Prisma Client to ./some/totally/different/node_modules/@prisma/custom-client-dir in 99ms\n";

        var result = PrismaCommand.FilterPrismaGenerate(output);

        Assert.Contains("Output: node_modules/@prisma/client", result, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-client-dir", result, StringComparison.Ordinal);
        Assert.DoesNotContain("totally/different", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPrismaGenerate_NoOutputPathLine_OmitsOutputLine()
    {
        const string output = "Prisma Client generated without a detectable path line\n";

        var result = PrismaCommand.FilterPrismaGenerate(output);

        Assert.DoesNotContain("Output:", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // filter_migrate_dev - ports prisma_cmd.rs's test_filter_migrate_dev
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterMigrateDev_ExtractsNameAndChangeCounts_ReportsApplied()
    {
        const string output = """

        Applying migration 20260128_add_sessions

        CREATE TABLE "Session" (
          "id" TEXT NOT NULL,
          "userId" TEXT NOT NULL,
          FOREIGN KEY ("userId") REFERENCES "User"("id")
        );

        CREATE INDEX "session_status_idx" ON "Session"("status");

        ✓ Migration applied

        """;

        var result = PrismaCommand.FilterMigrateDev(output);

        Assert.Contains("20260128_add_sessions", result, StringComparison.Ordinal);
        Assert.Contains("+ 1 table", result, StringComparison.Ordinal);
        Assert.Contains("Applied", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression test for compatibility-ledger quirk #2 (class remarks item 2): the migration name
    /// is extracted via a literal <c>"202"</c> substring search, not by parsing a
    /// <c>YYYYMMDDHHMMSS</c> timestamp prefix. A migration named with a post-2029 (Y2030-cliff)
    /// prefix that does NOT contain the literal substring "202" anywhere is never extracted, proving
    /// this is substring matching, not date-aware parsing.
    /// </summary>
    [Fact]
    public void FilterMigrateDev_MigrationNameExtraction_IsLiteral202SubstringSearch_NotDateParsing()
    {
        // "30301..." never contains the literal substring "202" -> extraction silently fails
        // (Y2030-cliff bug, preserved verbatim per the compatibility ledger).
        const string y2030Output = "Applying migration 30301128000000_add_sessions\n✓ Migration applied\n";

        var y2030Result = PrismaCommand.FilterMigrateDev(y2030Output);
        Assert.DoesNotContain("Migration:", y2030Result, StringComparison.Ordinal);

        // Conversely, a "202"-containing name that is NOT a real timestamp prefix (a plain word
        // that just happens to contain "202" somewhere) is matched by the same naive substring
        // search, proving it is substring-based rather than validating timestamp structure.
        const string fakeDateOutput = "Applying migration room202_conf_room_booking\n✓ Migration applied\n";

        var fakeDateResult = PrismaCommand.FilterMigrateDev(fakeDateOutput);
        Assert.Contains("Migration: 202_conf_room_booking", fakeDateResult, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression test for compatibility-ledger quirk #3 (class remarks item 3): the reported
    /// pending count is a hardcoded literal <c>0</c>, not computed from any pending-migration state
    /// in the input. Constructs input that clearly represents pending migrations (lines explicitly
    /// mentioning "pending") alongside an "applied" trigger line, and confirms the output still
    /// reports "Pending: 0" - proving the 0 is hardcoded, not incidentally correct.
    /// </summary>
    [Fact]
    public void FilterMigrateDev_AlwaysReportsPendingZero_EvenWithConstructedPendingScenario()
    {
        const string output = """
        Applying migration 20260101000000_first

        3 migrations pending, 2 migrations unapplied

        ✓ Migration applied
        """;

        var result = PrismaCommand.FilterMigrateDev(output);

        Assert.Contains("Applied | Pending: 0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMigrateDev_NotApplied_OmitsAppliedPendingLine()
    {
        const string output = "Applying migration 20260101000000_first\nCREATE TABLE \"X\" (id TEXT);\n";

        var result = PrismaCommand.FilterMigrateDev(output);

        Assert.DoesNotContain("Applied", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Pending", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // filter_migrate_status
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterMigrateStatus_CountsAppliedAndPending_ExtractsLatest()
    {
        const string output = """
        20260101000000_init applied
        20260201000000_add_sessions applied
        20260301000000_add_index pending
        20260401000000_unapplied_one unapplied
        """;

        var result = PrismaCommand.FilterMigrateStatus(output);

        // "unapplied" contains the substring "applied", so the naive `line.Contains("applied")`
        // check (faithfully ported from Rust) double-counts that line as both applied AND pending -
        // 3 applied (init/add_sessions/unapplied_one), 2 pending (add_index/unapplied_one).
        Assert.Contains("Migrations: 3 applied, 2 pending", result, StringComparison.Ordinal);
        Assert.Contains("Latest: 20260101000000", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMigrateStatus_NoMigrations_ReportsZeroCounts()
    {
        var result = PrismaCommand.FilterMigrateStatus("No migrations found.\n");

        Assert.Contains("Migrations: 0 applied, 0 pending", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Latest:", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // filter_migrate_deploy
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterMigrateDeploy_NoErrors_ReportsDeployedCount()
    {
        const string output = "20260101000000_init applied\n20260201000000_next ✓\n";

        var result = PrismaCommand.FilterMigrateDeploy(output);

        Assert.Equal("2 migration(s) deployed", result);
    }

    [Fact]
    public void FilterMigrateDeploy_ErrorsPresent_ReportsFailureWithUpToFiveLines()
    {
        var output = string.Join('\n', Enumerable.Range(1, 7).Select(i => $"error: migration {i} failed"));

        var result = PrismaCommand.FilterMigrateDeploy(output);

        Assert.StartsWith("[FAIL] Deployment failed:", result, StringComparison.Ordinal);
        Assert.Equal(5, result.Split('\n').Length - 1);
    }

    // -----------------------------------------------------------------------
    // filter_db_push
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterDbPush_CountsSchemaChanges_AlwaysPrintsHeader()
    {
        const string output = "CREATE TABLE \"A\" (id TEXT);\nALTER TABLE \"B\" ADD COLUMN \"c\" TEXT;\nDROP TABLE \"Old\";\n";

        var result = PrismaCommand.FilterDbPush(output);

        Assert.Contains("Schema pushed to database", result, StringComparison.Ordinal);

        // The single "ALTER TABLE ... ADD COLUMN ..." line matches the Rust source's single
        // `line.Contains("ALTER") || line.Contains("ADD COLUMN")` condition once, not twice -
        // columnsModified increments by 1 per matching line regardless of how many of the two
        // substrings it contains.
        Assert.Contains("+ 1 tables, ~ 1 columns, - 1 dropped", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDbPush_NoChanges_OnlyPrintsHeader()
    {
        var result = PrismaCommand.FilterDbPush("Everything up to date\n");

        Assert.Equal("Schema pushed to database", result);
    }

    // -----------------------------------------------------------------------
    // extract_number - ports prisma_cmd.rs's test_extract_number
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractNumber_FirstParseableToken_ReturnsValue()
    {
        Assert.Equal(42, PrismaCommand.ExtractNumber("42 models generated"));
        Assert.Null(PrismaCommand.ExtractNumber("no numbers here"));
    }

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
    public async Task DispatchAsync_EmptyArgs_ReturnsUsageError()
    {
        var exitCode = await PrismaCommand.DispatchAsync([], verbose: 0, executor: null);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task DispatchAsync_UnknownSubcommand_ReturnsUsageError()
    {
        var exitCode = await PrismaCommand.DispatchAsync(["frobnicate"], verbose: 0, executor: null);

        Assert.Equal(2, exitCode);
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
