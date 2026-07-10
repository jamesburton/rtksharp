using RtkSharp.Cli;
using RtkSharp.Commands.Git;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Rust;

namespace RtkSharp.Commands.Rust;

/// <summary>
/// Implements the <c>rtk cargo</c> CLI verb: <c>build</c>/<c>test</c>/<c>check</c> stream their output
/// live through a <see cref="BlockStreamFilter{THandler}"/>; <c>clippy</c>/<c>install</c>/<c>nextest</c>
/// buffer the full output before filtering; any other cargo subcommand runs completely unfiltered.
/// Faithful port of <c>src/cmds/rust/cargo_cmd.rs</c> plus its dispatch arm in <c>src/main.rs</c>
/// (<c>CargoCommands</c> enum, <c>Commands::Cargo</c> match arm).
/// </summary>
/// <remarks>
/// <para>
/// <b>PASSTHROUGH-classified, not a meta-command.</b> Rust's own <c>RTK_META_COMMANDS</c> list does
/// not include <c>"cargo"</c> — it is classified under <c>PASSTHROUGH</c> instead
/// (<c>main.rs</c>'s <c>test_every_subcommand_is_classified</c>). Registered in
/// <see cref="RtkSharp.Cli.CommandRegistry"/> the same way <c>git</c>/<c>gh</c>/<c>dotnet</c> are: a
/// normal dispatch entry, not the meta-command-style guarantee <c>run</c>/<c>proxy</c>/<c>pipe</c>/
/// <c>gain</c> get.
/// </para>
/// <para>
/// <b>No <see cref="CommandArgumentParseException"/> path — verified, not assumed.</b> Every one of
/// <c>Build</c>/<c>Test</c>/<c>Clippy</c>/<c>Check</c>/<c>Install</c>/<c>Nextest</c> is declared in Rust
/// with <c>#[arg(trailing_var_arg = true, allow_hyphen_values = true)] args: Vec&lt;String&gt;</c> —
/// clap accepts literally any token stream for these six (no flag/value can ever fail to parse), so
/// there is no clap-equivalent rejection to reproduce for them. The only failure mode is
/// <c>rtk cargo</c> with zero further tokens (clap's derived <c>Subcommand</c> requires one, since
/// <c>CargoCommands::Other</c> still needs at least the subcommand-name token to capture); this port
/// matches <see cref="RtkSharp.Commands.Dotnet.DotnetCommand"/>'s established precedent for the
/// identical zero-args case (a direct <c>"cargo: no subcommand specified"</c> + exit 1, not a thrown
/// <see cref="CommandArgumentParseException"/>) rather than inventing a new convention. No outer
/// <c>catch (Exception ex) when (ex is not CommandArgumentParseException)</c> guard is needed here for
/// the same reason <c>DotnetCommand</c> has none: this module never throws that exception, so there is
/// nothing for such a guard to protect.
/// </para>
/// <para>
/// <b>Quirk preserved verbatim: <c>cargo check</c> reports as "cargo build".</b> See
/// <see cref="CargoBuildHandler"/>'s remarks.
/// </para>
/// </remarks>
public static class CargoCommand
{
    /// <summary>
    /// Registry entry point. Dispatches on the first argument (the cargo subcommand) exactly as
    /// Rust's <c>CargoCommands</c> routing does.
    /// </summary>
    /// <param name="args">The arguments following the <c>cargo</c> verb (subcommand first).</param>
    /// <returns>The wrapped <c>cargo</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, lineFilteringExecutor: null, processExecutor: null);

