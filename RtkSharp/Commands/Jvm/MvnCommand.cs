using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Jvm;

/// <summary>
/// The Maven phase family detected from the raw argument list, driving which buffered filter (if
/// any) is applied. Faithful port of Rust's <c>MvnPhase</c> enum
/// (<c>src/cmds/jvm/mvn_cmd.rs</c>:79-85).
/// </summary>
public enum MvnPhase
{
    /// <summary><c>test</c> / <c>integration-test</c> (Failsafe shares Surefire's output shape).</summary>
    Test,

    /// <summary><c>compile</c> / <c>test-compile</c>.</summary>
    Compile,

    /// <summary><c>package</c> / <c>install</c> / <c>verify</c> / <c>deploy</c>.</summary>
    Package,

    /// <summary><c>clean</c>, <c>site</c>, plugin goals, version/help, or an empty argument list.</summary>
    Passthrough,
}

/// <summary>
/// Implements the <c>rtk mvn</c> CLI verb: an Apache Maven wrapper that buffers and filters
/// <c>test</c>/<c>compile</c>/<c>package</c>-family goals, handles <c>-q</c>/<c>--quiet</c> as a
/// distinct residual-output shape, and passes every other invocation through completely unfiltered.
/// Faithful port of <c>src/cmds/jvm/mvn_cmd.rs</c> plus its dispatch arm in <c>src/main.rs</c>
/// (<c>Commands::Mvn</c> match arm).
/// </summary>
/// <remarks>
/// Like <see cref="GradlewCommand"/>, this is a flat <c>trailing_var_arg</c> command with no nested
/// clap subcommand and (verified against the actual Rust source) no <c>restore_double_dash</c> step:
/// <c>args</c> is forwarded to the child process exactly as received.
/// </remarks>
public static class MvnCommand
{
    private const string DebugShortFlag = "-X";
    private const string DebugLongFlag = "--debug";
    private const string ErrorsShortFlag = "-e";
    private const string ErrorsLongFlag = "--errors";

    /// <summary>
    /// Registry entry point.
    /// </summary>
    /// <param name="args">The Maven goals and arguments following the <c>mvn</c> verb.</param>
    /// <returns>The wrapped Maven process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, processExecutor: null);

    /// <summary>
    /// Test-friendly overload accepting an explicit process executor.
    /// </summary>
    /// <param name="args">The Maven goals and arguments following the <c>mvn</c> verb.</param>
    /// <param name="processExecutor">The buffered/passthrough process executor, or null for the default.</param>
    /// <returns>The wrapped Maven process's exit code.</returns>
    internal static async Task<int> RunAsync(string[] args, IProcessExecutor? processExecutor)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Verbose flags bypass filtering — user wants full output.
        if (args.Any(a => a is DebugShortFlag or DebugLongFlag or ErrorsShortFlag or ErrorsLongFlag))
        {
            return await RunPassthroughAsync(args, processExecutor).ConfigureAwait(false);
        }

        var tool = MvnBinary();
        var argsDisplay = string.Join(' ', args);

        // Quiet mode: standard footer guard can't fire (no `BUILD SUCCESS` line under `-q`). Route to
        // FilterQuiet for any non-passthrough phase so failure output gets framework frames + help
        // boilerplate stripped.
        if (IsQuiet(args))
        {
            var quietPhase = DetectPhase(args);
            if (quietPhase == MvnPhase.Passthrough)
            {
                return await RunPassthroughAsync(args, processExecutor).ConfigureAwait(false);
            }

            return await RunBufferedAsync(tool, args, argsDisplay, MvnCompileQuietFilters.FilterQuiet, "mvn_quiet").ConfigureAwait(false);
        }

        return DetectPhase(args) switch
        {
            MvnPhase.Test => await RunBufferedAsync(tool, args, argsDisplay, MvnSurefireFilter.FilterSurefire, "mvn_test").ConfigureAwait(false),
            MvnPhase.Compile => await RunBufferedAsync(tool, args, argsDisplay, MvnCompileQuietFilters.FilterCompile, "mvn_compile").ConfigureAwait(false),
            MvnPhase.Package => await RunBufferedAsync(tool, args, argsDisplay, MvnSurefireFilter.FilterPackage, "mvn_package").ConfigureAwait(false),
            _ => await RunPassthroughAsync(args, processExecutor).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Detects whether <c>-q</c>/<c>--quiet</c> is present anywhere in the argument list. Faithful
    /// port of Rust's <c>is_quiet</c> (<c>mvn_cmd.rs</c>:73-75).
    /// </summary>
    /// <param name="args">The full Maven argument list.</param>
    /// <returns>True if quiet mode was requested.</returns>
    internal static bool IsQuiet(IReadOnlyList<string> args) => args.Any(a => a is "-q" or "--quiet");

    /// <summary>
    /// Scans args left-to-right, skipping flags and <c>-D…</c> system props, and picks the LAST
    /// remaining token to determine the phase family. Faithful port of Rust's <c>detect_phase</c>
    /// (<c>mvn_cmd.rs</c>:89-107).
    /// </summary>
    /// <param name="args">The full Maven argument list.</param>
    /// <returns>The detected phase family.</returns>
    public static MvnPhase DetectPhase(IReadOnlyList<string> args)
    {
        var last = string.Empty;
        foreach (var a in args)
        {
            if (a.StartsWith('-'))
            {
                continue;
            }

            last = a;
        }

        if (last.Length == 0 || last.Contains(':', StringComparison.Ordinal))
        {
            return MvnPhase.Passthrough;
        }

        return last switch
        {
            "clean" or "site" or "site-deploy" => MvnPhase.Passthrough,
            "test" or "integration-test" => MvnPhase.Test,
            "compile" or "test-compile" => MvnPhase.Compile,
            "package" or "install" or "verify" or "deploy" => MvnPhase.Package,
            _ => MvnPhase.Passthrough,
        };
    }

    /// <summary>
    /// Returns the Maven executable: prefers the local wrapper (<c>mvnw.cmd</c>/<c>./mvnw</c>), falls
    /// back to the <c>mvn</c> binary resolved via <c>PATH</c>/<c>PATHEXT</c> at execution time
    /// (handled transparently by <see cref="PathResolver"/>). Faithful port of Rust's <c>mvn_binary</c>
    /// / <c>new_mvn_command</c> (<c>mvn_cmd.rs</c>:874-902).
    /// </summary>
    /// <returns>The Maven executable name or relative wrapper path.</returns>
    internal static string MvnBinary()
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(".\\mvnw.cmd") ? ".\\mvnw.cmd" : "mvn";
        }

        return File.Exists("./mvnw") ? "./mvnw" : "mvn";
    }

    private static Task<int> RunBufferedAsync(
        string tool,
        string[] args,
        string argsDisplay,
        Func<string, string> filter,
        string teeLabel)
    {
        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running: {tool} {argsDisplay}\n");
        }

        return CommandRunner.RunFilteredAsync(
            tool,
            args,
            tool,
            argsDisplay,
            filter,
            new RunOptions(TeeLabel: teeLabel)
        );
    }

    private static async Task<int> RunPassthroughAsync(string[] args, IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();
        var fileName = MvnBinary();
        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest(fileName, args, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        var cmdLabel = $"mvn {string.Join(' ', args)}".Trim();
        timer.TrackPassthrough(cmdLabel, $"rtk {cmdLabel} (passthrough)");
        return result.ExitCode;
    }
}
