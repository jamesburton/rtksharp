using System;
using System.IO;
using System.Linq;
using RtkSharp.Filters.Toml;
using RtkSharp.Tests.Hooks;
using Xunit;

namespace RtkSharp.Tests.Filters;

/// <summary>
/// Tests for <see cref="TomlFilterEngine.RunFilterTests"/> — the inline-test battery that
/// <c>rtk verify</c> dispatches. Faithful-port coverage of Rust <c>run_filter_tests</c>
/// (<c>toml_filter.rs:545-598</c>): built-in + trust-gated project sources, per-filter outcome
/// collection, and the <c>--require-all</c> missing-tests detection. Uses a scratch CWD + injected
/// <see cref="TrustChecker"/> so a controlled project-local filter is exercised in isolation from the
/// built-ins (via <c>--filter</c> targeting a name no built-in defines).
/// </summary>
public sealed class RunFilterTestsTests
{
    private const string ProjectToml = """
        schema_version = 1

        [filters.zz-echo]
        match_command = "^zz-echo\\b"

        [filters.zz-notest]
        match_command = "^zz-notest\\b"

        [[tests.zz-echo]]
        name = "passes"
        input = "hello"
        expected = "hello"

        [[tests.zz-echo]]
        name = "fails"
        input = "hello"
        expected = "goodbye"
        """;

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

    private static readonly TrustChecker Untrusted =
        _ => new FilterTrustResult(FilterTrustStatus.Untrusted, null);

    [Fact]
    public void RunFilterTests_TrustedProjectFilter_CollectsItsOutcomes()
    {
        using var scope = new ProjectScope(ProjectToml);

        var results = TomlFilterEngine.RunFilterTests("zz-echo", Trusted(ProjectToml), TextWriter.Null);

        Assert.Equal(2, results.Outcomes.Count);
        Assert.True(results.Outcomes[0].Passed);   // "passes": hello -> hello
        Assert.False(results.Outcomes[1].Passed);  // "fails": hello != goodbye
        Assert.All(results.Outcomes, o => Assert.Equal("zz-echo", o.FilterName));
        Assert.Empty(results.FiltersWithoutTests); // filter-restricted to a tested filter
    }

    [Fact]
    public void RunFilterTests_FilterWithoutTests_ReportedForRequireAll()
    {
        using var scope = new ProjectScope(ProjectToml);

        var results = TomlFilterEngine.RunFilterTests("zz-notest", Trusted(ProjectToml), TextWriter.Null);

        Assert.Empty(results.Outcomes);
        Assert.Equal(new[] { "zz-notest" }, results.FiltersWithoutTests);
    }

    [Fact]
    public void RunFilterTests_UntrustedProject_SkippedWithWarning()
    {
        using var scope = new ProjectScope(ProjectToml);
        var warnings = new StringWriter { NewLine = "\n" };

        var results = TomlFilterEngine.RunFilterTests("zz-echo", Untrusted, warnings);

        // Project tier not loaded → the project-only "zz-echo" contributes nothing.
        Assert.Empty(results.Outcomes);
        Assert.Empty(results.FiltersWithoutTests);
        Assert.Contains("[rtk] WARNING: untrusted project filters skipped in verify\n", warnings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunFilterTests_BuiltinsOnly_AllInlineTestsPass()
    {
        // No project file, no global override interference: bare battery over the 63 built-ins should
        // be entirely green (mirrors the oracle's own inline self-tests).
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);
        using var cwd = new CwdGuard(temp.Root); // no .rtk/filters.toml here

        var results = TomlFilterEngine.RunFilterTests(null, _ => new FilterTrustResult(FilterTrustStatus.Untrusted, null), TextWriter.Null);

        Assert.NotEmpty(results.Outcomes);
        var failures = results.Outcomes.Where(o => !o.Passed).ToList();
        Assert.True(failures.Count == 0, "built-in inline tests failed: " +
            string.Join(", ", failures.Select(f => $"[{f.FilterName}] {f.TestName}")));
    }
}