    /// <summary>
    /// Test-friendly overload accepting explicit executors.
    /// </summary>
    /// <param name="args">The arguments following the <c>cargo</c> verb (subcommand first).</param>
    /// <param name="lineFilteringExecutor">The streaming executor used for build/test/check, or null for the default.</param>
    /// <param name="processExecutor">The buffered process executor used for clippy/install/nextest/passthrough, or null for the default.</param>
    /// <returns>The wrapped <c>cargo</c> process's exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args,
        ILineFilteringExecutor? lineFilteringExecutor,
        IProcessExecutor? processExecutor)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            Console.Error.Write("cargo: no subcommand specified\n");
            return 1;
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "build" => await RunStreamedAsync("build", rest, lineFilteringExecutor, new BlockStreamFilter<CargoBuildHandler>(new CargoBuildHandler())).ConfigureAwait(false),
            "check" => await RunStreamedAsync("check", rest, lineFilteringExecutor, new BlockStreamFilter<CargoBuildHandler>(new CargoBuildHandler())).ConfigureAwait(false),
            "test" => await RunStreamedAsync("test", rest, lineFilteringExecutor, new BlockStreamFilter<CargoTestHandler>(new CargoTestHandler())).ConfigureAwait(false),
            "clippy" => await RunBufferedAsync("clippy", rest, CargoFilters.FilterCargoClippy).ConfigureAwait(false),
            "install" => await RunBufferedAsync("install", rest, CargoFilters.FilterCargoInstall).ConfigureAwait(false),
            "nextest" => await RunBufferedAsync("nextest", rest, CargoFilters.FilterCargoNextest).ConfigureAwait(false),
            _ => await RunPassthroughAsync(args, processExecutor).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Restores any <c>--</c> tokens that would have been consumed by clap's <c>trailing_var_arg</c>
    /// under Rust. Ports <c>args_utils::restore_double_dash</c> (<c>cargo_cmd.rs</c>:256, 283) by
    /// delegating to the already-ported <see cref="GitCommand.RestoreDoubleDashWithRaw"/> (the same
    /// underlying algorithm as <c>args_utils::restore_double_dash_with_raw</c>) — as with
    /// <c>GitCommand</c>'s own port, this reduces to the identity function at runtime: RtkSharp's
    /// top-level argument parser never strips <c>--</c> from a command's args in the first place, so
    /// <paramref name="parsedArgs"/> already carries any <c>--</c> the user typed. The full algorithm is
    /// exercised (against cargo-specific scenarios) in <c>CargoCommandTests</c> so the parity guarantee
    /// survives any future change to the top-level parser.
    /// </summary>
    /// <param name="parsedArgs">The arguments following the cargo subcommand.</param>
    /// <returns>The argument vector with any consumed <c>--</c> tokens restored.</returns>
    internal static IReadOnlyList<string> RestoreDoubleDash(IReadOnlyList<string> parsedArgs) =>
        GitCommand.RestoreDoubleDashWithRaw(parsedArgs, Environment.GetCommandLineArgs());

    private static async Task<int> RunStreamedAsync(
        string subcommand,
        string[] rest,
        ILineFilteringExecutor? executor,
        IStreamFilter filter)
    {
        var restoredArgs = RestoreDoubleDash(rest);

        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running: cargo {subcommand} {string.Join(' ', restoredArgs)}\n");
        }

        var invocation = new List<string> { subcommand };
        invocation.AddRange(restoredArgs);

        var exec = executor ?? new LineFilteringExecutor();
        var request = new ExecutionRequest("cargo", invocation);

        var timer = TimedExecution.Start();
        var result = await exec.ExecuteAsync(request, filter).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run cargo {subcommand}{detail}");
        }

        // core::runner.rs's Streamed branch only prints the tee hint line after the fact — the
        // filtered content itself was already streamed live to the console while cargo ran.
        var hint = Tee.TeeAndHint(result.Raw, $"cargo_{subcommand}", result.ExitCode);
        if (hint is not null)
        {
            Console.Out.Write(hint + "\n");
        }

        var cmdLabel = $"cargo {subcommand} {string.Join(' ', restoredArgs)}";
        timer.Track(cmdLabel, $"rtk {cmdLabel}", result.Raw, result.Filtered);

        return result.ExitCode;
    }

    /// <summary>
    /// Runs a buffered (non-streaming) cargo subcommand via the shared <see cref="CommandRunner"/>
    /// pipeline — the C# port of Rust's <c>runner::run_filtered</c>, reused as-is rather than
    /// reimplemented, since it already faithfully ports the capture/filter/tee/track/print skeleton
    /// <c>clippy</c>/<c>install</c>/<c>nextest</c> need. No executor injection point is exposed here
    /// (unlike <see cref="RunStreamedAsync"/>/<see cref="RunPassthroughAsync"/>): the ~40 ported Rust
    /// unit tests for these three subcommands all exercise the pure filter functions directly (mirroring
    /// Rust's own <c>#[cfg(test)]</c> suite, which never drives <c>filter_cargo_clippy</c>/
    /// <c>filter_cargo_install</c>/<c>filter_cargo_nextest</c> through a live process either), so no
    /// dispatch-level fake-executor test is needed for this path.
    /// </summary>
    private static Task<int> RunBufferedAsync(string subcommand, string[] rest, Func<string, string> filter)
    {
        var restoredArgs = RestoreDoubleDash(rest);

        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running: cargo {subcommand} {string.Join(' ', restoredArgs)}\n");
        }

        var invocation = new List<string> { subcommand };
        invocation.AddRange(restoredArgs);

        return CommandRunner.RunFilteredAsync(
            "cargo",
            invocation,
            $"cargo {subcommand}",
            string.Join(' ', restoredArgs),
            filter,
            new RunOptions(TeeLabel: $"cargo_{subcommand}")
        );
    }

    private static async Task<int> RunPassthroughAsync(string[] args, IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();
        var fileName = PathResolver.Resolve("cargo");
        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest(fileName, args, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        var cmdLabel = $"cargo {string.Join(' ', args)}".Trim();
        timer.TrackPassthrough(cmdLabel, $"rtk {cmdLabel} (passthrough)");
        return result.ExitCode;
    }
}
