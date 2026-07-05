using System;
using System.Collections.Generic;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Covers <see cref="HookAuditCommand"/>, a faithful port of Rust <c>src/hooks/hook_audit_cmd.rs</c>.
/// Mirrors the oracle's own <c>mod tests</c> battery (parse_line, base_command, filter_since_days,
/// token-savings shape) plus .NET-specific coverage for <see cref="HookAuditCommand.RunCore"/>'s full
/// output contract, argument parsing, and log-path resolution.
/// </summary>
public sealed class HookAuditCommandTests
{
    // ===================== ParseLine (hook_audit_cmd.rs:182-201) =====================

    [Fact]
    public void ParseLine_RewriteEntry_ParsesAllFields()
    {
        var entry = HookAuditCommand.ParseLine("2026-02-16T14:30:01Z | rewrite | git status | rtk git status");

        Assert.NotNull(entry);
        Assert.Equal("rewrite", entry!.Action);
        Assert.Equal("git status", entry.OriginalCmd);
    }

    [Fact]
    public void ParseLine_SkipEntry_ParsesActionAndCommand()
    {
        var entry = HookAuditCommand.ParseLine("2026-02-16T14:30:02Z | skip:no_match | echo hello | -");

        Assert.NotNull(entry);
        Assert.Equal("skip:no_match", entry!.Action);
        Assert.Equal("echo hello", entry.OriginalCmd);
    }

