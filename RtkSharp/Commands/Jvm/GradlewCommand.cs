using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Jvm;

namespace RtkSharp.Commands.Jvm;

/// <summary>
/// Implements the <c>rtk gradlew</c> CLI verb: an Android Gradle wrapper that streams
/// <c>build</c>-family tasks live through a line filter, buffers and filters
/// <c>test</c>/<c>connectedTest</c>/<c>lint</c>/<c>dependencies</c> tasks, and passes every other task
/// through completely unfiltered. Faithful port of <c>src/cmds/jvm/gradlew_cmd.rs</c> plus its dispatch
/// arm in <c>src/main.rs</c> (<c>Commands::Gradlew</c> match arm).
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat <c>trailing_var_arg</c>, no nested subcommand.</b> Unlike <c>cargo</c>, Rust's
/// <c>Commands::Gradlew</c> variant declares a single flat
/// <c>#[arg(trailing_var_arg = true, allow_hyphen_values = true)] args: Vec&lt;String&gt;</c> field with
/// no <c>#[command(subcommand)]</c> nesting — the Gradle task name (e.g. <c>assembleDebug</c>,
/// <c>testDebugUnitTest</c>) is just one of the tokens in that flat list, detected by
/// <see cref="DetectTask"/> scanning the list rather than by clap parsing a subcommand keyword. There is
/// consequently no <c>CommandArgumentParseException</c> path to reproduce here (every token stream
/// parses) and, per the actual Rust source (verified — neither <c>gradlew_cmd.rs</c> nor
/// <c>mvn_cmd.rs</c> calls any <c>restore_double_dash</c> helper), no <c>--</c>-restoration step either:
/// <paramref name="args"/> is forwarded to the child process exactly as received.
/// </para>
/// </remarks>
public static class GradlewCommand
{
    private const string StacktraceFlag = "--stacktrace";
    private const string InfoFlag = "--info";
    private const string DebugFlag = "--debug";
    private const string FullStacktraceFlag = "--full-stacktrace";

    /// <summary>
    /// Registry entry point.
    /// </summary>
    /// <param name="args">The Gradle tasks and arguments following the <c>gradlew</c> verb.</param>
    /// <returns>The wrapped Gradle process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, lineFilteringExecutor: null, processExecutor: null);

