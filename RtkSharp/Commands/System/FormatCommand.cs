using System.Linq;
using System.Text;
using RtkSharp.Cli;
using RtkSharp.Commands.Js;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

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
    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs:7</c>) — the black-formatter per-report file cap (<c>MAX_FORMAT_FILES</c>, <c>format_cmd.rs</c>:190).</summary>
    private const int CapWarnings = 10;

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
            "black" => FilterBlackOutput(raw),
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

    /// <summary>
    /// Filters Black output down to a "files needing formatting" summary. Faithful port of
    /// <c>filter_black_output</c> (<c>format_cmd.rs</c>:129-231).
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>black --check</c>.</param>
    /// <returns>The condensed summary.</returns>
    internal static string FilterBlackOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var filesToFormat = new List<string>();
        var filesUnchanged = 0;
        var filesWouldReformat = 0;
        var allDone = false;
        var ohNo = false;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var trimmed = line.Trim();
            var lower = trimmed.ToLowerInvariant();

            // Check for "would reformat" lines.
            if (lower.StartsWith("would reformat:", StringComparison.Ordinal))
            {
                // Extract filename from "would reformat: path/to/file.py" - splits on the FIRST ':'
                // only, matching Rust's `trimmed.split(':').nth(1)` (a Windows path like "C:\foo.py"
                // would be mis-split here too - preserved faithfully, not "fixed").
                var parts = trimmed.Split(':');
                if (parts.Length > 1)
                {
                    filesToFormat.Add(parts[1].Trim());
                }
            }

            // Parse summary line like "2 files would be reformatted, 3 files would be left unchanged."
            if (lower.Contains("would be reformatted", StringComparison.Ordinal) || lower.Contains("would be left unchanged", StringComparison.Ordinal))
            {
                // Split by comma to handle both parts.
                foreach (var part in trimmed.Split(','))
                {
                    var partLower = part.ToLowerInvariant();
                    var words = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                    if (partLower.Contains("would be reformatted", StringComparison.Ordinal))
                    {
                        // Parse "X file(s) would be reformatted".
                        for (var i = 0; i < words.Length; i++)
                        {
                            if ((words[i] == "file" || words[i] == "files") && i > 0 && int.TryParse(words[i - 1], out var count))
                            {
                                filesWouldReformat = count;
                                break;
                            }
                        }
                    }

                    if (partLower.Contains("would be left unchanged", StringComparison.Ordinal))
                    {
                        // Parse "X file(s) would be left unchanged".
                        for (var i = 0; i < words.Length; i++)
                        {
                            if ((words[i] == "file" || words[i] == "files") && i > 0 && int.TryParse(words[i - 1], out var count))
                            {
                                filesUnchanged = count;
                                break;
                            }
                        }
                    }
                }
            }

            // Check for "left unchanged" (standalone).
            if (lower.Contains("left unchanged", StringComparison.Ordinal) && !lower.Contains("would be", StringComparison.Ordinal))
            {
                var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < words.Length; i++)
                {
                    if ((words[i] == "file" || words[i] == "files") && i > 0 && int.TryParse(words[i - 1], out var count))
                    {
                        filesUnchanged = count;
                        break;
                    }
                }
            }

            // Check for success/failure indicators.
            if (lower.Contains("all done!", StringComparison.Ordinal) || lower.Contains("all done ✨", StringComparison.Ordinal))
            {
                allDone = true;
            }

            if (lower.Contains("oh no!", StringComparison.Ordinal))
            {
                ohNo = true;
            }
        }

        // Build output.
        var result = new StringBuilder();

        // Determine if all files are formatted.
        var needsFormatting = filesToFormat.Count > 0 || filesWouldReformat > 0 || ohNo;

        if (!needsFormatting && (allDone || filesUnchanged > 0))
        {
            // All files formatted correctly.
            result.Append("Format (black): All files formatted");
            if (filesUnchanged > 0)
            {
                result.Append($" ({filesUnchanged} files checked)");
            }
        }
        else if (needsFormatting)
        {
            // Files need formatting.
            var count = filesToFormat.Count > 0 ? filesToFormat.Count : filesWouldReformat;

            result.Append($"Format (black): {count} files need formatting\n");

            if (filesToFormat.Count > 0)
            {
                var index = 0;
                foreach (var file in filesToFormat.Take(CapWarnings))
                {
                    index++;
                    result.Append($"{index}. {CompactPath(file)}\n");
                }

                if (filesToFormat.Count > CapWarnings)
                {
                    result.Append($"\n... +{filesToFormat.Count - CapWarnings} more files\n");
                }
            }

            if (filesUnchanged > 0)
            {
                result.Append($"\n{filesUnchanged} files already formatted\n");
            }

            result.Append("\n[hint] Run `black .` to format these files\n");
        }
        else
        {
            // Fallback: show raw output.
            result.Append(output.Trim());
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Shortens a file path by keeping only the portion from the last <c>src/</c>/<c>lib/</c>/
    /// <c>tests/</c> segment onward, or just the file name if none is present. Faithful port of
    /// <c>compact_path</c> (<c>format_cmd.rs</c>:234-247).
    /// </summary>
    /// <param name="path">The file path to shorten.</param>
    /// <returns>The shortened path.</returns>
    internal static string CompactPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.Replace('\\', '/');

        var srcPos = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
        if (srcPos >= 0)
        {
            return "src/" + normalized[(srcPos + 5)..];
        }

        var libPos = normalized.LastIndexOf("/lib/", StringComparison.Ordinal);
        if (libPos >= 0)
        {
            return "lib/" + normalized[(libPos + 5)..];
        }

        var testsPos = normalized.LastIndexOf("/tests/", StringComparison.Ordinal);
        if (testsPos >= 0)
        {
            return "tests/" + normalized[(testsPos + 7)..];
        }

        var slashPos = normalized.LastIndexOf('/');
        return slashPos >= 0 ? normalized[(slashPos + 1)..] : normalized;
    }
}
