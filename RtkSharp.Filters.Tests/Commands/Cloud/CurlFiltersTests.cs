using System;
using System.IO;
using System.Text;
using RtkSharp.Core;
using RtkSharp.Filters.Commands.Cloud;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="CurlFilters.FilterCurlOutput"/>, a faithful, test-for-test port of Rust
/// <c>src/cmds/cloud/curl_cmd.rs</c>'s own <c>#[cfg(test)] mod tests</c> for <c>filter_curl_output</c>.
/// <see cref="CurlFilters.FilterCurlOutput"/>'s sibling <c>is_binary</c> coverage and
/// <c>RunCoreAsync</c>-level dispatch tests stay in
/// <c>RtkSharp.Tests.Commands.Cloud.CurlCommandTests</c> (CLI-level, not pure-filter).
/// </summary>
public sealed class CurlFiltersTests
{
    // ===================== FilterCurlOutput (curl_cmd.rs's own mod tests) =====================

    [Fact]
    public void FilterCurlOutput_SmallJson_PassthroughNoTeeHint()
    {
        const string output = """{"r2Ready":true,"status":"ok"}""";

        var result = CurlFilters.FilterCurlOutput(output, isTty: true);

        Assert.Equal(output, result.Content);
        Assert.Null(result.TeeHint);
    }

    [Fact]
    public void FilterCurlOutput_NonJson_Passthrough()
    {
        const string output = "Hello, World!\nThis is plain text.";

        var result = CurlFilters.FilterCurlOutput(output, isTty: true);

        Assert.Equal(output, result.Content);
    }

    [Fact]
    public void FilterCurlOutput_LongOutput_TruncatedWithTeeHint()
    {
        using var tee = new TeeDirGuard();
        var longOutput = new string('x', 1000);

        var result = CurlFilters.FilterCurlOutput(longOutput, isTty: true);

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

        var result = CurlFilters.FilterCurlOutput(content, isTty: true);

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

        var result = CurlFilters.FilterCurlOutput(content, isTty: true);

        Assert.Contains("bytes total", result.Content, StringComparison.Ordinal);
    }

    // --- #1536: large JSON must remain parseable for downstream tools ---

    [Fact]
    public void FilterCurlOutput_LargeJsonObject_Passthrough()
    {
        var payload = new string('x', 600);
        var json = $$"""{"data":"{{payload}}"}""";

        var result = CurlFilters.FilterCurlOutput(json, isTty: true);

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

        var result = CurlFilters.FilterCurlOutput(json, isTty: true);

        Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
        Assert.StartsWith("[", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("]", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCurlOutput_LargeJsonBareString_Passthrough()
    {
        var token = new string('z', 800);
        var json = $"\"{token}\"";

        var result = CurlFilters.FilterCurlOutput(json, isTty: true);

        Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
        Assert.StartsWith("\"", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("\"", result.Content, StringComparison.Ordinal);
    }

    // --- #1282: pipes / redirects (non-TTY) must receive full body ---

    [Fact]
    public void FilterCurlOutput_PipeNonJson_NoTruncation()
    {
        var longOutput = new string('x', 1000);

        var result = CurlFilters.FilterCurlOutput(longOutput, isTty: false);

        Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
        Assert.Equal(1000, result.Content.Length);
        Assert.Null(result.TeeHint);
    }

    [Fact]
    public void FilterCurlOutput_PipeJson_NoTruncation()
    {
        var payload = new string('y', 600);
        var json = $$"""{"data":"{{payload}}"}""";

        var result = CurlFilters.FilterCurlOutput(json, isTty: false);

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

            var result = CurlFilters.FilterCurlOutput(longOutput, isTty: true);

            Assert.DoesNotContain("bytes total", result.Content, StringComparison.Ordinal);
            Assert.Null(result.TeeHint);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_TEE", previous);
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
