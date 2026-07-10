using System;
using System.Linq;
using RtkSharp.Filters.Commands.Js;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PrismaFilters"/>, moved from
/// <c>RtkSharp.Tests.Commands.Js.PrismaCommandTests</c> (Task 7 of the filters-library extraction) —
/// ported directly from Rust's own <c>#[cfg(test)]</c> module in <c>prisma_cmd.rs</c>
/// (<c>test_filter_generate</c>, <c>test_filter_migrate_dev</c>, <c>test_extract_number</c>) plus
/// EXPLICIT discriminating regression tests for the three baked-in Rust-source quirks disclosed in
/// <see cref="PrismaFilters"/>'s class remarks (unused <c>output_path</c>, the literal-<c>"202"</c>-
/// substring migration-name search, and the always-hardcoded-zero pending count). None of these three
/// quirks is "fixed" here - they are captured as intentional-quirk regression tests.
/// </summary>
public sealed class PrismaFiltersTests
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

        var result = PrismaFilters.FilterPrismaGenerate(output);

        Assert.Contains("Prisma Client generated", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Prisma schema loaded", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Start by importing", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPrismaGenerate_StripsBoxDrawingAndBlockGlyphLines()
    {
        const string output = "█▀▄ ascii art banner\n┌───┐\n│ box │\n└───┘\nPrisma Client generated fine\n";

        var result = PrismaFilters.FilterPrismaGenerate(output);

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

        var result = PrismaFilters.FilterPrismaGenerate(output);

        Assert.Contains("Output: node_modules/@prisma/client", result, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-client-dir", result, StringComparison.Ordinal);
        Assert.DoesNotContain("totally/different", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPrismaGenerate_NoOutputPathLine_OmitsOutputLine()
    {
        const string output = "Prisma Client generated without a detectable path line\n";

        var result = PrismaFilters.FilterPrismaGenerate(output);

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

        var result = PrismaFilters.FilterMigrateDev(output);

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

        var y2030Result = PrismaFilters.FilterMigrateDev(y2030Output);
        Assert.DoesNotContain("Migration:", y2030Result, StringComparison.Ordinal);

        // Conversely, a "202"-containing name that is NOT a real timestamp prefix (a plain word
        // that just happens to contain "202" somewhere) is matched by the same naive substring
        // search, proving it is substring-based rather than validating timestamp structure.
        const string fakeDateOutput = "Applying migration room202_conf_room_booking\n✓ Migration applied\n";

        var fakeDateResult = PrismaFilters.FilterMigrateDev(fakeDateOutput);
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

        var result = PrismaFilters.FilterMigrateDev(output);

        Assert.Contains("Applied | Pending: 0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMigrateDev_NotApplied_OmitsAppliedPendingLine()
    {
        const string output = "Applying migration 20260101000000_first\nCREATE TABLE \"X\" (id TEXT);\n";

        var result = PrismaFilters.FilterMigrateDev(output);

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

        var result = PrismaFilters.FilterMigrateStatus(output);

        // "unapplied" contains the substring "applied", so the naive `line.Contains("applied")`
        // check (faithfully ported from Rust) double-counts that line as both applied AND pending -
        // 3 applied (init/add_sessions/unapplied_one), 2 pending (add_index/unapplied_one).
        Assert.Contains("Migrations: 3 applied, 2 pending", result, StringComparison.Ordinal);
        Assert.Contains("Latest: 20260101000000", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterMigrateStatus_NoMigrations_ReportsZeroCounts()
    {
        var result = PrismaFilters.FilterMigrateStatus("No migrations found.\n");

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

        var result = PrismaFilters.FilterMigrateDeploy(output);

        Assert.Equal("2 migration(s) deployed", result);
    }

    [Fact]
    public void FilterMigrateDeploy_ErrorsPresent_ReportsFailureWithUpToFiveLines()
    {
        var output = string.Join('\n', Enumerable.Range(1, 7).Select(i => $"error: migration {i} failed"));

        var result = PrismaFilters.FilterMigrateDeploy(output);

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

        var result = PrismaFilters.FilterDbPush(output);

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
        var result = PrismaFilters.FilterDbPush("Everything up to date\n");

        Assert.Equal("Schema pushed to database", result);
    }

    // -----------------------------------------------------------------------
    // extract_number - ports prisma_cmd.rs's test_extract_number
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractNumber_FirstParseableToken_ReturnsValue()
    {
        Assert.Equal(42, PrismaFilters.ExtractNumber("42 models generated"));
        Assert.Null(PrismaFilters.ExtractNumber("no numbers here"));
    }
}
