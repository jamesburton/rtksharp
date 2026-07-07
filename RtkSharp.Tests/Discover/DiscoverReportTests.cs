using System.IO;
using System.Text.Json;
using RtkSharp.Discover;
using Xunit;

namespace RtkSharp.Tests.Discover;

/// <summary>
/// Test-for-test port of Rust <c>src/discover/report.rs</c>'s <c>#[cfg(test)] mod tests</c>, covering
/// <see cref="DiscoverReportFormatter.FormatText"/>/<see cref="DiscoverReportFormatter.FormatJson"/>
/// and <see cref="AgentIntegrationStatus"/> detection.
/// </summary>
public sealed class DiscoverReportTests
{
    private static DiscoverReport MakeReport(int totalCommands, int alreadyRtk) => new()
    {
        SessionsScanned = 1,
        TotalCommands = totalCommands,
        AlreadyRtk = alreadyRtk,
        SinceDays = 30,
        Supported = [],
        Unsupported = [],
        ParseErrors = 0,
        RtkDisabledCount = 0,
        RtkDisabledExamples = [],
        AgentStatus = AgentIntegrationStatus.Default,
    };

    // B6 regression: integer division truncated small percentages to 0%.
    // Example: 3/1000 = 0% (old bug), should be "0.3%".
    [Fact]
    public void FormatText_AlreadyRtkPercent_ShowsDecimal()
    {
        var report = MakeReport(1000, 3);
        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("0.3%", output);
        Assert.DoesNotContain("(0%)", output);
    }

    [Fact]
    public void FormatText_AlreadyRtkPercent_ZeroTotal_NoDivideByZero()
    {
        var report = MakeReport(0, 0);
        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("0 commands (0.0%)", output);
    }

    [Fact]
    public void FormatText_AlreadyRtkPercent_Full()
    {
        var report = MakeReport(1000, 1000);
        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("100.0%", output);
    }

