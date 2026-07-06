using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Cloud;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="DockerCommand"/>, a faithful, test-for-test port of the docker-specific portions
/// of Rust <c>src/cmds/cloud/container.rs</c>'s own <c>#[cfg(test)] mod tests</c> (compact_ports,
/// format_compose_ps/logs/build), plus new dispatch/formatting coverage for <c>ps</c>/<c>ps -a</c>/
/// <c>images</c>/<c>logs</c> — code paths the Rust oracle itself never exposed as pure, independently
/// testable functions (they inline formatting directly inside the process-invoking function).
/// </summary>
public sealed class DockerCommandTests
{
    // ===================== CompactPorts (container.rs's own mod tests) =====================

    [Fact]
    public void CompactPorts_Empty_ReturnsDash()
    {
        Assert.Equal("-", DockerCommand.CompactPorts(""));
    }

    [Fact]
    public void CompactPorts_Single_ContainsPortNumber()
    {
        Assert.Contains("8080", DockerCommand.CompactPorts("0.0.0.0:8080->80/tcp"), StringComparison.Ordinal);
    }

    [Fact]
    public void CompactPorts_MoreThanThree_Truncates()
    {
        var result = DockerCommand.CompactPorts(
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

        var result = DockerCommand.FormatComposePs(raw);

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
        Assert.Contains("0", DockerCommand.FormatComposePs(""), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposePs_WhitespaceOnly_ShowsZero()
    {
        Assert.Contains("0", DockerCommand.FormatComposePs("   \n  \n"), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposePs_ExitedService_ShowsNameAndStatus()
    {
        const string raw = "worker-1\tpython:3.12\tExited (1) 2 minutes ago\t";

        var result = DockerCommand.FormatComposePs(raw);

        Assert.Contains("worker", result, StringComparison.Ordinal);
        Assert.Contains("Exited", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposePs_NoPorts_OmitsPortBrackets()
    {
        const string raw = "redis-1\tredis:7\tUp 5 hours\t";

        var result = DockerCommand.FormatComposePs(raw);
        var redisLine = Array.Find(result.Split('\n'), l => l.Contains("redis", StringComparison.Ordinal));

        Assert.NotNull(redisLine);
        Assert.DoesNotContain("] [", redisLine, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposePs_LongImagePath_ShortenedToLastSegment()
    {
        const string raw = "app-1\tghcr.io/myorg/myapp:latest\tUp 1 hour\t0.0.0.0:8080->8080/tcp";

        var result = DockerCommand.FormatComposePs(raw);

        Assert.Contains("myapp:latest", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ghcr.io", result, StringComparison.Ordinal);
    }

    // ===================== FormatComposeLogs (container.rs's own mod tests) =====================

    [Fact]
    public void FormatComposeLogs_Basic_HasHeader()
    {
        const string raw =
            "web-1  | 192.168.1.1 - GET / 200\n" +
            "web-1  | 192.168.1.1 - GET /favicon.ico 404\n" +
            "api-1  | Server listening on port 3000\n" +
            "api-1  | Connected to database";

        Assert.Contains("Logs", DockerCommand.FormatComposeLogs(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatComposeLogs_Empty_IndicatesNoLogs()
    {
        Assert.Contains("No logs", DockerCommand.FormatComposeLogs(""), StringComparison.Ordinal);
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

        var result = DockerCommand.FormatComposeBuild(raw);

        Assert.Contains("12.3s", result, StringComparison.Ordinal);
        Assert.Contains("web", result, StringComparison.Ordinal);
        Assert.True(result.Length < raw.Length, "should be shorter than raw");
    }

    [Fact]
    public void FormatComposeBuild_Empty_ProducesNonEmptyOutput()
    {
        Assert.False(string.IsNullOrEmpty(DockerCommand.FormatComposeBuild("")));
    }

    // ===================== FormatContainerLineFromParts (no Rust fixture — new coverage) =====================

    [Fact]
    public void FormatContainerLineFromParts_FewerThanFourParts_ReturnsNull()
    {
        Assert.Null(DockerCommand.FormatContainerLineFromParts(["a", "b", "c"], withPorts: true));
    }

    [Fact]
    public void FormatContainerLineFromParts_WithPorts_IncludesBracketedPorts()
    {
        var result = DockerCommand.FormatContainerLineFromParts(
            ["abcdef0123456789", "my-container", "Up 2 hours", "myrepo/myimage:latest", "0.0.0.0:8080->80/tcp"],
            withPorts: true);

        Assert.NotNull(result);
        Assert.StartsWith("  abcdef012345 my-container (myimage:latest) Up 2 hours [8080]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatContainerLineFromParts_NoPortsRequested_OmitsBrackets()
    {
        var result = DockerCommand.FormatContainerLineFromParts(
            ["abc", "name", "Up", "image"], withPorts: false);

        Assert.NotNull(result);
        Assert.DoesNotContain("[", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatContainerLineFromParts_IdLongerThanTwelveChars_Truncated()
    {
        var result = DockerCommand.FormatContainerLineFromParts(
            ["0123456789abcdefextra", "name", "Up", "image"], withPorts: false);

        Assert.NotNull(result);
        Assert.Contains("0123456789ab", result, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", result, StringComparison.Ordinal);
    }

    // ===================== RunCoreAsync: dispatch via a recording/responding fake executor =====================

    [Fact]
    public async Task RunCoreAsync_NoArgs_PrintsUsageErrorAndReturnsTwo()
    {
        var executor = new RecordingExecutor(_ => Ok(""));
        var stderr = await CaptureStderrAsync(() => DockerCommand.RunCoreAsync([], verbose: 0, executor));

        Assert.Contains("requires a subcommand", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_Ps_NoContainers_PrintsZeroContainers()
    {
        var executor = new RecordingExecutor(req => req.Arguments.Contains("--format") ? Ok("") : Ok("raw ps output"));

        var stdout = await CaptureStdoutAsync(() => DockerCommand.RunCoreAsync(["ps"], verbose: 0, executor));

        Assert.Equal("[docker] 0 containers\n", stdout);
        // Confirms the double-invocation: one plain "ps" call, one "--format"-flagged call.
        Assert.Equal(2, executor.Requests.Count);
    }

    [Fact]
    public async Task RunCoreAsync_Ps_WithContainers_GroupsAndFormats()
    {
        const string formatted = "abcdef012345\tweb\tUp 2 hours\tmyrepo/nginx:latest\t0.0.0.0:80->80/tcp\n";
        var executor = new RecordingExecutor(req => req.Arguments.Contains("--format") ? Ok(formatted) : Ok(""));

        var stdout = await CaptureStdoutAsync(() => DockerCommand.RunCoreAsync(["ps"], verbose: 0, executor));

        Assert.Contains("[docker] 1 containers:", stdout, StringComparison.Ordinal);
        Assert.Contains("web (nginx:latest) Up 2 hours [80]", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_PsAll_SeparatesRunningFromStopped()
    {
        const string formatted =
            "running\tabc123456789\tweb\tUp 2 hours\tnginx:latest\t\n" +
            "exited\tdef123456789\tworker\tExited (0)\tpython:3.12\t\n";
        var executor = new RecordingExecutor(req => req.Arguments.Contains("--format") ? Ok(formatted) : Ok(""));

        var stdout = await CaptureStdoutAsync(() => DockerCommand.RunCoreAsync(["ps", "-a"], verbose: 0, executor));

        Assert.Contains("[docker] 1 running:", stdout, StringComparison.Ordinal);
        Assert.Contains("[docker] 1 stopped/exited:", stdout, StringComparison.Ordinal);
        Assert.Contains("web", stdout, StringComparison.Ordinal);
        Assert.Contains("worker", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_PsFailure_PrintsStderrAndPropagatesExitCode()
    {
        var executor = new RecordingExecutor(_ => new ExecutionResult(
            "", "Cannot connect to the Docker daemon", ExitCode: 1, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false));

        var (stderr, exit) = await CaptureStderrAndExitAsync(() => DockerCommand.RunCoreAsync(["ps"], verbose: 0, executor));

        Assert.Equal(1, exit);
        Assert.Contains("Cannot connect to the Docker daemon", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_Images_NoImages_PrintsZeroImages()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var stdout = await CaptureStdoutAsync(() => DockerCommand.RunCoreAsync(["images"], verbose: 0, executor));

        Assert.Equal("[docker] 0 images\n", stdout);
    }

    [Fact]
    public async Task RunCoreAsync_Images_SumsSizesAcrossGbAndMb()
    {
        const string formatted = "myapp:latest\t1.5GB\nother:tag\t500MB\n";
        var executor = new RecordingExecutor(req => req.Arguments.Contains("--format") ? Ok(formatted) : Ok(""));

        var stdout = await CaptureStdoutAsync(() => DockerCommand.RunCoreAsync(["images"], verbose: 0, executor));

        // 1.5GB = 1536MB + 500MB = 2036MB -> shown in GB since > 1024MB total.
        Assert.Contains("[docker] 2 images (2.0GB)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_NoArgsAtAll_UsageErrorExitTwo()
    {
        // Rust's `container: String` positional is REQUIRED at the clap layer — zero args never
        // reaches docker_logs at all; it's a clap usage error, exit 2.
        var executor = new RecordingExecutor(_ => Ok(""));

        var (stderr, exit) = await CaptureStderrAndExitAsync(() => DockerCommand.RunCoreAsync(["logs"], verbose: 0, executor));

        Assert.Equal(2, exit);
        Assert.Contains("required arguments", stderr, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_ExplicitEmptyStringContainer_PrintsModuleUsageMessage()
    {
        // The module's own container.is_empty() check (container.rs:309) IS reachable via an
        // explicit empty-string argument, which clap itself does not reject.
        var executor = new RecordingExecutor(_ => Ok(""));

        var stdout = await CaptureStdoutAsync(() => DockerCommand.RunCoreAsync(["logs", ""], verbose: 0, executor));

        Assert.Equal("Usage: rtk docker logs <container>\n", stdout);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task RunCoreAsync_Logs_AnalyzesViaLogCommand()
    {
        var executor = new RecordingExecutor(_ => Ok("2024-01-01 10:00:00 ERROR: boom\n"));

        var stdout = await CaptureStdoutAsync(() => DockerCommand.RunCoreAsync(["logs", "my-container"], verbose: 0, executor));

        Assert.Contains("[docker] Logs for my-container:", stdout, StringComparison.Ordinal);
        Assert.Contains("ERRORS", stdout, StringComparison.Ordinal);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["logs", "--tail", "100", "my-container"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_ComposePs_DispatchesAndFormats()
    {
        const string formatted = "web-1\tnginx:latest\tUp\t0.0.0.0:80->80/tcp\n";
        var executor = new RecordingExecutor(req => req.Arguments.Contains("--format") ? Ok(formatted) : Ok(""));

        var stdout = await CaptureStdoutAsync(() => DockerCommand.RunCoreAsync(["compose", "ps"], verbose: 0, executor));

        Assert.Contains("[compose] 1 services:", stdout, StringComparison.Ordinal);
        Assert.Equal(2, executor.Requests.Count);
    }

    [Fact]
    public async Task RunCoreAsync_ComposeBuild_WithServiceArg_ForwardsServiceName()
    {
        var executor = new RecordingExecutor(_ => Ok("[+] Building 1.0s (1/1) FINISHED\n"));

        await DockerCommand.RunCoreAsync(["compose", "build", "web"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["compose", "build", "web"], request.Arguments);
    }

    [Fact]
    public async Task RunCoreAsync_UnrecognizedSubcommand_UsesInheritedPassthrough()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        var exit = await DockerCommand.RunCoreAsync(["build", "-t", "myimage", "."], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["build", "-t", "myimage", "."], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RunCoreAsync_ComposeUnrecognizedSubcommand_PassthroughIncludesComposePrefix()
    {
        var executor = new RecordingExecutor(_ => Ok(""));

        await DockerCommand.RunCoreAsync(["compose", "down"], verbose: 0, executor);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(["compose", "down"], request.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    private static ExecutionResult Ok(string stdout) =>
        new(stdout, "", ExitCode: 0, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

    private static async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var previous = Console.Out;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetOut(writer);
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private static async Task<string> CaptureStderrAsync(Func<Task<int>> action)
    {
        var previous = Console.Error;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetError(writer);
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    private static async Task<(string Stderr, int Exit)> CaptureStderrAndExitAsync(Func<Task<int>> action)
    {
        var previous = Console.Error;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetError(writer);
        try
        {
            var exit = await action();
            return (writer.ToString(), exit);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    private sealed class RecordingExecutor(Func<ExecutionRequest, ExecutionResult> responder) : IProcessExecutor
    {
        public List<ExecutionRequest> Requests { get; } = [];

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(responder(request));
        }
    }
}
