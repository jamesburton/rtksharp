using System.Linq;
using RtkSharp.Cli;
using RtkSharp.Commands.Js;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk format</c> CLI verb: a multi-formatter dispatcher that auto-detects Prettier,
/// Black, Ruff, or Biome from an explicit argument or the project's own files, runs it, and condenses
/// its output. Faithful port of <c>src/cmds/system/format_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reuses <see cref="PrettierCommand.FilterPrettierOutput"/> rather than duplicating it.</b> Rust's
/// <c>format_cmd.rs</c> imports <c>crate::prettier_cmd</c> and calls
/// <c>prettier_cmd::filter_prettier_output(&amp;raw)</c> directly for the <c>"prettier"</c> dispatch
/// branch (<c>format_cmd.rs</c>:104) — the exact same function Rust's own <c>rtk prettier</c> command
/// uses. This port calls the identical <see cref="PrettierCommand.FilterPrettierOutput"/> method (marked
/// <c>internal</c> specifically for this cross-command reuse) rather than reimplementing prettier output
/// filtering a second time, matching the phase's "shared logic lives in one new file, referenced by the
/// other" requirement without editing any pre-existing file.
/// </para>
/// <para>
/// <b>Scope cut: <c>ruff format</c> delegates to an unported Python-ecosystem module — not faked,
/// disclosed instead.</b> Rust's <c>format_cmd.rs</c> imports <c>crate::ruff_cmd</c> and calls
/// <c>ruff_cmd::filter_ruff_format(&amp;raw)</c> directly for the <c>"ruff"</c> branch
/// (<c>format_cmd.rs</c>:105) — that module does not exist anywhere in RtkSharp yet (the Python
/// ecosystem hasn't been ported by any phase), the same class of hidden dependency
/// <see cref="RtkSharp.Commands.Js.LintCommand"/> discloses for its own <c>ruff</c>/<c>mypy</c> branches.
/// Rather than fabricate a plausible-looking ruff-format summary this port has never verified against
/// the real filter, <see cref="RunCoreAsync"/> still resolves and invokes <c>ruff format</c> with the
/// correct flags (that part of <c>run()</c> is generic and has no Python-specific dependency), but falls
/// through to the wildcard <c>raw.Trim()</c> passthrough branch for it — an honest "not summarized yet"
/// passthrough rather than a silently wrong summary, matching the Phase 8 "genuinely unimplemented
/// ecosystem, don't fake it" convention. This is also exactly what Rust's own wildcard <c>_ =>
/// raw.trim().to_string()</c> arm (<c>format_cmd.rs</c>:106) does for any formatter it doesn't
/// specifically recognize, so <c>"ruff"</c> falling into that same generic path here is a faithful,
/// disclosed narrowing rather than a divergence in shape.
/// </para>
/// <para>
/// <b>Not in the Rust integrity/hook-pipeline whitelist — unlike <c>next</c>/<c>prettier</c>.</b>
/// <c>main.rs</c>'s <c>is_operational_command</c> lists <c>Commands::Next</c> and
/// <c>Commands::Prettier</c> but conspicuously omits <c>Commands::Format</c> — meaning <c>rtk format</c>
/// is treated as a non-operational/meta command for integrity-check purposes in Rust (no SHA-256 hook
/// verification gate applied to it). That whitelist lives in dispatch/registry wiring reserved for the
/// orchestrating session, not in this file, but is called out here so the eventual wiring preserves the
/// asymmetry rather than "normalizing" all three commands identically.
/// </para>
/// <para>
/// <b>Manual <see cref="TimedExecution"/>, not <see cref="CommandRunner"/>.</b> Rust's <c>run()</c>
/// (<c>format_cmd.rs</c>:52-116) calls <c>tracking::TimedExecution::start()</c>/<c>.track(...)</c>
/// directly and executes via <c>core::stream::exec_capture</c> rather than
/// <c>core::runner::run_filtered</c> — there is no tee-to-disk hint on this path in Rust, and the
/// dispatch-per-formatter filter selection has no single common <c>Func&lt;string, string&gt;</c> shape
/// the shared <see cref="CommandRunner"/> skeleton was designed around (Rust computes <c>raw</c> and the
/// filter dispatch inline in <c>run()</c> itself, matching <see cref="PnpmCommand"/>'s established
/// manual-tracking convention for the same reason).
/// </para>
/// </remarks>
public static class FormatCommand
{
    /// <summary>
    /// Registry entry point for the <c>format</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/> (the
    /// registry delegate cannot receive it as an argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>format</c> verb.</param>
    /// <returns>The resolved formatter's exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunFormatSafeAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor());

    /// <summary>
    /// Test-friendly overload of the <c>format</c> entry point taking an explicit verbosity value and an
    /// injectable <see cref="IProcessExecutor"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>format</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <param name="executor">The process executor to spawn the resolved formatter with.</param>
    /// <returns>The resolved formatter's exit code (or 1 on an rtk-level failure).</returns>
    public static async Task<int> RunFormatSafeAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await RunCoreAsync(args, verbose, executor).ConfigureAwait(false);
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
    /// The testable core of <see cref="RunFormatSafeAsync"/>. Faithful port of <c>run</c>
    /// (<c>format_cmd.rs</c>:52-116).
    /// </summary>
    /// <param name="args">The arguments following the <c>format</c> verb.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to spawn the resolved formatter with.</param>
    /// <returns>The resolved formatter's exit code.</returns>
    internal static async Task<int> RunCoreAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var formatter = DetectFormatter(args);

