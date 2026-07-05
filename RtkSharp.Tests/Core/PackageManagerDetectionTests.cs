using System.IO;
using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Core;

/// <summary>
/// Tests for <see cref="PackageManagerDetection"/>. Ports the spirit of Rust's
/// <c>test_detect_package_manager_default</c> (<c>src/core/utils.rs:526-531</c>, which only asserts
/// the result is one of the three known names since the Rust test runs against whatever lockfiles
/// happen to be in the repo) with deterministic, directory-scoped coverage of each of the three
/// lockfile branches, plus each <see cref="PackageManagerDetection.PackageManagerExec(string)"/>
/// fallback branch (direct resolution; pnpm/yarn/npm exec) per the task brief.
/// </summary>
public sealed class PackageManagerDetectionTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rtksharp-pm-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void DetectPackageManager_PnpmLockPresent_ReturnsPnpm()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "pnpm-lock.yaml"), "");
            Assert.Equal("pnpm", PackageManagerDetection.DetectPackageManager(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectPackageManager_YarnLockPresent_ReturnsYarn()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "yarn.lock"), "");
            Assert.Equal("yarn", PackageManagerDetection.DetectPackageManager(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectPackageManager_BothLockfilesPresent_PnpmTakesPrecedence()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "pnpm-lock.yaml"), "");
            File.WriteAllText(Path.Combine(dir, "yarn.lock"), "");
            Assert.Equal("pnpm", PackageManagerDetection.DetectPackageManager(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectPackageManager_NoLockfile_DefaultsToNpm()
    {
        var dir = CreateTempDir();
        try
        {
            Assert.Equal("npm", PackageManagerDetection.DetectPackageManager(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectPackageManager_DefaultOverload_ReturnsOneOfTheThreeKnownNames()
    {
        // Matches Rust's own loose assertion in the repo's real working directory.
        var pm = PackageManagerDetection.DetectPackageManager();
        Assert.Contains(pm, new[] { "pnpm", "yarn", "npm" });
    }

    [Fact]
    public void PackageManagerExec_ToolResolvesDirectly_UsesToolWithNoBaseArguments()
    {
        var cmd = PackageManagerDetection.PackageManagerExec("vitest", toolExists: static _ => true, detectPackageManager: static () => throw new InvalidOperationException("should not be called"));

        Assert.Equal("vitest", cmd.FileName);
        Assert.Empty(cmd.BaseArguments);
    }

    [Fact]
    public void PackageManagerExec_ToolMissing_PnpmDetected_FallsBackToPnpmExec()
    {
        var cmd = PackageManagerDetection.PackageManagerExec("vitest", toolExists: static _ => false, detectPackageManager: static () => "pnpm");

        Assert.Equal("pnpm", cmd.FileName);
        Assert.Equal(["exec", "--", "vitest"], cmd.BaseArguments);
    }

    [Fact]
    public void PackageManagerExec_ToolMissing_YarnDetected_FallsBackToYarnExec()
    {
        var cmd = PackageManagerDetection.PackageManagerExec("vitest", toolExists: static _ => false, detectPackageManager: static () => "yarn");

        Assert.Equal("yarn", cmd.FileName);
        Assert.Equal(["exec", "--", "vitest"], cmd.BaseArguments);
    }

    [Fact]
    public void PackageManagerExec_ToolMissing_NpmDetected_FallsBackToNpxNoInstall()
    {
        var cmd = PackageManagerDetection.PackageManagerExec("vitest", toolExists: static _ => false, detectPackageManager: static () => "npm");

        Assert.Equal("npx", cmd.FileName);
        Assert.Equal(["--no-install", "--", "vitest"], cmd.BaseArguments);
    }

    [Fact]
    public void PackageManagerExec_PublicOverload_DoesNotThrow()
    {
        // Exercises the real PATH-resolution + directory-lockfile-detection wiring end to end
        // without asserting a specific outcome (host-dependent).
        var cmd = PackageManagerDetection.PackageManagerExec("some_definitely_nonexistent_tool_xyz");
        Assert.False(string.IsNullOrEmpty(cmd.FileName));
    }
}
