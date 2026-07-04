using System;
using System.IO;
using System.Linq;
using RtkSharp.Filters;
using RtkSharp.Tests.Hooks;
using Xunit;

namespace RtkSharp.Tests.Filters;

/// <summary>
/// Tests for <see cref="TomlFilterRegistry"/>: the 3-tier project/user-global/built-in precedence
/// load, the trust-gated project tier (via a fake <see cref="TrustChecker"/> standing in for Task
/// 4's not-yet-built trust store), and the <c>RTK_NO_TOML</c>/<c>RTK_TOML_DEBUG</c> environment
/// variables. Mirrors the Rust oracle's <c>test_project_filters_priority_over_builtin</c> and the
/// env-var behavior documented at the top of <c>src/core/toml_filter.rs</c>.
/// </summary>
public sealed class TomlFilterRegistryTests
{
    private static TrustChecker AlwaysTrusted(string content) =>
        _ => new FilterTrustResult(FilterTrustStatus.Trusted, content);

    private static readonly TrustChecker AlwaysUntrusted = _ => new FilterTrustResult(FilterTrustStatus.Untrusted, null);

    /// <summary>Redirects both the process CWD (for the project tier) and the global config dir
    /// (for the user-global tier) to a scratch directory, so <see cref="TomlFilterRegistry.Load"/>
    /// never touches this repo's real <c>.rtk/filters.toml</c> or the machine's real user-global
    /// config during these tests.</summary>
    private sealed class RegistryScope : IDisposable
    {
        public TempDir Tmp { get; } = new();
        private readonly GlobalScopeGuard _globalScope;
        private readonly CwdGuard _cwdGuard;

        public RegistryScope()
        {
            _globalScope = new GlobalScopeGuard(Tmp);
            _cwdGuard = new CwdGuard(Tmp.Root);
        }

        public void Dispose()
        {
            _cwdGuard.Dispose();
            _globalScope.Dispose();
        }
    }

    [Fact]
    public void Load_ProjectFilterMatchingBuiltin_ShadowsBuiltinByListOrder()
    {
        using var scope = new RegistryScope();

        // Same match_command pattern as the real "make" built-in (src/filters/make.toml), but a
        // distinguishing max_lines so we can tell which one actually matched.
        const string projectToml = """
            schema_version = 1
            [filters.make]
            match_command = "^make\\b"
            max_lines = 999
            """;
        Directory.CreateDirectory(Path.Combine(scope.Tmp.Root, ".rtk"));
        File.WriteAllText(Path.Combine(scope.Tmp.Root, ".rtk", "filters.toml"), projectToml);

        var registry = TomlFilterRegistry.Load(AlwaysTrusted(projectToml), TextWriter.Null);

        // The combined list is not deduplicated: both the project "make" and the built-in "make"
        // are present, project first — proving the shadowing is "checked first", not "replaced".
        var makeFilters = registry.Filters.Where(f => f.Name == "make").ToList();
        Assert.True(makeFilters.Count >= 2, "expected both project and builtin 'make' filters to be present in the combined list");
        Assert.Equal(999, makeFilters[0].MaxLines);

        var matched = registry.FindMatchingFilter("make all", TextWriter.Null);
        Assert.NotNull(matched);
        Assert.Equal("make", matched!.Name);
        Assert.Equal(999, matched.MaxLines);
    }

    [Fact]
    public void Load_UntrustedProjectFilters_TierAbsentFromRegistry()
    {
        using var scope = new RegistryScope();

        const string projectToml = """
            schema_version = 1
            [filters.make]
            match_command = "^make\\b"
            max_lines = 999
            """;
        Directory.CreateDirectory(Path.Combine(scope.Tmp.Root, ".rtk"));
        File.WriteAllText(Path.Combine(scope.Tmp.Root, ".rtk", "filters.toml"), projectToml);

        using var console = new ConsoleCapture();
        var registry = TomlFilterRegistry.Load(AlwaysUntrusted, TextWriter.Null);

        // Project tier is genuinely excluded — not loaded with a warning-but-applied fallback.
        var matched = registry.FindMatchingFilter("make all", TextWriter.Null);
        Assert.NotNull(matched);
        Assert.NotEqual(999, matched!.MaxLines); // the built-in's own max_lines (50), not the project's override
        Assert.Equal(50, matched.MaxLines);
    }

    [Fact]
    public void Load_UntrustedProjectFilters_PrintsExactWarningText()
    {
        using var scope = new RegistryScope();

        Directory.CreateDirectory(Path.Combine(scope.Tmp.Root, ".rtk"));
        File.WriteAllText(Path.Combine(scope.Tmp.Root, ".rtk", "filters.toml"), "schema_version = 1\n");

        var warnings = new StringWriter { NewLine = "\n" };
        TomlFilterRegistry.Load(AlwaysUntrusted, warnings);

        Assert.Equal(
            "[rtk] WARNING: untrusted project filters (.rtk/filters.toml)\n" +
            "[rtk] Filters NOT applied. Run `rtk trust` to review and enable.\n",
            warnings.ToString());
    }