    [Fact]
    public void ParseLine_MissingFourthField_ReturnsEntryAnyway()
    {
        // Rust's parse_line only requires 3 fields (>= 3); the 4th (rewritten_cmd) defaults to "-" and
        // is unused by any summary output, so a 3-field line still parses (hook_audit_cmd.rs:30-38).
        var entry = HookAuditCommand.ParseLine("2026-02-16T14:30:01Z | rewrite | git status");

        Assert.NotNull(entry);
        Assert.Equal("rewrite", entry!.Action);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void ParseLine_Invalid_ReturnsNull(string line)
    {
        Assert.Null(HookAuditCommand.ParseLine(line));
    }

    // ===================== BaseCommand (hook_audit_cmd.rs:205-219) =====================

    [Theory]
    [InlineData("git status", "git status")]
    [InlineData("cargo test --nocapture", "cargo test")]
    [InlineData("ls", "ls")]
    [InlineData("pytest", "pytest")]
    public void BaseCommand_SimpleCommands_ReturnsFirstOneOrTwoTokens(string cmd, string expected)
    {
        Assert.Equal(expected, HookAuditCommand.BaseCommand(cmd));
    }

    [Theory]
    [InlineData("GIT_PAGER=cat git status", "git status")]
    [InlineData("NODE_ENV=test CI=1 npx vitest", "npx vitest")]
    public void BaseCommand_SkipsLeadingEnvAssignments(string cmd, string expected)
    {
        Assert.Equal(expected, HookAuditCommand.BaseCommand(cmd));
    }

    [Fact]
    public void BaseCommand_AllEnvAssignments_ReturnsOriginalString()
    {
        // stripped.len() == 0 falls back to the raw, un-split command string (hook_audit_cmd.rs:50).
        Assert.Equal("A=1 B=2", HookAuditCommand.BaseCommand("A=1 B=2"));
    }

    // ===================== FilterSinceDays (hook_audit_cmd.rs:222-239) =====================

    [Fact]
    public void FilterSinceDays_ZeroDays_ReturnsAllEntries()
    {
        var entries = new List<AuditEntryForTest>
        {
            new("2020-01-01T00:00:00Z", "rewrite", "git status"),
            new("2026-01-01T00:00:00Z", "skip:no_match", "echo hi"),
        };

        var result = HookAuditCommand.FilterSinceDays(ToEntries(entries), days: 0, utcNow: new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterSinceDays_ExcludesEntriesBeforeCutoff()
    {
        var entries = new List<AuditEntryForTest>
        {
            new("2026-06-01T00:00:00Z", "rewrite", "old command"), // outside a 7-day window from 2026-07-05
            new("2026-07-04T00:00:00Z", "rewrite", "recent command"), // inside
        };

        var result = HookAuditCommand.FilterSinceDays(
            ToEntries(entries), days: 7, utcNow: new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(result);
        Assert.Equal("recent command", result[0].OriginalCmd);
    }

    private static List<AuditEntry> ToEntries(List<AuditEntryForTest> entries) =>
        entries.ConvertAll(e => new AuditEntry(e.Timestamp, e.Action, e.OriginalCmd));

    private sealed record AuditEntryForTest(string Timestamp, string Action, string OriginalCmd);

    // ===================== RunCore: no log / empty log / no-entries-in-window =====================

    [Fact]
    public void RunCore_NoLogFile_PrintsEnableInstructions()
    {
        using var temp = new TempFile();
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = HookAuditCommand.RunCore(temp.NonExistentPath, sinceDays: 7, verbose: 0, stdout: stdout);

        Assert.Equal(0, exit);
        Assert.Contains($"No audit log found at {temp.NonExistentPath}", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("export RTK_HOOK_AUDIT=1", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunCore_EmptyLogFile_PrintsEmptyMessage()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, "");
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = HookAuditCommand.RunCore(temp.Path, sinceDays: 7, verbose: 0, stdout: stdout);

        Assert.Equal(0, exit);
        Assert.Equal("Audit log is empty.\n", stdout.ToString());
    }

    [Fact]
    public void RunCore_LogHasOnlyUnparsableLines_TreatedAsEmpty()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, "garbage\nmore garbage\n");
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = HookAuditCommand.RunCore(temp.Path, sinceDays: 7, verbose: 0, stdout: stdout);

        Assert.Equal(0, exit);
        Assert.Equal("Audit log is empty.\n", stdout.ToString());
    }

    // ===================== RunCore: full summary shape =====================

    [Fact]
    public void RunCore_MixedEntries_PrintsFullSummaryWithBreakdownAndTopCommands()
    {
        using var temp = new TempFile();
        File.WriteAllText(
            temp.Path,
            "2026-02-16T14:30:01Z | rewrite | git status | rtk git status\n" +
            "2026-02-16T14:30:02Z | skip:no_match | echo hello | -\n" +
            "2026-02-16T14:30:03Z | rewrite | cargo test | rtk cargo test\n" +
            "2026-02-16T14:30:04Z | skip:already_rtk | rtk git log | -\n" +
            "2026-02-16T14:30:05Z | rewrite | git log --oneline -10 | rtk git log --oneline -10\n" +
            "2026-02-16T14:30:06Z | rewrite | gh pr view 42 | rtk gh pr view 42\n" +
            "2026-02-16T14:30:07Z | skip:no_match | mkdir -p foo | -\n" +
            "2026-02-16T14:30:08Z | rewrite | cargo clippy --all-targets | rtk cargo clippy --all-targets\n");
        var stdout = new StringWriter { NewLine = "\n" };

        // since=0 (all time) avoids any dependency on "now" for this fixed 2026-02-16 dataset.
        var exit = HookAuditCommand.RunCore(temp.Path, sinceDays: 0, verbose: 0, stdout: stdout);
        var output = stdout.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Hook Audit (all time)", output, StringComparison.Ordinal);
        Assert.Contains("Total invocations: 8", output, StringComparison.Ordinal);
        Assert.Contains("Rewrites:          5 (62.5%)", output, StringComparison.Ordinal);
        Assert.Contains("Skips:             3 (37.5%)", output, StringComparison.Ordinal);
        Assert.Contains("no_match:      2", output, StringComparison.Ordinal);
        Assert.Contains("already_rtk:   1", output, StringComparison.Ordinal);
        Assert.Contains("Top commands:", output, StringComparison.Ordinal);
        Assert.Contains("git status (1)", output, StringComparison.Ordinal);
        Assert.Contains("cargo test (1)", output, StringComparison.Ordinal);
        Assert.Contains("cargo clippy (1)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunCore_SkipReasonAtThirteenCharThreshold_ClampsPaddingToOneSpace()
    {
        // Rust's padding formula (hook_audit_cmd.rs:152) is `14 - reason.len().min(13)`, which clamps
        // to a minimum of 1 space once the reason reaches 13 chars — never 0 or negative. A reason
        // shorter than that (e.g. "no_match", 8 chars) never reaches the clamp, so this exercises it
        // directly rather than relying on incidental fixture data.
        using var temp = new TempFile();
        File.WriteAllText(
            temp.Path,
            "2026-01-01T00:00:00Z | skip:exactly13chars | echo hi | -\n" + // reason = "exactly13chars" (14 chars, over the clamp)
            "2026-01-01T00:00:01Z | skip:a | echo bye | -\n"); // reason = "a" (1 char, well under)
        var stdout = new StringWriter { NewLine = "\n" };

        HookAuditCommand.RunCore(temp.Path, sinceDays: 0, verbose: 0, stdout: stdout);
        var output = stdout.ToString();

        Assert.Contains("exactly13chars: 1", output, StringComparison.Ordinal); // 14 - min(14,13) = 1 space
        Assert.Contains("a:" + new string(' ', 13) + "1", output, StringComparison.Ordinal); // 14 - min(1,13) = 13 spaces
    }

    [Fact]
    public void RunCore_VerboseZero_OmitsLogPathLine()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, "2026-01-01T00:00:00Z | rewrite | git status | rtk git status\n");
        var stdout = new StringWriter { NewLine = "\n" };

        HookAuditCommand.RunCore(temp.Path, sinceDays: 0, verbose: 0, stdout: stdout);

        Assert.DoesNotContain("Log:", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunCore_VerboseNonZero_AppendsLogPathLine()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, "2026-01-01T00:00:00Z | rewrite | git status | rtk git status\n");
        var stdout = new StringWriter { NewLine = "\n" };

        HookAuditCommand.RunCore(temp.Path, sinceDays: 0, verbose: 1, stdout: stdout);

        Assert.EndsWith($"\nLog: {temp.Path}\n", stdout.ToString());
    }

    [Fact]
    public void RunCore_VerboseNonZero_NoLogFile_DoesNotAppendLogPathLine()
    {
        // Rust only prints the trailing "Log: ..." line on the full-summary return path
        // (hook_audit_cmd.rs:170-172) — the "no audit log found" early return never reaches it.
        using var temp = new TempFile();
        var stdout = new StringWriter { NewLine = "\n" };

        HookAuditCommand.RunCore(temp.NonExistentPath, sinceDays: 0, verbose: 1, stdout: stdout);

        Assert.DoesNotContain("Log:", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunCore_SinceDaysNonZero_UsesLastNDaysLabel()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} | rewrite | git status | rtk git status\n");
        var stdout = new StringWriter { NewLine = "\n" };

        HookAuditCommand.RunCore(temp.Path, sinceDays: 7, verbose: 0, stdout: stdout);

        Assert.Contains("Hook Audit (last 7 days)", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunCore_NoEntriesInWindow_PrintsNoEntriesMessage()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, "2020-01-01T00:00:00Z | rewrite | git status | rtk git status\n");
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = HookAuditCommand.RunCore(temp.Path, sinceDays: 7, verbose: 0, stdout: stdout);

        Assert.Equal(0, exit);
        Assert.Equal("No entries in the last 7 days.\n", stdout.ToString());
    }

    [Fact]
    public void RunCore_NoSkipEntries_OmitsBreakdownSectionAndFormatsWholePercentages()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, "2026-01-01T00:00:00Z | rewrite | git status | rtk git status\n");
        var stdout = new StringWriter { NewLine = "\n" };

        HookAuditCommand.RunCore(temp.Path, sinceDays: 0, verbose: 0, stdout: stdout);

        Assert.Equal(
            "Hook Audit (all time)\n" +
            "──────────────────────────────\n" +
            "Total invocations: 1\n" +
            "Rewrites:          1 (100.0%)\n" +
            "Skips:             0 (0.0%)\n" +
            "Top commands: git status (1)\n",
            stdout.ToString());
    }

    [Fact]
    public void RunCore_NoRewriteEntries_OmitsTopCommandsLine()
    {
        using var temp = new TempFile();
        File.WriteAllText(temp.Path, "2026-01-01T00:00:00Z | skip:no_match | echo hi | -\n");
        var stdout = new StringWriter { NewLine = "\n" };

        HookAuditCommand.RunCore(temp.Path, sinceDays: 0, verbose: 0, stdout: stdout);

        Assert.DoesNotContain("Top commands", stdout.ToString(), StringComparison.Ordinal);
    }

    // ===================== ParseArgs =====================

    [Fact]
    public void ParseArgs_NoArgs_ReturnsDefaultSevenDays()
    {
        Assert.Equal(7UL, HookAuditCommand.ParseArgs([]));
    }

    [Fact]
    public void ParseArgs_SinceFlagLongForm_ParsesValue()
    {
        Assert.Equal(30UL, HookAuditCommand.ParseArgs(["--since", "30"]));
    }

    [Fact]
    public void ParseArgs_SinceFlagShortForm_ParsesValue()
    {
        Assert.Equal(30UL, HookAuditCommand.ParseArgs(["-s", "30"]));
    }

    [Fact]
    public void ParseArgs_SinceFlagEqualsForm_ParsesValue()
    {
        Assert.Equal(30UL, HookAuditCommand.ParseArgs(["--since=30"]));
    }

    [Fact]
    public void ParseArgs_SinceZero_ParsesAsAllTime()
    {
        Assert.Equal(0UL, HookAuditCommand.ParseArgs(["--since", "0"]));
    }

    [Fact]
    public void ParseArgs_UnrecognizedFlag_ThrowsUsageError()
    {
        Assert.Throws<HookAuditArgsException>(() => HookAuditCommand.ParseArgs(["--bogus"]));
    }

    [Fact]
    public void ParseArgs_SinceMissingValue_ThrowsUsageError()
    {
        Assert.Throws<HookAuditArgsException>(() => HookAuditCommand.ParseArgs(["--since"]));
    }

    [Fact]
    public void ParseArgs_SinceNonNumeric_ThrowsUsageError()
    {
        Assert.Throws<HookAuditArgsException>(() => HookAuditCommand.ParseArgs(["--since", "abc"]));
    }

    // ===================== ResolveLogPath =====================

    [Fact]
    public void ResolveLogPath_HonorsAuditDirOverride()
    {
        var previous = Environment.GetEnvironmentVariable("RTK_AUDIT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("RTK_AUDIT_DIR", "/tmp/custom-audit-dir");

            var path = HookAuditCommand.ResolveLogPath();

            Assert.Equal(Path.Combine("/tmp/custom-audit-dir", "hook-audit.log"), path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_AUDIT_DIR", previous);
        }
    }

    [Fact]
    public void ResolveLogPath_WithoutOverride_FallsBackToHomeLocalShareRtk()
    {
        var previous = Environment.GetEnvironmentVariable("RTK_AUDIT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("RTK_AUDIT_DIR", null);

            var path = HookAuditCommand.ResolveLogPath();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Assert.Equal(Path.Combine(home, ".local", "share", "rtk", "hook-audit.log"), path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_AUDIT_DIR", previous);
        }
    }

    // ===================== SplitLines =====================

    [Fact]
    public void SplitLines_HandlesLfAndCrLfAndNoTrailingNewline()
    {
        var lines = new List<string>(HookAuditCommand.SplitLines("a\nb\r\nc"));

        Assert.Equal(["a", "b", "c"], lines);
    }

    [Fact]
    public void SplitLines_TrailingNewline_NoEmptyFinalEntry()
    {
        var lines = new List<string>(HookAuditCommand.SplitLines("a\nb\n"));

        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public void SplitLines_EmptyContent_ReturnsNoLines()
    {
        Assert.Empty(HookAuditCommand.SplitLines(""));
    }

    /// <summary>A throwaway file path holder: gives a real temp path plus a guaranteed-nonexistent one, cleaned up on dispose.</summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rtk-hook-audit-test-" + Guid.NewGuid().ToString("N") + ".log");

        public string NonExistentPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rtk-hook-audit-missing-" + Guid.NewGuid().ToString("N") + ".log");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
