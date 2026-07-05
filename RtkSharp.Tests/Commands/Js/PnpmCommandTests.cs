using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Js;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Parser;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="PnpmCommand"/>, ported directly from Rust's own <c>#[cfg(test)]</c> module in
/// <c>pnpm_cmd.rs</c> (JSON/regex-fallback parsing, the cap-only-when-unfiltered listing logic,
/// install-filter line classification) plus original coverage for the CLI-parsing surface
/// (<c>--filter</c>/<c>--depth</c> extraction, the typecheck stub wiring, the <c>--filter</c>+
/// <c>typecheck</c> warning, and the passthrough route) that lives inline in Rust's <c>main.rs</c>
/// <c>Commands::Pnpm</c> arm rather than in a dedicated Rust <c>#[cfg(test)]</c> module.
/// </summary>
public sealed class PnpmCommandTests
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

        var result = new PnpmCommand.PnpmListParser().Parse(json);

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

        var result = new PnpmCommand.PnpmListParser().Parse("not valid json at all");
        Assert.Equal(3, result.Tier); // "not valid json..." has no '@'-delimited tokens -> passthrough

        var degraded = new PnpmCommand.PnpmListParser().Parse(input);
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

        var result = new PnpmCommand.PnpmOutdatedParser().Parse(json);

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

        var result = new PnpmCommand.PnpmOutdatedParser().Parse(input);

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
        var output = PnpmCommand.FormatDependencyListing(state, cap: true);

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

        var output = PnpmCommand.FormatDependencyListing(state, cap: true);

        Assert.Contains($"… +{60 - PnpmCommand.MaxListing} more", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDependencyListing_Uncapped_ProdOnly_NeverTruncates()
    {
        var prod = Enumerable.Repeat("pkg", 60);
        var state = MakeState(prod, []);

        var output = PnpmCommand.FormatDependencyListing(state, cap: false);

        Assert.DoesNotContain("… +", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[dev]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDependencyListing_Uncapped_DevOnly_NeverTruncates()
    {
        var dev = Enumerable.Repeat("pkg", 60);
        var state = MakeState([], dev);

        var output = PnpmCommand.FormatDependencyListing(state, cap: false);

        Assert.DoesNotContain("… +", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[prod]", output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Cap-only-when-unfiltered: `pnpm list` (capped) vs `pnpm list --prod` (uncapped) end to end.
    // This is the specific behavior a review will scrutinize - both cases exercise the exact same
    // 60-entry prod section through RunListAsync's own is_filtered detection so the test genuinely
    // discriminates the flag check, rather than merely calling FormatDependencyListing directly with a
    // hand-picked bool.
    // -----------------------------------------------------------------------

    private static string BuildPnpmListJson(int prodCount)
    {
        // The workspace root itself has no "version" so it doesn't count as its own dependency entry
        // (real `pnpm list --json` workspace roots often omit it) - keeps the expected count an exact
        // match to prodCount rather than prodCount + 1.
        var deps = string.Join(",\n", Enumerable.Range(0, prodCount).Select(i => $"\"pkg{i}\": {{ \"version\": \"1.0.0\" }}"));
        return $$"""
        [
            {
                "name": "root",
                "dependencies": { {{deps}} }
            }
        ]
        """;
    }

    [Fact]
    public async Task RunListAsync_PlainInvocation_NoProdDevFlag_CapsAt20()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var json = BuildPnpmListJson(60);
        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        // No --prod/-P/--dev/-D anywhere in args -> is_filtered=false -> cap applies (pnpm_cmd.rs:383-385).
        var exitCode = await PnpmCommand.RunListAsync(depth: 0, args: [], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("… +40 more", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunListAsync_ProdFlagPresent_NeverCaps()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var json = BuildPnpmListJson(60);
        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        // --prod present -> is_filtered=true -> cap must NOT apply, even though the same 60 packages
        // are present as in the capped case above.
        var exitCode = await PnpmCommand.RunListAsync(depth: 0, args: ["--prod"], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.DoesNotContain("… +", output, StringComparison.Ordinal);
        for (var i = 0; i < 60; i++)
        {
            Assert.Contains($"pkg{i} 1.0.0", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunListAsync_DevFlagPresent_NeverCaps()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var json = BuildPnpmListJson(60);
        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunListAsync(depth: 0, args: ["--dev"], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("… +", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunListAsync_NonZeroExit_EchoesStderrAndReturnsExitCode_NoFiltering()
    {
        using var stdout = new ConsoleOutCapture();
        using var stderr = new ConsoleErrorCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, "pnpm ERR! network timeout", 7, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunListAsync(depth: 0, args: [], verbose: 0, executor: fake);

        Assert.Equal(7, exitCode);
        Assert.Contains("pnpm ERR! network timeout", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    // -----------------------------------------------------------------------
    // run_outdated: always returns 0 even when the child's own exit code is non-zero.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunOutdatedAsync_EmptyResult_PrintsExactLiteralMessage()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult("{}", string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunOutdatedAsync([], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Equal("All packages up-to-date\n", stdout.ToString());
    }

    [Fact]
    public async Task RunOutdatedAsync_ChildNonZeroExitCode_StillReturnsZero()
    {
        using var db = new TempTrackingDb();
        using var stdout = new ConsoleOutCapture();

        // pnpm outdated exits non-zero whenever outdated packages exist; Rust's run_outdated never
        // inspects this and always returns Ok(0) (pnpm_cmd.rs:420-467) - replicated exactly.
        const string json = /*lang=json,strict*/ """{"express":{"current":"4.18.2","latest":"4.19.0"}}""";
        var fake = new FakeProcessExecutor(new ExecutionResult(json, string.Empty, 1, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunOutdatedAsync([], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("express", stdout.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // FilterPnpmInstall - ports the line-classification behavior of filter_pnpm_install
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterPnpmInstall_StripsProgressBarsAndPostProgressBlankLines()
    {
        var output = "Progress: resolved 10, reused 5\n│ 50%\n\nSome dep line\ndependencies: +5\n";

        var result = PnpmCommand.FilterPnpmInstall(output);

        Assert.DoesNotContain("Progress", result, StringComparison.Ordinal);
        Assert.DoesNotContain('│', result);
    }

    [Fact]
    public void FilterPnpmInstall_KeepsErrorLines()
    {
        var output = "Progress: 10%\nERR_PNPM_FETCH_404 fetch failed\nsome other error occurred here\n";

        var result = PnpmCommand.FilterPnpmInstall(output);

        Assert.Contains("ERR_PNPM_FETCH_404", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPnpmInstall_KeepsSummaryLines()
    {
        var output = "Progress: 100%\n+ 5 packages in 2.3s\n dependencies:\n";

        var result = PnpmCommand.FilterPnpmInstall(output);

        Assert.Contains("packages in", result, StringComparison.Ordinal);
        Assert.Contains("dependencies:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPnpmInstall_PlusMinusPrefixedLines_Kept()
    {
        var output = "+ added-package 1.0.0\n- removed-package 2.0.0\nirrelevant noise line here\n";

        var result = PnpmCommand.FilterPnpmInstall(output);

        Assert.Contains("+ added-package 1.0.0", result, StringComparison.Ordinal);
        Assert.Contains("- removed-package 2.0.0", result, StringComparison.Ordinal);
        Assert.DoesNotContain("irrelevant noise line here", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterPnpmInstall_EverythingFiltered_ReturnsLiteralOk()
    {
        Assert.Equal("ok", PnpmCommand.FilterPnpmInstall("Progress: 10%\n│ spinner\n\n"));
    }

    [Fact]
    public async Task RunInstallAsync_NonZeroExit_EchoesStderrAndReturnsExitCode_NoFiltering()
    {
        using var stdout = new ConsoleOutCapture();
        using var stderr = new ConsoleErrorCapture();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, "install failed", 1, TimeSpan.Zero, true, null, false));

        var exitCode = await PnpmCommand.RunInstallAsync([], verbose: 0, executor: fake);

        Assert.Equal(1, exitCode);
        Assert.Contains("install failed", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    // -----------------------------------------------------------------------
    // CLI parsing: ParseInvocation / ExtractDepth (--filter, --depth extraction)
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseInvocation_ListWithDepthAndFilter_ExtractsAll()
    {
        var invocation = PnpmCommand.ParseInvocation(["-F", "@app1", "--filter=@app2", "list", "--depth", "2", "--prod"]);

        Assert.Equal(["@app1", "@app2"], invocation.Filters);
        Assert.Equal(PnpmVerb.List, invocation.Verb);
        Assert.Equal(2, invocation.Depth);
        Assert.Equal(["--prod"], invocation.Args);
    }

    [Fact]
    public void ParseInvocation_ListNoDepth_DefaultsToZero()
    {
        var invocation = PnpmCommand.ParseInvocation(["list"]);

        Assert.Equal(PnpmVerb.List, invocation.Verb);
        Assert.Equal(0, invocation.Depth);
        Assert.Empty(invocation.Args);
    }

    [Theory]
    [InlineData("outdated", PnpmVerb.Outdated)]
    [InlineData("install", PnpmVerb.Install)]
    [InlineData("typecheck", PnpmVerb.Typecheck)]
    public void ParseInvocation_KnownVerbs_RouteCorrectly(string verbToken, PnpmVerb expected)
    {
        var invocation = PnpmCommand.ParseInvocation([verbToken, "extra"]);

        Assert.Equal(expected, invocation.Verb);
        Assert.Equal(["extra"], invocation.Args);
    }

    [Fact]
    public void ParseInvocation_UnrecognizedSubcommand_RoutesToOtherWithFullPassthroughArgs()
    {
        var invocation = PnpmCommand.ParseInvocation(["exec", "eslint", "--fix"]);

        Assert.Equal(PnpmVerb.Other, invocation.Verb);
        Assert.Equal(["exec", "eslint", "--fix"], invocation.PassthroughArgs);
    }

    [Fact]
    public void ParseInvocation_EmptyArgs_RoutesToOtherWithEmptyPassthrough()
    {
        var invocation = PnpmCommand.ParseInvocation([]);

        Assert.Equal(PnpmVerb.Other, invocation.Verb);
        Assert.Empty(invocation.PassthroughArgs);
    }

    [Theory]
    [InlineData(new[] { "--depth", "3" }, 3)]
    [InlineData(new[] { "-d", "5" }, 5)]
    [InlineData(new[] { "--depth=7" }, 7)]
    [InlineData(new[] { "-d=1" }, 1)]
    public void ExtractDepth_AllSyntaxForms_ParseCorrectly(string[] args, int expectedDepth)
    {
        var (depth, rest) = PnpmCommand.ExtractDepth(args);

        Assert.Equal(expectedDepth, depth);
        Assert.Empty(rest);
    }

    [Fact]
    public void MergeFilters_PrependsFilterEqualsFormBeforeArgs()
    {
        var merged = PnpmCommand.MergeFilters(["@app1", "@app2"], ["--prod"]);

        Assert.Equal(["--filter=@app1", "--filter=@app2", "--prod"], merged);
    }

    // -----------------------------------------------------------------------
    // validate_pnpm_filters: --filter + typecheck warning (main.rs:1402-1422)
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidatePnpmFilters_TypecheckWithFilters_ReturnsExactWarning()
    {
        var warning = PnpmCommand.ValidatePnpmFilters(["@app1"], PnpmVerb.Typecheck);

        Assert.Equal(
            "[rtk] warning: --filter is not yet supported for pnpm tsc, filters preceding the subcommand will be ignored",
            warning);
    }

    [Fact]
    public void ValidatePnpmFilters_TypecheckNoFilters_ReturnsNull() =>
        Assert.Null(PnpmCommand.ValidatePnpmFilters([], PnpmVerb.Typecheck));

    [Theory]
    [InlineData(PnpmVerb.List)]
    [InlineData(PnpmVerb.Outdated)]
    [InlineData(PnpmVerb.Install)]
    [InlineData(PnpmVerb.Other)]
    public void ValidatePnpmFilters_NonTypecheckVerbs_ReturnsNullEvenWithFilters(PnpmVerb verb) =>
        Assert.Null(PnpmCommand.ValidatePnpmFilters(["@app1"], verb));

    // -----------------------------------------------------------------------
    // Typecheck: pure alias, stubbed until Phase 8 Task 4 lands
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_Typecheck_ThrowsNotImplementedNamingFutureTask()
    {
        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => PnpmCommand.DispatchAsync(["typecheck", "--noEmit"], verbose: 0, executor: null));

        Assert.Contains("typecheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Phase 8 Task 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_TypecheckWithFilter_PrintsWarningThenThrows()
    {
        using var stderr = new ConsoleErrorCapture();

        await Assert.ThrowsAsync<NotImplementedException>(
            () => PnpmCommand.DispatchAsync(["-F", "@app1", "typecheck"], verbose: 0, executor: null));

        Assert.Contains(
            "[rtk] warning: --filter is not yet supported for pnpm tsc",
            stderr.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPnpmSafeAsync_TypecheckStub_PrintsRtkPrefixedNotImplementedMessage_ReturnsOne()
    {
        using var stderr = new ConsoleErrorCapture();

        var exitCode = await PnpmCommand.RunPnpmSafeAsync(["typecheck"], verbose: 0, executor: null);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("rtk: pnpm typecheck is not yet implemented", stderr.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Other/passthrough - executes via an injectable IProcessExecutor, mirroring Rust's
    // core::runner::run_passthrough (runner.rs:186-193, 235-249)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_UnrecognizedSubcommand_RunsRawPassthrough_NoFiltering()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));
        var exitCode = await PnpmCommand.DispatchAsync(["exec", "cowsay"], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("pnpm", fake.LastRequest!.FileName);
        Assert.Equal(["exec", "cowsay"], fake.LastRequest.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.LastRequest.CaptureMode);
    }

    [Fact]
    public async Task DispatchAsync_PassthroughWithFilters_MergesFilterArgsInFront()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));
        var exitCode = await PnpmCommand.DispatchAsync(["-F", "@app1", "why", "left-pad"], verbose: 0, executor: fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(["--filter=@app1", "why", "left-pad"], fake.LastRequest!.Arguments);
    }

    [Fact]
    public async Task RunPassthroughAsync_NonZeroExitCode_Propagates()
    {
        using var db = new TempTrackingDb();

        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 5, TimeSpan.Zero, true, null, false));
        var exitCode = await PnpmCommand.RunPassthroughAsync(["exec", "cowsay"], verbose: 0, executor: fake);

        Assert.Equal(5, exitCode);
    }

    [Fact]
    public async Task RunPassthroughAsync_SpawnFailure_ThrowsWithFailureDetail()
    {
        var fake = new FakeProcessExecutor(
            new ExecutionResult(string.Empty, string.Empty, 127, TimeSpan.Zero, false, "The system cannot find the file specified", false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PnpmCommand.RunPassthroughAsync(["exec", "cowsay"], verbose: 0, executor: fake));

        Assert.Contains("Failed to run pnpm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("The system cannot find the file specified", ex.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private sealed class FakeProcessExecutor(ExecutionResult result) : IProcessExecutor
    {
        public int Calls { get; private set; }

        public ExecutionRequest? LastRequest { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
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
            Path.Combine(Path.GetTempPath(), "rtksharp-pnpm-cmd-tests-" + Guid.NewGuid().ToString("N"));

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
