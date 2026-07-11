using System;
using RtkSharp.Filters.Commands.Cloud;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="DockerFilters"/>'s already-pure surface (<c>CompactPorts</c>,
/// <c>FormatComposePs</c>, <c>FormatComposeBuild</c>, <c>FormatContainerLineFromParts</c>), a
/// faithful, test-for-test port of the docker-specific portions of Rust
/// <c>src/cmds/cloud/container.rs</c>'s own <c>#[cfg(test)] mod tests</c>. <c>FormatComposeLogs</c>
/// coverage stays in <c>RtkSharp.Tests.Commands.Cloud.DockerCommandTests</c> — that method did NOT
/// move (see <c>DockerFilters</c>'s class remarks for why). <c>RunCoreAsync</c>-level dispatch
/// coverage (including <c>FormatPsSummary</c>/<c>FormatImagesSummary</c>, exercised only
/// end-to-end pre-extraction) also stays there.
/// </summary>
public sealed class DockerFiltersTests
{
    // ===================== CompactPorts (container.rs's own mod tests) =====================

    [Fact]
    public void CompactPorts_Empty_ReturnsDash()
    {
        Assert.Equal("-", DockerFilters.CompactPorts(""));
    }

    [Fact]
    public void CompactPorts_Single_ContainsPortNumber()
    {
        Assert.Contains("8080", DockerFilters.CompactPorts("0.0.0.0:8080->80/tcp"), StringComparison.Ordinal);
    }

    [Fact]
    public void CompactPorts_MoreThanThree_Truncates()
    {
        var result = DockerFilters.CompactPorts(
            "0.0.0.0:80->80/tcp, 0.0.0.0:443->443/tcp, 0.0.0.0:8080->8080/tcp, 0.0.0.0:9090->9090/tcp");

        Assert.Contains("…", result, StringComparison.Ordinal);
    }

    // ===================== FormatComposePs (container.rs's own mod tests) =====================

    [Fact]
    public void FormatComposePs_Basic_ShowsCountAndServiceNamesAndStatus()
    {
        const string raw =
            "web-1\tnginx:latest\tUp 2 hours\t0.0.0.0:80->80/tcp\n" +
            "api-1\tnode:20\tUp 2 hours\t0.0.0.0:3000->3000/tcp\n" +
            "db-1\tpostgres:16\tUp 2 hours\t0.0.0.0:5432->5432/tcp";

        var result = DockerFilters.FormatComposePs(raw);

        Assert.Contains("3", result, StringComparison.Ordinal);
        Assert.Contains("web", result, StringComparison.Ordinal);
        Assert.Contains("api", result, StringComparison.Ordinal);
        Assert.Contains("db", result, StringComparison.Ordinal);
        Assert.Contains("Up 2 hours", result, StringComparison.Ordinal);
        Assert.True(result.Length < raw.Length, "output should be shorter than raw");
    }

