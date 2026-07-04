using System;
using System.IO;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Shared test helpers for the <c>rtk init</c> test suite: a throwaway temp directory, a
/// process-current-directory guard, and console output capture. Both
/// <see cref="InitArtifactsTests"/> and <see cref="InitCommandTests"/> mutate the process CWD (a
/// process-global resource), so <see cref="CwdGuard"/> shares a single static lock across both test
/// classes to prevent cross-class races under xUnit's default parallel-by-class execution.
/// </summary>
internal static class InitTestSupport
{
    /// <summary>The single lock serializing every CWD mutation across the init test suite.</summary>
    public static readonly object CwdLock = new();

    /// <summary>
    /// The single lock serializing every <c>CLAUDE_CONFIG_DIR</c>/<c>RTK_CONFIG_DIR_OVERRIDE</c>
    /// environment-variable mutation across the init test suite (both are process-global, like the
    /// CWD guarded by <see cref="CwdLock"/>).
    /// </summary>
    public static readonly object EnvLock = new();
}

/// <summary>
/// Redirects the two environment variables that isolate global-scope <c>rtk init</c> from the real
/// user profile: <c>CLAUDE_CONFIG_DIR</c> (honored by <see cref="RtkSharp.Hooks.SettingsPatcher.ResolveClaudeDir"/>,
/// the .NET equivalent of Rust's <c>resolve_claude_dir</c>) and <c>RTK_CONFIG_DIR_OVERRIDE</c>
/// (honored by <see cref="RtkSharp.Hooks.InitArtifacts.ResolveGlobalConfigDir"/>, guarding the
/// user-global filters template path that <c>generate_global_filters_template</c> always writes to).
/// Without both set, global-scope init would read/write the real <c>~/.claude</c> and
/// <c>%APPDATA%/rtk</c> (or platform equivalent) on the machine running the tests.
/// </summary>
internal sealed class GlobalScopeGuard : IDisposable
{
    private const string ClaudeConfigDirEnvVar = "CLAUDE_CONFIG_DIR";

    private readonly string? _previousClaudeConfigDir;
    private readonly string? _previousConfigDirOverride;

    /// <summary>The throwaway directory standing in for <c>~/.claude</c>.</summary>
    public string ClaudeDir { get; }

    /// <summary>The throwaway directory standing in for the user-global config root (e.g. <c>%APPDATA%</c>).</summary>
    public string ConfigDir { get; }

    public GlobalScopeGuard(TempDir tmp)
    {
        System.Threading.Monitor.Enter(InitTestSupport.EnvLock);

        ClaudeDir = Path.Combine(tmp.Root, ".claude");
        ConfigDir = Path.Combine(tmp.Root, "config");
        Directory.CreateDirectory(ClaudeDir);
        Directory.CreateDirectory(ConfigDir);

        _previousClaudeConfigDir = Environment.GetEnvironmentVariable(ClaudeConfigDirEnvVar);
        _previousConfigDirOverride = Environment.GetEnvironmentVariable(RtkSharp.Hooks.InitArtifacts.ConfigDirOverrideEnvVar);

        Environment.SetEnvironmentVariable(ClaudeConfigDirEnvVar, ClaudeDir);
        Environment.SetEnvironmentVariable(RtkSharp.Hooks.InitArtifacts.ConfigDirOverrideEnvVar, ConfigDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ClaudeConfigDirEnvVar, _previousClaudeConfigDir);
        Environment.SetEnvironmentVariable(RtkSharp.Hooks.InitArtifacts.ConfigDirOverrideEnvVar, _previousConfigDirOverride);
        System.Threading.Monitor.Exit(InitTestSupport.EnvLock);
    }
}

/// <summary>A throwaway directory, deleted best-effort on disposal.</summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "rtksharp-init-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}

/// <summary>
/// Temporarily changes the process current directory, serialized via
/// <see cref="InitTestSupport.CwdLock"/> since <see cref="Directory.SetCurrentDirectory"/> is
/// process-global (mirrors the Rust test suite's own <c>CWD_LOCK</c> mutex, e.g.
/// <c>test_local_init_no_hook</c> in <c>src/hooks/init.rs</c>).
/// </summary>
internal sealed class CwdGuard : IDisposable
{
    private readonly string _previous;

    public CwdGuard(string newCwd)
    {
        System.Threading.Monitor.Enter(InitTestSupport.CwdLock);
        _previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(newCwd);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previous);
        System.Threading.Monitor.Exit(InitTestSupport.CwdLock);
    }
}

/// <summary>Redirects <see cref="Console.Out"/>/<see cref="Console.Error"/> for the instance's lifetime.</summary>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousError = Console.Error;

    public StringWriter Out { get; } = new() { NewLine = "\n" };
    public StringWriter Error { get; } = new() { NewLine = "\n" };

    public ConsoleCapture()
    {
        Console.SetOut(Out);
        Console.SetError(Error);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Console.SetError(_previousError);
    }
}
