using System;
using System.IO;
using System.Text;
using RtkSharp.Commands.Cloud;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="CurlCommand"/>'s pure logic (<see cref="CurlCommand.FilterCurlOutput"/>,
/// <see cref="CurlCommand.IsBinary"/>), a faithful, test-for-test port of Rust
/// <c>src/cmds/cloud/curl_cmd.rs</c>'s own <c>#[cfg(test)] mod tests</c> — which, like this file,
/// never invokes the real <c>curl</c> binary or the network; only <c>filter_curl_output</c>/
/// <c>is_binary</c> are unit-tested upstream. <see cref="RunCoreAsync_*"/> tests instead drive the
/// full process-spawning pipeline safely via <c>curl</c>'s own <c>file://</c> scheme against local
/// fixture files — zero network calls, fully deterministic.
/// </summary>
public sealed class CurlCommandTests
{
    // ===================== FilterCurlOutput (curl_cmd.rs's own mod tests) =====================

    [Fact]
    public void FilterCurlOutput_SmallJson_PassthroughNoTeeHint()
    {
        const string output = """{"r2Ready":true,"status":"ok"}""";

        var result = CurlCommand.FilterCurlOutput(output, isTty: true);

        Assert.Equal(output, result.Content);
        Assert.Null(result.TeeHint);
    }

    [Fact]
    public void FilterCurlOutput_NonJson_Passthrough()
    {
        const string output = "Hello, World!\nThis is plain text.";

        var result = CurlCommand.FilterCurlOutput(output, isTty: true);

        Assert.Equal(output, result.Content);
    }

    [Fact]
    public void FilterCurlOutput_LongOutput_TruncatedWithTeeHint()
    {
        using var tee = new TeeDirGuard();
        var longOutput = new string('x', 1000);

        var result = CurlCommand.FilterCurlOutput(longOutput, isTty: true);

        Assert.StartsWith("x", result.Content, StringComparison.Ordinal);
        Assert.Contains("bytes total", result.Content, StringComparison.Ordinal);
        Assert.Contains("1000", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length < 600);
        Assert.NotNull(result.TeeHint);
    }

    [Fact]
    public void FilterCurlOutput_MultibyteBoundary_TruncatesWithoutSplittingCharacter()
    {
        using var tee = new TeeDirGuard();
        var content = new string('a', 499) + "é";

        var result = CurlCommand.FilterCurlOutput(content, isTty: true);

        Assert.Contains("bytes total", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length < 600);
        // The truncated prefix must be valid UTF-16 text (no lone surrogate / mangled character) —
        // if the truncation cut a multi-byte UTF-8 sequence in half, decoding it back would either
        // throw or produce U+FFFD, neither of which happens here.
        Assert.DoesNotContain('�', result.Content);
    }

    [Fact]
    public void FilterCurlOutput_Exactly500Bytes_Truncated()
    {
        using var tee = new TeeDirGuard();
        var content = new string('a', 500);

        var result = CurlCommand.FilterCurlOutput(content, isTty: true);

        Assert.Contains("bytes total", result.Content, StringComparison.Ordinal);
    }

    // --- #1536: large JSON must remain parseable for downstream tools ---

    [Fact]
    public void FilterCurlOutput_LargeJsonObject_Passthrough()
    {
        var payload = new string('x', 600);
        var json = $$"""{"data":"{{payload}}"}""";

        var result = CurlCommand.FilterCurlOutput(json, isTty: true);

        Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
        Assert.StartsWith("{", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("}", result.Content, StringComparison.Ordinal);
        Assert.Null(result.TeeHint);
    }

    [Fact]
    public void FilterCurlOutput_LargeJsonArray_Passthrough()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 50; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($$"""{"id":{{i}},"name":"item-{{i:D4}}"}""");
        }

        var json = $"[{sb}]";
        Assert.True(Encoding.UTF8.GetByteCount(json) >= 500, "fixture must exceed cap");

        var result = CurlCommand.FilterCurlOutput(json, isTty: true);

        Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
        Assert.StartsWith("[", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("]", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCurlOutput_LargeJsonBareString_Passthrough()
    {
        var token = new string('z', 800);
        var json = $"\"{token}\"";

        var result = CurlCommand.FilterCurlOutput(json, isTty: true);

        Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
        Assert.StartsWith("\"", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("\"", result.Content, StringComparison.Ordinal);
    }

    // --- #1282: pipes / redirects (non-TTY) must receive full body ---

    [Fact]
    public void FilterCurlOutput_PipeNonJson_NoTruncation()
    {
        var longOutput = new string('x', 1000);

        var result = CurlCommand.FilterCurlOutput(longOutput, isTty: false);

        Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
        Assert.Equal(1000, result.Content.Length);
        Assert.Null(result.TeeHint);
    }

    [Fact]
    public void FilterCurlOutput_PipeJson_NoTruncation()
    {
        var payload = new string('y', 600);
        var json = $$"""{"data":"{{payload}}"}""";

        var result = CurlCommand.FilterCurlOutput(json, isTty: false);

        Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("}", result.Content, StringComparison.Ordinal);
        Assert.Null(result.TeeHint);
    }

    // --- Tee disabled: falls back to passthrough rather than an unrecoverable truncation marker ---

    [Fact]
    public void FilterCurlOutput_TeeDisabled_LongBody_PassesThroughInstead()
    {
        var previous = Environment.GetEnvironmentVariable("RTK_TEE");
        try
        {
            Environment.SetEnvironmentVariable("RTK_TEE", "0");
            var longOutput = new string('x', 1000);

            var result = CurlCommand.FilterCurlOutput(longOutput, isTty: true);

            Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
            Assert.Null(result.TeeHint);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_TEE", previous);
        }
    }

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

    private sealed class TeeDirGuard : IDisposable
    {
        private readonly string? _previous;

        public string TeeDir { get; } = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "rtk-curl-tee-test-" + Guid.NewGuid().ToString("N"));

        public TeeDirGuard()
        {
            _previous = Environment.GetEnvironmentVariable("RTK_TEE_DIR");
            Environment.SetEnvironmentVariable("RTK_TEE_DIR", TeeDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("RTK_TEE_DIR", _previous);
            try
            {
                Directory.Delete(TeeDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
