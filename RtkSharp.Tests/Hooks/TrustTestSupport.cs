using System;
using System.IO;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Redirects <see cref="RtkSharp.Hooks.TrustCommand.DataDirOverrideEnvVar"/> (<c>RTK_DATA_DIR_OVERRIDE</c>)
/// to a throwaway temp directory so no <c>TrustCommandTests</c> test ever reads or writes the real
/// user's actual <c>trusted_filters.json</c> — the trust-store analog of
/// <see cref="InitTestSupport"/>'s <c>GlobalScopeGuard</c>/<c>RTK_CONFIG_DIR_OVERRIDE</c> pattern.
/// </summary>
internal sealed class DataDirGuard : IDisposable
{
    private readonly string? _previous;

    /// <summary>The throwaway directory standing in for the platform local-data root (e.g. <c>%LOCALAPPDATA%</c>).</summary>
    public string DataDir { get; }

    public DataDirGuard(TempDir tmp)
    {
        System.Threading.Monitor.Enter(InitTestSupport.EnvLock);

        DataDir = Path.Combine(tmp.Root, "data");
        Directory.CreateDirectory(DataDir);

        _previous = Environment.GetEnvironmentVariable(RtkSharp.Hooks.TrustCommand.DataDirOverrideEnvVar);
        Environment.SetEnvironmentVariable(RtkSharp.Hooks.TrustCommand.DataDirOverrideEnvVar, DataDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RtkSharp.Hooks.TrustCommand.DataDirOverrideEnvVar, _previous);
        System.Threading.Monitor.Exit(InitTestSupport.EnvLock);
    }
}

/// <summary>
/// Temporarily sets (or clears) an arbitrary environment variable, restoring its previous value on
/// disposal. Used by the CI-env-override tests to set/clear <c>RTK_TRUST_PROJECT_FILTERS</c> and the
/// various CI-indicator variables without permanently mutating the test process's environment.
/// </summary>
internal sealed class EnvVarScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public EnvVarScope(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}

/// <summary>
/// Temporarily clears (or sets) a batch of environment variables at once, restoring each one's
/// previous value on disposal. Used to reliably clear all five CI-indicator variables Rust checks
/// (<c>CI</c>, <c>GITHUB_ACTIONS</c>, <c>GITLAB_CI</c>, <c>JENKINS_URL</c>, <c>BUILDKITE</c>) so the
/// "env override ignored without CI" test is deterministic even when the test suite itself happens to
/// be running inside a real CI environment.
/// </summary>
internal sealed class MultiEnvVarScope : IDisposable
{
    private readonly string[] _names;
    private readonly string?[] _previous;

    public MultiEnvVarScope(string[] names, string? value)
    {
        _names = names;
        _previous = new string?[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            _previous[i] = Environment.GetEnvironmentVariable(names[i]);
            Environment.SetEnvironmentVariable(names[i], value);
        }
    }

    public void Dispose()
    {
        for (var i = 0; i < _names.Length; i++)
        {
            Environment.SetEnvironmentVariable(_names[i], _previous[i]);
        }
    }
}
