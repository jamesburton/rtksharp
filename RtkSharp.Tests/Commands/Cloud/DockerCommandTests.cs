using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.Cloud;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="DockerCommand"/>'s remaining impure dispatch surface: new coverage for
/// <c>ps</c>/<c>ps -a</c>/<c>images</c>/<c>logs</c> — code paths the Rust oracle itself never exposed
/// as pure, independently testable functions (they inline formatting directly inside the
/// process-invoking function). Pure formatting logic (<c>compact_ports</c>,
/// <c>format_compose_ps/logs/build</c>) moved to
/// <c>RtkSharp.Filters.Tests.Commands.Cloud.DockerFiltersTests</c>.
/// </summary>
public sealed class DockerCommandTests
{
    // ===================== RunCoreAsync: dispatch via a recording/responding fake executor =====================

    [Fact]
    public async Task RunCoreAsync_NoArgs_ThrowsCommandArgumentParseException()
    {
        // `docker` is Rust-classified PASSTHROUGH, not RTK_META_COMMANDS — confirmed against
        // src/main.rs's test_every_subcommand_is_classified and the real oracle, which for `rtk
        // docker` (no subcommand) falls back to running the REAL docker binary (prints ITS OWN
        // usage, exits 0 if docker.exe is present) rather than a clean clap-style exit 2. This
        // now throws so RtkProgram's dispatch layer can re-route there instead of RunCoreAsync
        // printing its own message.
        var executor = new RecordingExecutor(_ => Ok(""));
        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => DockerCommand.RunCoreAsync([], verbose: 0, executor));
        Assert.Contains("requires a subcommand", ex.Message, StringComparison.Ordinal);
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
    public async Task RunCoreAsync_Logs_NoArgsAtAll_ThrowsCommandArgumentParseException()
    {
        // Rust's `container: String` positional is REQUIRED at the clap layer — zero args never
        // reaches docker_logs at all. `docker` is Rust-classified PASSTHROUGH, not
        // RTK_META_COMMANDS, so the real oracle falls back to running the REAL docker binary
        // with the original argv (its own "'docker logs' requires 1 argument" message, exit 1),
        // not a clean clap-style exit 2 — verified directly against target/release/rtk.exe.
        var executor = new RecordingExecutor(_ => Ok(""));

        var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => DockerCommand.RunCoreAsync(["logs"], verbose: 0, executor));
        Assert.Contains("required arguments", ex.Message, StringComparison.Ordinal);
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
