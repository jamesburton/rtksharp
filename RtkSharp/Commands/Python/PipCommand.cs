using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Python;

/// <summary>
/// Implements the <c>rtk pip</c> CLI verb: <c>list</c>/<c>outdated</c> are filtered into compact
/// summaries via <c>--format=json</c>; <c>install</c>/<c>uninstall</c>/<c>show</c>, and any other
/// subcommand, run as a full passthrough. Auto-detects <c>uv</c> as a fallback when <c>pip</c> itself
/// is not on <c>PATH</c>. Faithful port of <c>src/cmds/python/pip_cmd.rs</c> plus its dispatch arm in
/// <c>src/main.rs</c> (<c>Commands::Pip</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat argument surface, no subcommand enum.</b> Rust declares <c>Pip { #[arg(trailing_var_arg =
/// true, allow_hyphen_values = true)] args: Vec&lt;String&gt; }</c> (<c>main.rs</c>:702-707) — a single
/// catch-all vector; subcommand dispatch (<c>list</c>/<c>outdated</c>/<c>install</c>/...) happens
/// inside this module itself by inspecting <c>args[0]</c>, not via clap. Clap accepts any token
/// stream, so there is no zero-args or unknown-subcommand failure mode to reproduce; this port never
/// throws <see cref="Cli.CommandArgumentParseException"/>.
/// </para>
/// <para>
/// <b><c>pip</c>-first, <c>uv</c> only as a genuine-absence fallback.</b> Rust's own comment
/// (<c>pip_cmd.rs</c>:21-25) explains this precisely: auto-substituting <c>uv pip</c> unconditionally
/// made <c>pip list</c> show the wrong (uv-discovered) environment instead of the one actually active,
/// often just uv's 2-package base interpreter. So <c>use_uv</c> is true only when <c>pip</c> itself is
/// NOT resolvable AND <c>uv</c> IS — never merely "uv is available".
/// </para>
/// <para>
/// <b>Manual exec + <see cref="TimedExecution"/>, not <see cref="Core.CommandRunner"/>.</b> Unlike
/// <see cref="RuffCommand"/>/<see cref="PytestCommand"/>/<see cref="MypyCommand"/> (which all go
/// through Rust's shared <c>runner::run_filtered</c>), Rust's <c>pip_cmd::run</c> calls
/// <c>core::stream::exec_capture</c> directly and drives <see cref="Tracking.TimedExecution"/> itself
/// — the same shape <see cref="Cloud.DockerCommand"/> already established in this codebase for
/// modules with real subcommand-specific dispatch instead of a single filter closure. This port
/// follows that precedent rather than reaching for <see cref="Core.CommandRunner"/>.
/// </para>
/// <para>
/// <b>The final <see cref="TimedExecution.Track"/> call uses the ORIGINAL <c>args</c>, not the
/// per-subcommand rest.</b> Rust's outer <c>run()</c> builds its tracking label from <c>base_cmd</c>
/// and the full incoming <c>args.join(" ")</c> (<c>pip_cmd.rs</c>:50-54) — including the subcommand
/// token itself — regardless of which branch handled the request. This port mirrors that exactly.
/// </para>
/// </remarks>
public static class PipCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk pip</c> with the given arguments (the remainder after the
    /// <c>pip</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>pip</c>.</param>
    /// <returns>The underlying pip/uv process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor(), static name => PathResolver.Resolve(name) != name);

    /// <summary>
    /// Testable core of <see cref="RunAsync(string[])"/>: the process executor and tool-existence
    /// check are both injected so the <c>uv</c>-fallback branch and subcommand handlers can be
    /// exercised deterministically.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>pip</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>pip</c>/<c>uv</c> with.</param>
    /// <param name="toolExists">Predicate reporting whether a named tool is resolvable on the host.</param>
    /// <returns>The underlying pip/uv process's exit code.</returns>
    internal static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor executor, Func<string, bool> toolExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(toolExists);

        var timer = TimedExecution.Start();

        var useUv = !toolExists("pip") && toolExists("uv");
        var baseCmd = useUv ? "uv" : "pip";

        if (verbose > 0 && useUv)
        {
            Console.Error.Write("pip not found — falling back to `uv pip`\n");
        }

        var subcommand = args.Length > 0 ? args[0] : "";
        var rest = args.Length > 0 ? args[1..] : [];

        var (cmdStr, filtered, exitCode) = subcommand switch
        {
            "list" => await RunListAsync(baseCmd, rest, verbose, executor).ConfigureAwait(false),
            "outdated" => await RunOutdatedAsync(baseCmd, rest, verbose, executor).ConfigureAwait(false),
            "install" or "uninstall" or "show" => await RunPassthroughAsync(baseCmd, args, verbose, executor).ConfigureAwait(false),
            _ => await RunPassthroughAsync(baseCmd, args, verbose, executor).ConfigureAwait(false),
        };

        var argsJoined = string.Join(' ', args);
        timer.Track($"{baseCmd} {argsJoined}", $"rtk {baseCmd} {argsJoined}", cmdStr, filtered);

        return exitCode;
    }

    private static async Task<(string CmdStr, string Filtered, int ExitCode)> RunListAsync(
        string baseCmd, string[] args, int verbose, IProcessExecutor executor)
    {
        List<string> invocation = baseCmd == "uv" ? ["pip", "list", "--format=json"] : ["list", "--format=json"];
        invocation.AddRange(args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {baseCmd} pip list --format=json\n");
        }

        var result = await ExecAsync(executor, baseCmd, invocation).ConfigureAwait(false);
        var raw = $"{result.Stdout}\n{result.Stderr}";
        var filtered = PipFilters.FilterPipList(result.Stdout);
        Console.Out.Write(filtered + "\n");

        return (raw, filtered, result.ExitCode);
    }

    private static async Task<(string CmdStr, string Filtered, int ExitCode)> RunOutdatedAsync(
        string baseCmd, string[] args, int verbose, IProcessExecutor executor)
    {
        List<string> invocation = baseCmd == "uv"
            ? ["pip", "list", "--outdated", "--format=json"]
            : ["list", "--outdated", "--format=json"];
        invocation.AddRange(args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {baseCmd} pip list --outdated --format=json\n");
        }

        var result = await ExecAsync(executor, baseCmd, invocation).ConfigureAwait(false);
        var raw = $"{result.Stdout}\n{result.Stderr}";
        var filtered = PipFilters.FilterPipOutdated(result.Stdout);
        Console.Out.Write(filtered + "\n");

        return (raw, filtered, result.ExitCode);
    }

    private static async Task<(string CmdStr, string Filtered, int ExitCode)> RunPassthroughAsync(
        string baseCmd, string[] args, int verbose, IProcessExecutor executor)
    {
        List<string> invocation = baseCmd == "uv" ? ["pip"] : [];
        invocation.AddRange(args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {baseCmd} pip {string.Join(' ', args)}\n");
        }

        var result = await ExecAsync(executor, baseCmd, invocation).ConfigureAwait(false);
        var raw = $"{result.Stdout}\n{result.Stderr}";

        Console.Out.Write(result.Stdout);
        Console.Error.Write(result.Stderr);

        return (raw, raw, result.ExitCode);
    }

    private static Task<ExecutionResult> ExecAsync(IProcessExecutor executor, string baseCmd, IReadOnlyList<string> args) =>
        executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve(baseCmd), args)).AsTask();
}
