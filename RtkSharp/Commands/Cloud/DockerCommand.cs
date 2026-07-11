using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Cloud;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk docker</c> CLI verb group (<c>ps</c>/<c>images</c>/<c>logs</c>/<c>compose</c>,
/// plus a generic passthrough fallback for any other subcommand). Faithful port of the
/// docker-specific portions of Rust <c>src/cmds/cloud/container.rs</c> — the module is shared with
/// <c>kubectl</c>/<c>oc</c> in Rust, ported separately as <see cref="KubectlCommand"/>/
/// <see cref="OcCommand"/> (see class remarks).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope: docker-specific logic only; kubectl/oc are separate sibling classes.</b> Rust's
/// <c>container.rs</c> also implements <c>kubectl</c>/<c>oc</c> (pods/services/logs via
/// <c>run_k8s_json</c>), which share almost no code with the docker-specific logic beyond the
/// execution/tee/truncation skeleton. This class covers only the <c>Docker</c>/<c>DockerCommands</c>/
/// <c>ComposeCommands</c> surface; <c>kubectl</c>/<c>oc</c> are ported as <see cref="KubectlCommand"/>/
/// <see cref="OcCommand"/>, sharing their own genuinely-common logic via <c>ContainerFilters.cs</c>
/// rather than being folded into this file.
/// </para>
/// <para>
/// <b><c>raw</c> is sequential concatenation, not true interleaved capture.</b> Rust's
/// <c>runner::run_filtered</c> (used by <c>docker logs</c>/<c>compose logs</c>/<c>compose build</c>)
/// builds its <c>raw</c> tracking/filter-input string as <c>raw_stdout + raw_stderr</c> — confirmed by
/// its own test comment ("raw = raw_stdout + raw_stderr", <c>stream.rs</c>:757) — not a byte-accurate
/// real-time interleave of the two streams. This port matches that exactly by using
/// <see cref="ExecutionCaptureMode.Separate"/> and concatenating <c>Stdout + Stderr</c> manually,
/// rather than reaching for <see cref="ExecutionCaptureMode.Merged"/> (which promises true
/// emission-order interleaving the oracle itself does not actually provide).
/// </para>
/// <para>
/// <b><c>docker ps</c>/<c>ps -a</c>/<c>images</c>/<c>compose ps</c> each spawn <c>docker</c> TWICE.</b>
/// Rust's <c>docker_ps</c>/<c>docker_ps_all</c>/<c>docker_images</c>/<c>run_compose_ps</c> each run a
/// plain invocation (used only to estimate input tokens for tracking — its stdout/stderr are never
/// printed) and a second, <c>--format</c>-flagged invocation (whose output is actually parsed and
/// shown). This port reproduces the double-invocation exactly rather than "optimizing" it away, since
/// the plain call is a real, if side-effect-free from the user's perspective, part of the oracle's
/// observable process-spawn behavior.
/// </para>
/// </remarks>
public static class DockerCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk docker</c> with the given arguments (the remainder after the
    /// <c>docker</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>docker</c>.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor());

    /// <summary>
    /// Runs <c>rtk docker</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>docker</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>docker</c> with.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        try
        {
            return await RunCoreAsync(args, verbose, executor).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // CommandArgumentParseException must propagate to RtkProgram's dispatch layer, which
            // re-routes it to the TOML-fallback/raw-passthrough path — it is not a generic
            // runtime failure, so it must not be swallowed by this safety net.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// The testable core of <see cref="RunAsync(string[],int,IProcessExecutor)"/>. Faithful port of
    /// the <c>Commands::Docker</c> dispatch arm (<c>main.rs</c>:1792-1820).
    /// </summary>
    /// <param name="args">The arguments following the <c>docker</c> verb.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>docker</c> with.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunCoreAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        if (args.Length == 0)
        {
            // Rust's `DockerCommands` is a REQUIRED clap subcommand enum (main.rs:1792) — `rtk
            // docker` with no subcommand fails at the clap layer before container.rs's body ever
            // runs. `docker` is Rust-classified PASSTHROUGH (not RTK_META_COMMANDS), so that clap
            // failure falls back to a raw PATH-exec attempt of "docker" (main.rs's run_fallback),
            // NOT a docker-specific usage message — this throw lets RtkProgram's dispatch layer
            // re-route there instead of printing this message directly.
            throw new CommandArgumentParseException("'rtk docker' requires a subcommand but one was not provided");
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "ps" => await RunPsAsync(rest, executor).ConfigureAwait(false),
            "images" => await RunImagesAsync(executor).ConfigureAwait(false),
            "logs" => await RunLogsAsync(rest, executor).ConfigureAwait(false),
            "compose" => await RunComposeAsync(rest, verbose, executor).ConfigureAwait(false),
            // Passthrough: runs any unsupported docker subcommand directly (main.rs's Other external
            // subcommand) — forwards the FULL original args verbatim, including the subcommand token.
            _ => await RunDockerPassthroughAsync(args, executor).ConfigureAwait(false),
        };
    }

    // ===================== docker ps / ps -a =====================

    private static Task<int> RunPsAsync(string[] rest, IProcessExecutor executor)
    {
        var all = rest.Contains("-a") || rest.Contains("--all");
        return all ? DockerPsAllAsync(executor) : DockerPsAsync(executor);
    }

    /// <summary>Faithful port of <c>docker_ps</c> (<c>container.rs</c>:57-109).</summary>
    private static async Task<int> DockerPsAsync(IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var raw = (await ExecAsync(executor, ["ps"]).ConfigureAwait(false)).Stdout;
        var result = await ExecAsync(executor, ["ps", "--format", "{{.ID}}\t{{.Names}}\t{{.Status}}\t{{.Image}}\t{{.Ports}}"]).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            Console.Error.Write(result.Stderr);
            timer.Track("docker ps", "rtk docker ps", raw, raw);
            return result.ExitCode;
        }

        var stdout = result.Stdout;
        var rtk = DockerFilters.FormatPsSummary(stdout);

        // FormatPsSummary returns "[docker] 0 containers" (no trailing newline) for the
        // empty/whitespace case and an always-newline-terminated summary otherwise — this mirrors
        // that same asymmetry in what gets PRINTED (both branches print a trailing newline) while
        // preserving the pre-extraction TRACKED-text asymmetry (the empty case tracks without one).
        Console.Out.Write(rtk.EndsWith('\n') ? rtk : rtk + "\n");
        timer.Track("docker ps", "rtk docker ps", raw, rtk);
        return 0;
    }

    /// <summary>Faithful port of <c>docker_ps_all</c> (<c>container.rs</c>:111-186).</summary>
    private static async Task<int> DockerPsAllAsync(IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var raw = (await ExecAsync(executor, ["ps", "-a"]).ConfigureAwait(false)).Stdout;
        var result = await ExecAsync(executor, ["ps", "-a", "--format", "{{.State}}\t{{.ID}}\t{{.Names}}\t{{.Status}}\t{{.Image}}\t{{.Ports}}"]).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            Console.Error.Write(result.Stderr);
            timer.Track("docker ps -a", "rtk docker ps -a", raw, raw);
            return result.ExitCode;
        }

        var runningLines = new List<string>();
        var stoppedLines = new List<string>();
        foreach (var line in ReadCommand.SplitLines(result.Stdout).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var parts = line.Split('\t');
            var state = parts.Length > 0 ? parts[0] : "";
            var isRunning = state is "running" or "restarting";
            if (DockerFilters.FormatContainerLineFromParts(parts.Skip(1).ToArray(), isRunning) is { } entry)
            {
                (isRunning ? runningLines : stoppedLines).Add(entry);
            }
        }

        const int maxContainers = 20;
        var truncated = runningLines.Count > maxContainers || stoppedLines.Count > maxContainers;

        var rtk = $"[docker] {runningLines.Count} running:\n";
        rtk += string.Concat(runningLines.Take(maxContainers));
        if (runningLines.Count > maxContainers)
        {
            rtk += $"  … +{runningLines.Count - maxContainers} more\n";
        }

        if (stoppedLines.Count > 0)
        {
            rtk += $"[docker] {stoppedLines.Count} stopped/exited:\n";
            rtk += string.Concat(stoppedLines.Take(maxContainers));
            if (stoppedLines.Count > maxContainers)
            {
                rtk += $"  … +{stoppedLines.Count - maxContainers} more\n";
            }
        }

        if (truncated)
        {
            var full = string.Concat(runningLines.Concat(stoppedLines));
            if (Tee.ForceTeeHint(full, "docker-ps-a") is { } hint)
            {
                rtk += hint + "\n";
            }
        }

        Console.Out.Write(rtk);
        timer.Track("docker ps -a", "rtk docker ps -a", raw, rtk);
        return 0;
    }

    // ===================== docker images =====================

    /// <summary>Faithful port of <c>docker_images</c> (<c>container.rs</c>:217-305).</summary>
    private static async Task<int> RunImagesAsync(IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var raw = (await ExecAsync(executor, ["images"]).ConfigureAwait(false)).Stdout;
        var result = await ExecAsync(executor, ["images", "--format", "{{.Repository}}:{{.Tag}}\t{{.Size}}"]).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            Console.Error.Write(result.Stderr);
            timer.Track("docker images", "rtk docker images", raw, raw);
            return result.ExitCode;
        }

        var rtk = DockerFilters.FormatImagesSummary(result.Stdout);

        // Same trailing-newline asymmetry as DockerFilters.FormatPsSummary — see RunPsAsync's
        // corresponding comment above.
        Console.Out.Write(rtk.EndsWith('\n') ? rtk : rtk + "\n");
        timer.Track("docker images", "rtk docker images", raw, rtk);
        return 0;
    }

    // ===================== docker logs =====================

    /// <summary>Faithful port of <c>docker_logs</c> (<c>container.rs</c>:307-331).</summary>
    private static async Task<int> RunLogsAsync(string[] rest, IProcessExecutor executor)
    {
        // Rust's `DockerCommands::Logs { container: String }` (main.rs:920) is a REQUIRED clap
        // positional — `rtk docker logs` with zero args never reaches this function at all; it
        // fails at the clap layer. `docker` is Rust-classified PASSTHROUGH (not
        // RTK_META_COMMANDS — confirmed against src/main.rs's test_every_subcommand_is_classified
        // and the real oracle, which for this exact case falls back to running the REAL `docker`
        // binary with the original argv, printing docker's OWN "'docker logs' requires 1
        // argument" usage and exiting 1 — NOT a clean clap-style exit 2, and NOT RtkSharp's own
        // message). Throwing here lets RtkProgram's dispatch layer re-route to that same
        // TOML-fallback/raw-passthrough path. The module's own `container.is_empty()` check below
        // is reachable ONLY via an explicit empty string argument (e.g. `rtk docker logs ""`),
        // which clap itself does not reject.
        if (rest.Length == 0)
        {
            throw new CommandArgumentParseException(
                "the following required arguments were not provided: <CONTAINER>");
        }

        var container = rest[0];
        if (container.Length == 0)
        {
            Console.Out.Write("Usage: rtk docker logs <container>\n");
            return 0;
        }

        var timer = TimedExecution.Start();
        var result = await ExecAsync(executor, ["logs", "--tail", "100", container]).ConfigureAwait(false);
        var raw = result.Stdout + result.Stderr;
        var cmdLabel = $"docker logs {container}";

        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                Console.Out.Write(result.Stdout);
            }

            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                Console.Error.Write(result.Stderr);
            }

            timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, raw);
            return result.ExitCode;
        }

        var filtered = $"[docker] Logs for {container}:\n{LogFilters.AnalyzeLogs(raw)}";
        Console.Out.Write(filtered + "\n");
        timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, filtered);
        return result.ExitCode;
    }

    // ===================== docker compose =====================

    private static async Task<int> RunComposeAsync(string[] rest, int verbose, IProcessExecutor executor)
    {
        if (rest.Length == 0)
        {
            return await RunComposePassthroughAsync([], executor).ConfigureAwait(false);
        }

        var subcommand = rest[0];
        var composeRest = rest[1..];

        return subcommand switch
        {
            "ps" => await RunComposePsAsync(composeRest, verbose, executor).ConfigureAwait(false),
            "logs" => await RunComposeLogsAsync(composeRest, verbose, executor).ConfigureAwait(false),
            "build" => await RunComposeBuildAsync(composeRest, verbose, executor).ConfigureAwait(false),
            _ => await RunComposePassthroughAsync(rest, executor).ConfigureAwait(false),
        };
    }

    /// <summary>Faithful port of <c>run_compose_ps</c> (<c>container.rs</c>:660-700).</summary>
    private static async Task<int> RunComposePsAsync(string[] composeRest, int verbose, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();
        var all = composeRest.Contains("-a") || composeRest.Contains("--all");

        List<string> rawArgs = ["compose", "ps"];
        if (all)
        {
            rawArgs.Add("-a");
        }

        var rawResult = await ExecAsync(executor, rawArgs).ConfigureAwait(false);
        if (rawResult.ExitCode != 0)
        {
            Console.Error.Write(rawResult.Stderr + "\n");
            return rawResult.ExitCode;
        }

        var raw = rawResult.Stdout;

        List<string> formatArgs = ["compose", "ps"];
        if (all)
        {
            formatArgs.Add("-a");
        }

        formatArgs.AddRange(["--format", "{{.Name}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}"]);
        var result = await ExecAsync(executor, formatArgs).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.Write(result.Stderr + "\n");
            return result.ExitCode;
        }

        if (verbose > 0)
        {
            Console.Error.Write($"raw docker compose ps:\n{raw}\n");
        }

        var rtk = DockerFilters.FormatComposePs(result.Stdout);
        Console.Out.Write(rtk + "\n");
        var label = all ? "docker compose ps -a" : "docker compose ps";
        var rtkLabel = all ? "rtk docker compose ps -a" : "rtk docker compose ps";
        timer.Track(label, rtkLabel, raw, rtk);
        return 0;
    }

    /// <summary>Faithful port of <c>run_compose_logs</c> (<c>container.rs</c>:702-723).</summary>
    private static async Task<int> RunComposeLogsAsync(string[] composeRest, int verbose, IProcessExecutor executor)
    {
        string? service = null;
        var tail = 100;
        for (var i = 0; i < composeRest.Length; i++)
        {
            if (composeRest[i] == "--tail" && i + 1 < composeRest.Length && int.TryParse(composeRest[i + 1], out var t))
            {
                tail = t;
                i++;
            }
            else if (!composeRest[i].StartsWith('-'))
            {
                service ??= composeRest[i];
            }
        }

        var timer = TimedExecution.Start();
        List<string> cmdArgs = ["compose", "logs", "--tail", tail.ToString(CultureInfo.InvariantCulture)];
        if (service is not null)
        {
            cmdArgs.Add(service);
        }

        var svcLabel = service ?? "all";
        var cmdLabel = $"docker compose logs {svcLabel}";
        var result = await ExecAsync(executor, cmdArgs).ConfigureAwait(false);
        var raw = result.Stdout + result.Stderr;

        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                Console.Out.Write(result.Stdout);
            }

            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                Console.Error.Write(result.Stderr);
            }

            timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, raw);
            return result.ExitCode;
        }

        if (verbose > 0)
        {
            Console.Error.Write($"raw docker compose logs:\n{raw}\n");
        }

        var filtered = DockerFilters.FormatComposeLogs(raw);
        Console.Out.Write(filtered + "\n");
        timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, filtered);
        return result.ExitCode;
    }

    /// <summary>Faithful port of <c>run_compose_build</c> (<c>container.rs</c>:725-745).</summary>
    private static async Task<int> RunComposeBuildAsync(string[] composeRest, int verbose, IProcessExecutor executor)
    {
        var service = composeRest.FirstOrDefault(a => !a.StartsWith('-'));

        var timer = TimedExecution.Start();
        List<string> cmdArgs = ["compose", "build"];
        if (service is not null)
        {
            cmdArgs.Add(service);
        }

        var svcLabel = service ?? "all";
        var cmdLabel = $"docker compose build {svcLabel}";
        var result = await ExecAsync(executor, cmdArgs).ConfigureAwait(false);
        var raw = result.Stdout + result.Stderr;

        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                Console.Out.Write(result.Stdout);
            }

            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                Console.Error.Write(result.Stderr);
            }

            timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, raw);
            return result.ExitCode;
        }

        if (verbose > 0)
        {
            Console.Error.Write($"raw docker compose build:\n{raw}\n");
        }

        var filtered = DockerFilters.FormatComposeBuild(raw);
        Console.Out.Write(filtered + "\n");
        timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, filtered);
        return result.ExitCode;
    }

    private static Task<int> RunComposePassthroughAsync(string[] args, IProcessExecutor executor)
    {
        List<string> combined = ["compose", .. args];
        return RunPassthroughCoreAsync("docker", combined, executor);
    }

    // ===================== shared helpers =====================

    private static Task<int> RunDockerPassthroughAsync(string[] args, IProcessExecutor executor) =>
        RunPassthroughCoreAsync("docker", args, executor);

    /// <summary>Faithful port of <c>run_passthrough</c> (<c>core/runner.rs</c>:235-249): inherits the child's console streams directly, tracked as 0%-savings passthrough.</summary>
    private static async Task<int> RunPassthroughCoreAsync(string tool, IReadOnlyList<string> args, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();
        var fileName = PathResolver.Resolve(tool);
        var request = new ExecutionRequest(fileName, args, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await executor.ExecuteAsync(request).ConfigureAwait(false);

        var cmdLabel = $"{tool} {string.Join(' ', args)}".Trim();
        timer.TrackPassthrough(cmdLabel, $"rtk {cmdLabel} (passthrough)");
        return result.ExitCode;
    }

    private static Task<ExecutionResult> ExecAsync(IProcessExecutor executor, IReadOnlyList<string> args) =>
        executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("docker"), args)).AsTask();
}