    [Fact]
    public void Load_NoProjectFiltersFile_TrustCheckerNeverInvoked()
    {
        using var scope = new RegistryScope();

        var invoked = false;
        TrustChecker checker = _ =>
        {
            invoked = true;
            return new FilterTrustResult(FilterTrustStatus.Trusted, string.Empty);
        };

        TomlFilterRegistry.Load(checker, TextWriter.Null);

        Assert.False(invoked);
    }

    [Fact]
    public void FindMatchingFilter_NoToml1_BypassesEntireEngine()
    {
        using var scope = new RegistryScope();
        var registry = TomlFilterRegistry.Load(AlwaysUntrusted, TextWriter.Null);

        var previous = Environment.GetEnvironmentVariable("RTK_NO_TOML");
        try
        {
            Environment.SetEnvironmentVariable("RTK_NO_TOML", "1");

            // "make all" would ordinarily match the built-in "make" filter.
            var matched = registry.FindMatchingFilter("make all", TextWriter.Null);

            Assert.Null(matched);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_NO_TOML", previous);
        }
    }

    [Fact]
    public void FindMatchingFilter_NoToml1_ProducesNoDebugOutputEvenWhenDebugEnabled()
    {
        using var scope = new RegistryScope();
        var registry = TomlFilterRegistry.Load(AlwaysUntrusted, TextWriter.Null);

        var previousNoToml = Environment.GetEnvironmentVariable("RTK_NO_TOML");
        var previousDebug = Environment.GetEnvironmentVariable("RTK_TOML_DEBUG");
        try
        {
            Environment.SetEnvironmentVariable("RTK_NO_TOML", "1");
            Environment.SetEnvironmentVariable("RTK_TOML_DEBUG", "1");

            var debugOutput = new StringWriter { NewLine = "\n" };
            registry.FindMatchingFilter("make all", debugOutput);

            Assert.Equal(string.Empty, debugOutput.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_NO_TOML", previousNoToml);
            Environment.SetEnvironmentVariable("RTK_TOML_DEBUG", previousDebug);
        }
    }

    [Fact]
    public void FindMatchingFilter_TomlDebug1_PrintsMatchDiagnostics()
    {
        using var scope = new RegistryScope();
        var registry = TomlFilterRegistry.Load(AlwaysUntrusted, TextWriter.Null);

        var previous = Environment.GetEnvironmentVariable("RTK_TOML_DEBUG");
        try
        {
            Environment.SetEnvironmentVariable("RTK_TOML_DEBUG", "1");

            var debugOutput = new StringWriter { NewLine = "\n" };
            var matched = registry.FindMatchingFilter("make all", debugOutput);

            Assert.NotNull(matched);
            Assert.Equal(
                $"[rtk:toml] looking up filter for: \"make all\" ({registry.Filters.Count} filters loaded)\n" +
                "[rtk:toml] matched filter: 'make'\n",
                debugOutput.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_TOML_DEBUG", previous);
        }
    }

    [Fact]
    public void FindMatchingFilter_TomlDebug1_PrintsNoMatchDiagnostics()
    {
        using var scope = new RegistryScope();
        var registry = TomlFilterRegistry.Load(AlwaysUntrusted, TextWriter.Null);

        var previous = Environment.GetEnvironmentVariable("RTK_TOML_DEBUG");
        try
        {
            Environment.SetEnvironmentVariable("RTK_TOML_DEBUG", "1");

            var debugOutput = new StringWriter { NewLine = "\n" };
            var matched = registry.FindMatchingFilter("totally-unmatched-command-xyz", debugOutput);

            Assert.Null(matched);
            Assert.Equal(
                $"[rtk:toml] looking up filter for: \"totally-unmatched-command-xyz\" ({registry.Filters.Count} filters loaded)\n" +
                "[rtk:toml] no filter matched — passthrough\n",
                debugOutput.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_TOML_DEBUG", previous);
        }
    }

    [Fact]
    public void FindMatchingFilter_DebugDisabled_ProducesNoOutput()
    {
        using var scope = new RegistryScope();
        var registry = TomlFilterRegistry.Load(AlwaysUntrusted, TextWriter.Null);

        var previous = Environment.GetEnvironmentVariable("RTK_TOML_DEBUG");
        try
        {
            Environment.SetEnvironmentVariable("RTK_TOML_DEBUG", null);

            var debugOutput = new StringWriter { NewLine = "\n" };
            registry.FindMatchingFilter("make all", debugOutput);

            Assert.Equal(string.Empty, debugOutput.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_TOML_DEBUG", previous);
        }
    }

    [Fact]
    public void Load_UserGlobalFilter_LoadsUnconditionallyAndShadowsBuiltin()
    {
        using var scope = new RegistryScope();

        var globalRtkDir = Path.Combine(scope.Tmp.Root, "config", "rtk");
        Directory.CreateDirectory(globalRtkDir);
        File.WriteAllText(Path.Combine(globalRtkDir, "filters.toml"), """
            schema_version = 1
            [filters.make]
            match_command = "^make\\b"
            max_lines = 777
            """);

        var registry = TomlFilterRegistry.Load(AlwaysUntrusted, TextWriter.Null);

        var matched = registry.FindMatchingFilter("make all", TextWriter.Null);
        Assert.NotNull(matched);
        Assert.Equal(777, matched!.MaxLines);
    }
}