    [Fact]
    public void AgentStatus_DetectsHermesPluginManifest()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "rtk-discover-report-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        try
        {
            var manifest = Path.Combine(tempHome, ".hermes", "plugins", "rtk-rewrite", "plugin.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            File.WriteAllText(manifest, "name: rtk-rewrite\n");

            var status = AgentIntegrationStatus.DetectFromHome(tempHome);

            Assert.True(status.HermesPluginInstalled);
            Assert.False(status.CursorHookInstalled);
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void AgentStatus_IgnoresHermesPluginDirWithoutManifest()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "rtk-discover-report-test-" + Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(tempHome, ".hermes", "plugins", "rtk-rewrite");
        Directory.CreateDirectory(pluginDir);
        try
        {
            var status = AgentIntegrationStatus.DetectFromHome(tempHome);

            Assert.False(status.HermesPluginInstalled);
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void FormatText_ReportsHermesPluginDetected()
    {
        var report = MakeReport(0, 0) with { AgentStatus = AgentIntegrationStatus.Default with { HermesPluginInstalled = true } };

        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("Hermes plugin is installed", output);
    }

    [Fact]
    public void FormatJson_IncludesAgentStatus()
    {
        var report = MakeReport(0, 0) with
        {
            AgentStatus = new AgentIntegrationStatus(true, true, true),
        };

        var output = DiscoverReportFormatter.FormatJson(report);
        using var doc = JsonDocument.Parse(output);

        Assert.True(doc.RootElement.GetProperty("agent_status").GetProperty("cursor_hook_installed").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("agent_status").GetProperty("hermes_plugin_installed").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("agent_status").GetProperty("copilot_hook_installed").GetBoolean());
    }

    [Fact]
    public void AgentStatus_DetectsCopilotHookInProject()
    {
        var temp = Path.Combine(Path.GetTempPath(), "rtk-discover-report-test-" + Guid.NewGuid().ToString("N"));
        var hookDir = Path.Combine(temp, ".github", "hooks");
        Directory.CreateDirectory(hookDir);
        try
        {
            File.WriteAllText(Path.Combine(hookDir, "rtk-rewrite.json"), "{}");

            Assert.True(AgentIntegrationStatus.CopilotHookInstalledIn(temp));

            var otherTemp = Path.Combine(Path.GetTempPath(), "rtk-discover-report-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherTemp);
            try
            {
                Assert.False(AgentIntegrationStatus.CopilotHookInstalledIn(otherTemp));
            }
            finally
            {
                Directory.Delete(otherTemp, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void FormatText_ReportsCopilotDetected()
    {
        var report = MakeReport(0, 0) with { AgentStatus = AgentIntegrationStatus.Default with { CopilotHookInstalled = true } };

        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("GitHub Copilot sessions are tracked via `rtk gain`", output);
    }

    [Fact]
    public void FormatText_NoSupportedOrUnsupported_PrintsUsageLooksGood()
    {
        var report = MakeReport(10, 10);
        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("No missed savings found. RTK usage looks good!", output);
    }

    [Fact]
    public void FormatText_WithSupportedEntries_PrintsTableAndTotal()
    {
        var report = MakeReport(5, 0) with
        {
            Supported =
            [
                new SupportedEntry("git status", 3, "rtk git", "Git", 300, 70.0, RtkStatus.Existing),
            ],
        };

        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("MISSED SAVINGS -- Commands RTK already handles", output);
        Assert.Contains("git status", output);
        Assert.Contains("rtk git", output);
        Assert.Contains("existing", output);
        Assert.Contains("Total: 3 commands -> ~300 tokens saveable", output);
    }

    [Fact]
    public void FormatText_WithUnsupportedEntries_PrintsUnhandledSection()
    {
        var report = MakeReport(5, 0) with
        {
            Unsupported = [new UnsupportedEntry("htop", 2, "htop -d 10")],
        };

        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("TOP UNHANDLED COMMANDS -- open an issue?", output);
        Assert.Contains("htop", output);
        Assert.Contains("github.com/rtk-ai/rtk/issues", output);
    }

    [Fact]
    public void FormatText_RtkDisabledCount_PrintsBypassWarning()
    {
        var report = MakeReport(5, 0) with
        {
            Supported = [new SupportedEntry("git status", 1, "rtk git", "Git", 100, 70.0, RtkStatus.Existing)],
            RtkDisabledCount = 2,
            RtkDisabledExamples = ["git status (2x)"],
        };

        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.Contains("RTK_DISABLED BYPASS -- 2 commands ran without filtering", output);
        Assert.Contains("git status (2x)", output);
        Assert.Contains("Remove RTK_DISABLED=1 to recover token savings", output);
    }

    [Fact]
    public void FormatText_Verbose_AppendsParseErrorsFooter()
    {
        // The parse-errors footer is only reachable past the "no missed savings" early-return
        // branch (report.rs:144-148), so this report needs at least one supported/unsupported
        // entry — an all-empty report short-circuits before the footer is ever considered.
        var report = MakeReport(0, 0) with
        {
            ParseErrors = 3,
            Supported = [new SupportedEntry("git status", 1, "rtk git", "Git", 100, 70.0, RtkStatus.Existing)],
        };

        var output = DiscoverReportFormatter.FormatText(report, 10, true);

        Assert.Contains("Parse errors skipped: 3", output);
    }

    [Fact]
    public void FormatText_NotVerbose_OmitsParseErrorsFooter()
    {
        var report = MakeReport(0, 0) with { ParseErrors = 3 };

        var output = DiscoverReportFormatter.FormatText(report, 10, false);

        Assert.DoesNotContain("Parse errors skipped", output);
    }

    [Fact]
    public void FormatJson_RoundTripsSupportedEntry()
    {
        var report = MakeReport(1, 0) with
        {
            Supported = [new SupportedEntry("git status", 1, "rtk git", "Git", 70, 70.0, RtkStatus.Existing)],
        };

        var json = DiscoverReportFormatter.FormatJson(report);
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("supported")[0];

        Assert.Equal("git status", entry.GetProperty("command").GetString());
        Assert.Equal("rtk git", entry.GetProperty("rtk_equivalent").GetString());
        Assert.Equal("Git", entry.GetProperty("category").GetString());
        Assert.Equal("Existing", entry.GetProperty("rtk_status").GetString());
        Assert.Equal("70.0", entry.GetProperty("estimated_savings_pct").GetRawText());
    }

    [Fact]
    public void TotalSaveableTokens_SumsAcrossSupportedEntries()
    {
        var report = MakeReport(0, 0) with
        {
            Supported =
            [
                new SupportedEntry("a", 1, "rtk a", "Cat", 100, 70.0, RtkStatus.Existing),
                new SupportedEntry("b", 1, "rtk b", "Cat", 200, 70.0, RtkStatus.Existing),
            ],
        };

        Assert.Equal(300, report.TotalSaveableTokens());
        Assert.Equal(2, report.TotalSupportedCount());
    }

    [Fact]
    public void FormatTokens_FormatsMagnitudeSuffixes()
    {
        Assert.Equal("42 tokens", DiscoverReportFormatter.FormatTokens(42));
        Assert.Equal("3.4K tokens", DiscoverReportFormatter.FormatTokens(3400));
        Assert.Equal("1.2M tokens", DiscoverReportFormatter.FormatTokens(1_200_000));
    }

    [Fact]
    public void TruncateStr_ShortStringUnchanged()
    {
        Assert.Equal("git status", DiscoverReportFormatter.TruncateStr("git status", 23));
    }

    [Fact]
    public void TruncateStr_LongStringTruncatedWithEllipsis()
    {
        var result = DiscoverReportFormatter.TruncateStr("this is a very long command line example", 10);
        Assert.Equal("this is ..", result);
    }
}
