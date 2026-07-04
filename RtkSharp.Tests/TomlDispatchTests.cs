using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Execution;
using RtkSharp.Filters;
using RtkSharp.Tests.Hooks;
using Xunit;

namespace RtkSharp.Tests;

/// <summary>
/// Tests for Phase 4 Task 5's TOML filter fallback dispatch (<see cref="RtkProgram.TryTomlFallbackAsync"/>):
/// a command with no dedicated module but a matching filter is filtered; one with no match passes
/// through unchanged; <c>RTK_NO_TOML=1</c> bypasses the engine; and commands already owned by a
/// dedicated module are never intercepted (zero regression). Faithful to Rust's <c>run_fallback</c>
/// TOML branch (<c>main.rs:1213-1292</c>).
/// </summary>
public sealed class TomlDispatchTests
{
    private const string ProjectToml = """
        schema_version = 1

        [filters.zz-strip]
        match_command = "^zz-strip\\b"
        strip_lines_matching = ["^DROP"]
        """;

    private sealed class FakeExecutor(ExecutionResult result) : IProcessExecutor
    {
        public int Calls { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ProjectScope : IDisposable
    {
        public TempDir Tmp { get; } = new();
        private readonly GlobalScopeGuard _globalScope;
        private readonly CwdGuard _cwdGuard;

        public ProjectScope(string projectToml)
        {
            _globalScope = new GlobalScopeGuard(Tmp);
            _cwdGuard = new CwdGuard(Tmp.Root);
            Directory.CreateDirectory(Path.Combine(Tmp.Root, ".rtk"));
            File.WriteAllText(Path.Combine(Tmp.Root, ".rtk", "filters.toml"), projectToml);
        }

        public void Dispose()
        {
            _cwdGuard.Dispose();
            _globalScope.Dispose();
        }
    }

    private static TrustChecker Trusted(string content) =>
        _ => new FilterTrustResult(FilterTrustStatus.Trusted, content);

    [Fact]
    public async Task TryTomlFallback_MatchingFilter_ProducesFilteredOutput()
    {
        using var scope = new ProjectScope(ProjectToml);
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var exec = new FakeExecutor(
            new ExecutionResult("keep1\nDROP me\nkeep2\n", string.Empty, 0, TimeSpan.Zero, true, null, false));

        var result = await RtkProgram.TryTomlFallbackAsync(
            "zz-strip", Array.Empty<string>(), stdout, stderr, exec, Trusted(ProjectToml), CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(1, exec.Calls);
        // Stripped "DROP me", then println! appends a trailing newline.
        Assert.Equal("keep1\nkeep2\n", stdout.ToString());
    }

    [Fact]
    public async Task TryTomlFallback_NoMatchingFilter_ReturnsNullAndDoesNotRun()
    {
        using var scope = new ProjectScope(ProjectToml);
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var exec = new FakeExecutor(
            new ExecutionResult("unused", string.Empty, 0, TimeSpan.Zero, true, null, false));

        var result = await RtkProgram.TryTomlFallbackAsync(
            "zz-nomatch", new[] { "arg" }, stdout, stderr, exec, Trusted(ProjectToml), CancellationToken.None);

        // Null → caller falls through to the unchanged raw passthrough; command is not run here.
        Assert.Null(result);
        Assert.Equal(0, exec.Calls);
        Assert.Equal("", stdout.ToString());
    }

    [Fact]
    public async Task TryTomlFallback_RtkNoToml_BypassesEngine()
    {
        using var scope = new ProjectScope(ProjectToml);
        var previous = Environment.GetEnvironmentVariable("RTK_NO_TOML");
        Environment.SetEnvironmentVariable("RTK_NO_TOML", "1");
        try
        {
            var stdout = new StringWriter { NewLine = "\n" };
            var stderr = new StringWriter { NewLine = "\n" };
            var exec = new FakeExecutor(
                new ExecutionResult("keep1\nDROP me\nkeep2\n", string.Empty, 0, TimeSpan.Zero, true, null, false));

            var result = await RtkProgram.TryTomlFallbackAsync(
                "zz-strip", Array.Empty<string>(), stdout, stderr, exec, Trusted(ProjectToml), CancellationToken.None);

            Assert.Null(result); // bypassed even though zz-strip would match
            Assert.Equal(0, exec.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_NO_TOML", previous);
        }
    }

    [Theory]
    [InlineData("git")]
    [InlineData("gh")]
    [InlineData("dotnet")]
    [InlineData("grep")]
    [InlineData("ls")]
    [InlineData("read")]
    [InlineData("find")]
    [InlineData("tree")]
    [InlineData("wc")]
    [InlineData("hook")]
    [InlineData("init")]
    [InlineData("verify")]
    [InlineData("config")]
    [InlineData("trust")]
    [InlineData("rewrite")]
    public void DedicatedModules_AreRoutedBeforeTomlFallback(string verb)
    {
        // Zero-regression proof: a dedicated verb resolves in the registry, so Program returns its
        // handler before ever reaching TryTomlFallbackAsync — the TOML engine can never intercept it.
        Assert.True(CommandRegistry.TryGet(verb, out _));
    }
}