    /// <summary>
    /// Test-friendly overload accepting explicit executors.
    /// </summary>
    /// <param name="args">The Gradle tasks and arguments following the <c>gradlew</c> verb.</param>
    /// <param name="lineFilteringExecutor">The streaming executor used for the build task family, or null for the default.</param>
    /// <param name="processExecutor">The buffered/passthrough process executor, or null for the default.</param>
    /// <returns>The wrapped Gradle process's exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args,
        ILineFilteringExecutor? lineFilteringExecutor,
        IProcessExecutor? processExecutor)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Verbose flags bypass filtering — user wants full output.
        if (args.Any(a => a is StacktraceFlag or InfoFlag or DebugFlag or FullStacktraceFlag))
        {
            return await RunPassthroughAsync(args, processExecutor).ConfigureAwait(false);
        }

        var tool = GradlewBinary();
        var argsDisplay = string.Join(' ', args);

        return DetectTask(args) switch
        {
            GradlewTask.Build => await RunStreamedAsync(tool, args, argsDisplay, lineFilteringExecutor).ConfigureAwait(false),
            GradlewTask.Test => await RunBufferedAsync(tool, args, argsDisplay, GradlewFilters.FilterTest, "gradlew_test").ConfigureAwait(false),
            GradlewTask.ConnectedTest => await RunBufferedAsync(tool, args, argsDisplay, GradlewFilters.FilterConnected, "gradlew_connected").ConfigureAwait(false),
            GradlewTask.Lint => await RunBufferedAsync(tool, args, argsDisplay, GradlewFilters.FilterLint, "gradlew_lint").ConfigureAwait(false),
            GradlewTask.Dependencies => await RunBufferedAsync(tool, args, argsDisplay, GradlewFilters.FilterDependencies, "gradlew_deps").ConfigureAwait(false),
            _ => await RunPassthroughAsync(args, processExecutor).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Detects which Gradle task family <paramref name="args"/> targets, driving filter selection.
    /// Faithful port of Rust's <c>detect_task</c> (<c>gradlew_cmd.rs</c>:31-64).
    /// </summary>
    /// <param name="args">The full Gradle argument list.</param>
    /// <returns>The detected task family.</returns>
    internal static GradlewTask DetectTask(IReadOnlyList<string> args)
    {
        // Use the last non-flag, non-clean task to determine the filter.
        // Example: `clean assembleDebug` -> Build (last non-clean task).
        // Note: for mixed-task invocations like `test assemble`, last wins.
        var task = string.Empty;
        foreach (var a in args)
        {
            if (a.StartsWith('-') || string.Equals(a, "clean", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            task = a.ToLowerInvariant();
        }

        if (task.Contains("connected", StringComparison.Ordinal))
        {
            return GradlewTask.ConnectedTest;
        }

        if (task.Contains("test", StringComparison.Ordinal))
        {
            return GradlewTask.Test;
        }

        if (task.Contains("assemble", StringComparison.Ordinal)
            || task.Contains("build", StringComparison.Ordinal)
            || task.Contains("bundle", StringComparison.Ordinal)
            || task.Contains("install", StringComparison.Ordinal))
        {
            return GradlewTask.Build;
        }

        if (task.Contains("lint", StringComparison.Ordinal)
            || task.Contains("ktlint", StringComparison.Ordinal)
            || task.Contains("detekt", StringComparison.Ordinal))
        {
            return GradlewTask.Lint;
        }

        if (task == "check")
        {
            return GradlewTask.Test;
        }

        if (task.Contains("dependencies", StringComparison.Ordinal))
        {
            return GradlewTask.Dependencies;
        }

        if (task.Length == 0)
        {
            // Only "clean" was passed (filtered out above) -> treat as Build to filter task noise.
            return GradlewTask.Build;
        }

        return GradlewTask.Other;
    }

    /// <summary>
    /// Returns the Gradle executable: prefers the local wrapper (<c>gradlew.bat</c>/<c>./gradlew</c>),
    /// falls back to the <c>gradle</c> binary resolved via <c>PATH</c>/<c>PATHEXT</c> at execution time
    /// (handled transparently by <see cref="PathResolver"/> — no explicit resolution needed here, unlike
    /// Rust's <c>resolved_command</c> call). Faithful port of Rust's <c>gradlew_binary</c> /
    /// <c>new_gradle_command</c> (<c>gradlew_cmd.rs</c>:67-101).
    /// </summary>
    /// <returns>The Gradle executable name or relative wrapper path.</returns>
    internal static string GradlewBinary()
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(".\\gradlew.bat") ? ".\\gradlew.bat" : "gradle";
        }

        return File.Exists("./gradlew") ? "./gradlew" : "gradle";
    }

    private static async Task<int> RunStreamedAsync(
        string tool,
        string[] args,
        string argsDisplay,
        ILineFilteringExecutor? executor)
    {
        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running: {tool} {argsDisplay}\n");
        }

        var exec = executor ?? new LineFilteringExecutor();
        var request = new ExecutionRequest(tool, args);

        var timer = TimedExecution.Start();
        var result = await exec.ExecuteAsync(request, new GradlewBuildLineFilter()).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run {tool} {argsDisplay}{detail}");
        }

        // core::runner.rs's Streamed branch only prints the tee hint line after the fact — the
        // filtered content itself was already streamed live to the console while gradle ran.
        var hint = Tee.TeeAndHint(result.Raw, "gradlew_build", result.ExitCode);
        if (hint is not null)
        {
            Console.Out.Write(hint + "\n");
        }

        var cmdLabel = $"{tool} {argsDisplay}";
        timer.Track(cmdLabel, $"rtk gradlew {argsDisplay}", result.Raw, result.Filtered);

        return result.ExitCode;
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
        var fileName = GradlewBinary();
        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest(fileName, args, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        var cmdLabel = $"gradlew {string.Join(' ', args)}".Trim();
        timer.TrackPassthrough(cmdLabel, $"rtk {cmdLabel} (passthrough)");
        return result.ExitCode;
    }
}

/// <summary>
/// The Gradle task family detected from the raw argument list, driving which filter (if any) is
/// applied. Faithful port of Rust's <c>GradlewTask</c> enum (<c>gradlew_cmd.rs</c>:21-29).
/// </summary>
public enum GradlewTask
{
    /// <summary>An assemble/build/bundle/install task — streamed through <see cref="GradlewBuildLineFilter"/>.</summary>
    Build,

    /// <summary>A unit-test task (or <c>check</c>) — buffered through <see cref="GradlewFilters.FilterTest"/>.</summary>
    Test,

    /// <summary>An instrumented/connected Android test task.</summary>
    ConnectedTest,

    /// <summary>A lint/ktlint/detekt task.</summary>
    Lint,

    /// <summary>A dependency-tree task.</summary>
    Dependencies,

    /// <summary>Any other task — passed through completely unfiltered.</summary>
    Other,
}

/// <summary>
/// Streaming line filter for the Gradle build task family: keeps a line only if
/// <see cref="GradlewFilters.FilterBuildLine"/> returns true. Faithful port of Rust's
/// <c>BuildLineFilter</c> (<c>gradlew_cmd.rs</c>:104-118).
/// </summary>
internal sealed class GradlewBuildLineFilter : IStreamFilter
{
    /// <inheritdoc />
    public string? FeedLine(string line) => GradlewFilters.FilterBuildLine(line) ? line + "\n" : null;

    /// <inheritdoc />
    public string Flush() => string.Empty;
}
