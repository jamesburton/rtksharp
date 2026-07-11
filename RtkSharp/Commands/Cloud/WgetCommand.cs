using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Cloud;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk wget</c> CLI verb: runs the real <c>wget</c> binary, strips its progress
/// bars, and shows only a compact one-line result. Faithful port of
/// <c>src/cmds/cloud/wget_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>PASSTHROUGH-classified, not a meta-command.</b> Rust's own <c>RTK_META_COMMANDS</c> list does
/// not include <c>"wget"</c>. Its clap arm has a REQUIRED <c>url: String</c> positional
/// (<c>main.rs</c>:383-392) — <c>rtk wget</c> with zero arguments fails at the clap layer before
/// <c>wget_cmd::run</c> ever executes, falling back to a raw PATH-exec attempt. This port matches
/// <see cref="AwsCommand"/>'s established precedent for the identical shape: throw
/// <see cref="CommandArgumentParseException"/> so <c>RtkProgram</c>'s dispatch layer can re-route to
/// that same passthrough path.
/// </para>
/// <para>
/// <b>Two execution modes, matching <c>main.rs</c>:1975-1988 exactly.</b> When <c>-O -</c> (stdout
/// mode) is requested, dispatch goes to <see cref="RunStdoutAsync"/> (Rust's <c>run_stdout</c>);
/// otherwise, any <c>-O</c>/<c>--output-document</c> value is re-injected as <c>-O &lt;file&gt;</c>
/// ahead of the remaining args before calling <see cref="RunAsync(string, string[], int, IProcessExecutor)"/>
/// (Rust's <c>run</c>) — this port never lets clap's own <c>output</c> field silently diverge from
/// what's actually passed to the real <c>wget</c> binary.
/// </para>
/// </remarks>
public static class WgetCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk wget</c> with the given arguments (the remainder after the
    /// <c>wget</c> verb: URL first, then any <c>-O</c>/other wget flags).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>wget</c>.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RunAsync(string[] args) => DispatchAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor());

    /// <summary>
    /// Parses <paramref name="args"/> (URL positional, optional <c>-O</c>/<c>--output-document</c>,
    /// remaining wget flags) and dispatches to <see cref="RunStdoutAsync"/> or
    /// <see cref="RunAsync(string, string[], int, IProcessExecutor)"/>, mirroring
    /// <c>main.rs</c>'s <c>Commands::Wget</c> arm exactly.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>wget</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>wget</c> with.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="CommandArgumentParseException">
    /// Thrown when no URL positional is present, matching clap's required <c>url: String</c> field.
    /// </exception>
    public static Task<int> DispatchAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        var (url, output, rest) = ParseArgs(args);
        if (url is null)
        {
            throw new CommandArgumentParseException(
                "the following required arguments were not provided: <URL>");
        }

        if (output == "-")
        {
            return RunStdoutAsync(url, rest, verbose, executor);
        }

        var allArgs = new List<string>();
        if (output is not null)
        {
            allArgs.Add("-O");
            allArgs.Add(output);
        }

        allArgs.AddRange(rest);
        return RunAsync(url, allArgs.ToArray(), verbose, executor);
    }

    /// <summary>
    /// Parses the URL positional, <c>-O</c>/<c>--output-document</c> value, and remaining trailing
    /// args, matching clap's derived parsing of <c>Commands::Wget</c>'s three fields.
    /// </summary>
    /// <param name="args">The raw CLI arguments following <c>wget</c>.</param>
    /// <returns>The parsed URL (or null if absent), output value (or null), and remaining args.</returns>
    private static (string? Url, string? Output, List<string> Remaining) ParseArgs(string[] args)
    {
        string? url = null;
        string? output = null;
        var rest = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "-O" or "--output-document")
            {
                output = i + 1 < args.Length ? args[++i] : null;
                continue;
            }

            if (arg.StartsWith("--output-document=", StringComparison.Ordinal))
            {
                output = arg["--output-document=".Length..];
                continue;
            }

            if (arg.StartsWith("-O", StringComparison.Ordinal) && arg.Length > 2)
            {
                output = arg[2..];
                continue;
            }

            if (url is null && !arg.StartsWith('-'))
            {
                url = arg;
                continue;
            }

            rest.Add(arg);
        }

        return (url, output, rest);
    }

    /// <summary>
    /// Faithful port of <c>run</c> (<c>wget_cmd.rs</c>:7-49): runs <c>wget</c> normally, capturing
    /// output to parse the saved filename and size, and prints a one-line
    /// <c>url ok | filename | size</c> or <c>url FAILED: error</c> result.
    /// </summary>
    /// <param name="url">The URL to download.</param>
    /// <param name="args">Additional wget arguments (including any re-injected <c>-O file</c>).</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>wget</c> with.</param>
    /// <returns>wget's real exit code, or 0 on success.</returns>
    internal static async Task<int> RunAsync(string url, string[] args, int verbose, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        if (verbose > 0)
        {
            Console.Error.Write($"wget: {url}\n");
        }

        var cmdArgs = new List<string>(args) { url };
        var result = await executor.ExecuteAsync(
            new ExecutionRequest("wget", cmdArgs, CaptureMode: ExecutionCaptureMode.Separate)).ConfigureAwait(false);

        var rawOutput = $"{result.Stderr}\n{result.Stdout}";

        if (result.ExitCode == 0 && result.WasStarted)
        {
            var filename = ExtractFilenameFromOutput(result.Stderr, url, args);
            var size = GetFileSize(filename);
            var msg = WgetFilters.FormatWgetOutput(url, filename, size);
            Console.Out.Write(msg + "\n");
            timer.Track($"wget {url}", "rtk wget", rawOutput, msg);
            return 0;
        }

        var error = WgetFilters.ParseError(result.Stderr, result.Stdout);
        var failMsg = WgetFilters.FormatWgetFailure(url, error);
        Console.Out.Write(failMsg + "\n");
        timer.Track($"wget {url}", "rtk wget", rawOutput, failMsg);
        return result.ExitCode;
    }

    /// <summary>
    /// Faithful port of <c>run_stdout</c> (<c>wget_cmd.rs</c>:52-108): runs <c>wget -q -O -</c> and
    /// prints either the full content (&le;20 lines) or a compact head-of-output summary.
    /// </summary>
    /// <param name="url">The URL to download.</param>
    /// <param name="args">Additional wget arguments.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>wget</c> with.</param>
    /// <returns>wget's real exit code, or 0 on success.</returns>
    internal static async Task<int> RunStdoutAsync(string url, List<string> args, int verbose, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        if (verbose > 0)
        {
            Console.Error.Write($"wget: {url} -> stdout\n");
        }

        var cmdArgs = new List<string> { "-q", "-O", "-" };
        cmdArgs.AddRange(args);
        cmdArgs.Add(url);

        var result = await executor.ExecuteAsync(
            new ExecutionRequest("wget", cmdArgs, CaptureMode: ExecutionCaptureMode.Separate)).ConfigureAwait(false);

        if (result.ExitCode == 0 && result.WasStarted)
        {
            var rtkOutput = WgetFilters.FormatWgetStdoutOutput(url, result.Stdout);
            Console.Out.Write(rtkOutput);
            timer.Track($"wget -O - {url}", "rtk wget -o", result.Stdout, rtkOutput);
            return 0;
        }

        var error = WgetFilters.ParseError(result.Stderr, "");
        var msg = WgetFilters.FormatWgetFailure(url, error);
        Console.Out.Write(msg + "\n");
        timer.Track($"wget -O - {url}", "rtk wget -o", result.Stderr, msg);
        return result.ExitCode;
    }

    /// <summary>Faithful port of <c>extract_filename_from_output</c> (<c>wget_cmd.rs</c>:110-165).</summary>
    /// <param name="stderr">wget's captured stderr.</param>
    /// <param name="url">The downloaded URL.</param>
    /// <param name="args">The wget arguments passed (checked for an explicit <c>-O</c>).</param>
    /// <returns>The resolved output filename.</returns>
    internal static string ExtractFilenameFromOutput(string stderr, string url, IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-O" or "--output-document")
            {
                if (i + 1 < args.Count)
                {
                    return args[i + 1];
                }
            }

            if (arg.StartsWith("-O", StringComparison.Ordinal) && arg.Length > 2)
            {
                return arg[2..];
            }
        }

        foreach (var line in SourceFilterLineSplitter.SplitLines(stderr))
        {
            if (line.Contains("Sauvegarde en") || line.Contains("Saving to"))
            {
                var chars = line;
                int? startIdx = null;
                int? endIdx = null;

                for (var i = 0; i < chars.Length; i++)
                {
                    var c = chars[i];
                    if (c == '«' || (c == '\'' && startIdx is null))
                    {
                        startIdx = i;
                    }

                    if (c == '»' || (c == '\'' && startIdx is not null))
                    {
                        endIdx = i;
                    }
                }

                if (startIdx is { } s && endIdx is { } e && e > s + 1)
                {
                    return chars[(s + 1)..e].Trim();
                }
            }
        }

        var path = url.Contains("://") ? url[(url.LastIndexOf("://", StringComparison.Ordinal) + 3)..] : url;
        var afterSlash = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var filename = afterSlash.Contains('?') ? afterSlash[..afterSlash.IndexOf('?')] : afterSlash;

        return filename.Length == 0 || !filename.Contains('.') ? "index.html" : filename;
    }

    private static ulong GetFileSize(string filename)
    {
        try
        {
            return (ulong)new FileInfo(filename).Length;
        }
        catch
        {
            return 0;
        }
    }

}
