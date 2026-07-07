using System;
using System.Collections.Generic;
using System.IO;
using RtkSharp.Learn;
using Xunit;

namespace RtkSharp.Tests.Learn;

/// <summary>
/// Test-for-test port of Rust <c>src/learn/report.rs</c>'s <c>#[cfg(test)] mod tests</c> (3 tests
/// covering <c>format_console_report</c> and <c>write_rules_file</c>).
/// </summary>
public sealed class LearnReportTests
{
    // ===================== format_console_report (report.rs:126-161) =====================

    [Fact]
    public void FormatConsoleReport_Empty()
    {
        var report = LearnReport.FormatConsoleReport([], 0, 0, 30);
        Assert.Contains("0 rules", report);
        Assert.Contains("0 corrections", report);
        Assert.Contains("No CLI corrections detected", report);
    }

    [Fact]
    public void FormatConsoleReport_WithRules()
    {
        var rules = new List<CorrectionRule>
        {
            new("git commit --ammend", "git commit --amend", ErrorType.UnknownFlag, 3, "git commit", "error: unexpected argument '--ammend'"),
            new("gh pr edit -t", "gh pr edit --title", ErrorType.UnknownFlag, 1, "gh pr", "unknown flag: -t"),
        };

        var report = LearnReport.FormatConsoleReport(rules, 4, 10, 30);
        Assert.Contains("2 rules", report);
        Assert.Contains("4 corrections", report);
        Assert.Contains("[3x]", report);
        Assert.Contains("--ammend", report);
        Assert.Contains("--amend", report);
        Assert.Contains("Error: error: unexpected argument", report);
    }

    // ===================== write_rules_file (report.rs:164-185) =====================

    [Fact]
    public void WriteRulesFile_Markdown()
    {
        var rules = new List<CorrectionRule>
        {
            new("git commit --ammend", "git commit --amend", ErrorType.UnknownFlag, 3, "git commit", "error: unexpected argument '--ammend'"),
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "rtk-learn-report-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "cli-corrections.md");

            LearnReport.WriteRulesFile(rules, path);

            var content = File.ReadAllText(path);
            Assert.Contains("# CLI Corrections", content);
            Assert.Contains("## Git commit", content);
            Assert.Contains("Use `git commit --amend` not `git commit --ammend`", content);
            Assert.Contains("(seen 3x)", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
