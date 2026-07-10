using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic for the <c>prisma</c> CLI proxy: <c>generate</c>/
/// <c>migrate dev|status|deploy</c>/<c>db push</c>. Extracted from
/// <c>RtkSharp.Commands.Js.PrismaCommand</c> (Task 7 of the filters-library extraction) — everything
/// here is a pure function of already-captured text, with no process execution or file I/O.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compatibility-ledger disclosure — three baked-in Rust-source bugs, preserved verbatim, not
/// fixed (see <c>docs/parity/compatibility-ledger.md</c>):</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b><see cref="FilterPrismaGenerate"/>'s unused <c>output_path</c>.</b> The Rust source
/// (<c>filter_prisma_generate</c>, <c>prisma_cmd.rs</c>:175-231) scans for a line containing both
/// <c>"node_modules"</c> and <c>"@prisma"</c> and stores its trimmed text in <c>output_path</c> — but
/// the only use of that variable anywhere is <c>!output_path.is_empty()</c>, gating whether a
/// HARDCODED literal <c>"  • Output: node_modules/@prisma/client\n"</c> is appended. Ported exactly:
/// this method detects presence/absence of such a line via the same two-substring check, but always
/// emits the same hardcoded string when one is found, never the real detected text.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b><see cref="FilterMigrateDev"/>/<see cref="FilterMigrateStatus"/>'s literal <c>"202"</c>
/// substring search (Y2030-cliff bug).</b> The Rust source extracts a migration name via
/// <c>line.find("202")</c> (<c>prisma_cmd.rs</c>:245, 314) — a literal three-character substring
/// search, not a date parser. Ported exactly, not "fixed" to be year-agnostic.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b><see cref="FilterMigrateDev"/>'s always-zero pending count.</b> The Rust source
/// (<c>prisma_cmd.rs</c>:296-299) prints the literal string <c>"Applied | Pending: 0\n"</c> whenever
/// any line contained <c>"applied"</c> or <c>"✓"</c> — the <c>0</c> is a hardcoded literal, not a
/// computed pending-migration count.
/// </description>
/// </item>
/// </list>
/// </remarks>
public static class PrismaFilters
{
    // -----------------------------------------------------------------------
    // filter_prisma_generate (prisma_cmd.rs:174-231)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma generate</c> output: strips ASCII-art/box-drawing lines, extracts
    /// model/enum/type counts, and reports a hardcoded output path string. See the intentional-quirk
    /// disclosure in the class remarks for the unused-<c>outputPath</c> behavior preserved here.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma generate</c>.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterPrismaGenerate(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var models = 0;
        var enums = 0;
        var types = 0;

        // Detected but, per the disclosed Rust-source quirk (see class remarks item 1), never used
        // for anything beyond its own emptiness check below - the actual line content is discarded.
        var outputPath = string.Empty;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (line.Contains('█') || line.Contains('▀') || line.Contains('▄')
                || line.Contains('┌') || line.Contains('└') || line.Contains('│'))
            {
                continue;
            }

            if (line.Contains("model", StringComparison.Ordinal) && line.Contains("generated", StringComparison.Ordinal))
            {
                var num = ExtractNumber(line);
                if (num is not null)
                {
                    models = num.Value;
                }
            }

            if (line.Contains("enum", StringComparison.Ordinal))
            {
                var num = ExtractNumber(line);
                if (num is not null)
                {
                    enums = num.Value;
                }
            }

            if (line.Contains("type", StringComparison.Ordinal))
            {
                var num = ExtractNumber(line);
                if (num is not null)
                {
                    types = num.Value;
                }
            }