    [Fact]
    public void FormatComposePs_Empty_ShowsZero()
    {
        Assert.Contains("0", DockerFilters.FormatComposePs(""), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposePs_WhitespaceOnly_ShowsZero()
    {
        Assert.Contains("0", DockerFilters.FormatComposePs("   \n  \n"), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposePs_ExitedService_ShowsNameAndStatus()
    {
        const string raw = "worker-1\tpython:3.12\tExited (1) 2 minutes ago\t";

        var result = DockerFilters.FormatComposePs(raw);

        Assert.Contains("worker", result, StringComparison.Ordinal);
        Assert.Contains("Exited", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposePs_NoPorts_OmitsPortBrackets()
    {
        const string raw = "redis-1\tredis:7\tUp 5 hours\t";

        var result = DockerFilters.FormatComposePs(raw);
        var redisLine = Array.Find(result.Split('\n'), l => l.Contains("redis", StringComparison.Ordinal));

        Assert.NotNull(redisLine);
        Assert.DoesNotContain("] [", redisLine, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposePs_LongImagePath_ShortenedToLastSegment()
    {
        const string raw = "app-1\tghcr.io/myorg/myapp:latest\tUp 1 hour\t0.0.0.0:8080->8080/tcp";

        var result = DockerFilters.FormatComposePs(raw);

        Assert.Contains("myapp:latest", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ghcr.io", result, StringComparison.Ordinal);
    }

    // ===================== FormatComposeBuild (container.rs's own mod tests) =====================

    [Fact]
    public void FormatComposeBuild_Basic_ShowsBuildTimeAndServiceName()
    {
        const string raw =
            "[+] Building 12.3s (8/8) FINISHED\n" +
            " => [web internal] load build definition from Dockerfile           0.0s\n" +
            " => [web internal] load metadata for docker.io/library/node:20     1.2s\n" +
            " => [web 1/4] FROM docker.io/library/node:20@sha256:abc123         0.0s\n" +
            " => [web 2/4] WORKDIR /app                                         0.1s\n" +
            " => [web 3/4] COPY package*.json ./                                0.1s\n" +
            " => [web 4/4] RUN npm install                                      8.5s\n" +
            " => [web] exporting to image                                       2.3s\n" +
            " => => naming to docker.io/library/myapp-web                       0.0s";

        var result = DockerFilters.FormatComposeBuild(raw);

        Assert.Contains("12.3s", result, StringComparison.Ordinal);
        Assert.Contains("web", result, StringComparison.Ordinal);
        Assert.True(result.Length < raw.Length, "should be shorter than raw");
    }

    [Fact]
    public void FormatComposeBuild_Empty_ProducesNonEmptyOutput()
    {
        Assert.False(string.IsNullOrEmpty(DockerFilters.FormatComposeBuild("")));
    }

    // ===================== FormatContainerLineFromParts (no Rust fixture — new coverage) =====================

    [Fact]
    public void FormatContainerLineFromParts_FewerThanFourParts_ReturnsNull()
    {
        Assert.Null(DockerFilters.FormatContainerLineFromParts(["a", "b", "c"], withPorts: true));
    }

    [Fact]
    public void FormatContainerLineFromParts_WithPorts_IncludesBracketedPorts()
    {
        var result = DockerFilters.FormatContainerLineFromParts(
            ["abcdef0123456789", "my-container", "Up 2 hours", "myrepo/myimage:latest", "0.0.0.0:8080->80/tcp"],
            withPorts: true);

        Assert.NotNull(result);
        Assert.StartsWith("  abcdef012345 my-container (myimage:latest) Up 2 hours [8080]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatContainerLineFromParts_NoPortsRequested_OmitsBrackets()
    {
        var result = DockerFilters.FormatContainerLineFromParts(
            ["abc", "name", "Up", "image"], withPorts: false);

        Assert.NotNull(result);
        Assert.DoesNotContain("[", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatContainerLineFromParts_IdLongerThanTwelveChars_Truncated()
    {
        var result = DockerFilters.FormatContainerLineFromParts(
            ["0123456789abcdefextra", "name", "Up", "image"], withPorts: false);

        Assert.NotNull(result);
        Assert.Contains("0123456789ab", result, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", result, StringComparison.Ordinal);
    }

    // ===================== FormatPsSummary / FormatImagesSummary (new pure-boundary coverage, =====================
    // ===================== carved out of DockerCommand's previously-fused DockerPsAsync/RunImagesAsync) =====

    [Fact]
    public void FormatPsSummary_EmptyStdout_ReturnsZeroContainersWithNoTrailingNewline()
    {
        var result = DockerFilters.FormatPsSummary("");

        Assert.Equal("[docker] 0 containers", result);
    }

    [Fact]
    public void FormatPsSummary_WithContainers_FormatsHeaderAndBracketedPorts()
    {
        const string formatted = "abcdef012345\tweb\tUp 2 hours\tmyrepo/nginx:latest\t0.0.0.0:80->80/tcp\n";

        var result = DockerFilters.FormatPsSummary(formatted);

        Assert.StartsWith("[docker] 1 containers:\n", result, StringComparison.Ordinal);
        Assert.Contains("web (nginx:latest) Up 2 hours [80]", result, StringComparison.Ordinal);
        Assert.EndsWith("\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatImagesSummary_EmptyStdout_ReturnsZeroImagesWithNoTrailingNewline()
    {
        var result = DockerFilters.FormatImagesSummary("");

        Assert.Equal("[docker] 0 images", result);
    }

    [Fact]
    public void FormatImagesSummary_MixedGbAndMbSizes_SumsIntoGigabytes()
    {
        const string formatted = "myapp:latest\t1.5GB\nother:tag\t500MB\n";

        var result = DockerFilters.FormatImagesSummary(formatted);

        // 1.5GB = 1536MB + 500MB = 2036MB -> shown in GB since > 1024MB total.
        Assert.StartsWith("[docker] 2 images (2.0GB)\n", result, StringComparison.Ordinal);
        Assert.Contains("myapp:latest [1.5GB]", result, StringComparison.Ordinal);
        Assert.EndsWith("\n", result, StringComparison.Ordinal);
    }
}
