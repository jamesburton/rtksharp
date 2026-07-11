using System;
using System.IO;
using System.Text;
using RtkSharp.Commands.Cloud;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="CurlCommand.IsBinary"/> and <see cref="CurlCommand.RunCoreAsync"/>'s CLI-level
/// dispatch, a faithful, test-for-test port of Rust <c>src/cmds/cloud/curl_cmd.rs</c>'s own
/// <c>#[cfg(test)] mod tests</c> for <c>is_binary</c>. <c>filter_curl_output</c>'s pure-filter
/// coverage moved to <c>RtkSharp.Filters.Tests.Commands.Cloud.CurlFiltersTests</c> — the
/// <c>RunCoreAsync_*</c> tests here instead drive the full process-spawning pipeline safely via
/// <c>curl</c>'s own <c>file://</c> scheme against local fixture files — zero network calls, fully
/// deterministic.
/// </summary>
public sealed class CurlCommandTests
{
    // ===================== IsBinary (curl_cmd.rs's own mod tests) =====================

    [Fact]
    public void IsBinary_GzipMagicBytes_IsBinary()
    {
        // gzip magic 1f 8b — 0x8b is an invalid UTF-8 continuation byte on its own.
        byte[] bytes = [0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03];

        Assert.True(CurlCommand.IsBinary(bytes));
    }

    [Fact]
    public void IsBinary_ValidUtf8Text_IsNotBinary()
    {
        Assert.False(CurlCommand.IsBinary(Encoding.UTF8.GetBytes("""{"key": "value"}""")));
        Assert.False(CurlCommand.IsBinary(Encoding.UTF8.GetBytes("<!DOCTYPE html>\n<html><body>Hi</body></html>")));
        Assert.False(CurlCommand.IsBinary(Encoding.UTF8.GetBytes("Plain ASCII text")));
        Assert.False(CurlCommand.IsBinary(Encoding.UTF8.GetBytes("Héllo wörld — emojis 🚀 ✓")));
    }

    [Fact]
    public void IsBinary_Empty_IsNotBinary()
    {
        Assert.False(CurlCommand.IsBinary(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsBinary_TextWithEmbeddedNul_IsNotBinary()
    {
        Assert.False(CurlCommand.IsBinary(Encoding.UTF8.GetBytes("text with\0embedded nul")));
    }

    // ===================== RunCoreAsync: full pipeline via curl's file:// scheme (no network) =====================

    [Fact]
    public async Task RunCoreAsync_SmallJsonFile_PrintsFullContentAndReturnsZero()
    {
        using var fixture = new TempFile("""{"hello":"world"}""");
        var stdout = await CaptureStdoutAsync(() => CurlCommand.RunCoreAsync([FileUrl(fixture.Path)], verbose: 0));

        Assert.Equal(0, stdout.ExitCode);
        Assert.Equal("{\"hello\":\"world\"}\n", stdout.Text);
    }

    [Fact]
    public async Task RunCoreAsync_BinaryFile_WritesRawBytesUnchanged()
    {
        byte[] binaryContent = [0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03];
        using var fixture = new TempFile(binaryContent);
        using var captured = new MemoryStream();

        var exitCode = await CurlCommand.RunCoreAsync([FileUrl(fixture.Path)], verbose: 0, captured);

        Assert.Equal(0, exitCode);
        Assert.Equal(binaryContent, captured.ToArray());
    }

    [Fact]
    public async Task RunCoreAsync_NonExistentFileUrl_PrintsFailedMessageAndReturnsNonZero()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "rtk-curl-test-missing-" + Guid.NewGuid().ToString("N") + ".json");
        var (exitCode, stderr) = await CaptureStderrAsync(() => CurlCommand.RunCoreAsync([FileUrl(missingPath)], verbose: 0));

        Assert.NotEqual(0, exitCode);
        Assert.StartsWith("FAILED: curl", stderr, StringComparison.Ordinal);
    }

    private static string FileUrl(string path) => "file:///" + path.Replace('\\', '/');

    private static async Task<(int ExitCode, string Text)> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var previous = Console.Out;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetOut(writer);
        try
        {
            var exit = await action();
            return (exit, writer.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private static async Task<(int ExitCode, string Stderr)> CaptureStderrAsync(Func<Task<int>> action)
    {
        var previous = Console.Error;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetError(writer);
        try
        {
            var exit = await action();
            return (exit, writer.ToString());
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "rtk-curl-test-" + Guid.NewGuid().ToString("N"));

        public TempFile(string content) => File.WriteAllText(Path, content);

        public TempFile(byte[] content) => File.WriteAllBytes(Path, content);

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

}
