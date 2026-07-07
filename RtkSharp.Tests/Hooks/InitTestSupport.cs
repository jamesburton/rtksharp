using System;
using System.IO;
using System.Threading;

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

    private static readonly SemaphoreSlim EnvSemaphore = new(1, 1);

    /// <summary>
    /// Per-logical-call-context reentrancy depth for <see cref="EnvSemaphore"/>. An
    /// <see cref="AsyncLocal{T}"/> (not <c>[ThreadStatic]</c>) is required: it flows with the async
    /// call chain across <c>await</c> continuations even when the continuation resumes on a
    /// different thread-pool thread than the one that entered the guard, which is exactly what
    /// happens in Phase 5's <c>HookCheckTests</c> (guards held across <c>await
    /// RtkSharp.RtkProgram.RunAsync(...)</c>).
    /// </summary>
    private static readonly AsyncLocal<int> EnvLockDepth = new();

    /// <summary>
    /// Enters the environment-variable lock serializing every <c>CLAUDE_CONFIG_DIR</c>/
    /// <c>RTK_CONFIG_DIR_OVERRIDE</c>/<c>CODEX_HOME</c>/<c>RTK_DATA_DIR_OVERRIDE</c> mutation across
    /// the init/trust test suites (all process-global, like the CWD guarded by
    /// <see cref="CwdLock"/>). Reentrant on the same logical call context (e.g. a test method that
    /// nests <c>GlobalScopeGuard</c> then <c>DataDirGuard</c> synchronously, both guarding the same
    /// resource) — a bare non-reentrant <see cref="SemaphoreSlim"/> would deadlock on that second,
    /// nested <c>Wait()</c> call on the same thread, since only the matching number of
    /// <see cref="ExitEnvLock"/> calls releases the underlying semaphore.
    /// </summary>
    public static void EnterEnvLock()
    {
        var depth = EnvLockDepth.Value;
        if (depth == 0)
        {
            EnvSemaphore.Wait();
        }

        EnvLockDepth.Value = depth + 1;
    }

    /// <summary>Releases one level of <see cref="EnterEnvLock"/>, releasing the underlying semaphore only when the outermost level unwinds.</summary>
    public static void ExitEnvLock()
    {
        var depth = EnvLockDepth.Value - 1;
        EnvLockDepth.Value = depth;
        if (depth == 0)
        {
            EnvSemaphore.Release();
        }
    }
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
        InitTestSupport.EnterEnvLock();

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
        InitTestSupport.ExitEnvLock();
    }
}

/// <summary>
/// Redirects <c>CODEX_HOME</c> so Codex-mode global-scope tests never touch the real
/// <c>~/.codex</c> directory on the machine running the tests. Unlike
/// <see cref="GlobalScopeGuard"/>'s <c>RTK_CONFIG_DIR_OVERRIDE</c> escape hatch (needed because
/// Rust's <c>dirs::config_dir()</c> ignores environment variables on Windows), Codex's
/// <c>resolve_codex_dir</c> reads <c>$CODEX_HOME</c> directly via a plain <c>std::env::var_os</c>
/// call — honored identically on every platform — so no platform-specific redirection machinery is
/// needed here.
/// </summary>
internal sealed class CodexScopeGuard : IDisposable
{
    private const string CodexHomeEnvVar = "CODEX_HOME";

    private readonly string? _previousCodexHome;

    /// <summary>The throwaway directory standing in for <c>$CODEX_HOME</c> (i.e. <c>~/.codex</c>).</summary>
    public string CodexDir { get; }

    public CodexScopeGuard(TempDir tmp)
    {
        InitTestSupport.EnterEnvLock();

        CodexDir = Path.Combine(tmp.Root, ".codex");
        Directory.CreateDirectory(CodexDir);

        _previousCodexHome = Environment.GetEnvironmentVariable(CodexHomeEnvVar);
        Environment.SetEnvironmentVariable(CodexHomeEnvVar, CodexDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHomeEnvVar, _previousCodexHome);
        InitTestSupport.ExitEnvLock();
    }
}

/// <summary>
/// Redirects <c>COPILOT_HOME</c> so Copilot-mode global-scope tests never touch the real
/// <c>~/.copilot</c> directory on the machine running the tests. Same shape as
/// <see cref="CodexScopeGuard"/>: <c>RtkSharp.Hooks.CopilotInit.CopilotUserDir</c> reads
/// <c>$COPILOT_HOME</c> directly via a plain environment-variable lookup, honored identically on
/// every platform, so no platform-specific redirection machinery is needed here.
/// </summary>
internal sealed class CopilotScopeGuard : IDisposable
{
    private const string CopilotHomeEnvVar = "COPILOT_HOME";

    private readonly string? _previousCopilotHome;

    /// <summary>The throwaway directory standing in for <c>$COPILOT_HOME</c> (i.e. <c>~/.copilot</c>).</summary>
    public string CopilotDir { get; }

    public CopilotScopeGuard(TempDir tmp)
    {
        InitTestSupport.EnterEnvLock();

        CopilotDir = Path.Combine(tmp.Root, ".copilot");
        Directory.CreateDirectory(CopilotDir);

        _previousCopilotHome = Environment.GetEnvironmentVariable(CopilotHomeEnvVar);
        Environment.SetEnvironmentVariable(CopilotHomeEnvVar, CopilotDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CopilotHomeEnvVar, _previousCopilotHome);
        InitTestSupport.ExitEnvLock();
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
