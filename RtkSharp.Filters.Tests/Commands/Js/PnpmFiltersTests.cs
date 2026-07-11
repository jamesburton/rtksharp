using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RtkSharp.Filters.Commands.Js;
using RtkSharp.Parser;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PnpmFilters"/>, moved from <c>RtkSharp.Tests.Commands.Js.PnpmCommandTests</c>
/// (Task 7 of the filters-library extraction) — ported directly from Rust's own <c>#[cfg(test)]</c>
/// module in <c>pnpm_cmd.rs</c> (JSON/regex-fallback parsing, the format-listing grouping logic,
/// install-filter line classification).
/// </summary>
public sealed class PnpmFiltersTests
{
    // -----------------------------------------------------------------------
    // PnpmListParser - ports pnpm_cmd.rs's test_pnpm_list_parser_json
    // -----------------------------------------------------------------------

    [Fact]
    public void PnpmListParser_JsonTier1_ParsesDependencyTree()
    {
        const string json = /*lang=json,strict*/ """
        [
            {
                "name": "my-project",
                "version": "1.0.0",
                "dependencies": {
                    "express": { "version": "4.18.2" }
                }
            }
        ]
        """;

        var result = new PnpmFilters.PnpmListParser().Parse(json);

        Assert.Equal(1, result.Tier);
        Assert.True(result.IsOk);
        var data = result.Unwrap();
        Assert.True(data.TotalPackages >= 2, "expected the workspace root plus its 'express' dependency");
    }

    [Fact]
    public void PnpmListParser_MalformedJson_FallsBackToTextExtraction_TracksDevSection()
    {
        // Mirrors pnpm_cmd.rs's test_extract_list_text_tracks_dev_section, but exercised through the
        // full tier system (Rust's own test calls extract_list_text directly).
        const string input = "dependencies:\nreact@18.0.0\ndevDependencies:\neslint@8.0.0\n";

        var result = new PnpmFilters.PnpmListParser().Parse("not valid json at all");
        Assert.Equal(3, result.Tier); // "not valid json..." has no '@'-delimited tokens -> passthrough

        var degraded = new PnpmFilters.PnpmListParser().Parse(input);
        Assert.Equal(2, degraded.Tier);
        var data = degraded.Unwrap();
        var react = data.Dependencies.Single(d => d.Name == "react");
        var eslint = data.Dependencies.Single(d => d.Name == "eslint");
        Assert.False(react.DevDependency, "react should be prod");
        Assert.True(eslint.DevDependency, "eslint should be dev");
    }

    // -----------------------------------------------------------------------
    // PnpmOutdatedParser - ports pnpm_cmd.rs's test_pnpm_outdated_parser_json
    // -----------------------------------------------------------------------

    [Fact]
    public void PnpmOutdatedParser_JsonTier1_ParsesOutdatedCount()
    {
        const string json = /*lang=json,strict*/ """
        {
            "express": {
                "current": "4.18.2",
                "latest": "4.19.0",
                "wanted": "4.18.2"
            }
        }
        """;

        var result = new PnpmFilters.PnpmOutdatedParser().Parse(json);

        Assert.Equal(1, result.Tier);
        Assert.True(result.IsOk);
        var data = result.Unwrap();
        Assert.Equal(1, data.OutdatedCount);
        Assert.Equal("express", data.Dependencies[0].Name);
    }

    [Fact]
    public void PnpmOutdatedParser_TextTableFallback_ParsesColumns()
    {
        var input = "Package  Current  Wanted  Latest\nexpress  4.18.2   4.18.2  4.19.0\n";

        var result = new PnpmFilters.PnpmOutdatedParser().Parse(input);

        Assert.Equal(2, result.Tier);
        var data = result.Unwrap();
        var express = data.Dependencies.Single(d => d.Name == "express");
        Assert.Equal("4.18.2", express.CurrentVersion);
        Assert.Equal("4.19.0", express.LatestVersion);
        Assert.Equal(1, data.OutdatedCount);
    }

    // -----------------------------------------------------------------------
    // FormatDependencyListing - ports test_format_listing_* (pnpm_cmd.rs)
    // -----------------------------------------------------------------------

    private static DependencyState MakeState(IEnumerable<string> prod, IEnumerable<string> dev)
    {
        var deps = new List<Dependency>();
        deps.AddRange(prod.Select(name => new Dependency { Name = name, CurrentVersion = "1.0.0", DevDependency = false }));
        deps.AddRange(dev.Select(name => new Dependency { Name = name, CurrentVersion = "1.0.0", DevDependency = true }));
        return new DependencyState { TotalPackages = deps.Count, OutdatedCount = 0, Dependencies = deps };
    }

