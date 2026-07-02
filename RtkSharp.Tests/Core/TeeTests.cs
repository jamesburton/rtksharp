using System;
using System.IO;
using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Core;

public class TeeTests : IDisposable
{
    private readonly string _tempDir;

    public TeeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rtk_tee_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore clean up failure in test
        }
    }

    [Fact]
    public void SanitizeSlug_MatchesExpectedOutputs()
    {
        Assert.Equal("cargo_test", Tee.SanitizeSlug("cargo_test"));
        Assert.Equal("cargo_test", Tee.SanitizeSlug("cargo test"));
        Assert.Equal("cargo-test", Tee.SanitizeSlug("cargo-test"));
        Assert.Equal("go_test___pkg", Tee.SanitizeSlug("go/test/./pkg"));
        
        var longSlug = new string('a', 50);
        Assert.Equal(40, Tee.SanitizeSlug(longSlug).Length);
    }

    [Fact]
    public void ShouldTee_ReturnsCorrectTeeDirBasedOnConfig()
    {
        var config = new TeeConfig { Enabled = false };
        Assert.Null(Tee.ShouldTee(config, 1000, 1, _tempDir));

        config = new TeeConfig { Mode = TeeMode.Never };
        Assert.Null(Tee.ShouldTee(config, 1000, 1, _tempDir));

        config = new TeeConfig { Mode = TeeMode.Failures };
        // Small size, expect null
        Assert.Null(Tee.ShouldTee(config, 100, 1, _tempDir));
        // Success exit code, expect null
        Assert.Null(Tee.ShouldTee(config, 1000, 0, _tempDir));
        // Fail exit code, large size, expect dir
        Assert.Equal(_tempDir, Tee.ShouldTee(config, 1000, 1, _tempDir));

        config = new TeeConfig { Mode = TeeMode.Always };
        // Success exit code, large size, expect dir
        Assert.Equal(_tempDir, Tee.ShouldTee(config, 1000, 0, _tempDir));
    }

    [Fact]
    public void WriteTeeFile_CreatesFileAndTruncatesAndRotates()
    {
        var content = string.Join("\n", System.Linq.Enumerable.Repeat("error: test failed", 50));
        var path = Tee.WriteTeeFile(content, "cargo_test", _tempDir, 1048576, 20);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        var written = File.ReadAllText(path);
        Assert.Contains("error: test failed", written);
    }

    [Fact]
    public void WriteTeeFile_TruncatesLargeOutput()
    {
        var bigOutput = new string('x', 2000);
        var path = Tee.WriteTeeFile(bigOutput, "test", _tempDir, 1000, 20);

        Assert.NotNull(path);
        var content = File.ReadAllText(path);
        Assert.Contains("--- truncated at 1000 bytes ---", content);
        Assert.True(content.Length < 2000);
    }

    [Fact]
    public void WriteTeeFile_RotatesOldFiles()
    {
        // Create 25 files
        for (int i = 0; i < 25; i++)
        {
            // Format to ensure chronological sorting by filename
            string filename = $"{1000000000 + i}_test.log";
            File.WriteAllText(Path.Combine(_tempDir, filename), "content");
        }

        // Trigger write which does rotation
        var path = Tee.WriteTeeFile("new content", "test", _tempDir, 1000, 20);
        Assert.NotNull(path);

        var remaining = Directory.GetFiles(_tempDir, "*.log");
        // Expect 20 files remaining (the rotation target is 20)
        Assert.Equal(20, remaining.Length);
    }
}
