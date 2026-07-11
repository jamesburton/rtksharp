using System;
using System.Linq;
using System.Text;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Covers <see cref="LogFilters"/>, a faithful, test-for-test port of Rust
/// <c>src/cmds/system/log_cmd.rs</c>'s own <c>#[cfg(test)] mod tests</c>. Extracted from
/// <c>RtkSharp.Tests.Commands.System.LogCommandTests</c> alongside <c>AnalyzeLogs</c>/
/// <c>NormalizeLogLine</c>'s relocation to <see cref="LogFilters"/>. <c>RunFile</c>/<c>RunStdin</c>
/// dispatch coverage stays in <c>RtkSharp.Tests.Commands.System.LogCommandTests</c> — those methods
/// did not move (still process/Console I/O, Task 14's job).
/// </summary>
public sealed class LogFiltersTests
{
    [Fact]
    public void AnalyzeLogs_DedupesRepeatedErrors()
    {
        const string logs = """

            2024-01-01 10:00:00 ERROR: Connection failed to /api/server
            2024-01-01 10:00:01 ERROR: Connection failed to /api/server
            2024-01-01 10:00:02 ERROR: Connection failed to /api/server
            2024-01-01 10:00:03 WARN: Retrying connection
            2024-01-01 10:00:04 INFO: Connected

            """;

        var result = LogFilters.AnalyzeLogs(logs);

        Assert.Contains("×3", result, StringComparison.Ordinal);
        Assert.Contains("ERRORS", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeLogs_ExtendedSeverityKeywords_CountedAsErrorOrWarning()
    {
        const string logs =
            "2024-01-01 10:00:00 CRITICAL: disk full\n" +
            "2024-01-01 10:00:01 ALERT: memory pressure\n" +
            "2024-01-01 10:00:02 emerg: system shutdown imminent\n" +
            "2024-01-01 10:00:03 SEVERE: data corruption detected\n" +
            "2024-01-01 10:00:04 notice: config reloaded\n";

        var result = LogFilters.AnalyzeLogs(logs);

        Assert.Contains("ERRORS", result, StringComparison.Ordinal);
        Assert.Contains("WARNINGS", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeLogs_MultibyteContent_DoesNotThrow()
    {
        var logs =
            $"2024-01-01 10:00:00 ERROR: {string.Concat(Enumerable.Repeat("ข้อผิดพลาด", 15))} connection failed\n" +
            $"2024-01-01 10:00:01 WARN: {string.Concat(Enumerable.Repeat("คำเตือน", 15))} retry attempt\n";

        var result = LogFilters.AnalyzeLogs(logs);

        Assert.Contains("ERRORS", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeLogs_NoMatchingLines_OnlySummaryPrinted()
    {
        var result = LogFilters.AnalyzeLogs("just some plain text\nnothing special here\n");

        Assert.Contains("Log Summary", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[ERRORS]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[WARNINGS]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeLogs_MoreThanTenUniqueErrors_ShowsOverflowCount()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 12; i++)
        {
            sb.Append($"2024-01-01 10:00:00 ERROR: distinct failure {i}\n");
        }

        var result = LogFilters.AnalyzeLogs(sb.ToString());

        Assert.Contains("+2 more unique errors", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeLogs_LongLine_TruncatedAtNinetySevenRunesWithEllipsis()
    {
        var longMessage = new string('x', 200);
        var result = LogFilters.AnalyzeLogs($"ERROR: {longMessage}\n");

        Assert.Contains("...", result, StringComparison.Ordinal);
        // The truncated line (97 runes + "...") must never contain the untruncated full message.
        Assert.DoesNotContain(longMessage, result, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeLogLine_StripsTimestampAndReplacesTokens()
    {
        var normalized = LogFilters.NormalizeLogLine(
            "2024-01-01T10:00:00.123 ERROR at /var/log/app.log with id 550e8400-e29b-41d4-a716-446655440000 code 0xDEADBEEF count 12345");

        Assert.DoesNotContain("2024-01-01", normalized, StringComparison.Ordinal);
        Assert.Contains("<PATH>", normalized, StringComparison.Ordinal);
        Assert.Contains("<UUID>", normalized, StringComparison.Ordinal);
        Assert.Contains("<HEX>", normalized, StringComparison.Ordinal);
        Assert.Contains("<NUM>", normalized, StringComparison.Ordinal);
    }
}
