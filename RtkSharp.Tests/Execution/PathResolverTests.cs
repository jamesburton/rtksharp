using System;
using System.IO;
using System.Runtime.InteropServices;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Execution;

public class PathResolverTests
{
    [Fact]
    public void Resolve_NonExistentBinary_ReturnsInput()
    {
        var resolved = PathResolver.Resolve("nonexistent_binary_xyz_99999");
        Assert.Equal("nonexistent_binary_xyz_99999", resolved);
    }

    [Fact]
    public void Resolve_KnownBinary_ReturnsAbsolutePath()
    {
        var target = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "sh";
        var resolved = PathResolver.Resolve(target);

        Assert.NotEqual(target, resolved);
        Assert.True(Path.IsPathRooted(resolved));
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void Resolve_RelativeFile_ReturnsAbsolutePath()
    {
        var tempDir = CreateTempDir();
        try
        {
            var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tool.cmd" : "tool";
            var path = Path.Combine(tempDir, fileName);
            File.WriteAllText(path, "");

            var resolved = PathResolver.Resolve(path);

            Assert.Equal(Path.GetFullPath(path), resolved, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Resolve_WithInjectedPath_FindsBinary()
    {
        var tempDir = CreateTempDir();
        try
        {
            var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rtk-test-tool.exe" : "rtk-test-tool";
            var path = Path.Combine(tempDir, fileName);
            File.WriteAllText(path, "");

            var resolved = PathResolver.Resolve(
                fileName,
                tempDir,
                ".EXE;.CMD",
                Directory.GetCurrentDirectory()
            );

            Assert.Equal(Path.GetFullPath(path), resolved, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [WindowsOnlyFact]
    public void Resolve_WindowsPathext_FindsCmdWrapper()
    {
        var tempDir = CreateTempDir();
        try
        {
            var path = Path.Combine(tempDir, "rtk-wrapper.cmd");
            File.WriteAllText(path, "@echo off");

            var resolved = PathResolver.Resolve(
                "rtk-wrapper",
                tempDir,
                ".EXE;.CMD",
                Directory.GetCurrentDirectory()
            );

            Assert.Equal(Path.GetFullPath(path), resolved, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rtk_path_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}

public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows-only path resolution behavior.";
        }
    }
}