    [Fact]
    public void FormatDependencyListing_GroupsIntoProdAndDevSections()
    {
        var state = MakeState(["react", "typescript"], ["eslint", "vitest"]);
        var output = PnpmFilters.FormatDependencyListing(state, cap: true);

        Assert.Contains("[prod]", output, StringComparison.Ordinal);
        Assert.Contains("[dev]", output, StringComparison.Ordinal);
        Assert.Contains("react", output, StringComparison.Ordinal);
        Assert.Contains("eslint", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(dev)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDependencyListing_Capped_ShowsTruncationHintWithOffset()
    {
        var prod = Enumerable.Repeat("pkg", 60);
        var state = MakeState(prod, ["eslint"]);

        var output = PnpmFilters.FormatDependencyListing(state, cap: true);

        Assert.Contains($"… +{60 - PnpmFilters.MaxListing} more", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDependencyListing_Uncapped_ProdOnly_NeverTruncates()
    {
        var prod = Enumerable.Repeat("pkg", 60);
        var state = MakeState(prod, []);

        var output = PnpmFilters.FormatDependencyListing(state, cap: false);

        Assert.DoesNotContain("… +", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[dev]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDependencyListing_Uncapped_DevOnly_NeverTruncates()
    {
        var dev = Enumerable.Repeat("pkg", 60);
        var state = MakeState([], dev);

        var output = PnpmFilters.FormatDependencyListing(state, cap: false);

        Assert.DoesNotContain("… +", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[prod]", output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FormatPnpmList/FormatPnpmOutdated - the newly-carved-out pure halves of PnpmCommand's
    // LogAndFormatList/LogAndFormatOutdated (Task 7 fused-method split). Direct coverage of the pure
    // boundary itself, complementing the existing end-to-end RunListAsync/RunOutdatedAsync tests that
    // remain in RtkSharp.Tests (which exercise the Console-diagnostic half + full dispatch).
    // -----------------------------------------------------------------------

    // Both FormatPnpmList and FormatDependencyListing tee their capped-off entries to disk and embed a
    // freshly generated (epoch-timestamped) filename in the returned "tail -n +N <path>" hint. Two
    // independent calls therefore legitimately produce different filenames whenever they straddle a
    // one-second boundary, so the hint's variable portion is masked before comparing the two outputs
    // for equality (the point of this test — that FormatPnpmList delegates to FormatDependencyListing
    // with cap:true — doesn't depend on the tee filename matching byte-for-byte).
    private static readonly Regex TeeHintPathRegex = new(@"tail -n \+\d+ \S+", RegexOptions.Compiled);

    private static string MaskTeeHint(string output) => TeeHintPathRegex.Replace(output, "tail -n +<offset> <tee-file>");

    [Fact]
    public void FormatPnpmList_IsFilteredFalse_DelegatesToFormatDependencyListingWithCapTrue()
    {
        var state = MakeState(Enumerable.Repeat("pkg", 60), []);

        var viaFormatPnpmList = PnpmFilters.FormatPnpmList(state, isFiltered: false);
        var viaDirectCall = PnpmFilters.FormatDependencyListing(state, cap: true);

        Assert.Equal(MaskTeeHint(viaDirectCall), MaskTeeHint(viaFormatPnpmList));
        Assert.Contains("… +", viaFormatPnpmList, StringComparison.Ordinal);
        Assert.Contains("tail -n +", viaDirectCall, StringComparison.Ordinal);
        Assert.Contains("tail -n +", viaFormatPnpmList, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPnpmList_IsFilteredTrue_DelegatesToFormatDependencyListingWithCapFalse()
    {
        var state = MakeState(Enumerable.Repeat("pkg", 60), []);

        var viaFormatPnpmList = PnpmFilters.FormatPnpmList(state, isFiltered: true);
        var viaDirectCall = PnpmFilters.FormatDependencyListing(state, cap: false);

        Assert.Equal(viaDirectCall, viaFormatPnpmList);
        Assert.DoesNotContain("… +", viaFormatPnpmList, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPnpmOutdated_DelegatesToSharedTokenFormatter()
    {
        var state = new DependencyState
        {
            TotalPackages = 1,
            OutdatedCount = 1,
            Dependencies = [new Dependency { Name = "express", CurrentVersion = "4.18.2", LatestVersion = "4.19.0", DevDependency = false }],
        };

        var result = PnpmFilters.FormatPnpmOutdated(state, FormatMode.Compact);

        Assert.Equal(((ITokenFormatter)state).Format(FormatMode.Compact), result);
        Assert.Contains("express", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterPnpmInstall - ports the line-classification behavior of filter_pnpm_install
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterPnpmInstall_StripsProgressBarsAndPostProgressBlankLines()
    {
        var output = "Progress: resolved 10, reused 5\n│ 50%\n\nSome dep line\ndependencies: +5\n";

        var result = PnpmFilters.FilterPnpmInstall(output);

        Assert.DoesNotContain("Progress", result, StringComparison.Ordinal);
        Assert.DoesNotContain('│', result);
    }

    [Fact]
    public void FilterPnpmInstall_KeepsErrorLines()
    {
        var output = "Progress: 10%\nERR_PNPM_FETCH_404 fetch failed\nsome other error occurred here\n";

        var result = PnpmFilters.FilterPnpmInstall(output);

        Assert.Contains("ERR_PNPM_FETCH_404", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPnpmInstall_KeepsSummaryLines()
    {
        var output = "Progress: 100%\n+ 5 packages in 2.3s\n dependencies:\n";

        var result = PnpmFilters.FilterPnpmInstall(output);

        Assert.Contains("packages in", result, StringComparison.Ordinal);
        Assert.Contains("dependencies:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPnpmInstall_PlusMinusPrefixedLines_Kept()
    {
        var output = "+ added-package 1.0.0\n- removed-package 2.0.0\nirrelevant noise line here\n";

        var result = PnpmFilters.FilterPnpmInstall(output);

        Assert.Contains("+ added-package 1.0.0", result, StringComparison.Ordinal);
        Assert.Contains("- removed-package 2.0.0", result, StringComparison.Ordinal);
        Assert.DoesNotContain("irrelevant noise line here", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPnpmInstall_EverythingFiltered_ReturnsLiteralOk()
    {
        Assert.Equal("ok", PnpmFilters.FilterPnpmInstall("Progress: 10%\n│ spinner\n\n"));
    }
}
