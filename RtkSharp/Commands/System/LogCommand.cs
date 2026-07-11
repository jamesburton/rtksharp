using System;
using System.IO;
using System.Text;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk log</c> CLI verb: deduplicates repeated log lines and shows counts instead.
/// Faithful port of Rust <c>src/cmds/system/log_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <b>Also used as shared infrastructure by <c>rtk docker logs</c>/<c>rtk docker compose logs</c>.</b>
/// Rust's <c>log_cmd::run_stdin_str</c> is called directly from <c>cmds/cloud/container.rs</c>'s
/// <c>docker_logs</c>/<c>format_compose_logs</c> — <see cref="LogFilters.AnalyzeLogs"/> is the
/// equivalent reusable entry point, consumed by <c>DockerCommand</c>/<c>DockerFilters</c>. The pure
/// analysis logic was extracted to <see cref="LogFilters"/> ahead of Task 14's full System-ecosystem
/// sweep to unblock <c>DockerFilters.FormatComposeLogs</c>'s extraction.
/// </remarks>
public static class LogCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk log</c> with the given arguments (the remainder after the
    /// <c>log</c> verb): an optional file path, or stdin if omitted. Faithful port of
    /// <c>Commands::Log</c>'s dispatch arm (<c>main.rs</c>:1775-1782).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>log</c> (at most one: a file path).</param>
    /// <returns>0, or 1 on an rtk-level failure (e.g. the file doesn't exist).</returns>
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length > 0)
            {
                return RunFile(args[0], RuntimeOptions.Verbosity, Console.Out);
            }

            return RunStdin(Console.In, Console.Out);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Guarded proactively, not thrown from this command today — see DockerCommand/
            // PrismaCommand's remarks for the swallowed-exception bug this prevents.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Analyzes a log file's content. Faithful port of <c>run_file</c> (<c>log_cmd.rs</c>:25-42).
    /// </summary>
    /// <param name="path">The log file to read.</param>
    /// <param name="verbose">The verbosity level; &gt; 0 prints the file being analyzed to stderr.</param>
    /// <param name="stdout">The destination for the summary.</param>
    /// <returns>0.</returns>
    internal static int RunFile(string path, int verbose, TextWriter stdout)
    {
        var timer = TimedExecution.Start();

        if (verbose > 0)
        {
            Console.Error.Write($"Analyzing log: {path}\n");
        }

        var content = File.ReadAllText(path);
        var result = LogFilters.AnalyzeLogs(content);
        stdout.Write(result + "\n");
        timer.Track($"cat {path}", "rtk log", content, result);
        return 0;
    }

    /// <summary>
    /// Analyzes log content read from stdin. Faithful port of <c>run_stdin</c> (<c>log_cmd.rs</c>:45-61).
    /// </summary>
    /// <param name="stdin">The source to read lines from.</param>
    /// <param name="stdout">The destination for the summary.</param>
    /// <returns>0.</returns>
    internal static int RunStdin(TextReader stdin, TextWriter stdout)
    {
        var timer = TimedExecution.Start();

        var sb = new StringBuilder();
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            sb.Append(line).Append('\n');
        }

        var content = sb.ToString();
        var result = LogFilters.AnalyzeLogs(content);
        stdout.Write(result + "\n");
        timer.Track("log (stdin)", "rtk log (stdin)", content, result);
        return 0;
    }
}