            if (line.Contains("node_modules", StringComparison.Ordinal) && line.Contains("@prisma", StringComparison.Ordinal))
            {
                outputPath = line.Trim();
            }
        }

        var result = new StringBuilder();
        result.Append("Prisma Client generated\n");

        if (models > 0 || enums > 0 || types > 0)
        {
            result.Append($"  • {models} models, {enums} enums, {types} types\n");
        }

        if (outputPath.Length > 0)
        {
            // Intentional quirk (class remarks item 1): always the hardcoded string, never
            // `outputPath` itself.
            result.Append("  • Output: node_modules/@prisma/client\n");
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // filter_migrate_dev (prisma_cmd.rs:233-302)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma migrate dev</c> output: extracts migration name (via the disclosed
    /// literal-<c>"202"</c>-substring quirk, class remarks item 2) and change counts, and reports
    /// <c>"Applied | Pending: 0"</c> with the pending count hardcoded to <c>0</c> (class remarks item
    /// 3) whenever any line indicates the migration was applied.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate dev</c>.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterMigrateDev(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var migrationName = string.Empty;
        var tablesAdded = 0;
        var tablesModified = 0;
        var relations = new List<string>();
        var indexes = new List<string>();
        var applied = false;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            // Intentional quirk (class remarks item 2): a literal "202" substring search, not a
            // date-prefix parser - a migration name not literally containing "202" is never
            // extracted, and any string containing "202" anywhere (not necessarily as a timestamp
            // prefix) matches.
            if (line.Contains("migration", StringComparison.Ordinal) && line.Contains('_'))
            {
                var pos = line.IndexOf("202", StringComparison.Ordinal);
                if (pos >= 0)
                {
                    var end = FindWhitespaceOffset(line, pos) ?? (line.Length - pos);
                    migrationName = line.Substring(pos, end);
                }
            }

            if (line.Contains("CREATE TABLE", StringComparison.Ordinal))
            {
                tablesAdded++;
            }

            if (line.Contains("ALTER TABLE", StringComparison.Ordinal))
            {
                tablesModified++;
            }

            if (line.Contains("FOREIGN KEY", StringComparison.Ordinal) || line.Contains("REFERENCES", StringComparison.Ordinal))
            {
                var table = ExtractTableName(line);
                if (table is not null)
                {
                    relations.Add(table);
                }
            }

            if (line.Contains("CREATE INDEX", StringComparison.Ordinal) || line.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal))
            {
                var idx = ExtractIndexName(line);
                if (idx is not null)
                {
                    indexes.Add(idx);
                }
            }

            if (line.Contains("applied", StringComparison.Ordinal) || line.Contains('✓'))
            {
                applied = true;
            }
        }

        var result = new StringBuilder();

        if (migrationName.Length > 0)
        {
            result.Append($"Migration: {migrationName}\n");
        }

        result.Append("Changes:\n");

        if (tablesAdded > 0)
        {
            result.Append($"  + {tablesAdded} table(s)\n");
        }

        if (tablesModified > 0)
        {
            result.Append($"  ~ {tablesModified} table(s) modified\n");
        }

        if (relations.Count > 0)
        {
            result.Append($"  + {relations.Count} relation(s)\n");
        }

        if (indexes.Count > 0)
        {
            result.Append($"  ~ {indexes.Count} index(es)\n");
        }

        result.Append('\n');

        if (applied)
        {
            // Intentional quirk (class remarks item 3): "Pending: 0" is a hardcoded literal, not a
            // computed pending-migration count - this fires unconditionally whenever `applied` is
            // true, regardless of any actual pending-migration state in `output`.
            result.Append("Applied | Pending: 0\n");
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // filter_migrate_status (prisma_cmd.rs:304-336)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma migrate status</c> output: counts <c>"applied"</c>/<c>"pending"</c>/
    /// <c>"unapplied"</c> lines and extracts a <c>"202"</c>-prefixed latest migration name (the same
    /// literal-substring assumption as <see cref="FilterMigrateDev"/>, class remarks item 2).
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate status</c>.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterMigrateStatus(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var appliedCount = 0;
        var pendingCount = 0;
        var latestMigration = string.Empty;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (line.Contains("applied", StringComparison.Ordinal))
            {
                appliedCount++;

                if (latestMigration.Length == 0 && line.Contains("202", StringComparison.Ordinal))
                {
                    var pos = line.IndexOf("202", StringComparison.Ordinal);
                    if (pos >= 0)
                    {
                        var end = FindWhitespaceOffset(line, pos) ?? 20;
                        // Faithful port of Rust's `line[pos..pos + end]`: the fallback of 20 is a
                        // literal length, not clamped to the remaining string length, matching the
                        // Rust source exactly (which would panic on an out-of-bounds slice under the
                        // same condition - preserved here via a defensive clamp so this filter never
                        // crashes rtk, per the mandatory fallback pattern).
                        end = Math.Min(end, line.Length - pos);
                        latestMigration = line.Substring(pos, end);
                    }
                }
            }

            if (line.Contains("pending", StringComparison.Ordinal) || line.Contains("unapplied", StringComparison.Ordinal))
            {
                pendingCount++;
            }
        }

        var result = new StringBuilder();
        result.Append($"Migrations: {appliedCount} applied, {pendingCount} pending\n");

        if (latestMigration.Length > 0)
        {
            result.Append($"Latest: {latestMigration}\n");
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // filter_migrate_deploy (prisma_cmd.rs:338-364)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma migrate deploy</c> output: reports up to 5 error lines under a
    /// <c>"[FAIL] Deployment failed:"</c> header if any error lines are found, otherwise
    /// <c>"{N} migration(s) deployed"</c>.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate deploy</c>.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterMigrateDeploy(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var deployed = 0;
        var errors = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (line.Contains("applied", StringComparison.Ordinal) || line.Contains('✓'))
            {
                deployed++;
            }

            if (line.Contains("error", StringComparison.Ordinal) || line.Contains("ERROR", StringComparison.Ordinal))
            {
                errors.Add(line.Trim());
            }
        }

        var result = new StringBuilder();

        if (errors.Count == 0)
        {
            result.Append($"{deployed} migration(s) deployed\n");
        }
        else
        {
            result.Append("[FAIL] Deployment failed:\n");
            foreach (var err in errors.Take(5))
            {
                result.Append($"  {err}\n");
            }
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // filter_db_push (prisma_cmd.rs:366-395)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma db push</c> output: counts <c>CREATE TABLE</c>/<c>ALTER</c>|<c>ADD COLUMN</c>/
    /// <c>DROP</c> occurrences and always emits the <c>"Schema pushed to database"</c> header.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma db push</c>.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterDbPush(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var tablesAdded = 0;
        var columnsModified = 0;
        var dropped = 0;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (line.Contains("CREATE TABLE", StringComparison.Ordinal))
            {
                tablesAdded++;
            }

            if (line.Contains("ALTER", StringComparison.Ordinal) || line.Contains("ADD COLUMN", StringComparison.Ordinal))
            {
                columnsModified++;
            }

            if (line.Contains("DROP", StringComparison.Ordinal))
            {
                dropped++;
            }
        }

        var result = new StringBuilder();
        result.Append("Schema pushed to database\n");

        if (tablesAdded > 0 || columnsModified > 0 || dropped > 0)
        {
            result.Append($"  + {tablesAdded} tables, ~ {columnsModified} columns, - {dropped} dropped\n");
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // extract_number / extract_table_name / extract_index_name (prisma_cmd.rs:397-435)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts the first whitespace-delimited token in <paramref name="line"/> that parses as a
    /// non-negative integer. Faithful port of <c>extract_number</c> (<c>prisma_cmd.rs</c>:398-401).
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <returns>The first parseable integer token, or null if none is found.</returns>
    public static int? ExtractNumber(string line)
    {
        foreach (var word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(word, out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the table name immediately following a bare <c>TABLE</c> token in
    /// <paramref name="line"/>, trimming surrounding backticks/quotes/semicolons. Faithful port of
    /// <c>extract_table_name</c> (<c>prisma_cmd.rs</c>:404-418).
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <returns>The extracted table name, or null if <c>TABLE</c> is absent or has no following token.</returns>
    public static string? ExtractTableName(string line) => ExtractTokenAfter(line, "TABLE");

    /// <summary>
    /// Extracts the index name immediately following a bare <c>INDEX</c> token in
    /// <paramref name="line"/>, trimming surrounding backticks/quotes/semicolons. Faithful port of
    /// <c>extract_index_name</c> (<c>prisma_cmd.rs</c>:421-435).
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <returns>The extracted index name, or null if <c>INDEX</c> is absent or has no following token.</returns>
    public static string? ExtractIndexName(string line) => ExtractTokenAfter(line, "INDEX");

    private static string? ExtractTokenAfter(string line, string marker)
    {
        if (!line.Contains(marker, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == marker && i + 1 < parts.Length)
            {
                return parts[i + 1].Trim('`', '"', ';');
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the offset (relative to <paramref name="start"/>) of the first whitespace character in
    /// <paramref name="line"/> at or after <paramref name="start"/>. Mirrors Rust's
    /// <c>line[pos..].find(|c: char| c.is_whitespace())</c>.
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <param name="start">The index to start scanning from.</param>
    /// <returns>The relative offset of the first whitespace character, or null if none is found.</returns>
    private static int? FindWhitespaceOffset(string line, int start)
    {
        for (var i = start; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                return i - start;
            }
        }

        return null;
    }
}
