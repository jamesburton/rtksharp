using System;
using System.IO;
using RtkSharp.Filters;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Tests for <see cref="TrustCommand"/>: the <see cref="TrustStore"/> model,
/// <see cref="TrustCommand.CheckTrustWithContent"/>'s four-state resolution, the <c>rtk
/// trust</c>/<c>rtk untrust</c> CLI verbs, the risk-summary triggers, and the Task-3 adapter. Every
/// test redirects the trust store via <see cref="DataDirGuard"/> (<c>RTK_DATA_DIR_OVERRIDE</c>) so
/// none of them ever touch the real user's actual <c>trusted_filters.json</c>.
/// </summary>
public sealed class TrustCommandTests
{
    private static readonly string[] CiIndicatorVars = ["CI", "GITHUB_ACTIONS", "GITLAB_CI", "JENKINS_URL", "BUILDKITE"];

    // -----------------------------------------------------------------------
    // Trust / untrust / content-changed round trips
    // -----------------------------------------------------------------------

    [Fact]
    public void Trust_ThenCheck_ReturnsTrustedWithContent()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);

        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");

        var hash = Integrity.ComputeHash(filter);
        TrustCommand.TrustFilterWithHash(filter, hash);

        var result = TrustCommand.CheckTrustWithContent(filter);

        Assert.Equal(TrustStatusKind.Trusted, result.Kind);
        Assert.Equal("[filters.test]\nmatch_command = \"echo\"", result.Content);
    }

    [Fact]
    public void Untrust_RemovesEntry_SubsequentCheckIsUntrusted()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);

        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");

        TrustCommand.TrustFilterWithHash(filter, Integrity.ComputeHash(filter));
        var removed = TrustCommand.UntrustFilter(filter);

        Assert.True(removed);
        Assert.Equal(TrustStatusKind.Untrusted, TrustCommand.CheckTrustWithContent(filter).Kind);
    }

    [Fact]
    public void Untrust_NoExistingEntry_ReturnsFalse()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);

        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");

        Assert.False(TrustCommand.UntrustFilter(filter));
    }

    [Fact]
    public void ContentChange_AfterTrust_IsDetected()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);

        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");
        TrustCommand.TrustFilterWithHash(filter, Integrity.ComputeHash(filter));

        File.WriteAllText(filter, "[filters.evil]\nmatch_command = \".*\"\nmatch_output = \"password\"");

        var result = TrustCommand.CheckTrustWithContent(filter);

        Assert.Equal(TrustStatusKind.ContentChanged, result.Kind);
        Assert.NotEqual(result.ExpectedHash, result.ActualHash);
        Assert.Equal(64, result.ExpectedHash!.Length);
        Assert.Equal(64, result.ActualHash!.Length);
    }

    [Fact]
    public void Untrusted_ByDefault_NoStoreEntry()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);

        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");

        Assert.Equal(TrustStatusKind.Untrusted, TrustCommand.CheckTrustWithContent(filter).Kind);
    }

    [Fact]
    public void UnreadableFile_ReturnsUntrusted()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);

        var missing = Path.Combine(tmp.Root, "does-not-exist.toml");

        Assert.Equal(TrustStatusKind.Untrusted, TrustCommand.CheckTrustWithContent(missing).Kind);
    }

    // -----------------------------------------------------------------------
    // CI env override
    // -----------------------------------------------------------------------

    [Fact]
    public void EnvOverride_WithRtkFlagAndCi_ReturnsEnvOverride()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);
        using var noCi = new MultiEnvVarScope(CiIndicatorVars, null);
        using var ci = new EnvVarScope("CI", "true");
        using var flag = new EnvVarScope("RTK_TRUST_PROJECT_FILTERS", "1");

        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");

        var result = TrustCommand.CheckTrustWithContent(filter);

        Assert.Equal(TrustStatusKind.EnvOverride, result.Kind);
        Assert.Equal("[filters.test]\nmatch_command = \"echo\"", result.Content);
    }

    [Fact]
    public void EnvOverride_WithRtkFlagButNoCi_IsIgnoredAndWarns()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);
        using var noCi = new MultiEnvVarScope(CiIndicatorVars, null);
        using var flag = new EnvVarScope("RTK_TRUST_PROJECT_FILTERS", "1");
        using var console = new ConsoleCapture();

        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");

        var result = TrustCommand.CheckTrustWithContent(filter);

        Assert.Equal(TrustStatusKind.Untrusted, result.Kind);
        Assert.Contains(
            "[rtk] WARNING: RTK_TRUST_PROJECT_FILTERS=1 ignored (CI environment not detected)\n",
            console.Error.ToString());
    }

    // -----------------------------------------------------------------------
    // Canonicalization failure -> fail closed
    // -----------------------------------------------------------------------

    [Fact]
    public void CanonicalKey_NonexistentPath_ReturnsNull_FailClosed()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Root, "nope.toml");

        Assert.Null(TrustCommand.CanonicalKey(missing));
    }

    [Fact]
    public void Untrust_WhenFileDeletedAfterTrust_FallsBackGracefully()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);
        using var cwd = new CwdGuard(tmp.Root);
        Directory.CreateDirectory(Path.Combine(tmp.Root, ".rtk"));
        var filter = Path.Combine(tmp.Root, ".rtk", "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");

        TrustCommand.TrustFilterWithHash(filter, Integrity.ComputeHash(filter));
        File.Delete(filter);

        using var console = new ConsoleCapture();
        var exit = TrustCommand.RunUntrust([]);

        Assert.Equal(0, exit);
        Assert.Equal("No trust entry found for current directory.\n", console.Out.ToString());
    }

    // -----------------------------------------------------------------------
    // Risk summary
    // -----------------------------------------------------------------------

    [Fact]
    public void RiskSummary_DetectsReplace()
    {
        using var console = new ConsoleCapture();
        TrustCommand.PrintRiskSummary("[filters.evil]\nmatch_command = \"git\"\nreplace = [[\"secret\", \"\"]]");

        Assert.Contains("[!] Contains 'replace' rules (can rewrite output)\n", console.Out.ToString());
    }

    [Fact]
    public void RiskSummary_DetectsMatchOutput()
    {
        using var console = new ConsoleCapture();
        TrustCommand.PrintRiskSummary("[filters.evil]\nmatch_command = \"scan\"\nmatch_output = \"vulnerability\"");

        Assert.Contains("[!] Contains 'match_output' rules (can replace entire output)\n", console.Out.ToString());
    }

    [Fact]
    public void RiskSummary_DetectsCatchAllDotPattern()
    {
        using var console = new ConsoleCapture();
        TrustCommand.PrintRiskSummary("[filters.evil]\npattern = \".\"\n");

        Assert.Contains("[!] Contains catch-all pattern '.' (matches everything)\n", console.Out.ToString());
    }

    [Fact]
    public void RiskSummary_NoHighRiskPatterns_PrintsSafeMessage()
    {
        using var console = new ConsoleCapture();
        TrustCommand.PrintRiskSummary("[filters.safe]\nmatch_command = \"ls\"\n");

        var output = console.Out.ToString();
        Assert.Contains("Risk summary:\n", output);
        Assert.Contains("  No high-risk patterns detected.\n", output);
        Assert.DoesNotContain("[!]", output);
    }

    [Fact]
    public void RiskSummary_CountsFilterSections()
    {
        using var console = new ConsoleCapture();
        TrustCommand.PrintRiskSummary("[filters.a]\nx = 1\n[filters.b]\ny = 2\n");

        Assert.Contains("  Filters: 2\n", console.Out.ToString());
    }

    // -----------------------------------------------------------------------
    // rtk trust / rtk untrust / rtk trust --list end-to-end
    // -----------------------------------------------------------------------

    [Fact]
    public void RunTrust_NoFiltersFile_FailsLoudWithExactMessage()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        var exit = TrustCommand.RunTrust([]);

        Assert.Equal(1, exit);
        Assert.Equal("rtk: No .rtk/filters.toml found in current directory\n", console.Error.ToString());
    }

    [Fact]
    public void RunTrust_ReviewsAndTrustsProjectFilters()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);
        using var cwd = new CwdGuard(tmp.Root);
        Directory.CreateDirectory(Path.Combine(tmp.Root, ".rtk"));
        File.WriteAllText(Path.Combine(tmp.Root, ".rtk", "filters.toml"), "[filters.test]\nmatch_command = \"echo\"");

        using var console = new ConsoleCapture();
        var exit = TrustCommand.RunTrust([]);
        var stdout = console.Out.ToString();

        Assert.Equal(0, exit);
        Assert.StartsWith("=== .rtk/filters.toml ===\n", stdout, StringComparison.Ordinal);
        Assert.Contains("[filters.test]\nmatch_command = \"echo\"\n", stdout);
        Assert.Contains("=========================\n", stdout);
        Assert.Contains("Risk summary:\n", stdout);
        Assert.Contains("  No high-risk patterns detected.\n", stdout);
        Assert.Contains("Project-local filters will now be applied.\n", stdout);

        var expectedShortHash = Integrity.ComputeHash(Path.Combine(tmp.Root, ".rtk", "filters.toml"))[..16];
        Assert.Contains($"Trusted .rtk/filters.toml (sha256:{expectedShortHash})\n", stdout);

        // A subsequent check must now report Trusted.
        var result = TrustCommand.CheckTrustWithContent(Path.Combine(tmp.Root, ".rtk", "filters.toml"));
        Assert.Equal(TrustStatusKind.Trusted, result.Kind);
    }

    [Fact]
    public void RunTrustList_Empty_PrintsExactMessage()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);
        using var console = new ConsoleCapture();

        var exit = TrustCommand.RunTrust(["--list"]);

        Assert.Equal(0, exit);
        Assert.Equal("No trusted project filters.\n", console.Out.ToString());
    }

    [Fact]
    public void RunTrustList_NonEmpty_PrintsHeaderDividerAndEntries()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);
        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");
        var hash = Integrity.ComputeHash(filter);
        TrustCommand.TrustFilterWithHash(filter, hash);

        using var console = new ConsoleCapture();
        var exit = TrustCommand.RunTrust(["--list"]);
        var stdout = console.Out.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Trusted project filters:\n", stdout);
        Assert.Contains(new string('═', 60) + "\n", stdout);
        Assert.Contains($"sha256:{hash}\n", stdout);
    }

    // -----------------------------------------------------------------------
    // Task 3 adapter
    // -----------------------------------------------------------------------

    [Fact]
    public void ToFilterTrustResult_MapsAllFourStates()
    {
        var trusted = TrustCommand.ToFilterTrustResult(TrustCheckResult.TrustedResult("content"));
        Assert.Equal(FilterTrustStatus.Trusted, trusted.Status);
        Assert.Equal("content", trusted.Content);

        var untrusted = TrustCommand.ToFilterTrustResult(TrustCheckResult.UntrustedResult);
        Assert.Equal(FilterTrustStatus.Untrusted, untrusted.Status);
        Assert.Null(untrusted.Content);

        var changed = TrustCommand.ToFilterTrustResult(TrustCheckResult.ContentChangedResult("expected", "actual"));
        Assert.Equal(FilterTrustStatus.ContentChanged, changed.Status);
        Assert.Null(changed.Content);

        var envOverride = TrustCommand.ToFilterTrustResult(TrustCheckResult.EnvOverrideResult("content"));
        Assert.Equal(FilterTrustStatus.EnvOverride, envOverride.Status);
        Assert.Equal("content", envOverride.Content);
    }

    [Fact]
    public void AsTrustChecker_DelegatesToCheckTrustWithContent()
    {
        using var tmp = new TempDir();
        using var data = new DataDirGuard(tmp);

        var filter = Path.Combine(tmp.Root, "filters.toml");
        File.WriteAllText(filter, "[filters.test]\nmatch_command = \"echo\"");
        TrustCommand.TrustFilterWithHash(filter, Integrity.ComputeHash(filter));

        TrustChecker checker = TrustCommand.AsTrustChecker();
        var result = checker(filter);

        Assert.Equal(FilterTrustStatus.Trusted, result.Status);
        Assert.Equal("[filters.test]\nmatch_command = \"echo\"", result.Content);
    }
}
