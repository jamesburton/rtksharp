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
    private const int CapList = 20;
    private const int CapInventory = 50;

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

        if (string.IsNullOrWhiteSpace(stdout))
        {
            const string rtkNoContainers = "[docker] 0 containers";
            Console.Out.Write(rtkNoContainers + "\n");
            timer.Track("docker ps", "rtk docker ps", raw, rtkNoContainers);
            return 0;
        }

        var lines = ReadCommand.SplitLines(stdout)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => FormatContainerLine(l, withPorts: true))
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

        var rtk = $"[docker] {lines.Count} containers:\n";
        rtk += string.Concat(lines.Take(CapList));
        if (lines.Count > CapList)
        {
            rtk += $"  … +{lines.Count - CapList} more\n";
            var full = string.Concat(lines);
            if (Tee.ForceTeeHint(full, "docker-ps") is { } hint)
            {
                rtk += hint + "\n";
            }
        }

        Console.Out.Write(rtk);
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
            if (FormatContainerLineFromParts(parts.Skip(1).ToArray(), isRunning) is { } entry)
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

    /// <summary>Faithful port of <c>format_container_line</c> (<c>container.rs</c>:188-191).</summary>
    internal static string? FormatContainerLine(string line, bool withPorts) =>
        FormatContainerLineFromParts(line.Split('\t'), withPorts);

    /// <summary>Faithful port of <c>format_container_line_from_parts</c> (<c>container.rs</c>:193-215).</summary>
    internal static string? FormatContainerLineFromParts(IReadOnlyList<string> parts, bool withPorts)
    {
        if (parts.Count < 4)
        {
            return null;
        }

        var id = parts[0][..Math.Min(12, parts[0].Length)];
        var name = parts[1];
        var status = parts[2].Trim();
        var shortImage = parts[3].Split('/').Last();
        var portSuffix = "";
        if (withPorts)
        {
            var ports = CompactPorts(parts.Count > 4 ? parts[4] : "");
            if (ports != "-")
            {
                portSuffix = $" [{ports}]";
            }
        }

        return $"  {id} {name} ({shortImage}) {status}{portSuffix}\n";
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

        var lines = ReadCommand.SplitLines(result.Stdout).ToList();

        if (lines.Count == 0)
        {
            const string rtkNoImages = "[docker] 0 images";
            Console.Out.Write(rtkNoImages + "\n");
            timer.Track("docker images", "rtk docker images", raw, rtkNoImages);
            return 0;
        }

        var totalSizeMb = 0.0;
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length <= 1)
            {
                continue;
            }

            var sizeStr = parts[1];
            if (sizeStr.Contains("GB", StringComparison.Ordinal))
            {
                if (double.TryParse(sizeStr.Replace("GB", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gb))
                {
                    totalSizeMb += gb * 1024.0;
                }
            }
            else if (sizeStr.Contains("MB", StringComparison.Ordinal))
            {
                if (double.TryParse(sizeStr.Replace("MB", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb))
                {
                    totalSizeMb += mb;
                }
            }
        }

        var totalDisplay = totalSizeMb > 1024.0
            ? $"{(totalSizeMb / 1024.0).ToString("F1", CultureInfo.InvariantCulture)}GB"
            : $"{totalSizeMb.ToString("F0", CultureInfo.InvariantCulture)}MB";

        var rtk = $"[docker] {lines.Count} images ({totalDisplay})\n";

        var imageLines = lines.Select(line =>
        {
            var parts = line.Split('\t');
            var image = parts.Length > 0 ? parts[0] : "";
            var size = parts.Length > 1 ? parts[1] : "";
            return $"  {image} [{size}]\n";
        }).ToList();

        var fullRtk = rtk + string.Concat(imageLines);

        rtk += string.Concat(imageLines.Take(CapInventory));
        if (imageLines.Count > CapInventory)
        {
            rtk += $"  … +{imageLines.Count - CapInventory} more\n";
            if (Tee.ForceTeeTailHint(fullRtk, "docker-images", CapInventory + 2) is { } hint)
            {
                rtk += hint + "\n";
            }
        }

        Console.Out.Write(rtk);
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

        var filtered = $"[docker] Logs for {container}:\n{LogCommand.AnalyzeLogs(raw)}";
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

        var rtk = FormatComposePs(result.Stdout);
        Console.Out.Write(rtk + "\n");
        var label = all ? "docker compose ps -a" : "docker compose ps";
        var rtkLabel = all ? "rtk docker compose ps -a" : "rtk docker compose ps";
        timer.Track(label, rtkLabel, raw, rtk);
        return 0;
    }

    /// <summary>Faithful port of <c>format_compose_ps</c> (<c>container.rs</c>:509-562).</summary>
    internal static string FormatComposePs(string raw)
    {
        var lines = ReadCommand.SplitLines(raw).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0)
        {
            return "[compose] 0 services";
        }

        var result = $"[compose] {lines.Count} services:\n";

        var allFormatted = lines.Select(line =>
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
            {
                return null;
            }

            var name = parts[0];
            var image = parts[1];
            var status = parts[2];
            var ports = parts[3];
            var shortImage = image.Contains('/') ? image.Split('/').Last() : image;
            var portStr = "";
            if (!string.IsNullOrWhiteSpace(ports))
            {
                var compact = CompactPorts(ports.Trim());
                if (compact != "-")
                {
                    portStr = $" [{compact}]";
                }
            }

            return $"  {name} ({shortImage}) {status}{portStr}";
        }).Where(l => l is not null).Select(l => l!).ToList();

        result += string.Join('\n', allFormatted.Take(CapList));
        if (allFormatted.Count > 0)
        {
            result += "\n";
        }

        if (allFormatted.Count > CapList)
        {
            result += $"  … +{allFormatted.Count - CapList} more\n";
            var allText = string.Join('\n', allFormatted);
            if (Tee.ForceTeeTailHint(allText, "compose-ps", CapList + 1) is { } hint)
            {
                result += $"  {hint}\n";
            }
        }

        return result.TrimEnd();
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

        var filtered = FormatComposeLogs(raw);
        Console.Out.Write(filtered + "\n");
        timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, filtered);
        return result.ExitCode;
    }

    /// <summary>Faithful port of <c>format_compose_logs</c> (<c>container.rs</c>:564-574).</summary>
    internal static string FormatComposeLogs(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? "[compose] No logs" : $"[compose] Logs:\n{LogCommand.AnalyzeLogs(raw)}";

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

        var filtered = FormatComposeBuild(raw);
        Console.Out.Write(filtered + "\n");
        timer.Track(cmdLabel, $"rtk {cmdLabel}", raw, filtered);
        return result.ExitCode;
    }

    /// <summary>Faithful port of <c>format_compose_build</c> (<c>container.rs</c>:577-631).</summary>
    internal static string FormatComposeBuild(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "[compose] Build: no output";
        }

        var lines = ReadCommand.SplitLines(raw).ToList();
        var result = "";

        foreach (var line in lines)
        {
            if (line.Contains("Building", StringComparison.Ordinal) && line.Contains("FINISHED", StringComparison.Ordinal))
            {
                result += $"[compose] {line.Trim()}\n";
                break;
            }
        }

        if (result.Length == 0)
        {
            var buildingLine = lines.FirstOrDefault(l => l.Contains("Building", StringComparison.Ordinal));
            result += buildingLine is not null ? $"[compose] {buildingLine.Trim()}\n" : "[compose] Build:\n";
        }

        var services = new List<string>();
        foreach (var line in lines)
        {
            var start = line.IndexOf('[');
            if (start < 0)
            {
                continue;
            }

            var end = line.IndexOf(']', start + 1);
            if (end < 0)
            {
                continue;
            }

            var bracket = line[(start + 1)..end];
            // Rust's split_whitespace() splits on any whitespace (space, tab, ...), not just ' '.
            var svc = bracket.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (svc.Length > 0 && svc != "+" && !services.Contains(svc))
            {
                services.Add(svc);
            }
        }

        if (services.Count > 0)
        {
            result += $"  Services: {string.Join(", ", services)}\n";
        }

        var stepCount = lines.Count(l => l.TrimStart().StartsWith("=> ", StringComparison.Ordinal));
        if (stepCount > 0)
        {
            result += $"  Steps: {stepCount}";
        }

        return result.TrimEnd();
    }

    private static Task<int> RunComposePassthroughAsync(string[] args, IProcessExecutor executor)
    {
        List<string> combined = ["compose", .. args];
        return RunPassthroughCoreAsync("docker", combined, executor);
    }

    // ===================== shared helpers =====================

    /// <summary>Faithful port of <c>compact_ports</c> (<c>container.rs</c>:633-653).</summary>
    internal static string CompactPorts(string ports)
    {
        if (ports.Length == 0)
        {
            return "-";
        }

        var portNums = ports.Split(',')
            .Select(p => p.Split("->")[0].Split(':').Last())
            .ToList();

        if (portNums.Count <= 3)
        {
            return string.Join(", ", portNums);
        }

        return $"{string.Join(", ", portNums.Take(2))}, … +{portNums.Count - 2}";
    }

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