        // Determine start index for actual arguments: skip the formatter name if it was explicitly
        // provided, otherwise use all args (formatter was auto-detected).
        var startIdx = args.Length > 0 && args[0] == formatter ? 1 : 0;
        var userArgs = args[startIdx..];

        if (verbose > 0)
        {
            Console.Error.Write($"Detected formatter: {formatter}\n");
            Console.Error.Write($"Arguments: {string.Join(' ', userArgs)}\n");
        }

        // Build command based on formatter.
        string fileName;
        var cmdArgs = new List<string>();

        switch (formatter)
        {
            case "prettier":
                {
                    var pm = PackageManagerDetection.PackageManagerExec("prettier");
                    fileName = pm.FileName;
                    cmdArgs.AddRange(pm.BaseArguments);
                    break;
                }

            case "biome":
                {
                    var pm = PackageManagerDetection.PackageManagerExec("biome");
                    fileName = pm.FileName;
                    cmdArgs.AddRange(pm.BaseArguments);
                    break;
                }

            // "black" | "ruff" | anything else (Rust's wildcard `_` arm): resolved_command(formatter).
            default:
                fileName = PathResolver.Resolve(formatter);
                break;
        }

        // Add formatter-specific flags (injected before user args, matching Rust's ordering).
        if (formatter == "black" && !userArgs.Any(a => a is "--check" or "--diff"))
        {
            // Inject --check if not present for check mode.
            cmdArgs.Add("--check");
        }
        else if (formatter == "ruff" && (userArgs.Length == 0 || !userArgs[0].StartsWith("format", StringComparison.Ordinal)))
        {
            // Add "format" subcommand if not present.
            cmdArgs.Add("format");
        }

        // Add user arguments.
        cmdArgs.AddRange(userArgs);

        // Default to current directory if no path specified.
        if (userArgs.All(a => a.StartsWith('-')))
        {
            cmdArgs.Add(".");
        }

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {formatter} {string.Join(' ', userArgs)}\n");
        }

        var request = new ExecutionRequest(fileName, cmdArgs, CaptureMode: ExecutionCaptureMode.Separate);
        ExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Failed to run {formatter}. Is it installed? Try: pip install {formatter} (or npm/pnpm for JS formatters): {ex.Message}", ex);
        }

        if (!result.WasStarted)
        {
            throw new IOException(
                $"Failed to run {formatter}. Is it installed? Try: pip install {formatter} (or npm/pnpm for JS formatters)"
                + (result.Failure is { } f ? $": {f}" : string.Empty));
        }

        var raw = $"{result.Stdout}\n{result.Stderr}";

        // Dispatch to the appropriate filter based on formatter.
        var filtered = formatter switch
        {
            "prettier" => PrettierCommand.FilterPrettierOutput(raw),
            "black" => FormatFilters.FilterBlackOutput(raw),
            // "ruff" (unported Python ecosystem) and any other formatter: unfiltered passthrough,
            // matching Rust's own wildcard `_ => raw.trim().to_string()` arm - see class remarks.
            _ => raw.Trim(),
        };

        Console.Out.Write(filtered + "\n");

        var userArgsJoined = string.Join(' ', userArgs);
        timer.Track($"{formatter} {userArgsJoined}", $"rtk format {formatter} {userArgsJoined}", raw, filtered);

        return result.ExitCode;
    }

    /// <summary>
    /// Detects the formatter from an explicit first argument or the current directory's project files.
    /// Faithful port of <c>detect_formatter</c> (<c>format_cmd.rs</c>:10-12).
    /// </summary>
    /// <param name="args">The raw arguments following the <c>format</c> verb.</param>
    /// <returns>The detected formatter name.</returns>
    internal static string DetectFormatter(string[] args) => DetectFormatterInDir(args, ".");

    /// <summary>
    /// Detects the formatter from an explicit first argument or <paramref name="directory"/>'s project
    /// files, for deterministic testing without mutating the process's current directory. Faithful port
    /// of <c>detect_formatter_in_dir</c> (<c>format_cmd.rs</c>:15-46).
    /// </summary>
    /// <param name="args">The raw arguments following the <c>format</c> verb.</param>
    /// <param name="directory">The directory to check for project marker files.</param>
    /// <returns>The detected formatter name.</returns>
    internal static string DetectFormatterInDir(string[] args, string directory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(directory);

        // Check if first arg is a known formatter.
        if (args.Length > 0 && args[0] is "prettier" or "black" or "ruff" or "biome")
        {
            return args[0];
        }

        // Auto-detect from project files. Priority: pyproject.toml > package.json > fallback.
        var pyprojectPath = Path.Combine(directory, "pyproject.toml");
        if (File.Exists(pyprojectPath))
        {
            string content;
            try
            {
                content = File.ReadAllText(pyprojectPath);
            }
            catch (IOException)
            {
                content = string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                content = string.Empty;
            }

            // Check for [tool.black] section.
            if (content.Contains("[tool.black]", StringComparison.Ordinal))
            {
                return "black";
            }

            // Check for [tool.ruff.format] section.
            if (content.Contains("[tool.ruff.format]", StringComparison.Ordinal) || content.Contains("[tool.ruff]", StringComparison.Ordinal))
            {
                return "ruff";
            }
        }

        // Check for package.json or prettier config.
        if (File.Exists(Path.Combine(directory, "package.json"))
            || File.Exists(Path.Combine(directory, ".prettierrc"))
            || File.Exists(Path.Combine(directory, ".prettierrc.json"))
            || File.Exists(Path.Combine(directory, ".prettierrc.js")))
        {
            return "prettier";
        }

        // Fallback: try ruff -> black -> prettier in order.
        return "ruff";
    }

}
