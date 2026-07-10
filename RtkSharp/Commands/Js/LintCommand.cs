using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Js;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Implements the <c>rtk lint</c> CLI verb: a multi-linter dispatcher (despite the "ESLint with
/// grouped rule violations" doc-comment/command-inventory description) that detects the linter from
/// its arguments and groups violations by rule and file. Faithful port of Rust
/// <c>src/cmds/js/lint_cmd.rs</c> for the <c>eslint</c>, <c>pylint</c>, and generic-fallback paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope cut: <c>ruff</c>/<c>mypy</c> delegate to unported Python-ecosystem modules — not faked,
/// disclosed instead.</b> Rust's <c>lint_cmd.rs</c> imports <c>crate::ruff_cmd</c>/<c>crate::mypy_cmd</c>
/// and calls <c>ruff_cmd::filter_ruff_check_json</c>/<c>mypy_cmd::filter_mypy_output</c> directly for
/// those two linters — neither module exists anywhere in RtkSharp yet (the Python ecosystem hasn't
/// been touched by any phase), the same class of hidden dependency <c>rtk session</c> had on
/// <c>discover</c>. Rather than fabricate a plausible-looking ruff/mypy grouped-output format this
/// port has never verified against the real filters, <see cref="RunCoreAsync"/> still resolves and
/// invokes <c>ruff</c>/<c>mypy</c> with the correct flags (that part of <c>run()</c> is generic and
/// has no Python-specific dependency), but prints the RAW combined stdout+stderr unfiltered for those
/// two linter names — an honest "not grouped yet" passthrough rather than a silently wrong grouping,
/// matching the Phase 8 "genuinely unimplemented ecosystem, don't fake it" convention applied to
/// <c>next</c>/<c>prettier</c>/<c>biome</c>. See <c>docs/parity/compatibility-ledger.md</c>.
/// </para>
/// <para>
/// <b>Preserved Rust-source oddity: the "not installed" hint always suggests <c>pip install</c>, even
/// for JS linters.</b> Rust's error context string (<c>lint_cmd.rs</c>:167-170) is
/// <c>"Failed to run {linter}. Is it installed? Try: pip install {linter} (or npm/pnpm for JS linters)"</c>
/// for every linter, including <c>eslint</c> — a copy-paste artifact from the Python-linter case,
/// preserved verbatim rather than "fixed" to suggest the right installer per linter.
/// </para>
/// </remarks>
public static class LintCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk lint</c> with the given arguments (the remainder after the
    /// <c>lint</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>lint</c>.</param>
    /// <returns>The linter's real exit code, or 1 on an rtk-level failure.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor());

    /// <summary>
    /// Runs <c>rtk lint</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>lint</c>.</param>
    /// <param name="verbose">The top-level verbosity count (Rust's <c>cli.verbose</c>).</param>
    /// <param name="executor">The process executor to spawn the linter with.</param>
    /// <returns>The linter's real exit code, or 1 on an rtk-level failure.</returns>
    public static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor executor)
    {
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
    /// The testable core of <see cref="RunAsync(string[],int,IProcessExecutor)"/>. Faithful port of
    /// <c>run</c> (<c>lint_cmd.rs</c>:90-220).
    /// </summary>
    /// <param name="args">The arguments following the <c>lint</c> verb.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn the linter with.</param>
    /// <returns>The linter's real exit code.</returns>
    internal static async Task<int> RunCoreAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var skip = StripPmPrefix(args);
        var effectiveArgs = args[skip..];
        var (linter, isExplicit) = DetectLinter(effectiveArgs);

        // Python linters use resolved_command() directly (they're on PATH via pip/pipx); JS linters
        // use package_manager_exec (npx/pnpm exec).
        string fileName;
        var cmdArgs = new List<string>();
        if (IsPythonLinter(linter))
        {
            fileName = PathResolver.Resolve(linter);
        }
        else
        {
            var pm = PackageManagerDetection.PackageManagerExec(linter);
            fileName = pm.FileName;
            cmdArgs.AddRange(pm.BaseArguments);
        }

        // Format flags per linter.
        if (linter == "eslint")
        {
            cmdArgs.Add("-f");
            cmdArgs.Add("json");
        }
        else if (linter == "ruff" && !effectiveArgs.Contains("--output-format"))
        {
            cmdArgs.Add("check");
            cmdArgs.Add("--output-format=json");
        }
        else if (linter == "pylint" && !effectiveArgs.Contains("--output-format"))
        {
            cmdArgs.Add("--output-format=json2");
        }

        // Determine the start index into effectiveArgs for the user's own arguments (skipping the
        // linter-name token, and "ruff check" if we already injected "check").
        int startIdx;
        if (!isExplicit)
        {
            startIdx = 0;
        }
        else if (linter == "ruff" && effectiveArgs.Length > 0 && effectiveArgs[0] == "ruff")
        {
            startIdx = effectiveArgs.Length > 1 && effectiveArgs[1] == "check" ? 2 : 1;
        }
        else
        {
            startIdx = 1;
        }

        for (var i = startIdx; i < effectiveArgs.Length; i++)
        {
            var arg = effectiveArgs[i];
            if ((linter == "ruff" || linter == "pylint") && arg.StartsWith("--output-format", StringComparison.Ordinal))
            {
                continue;
            }

            cmdArgs.Add(arg);
        }

        // Default to current directory if no path specified.
        if (linter is "ruff" or "pylint" or "mypy" or "eslint")
        {
            var hasPath = effectiveArgs.Skip(startIdx).Any(a => !a.StartsWith('-') && !a.Contains('='));
            if (!hasPath)
            {
                cmdArgs.Add(".");
            }
        }

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {linter} with structured output\n");
        }

        ExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(new ExecutionRequest(fileName, cmdArgs)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Failed to run {linter}. Is it installed? Try: pip install {linter} (or npm/pnpm for JS linters): {ex.Message}", ex);
        }

        if (!result.WasStarted)
        {
            throw new IOException(
                $"Failed to run {linter}. Is it installed? Try: pip install {linter} (or npm/pnpm for JS linters)"
                + (result.Failure is { } f ? $": {f}" : string.Empty));
        }

        // A process killed by a signal (SIGABRT, SIGKILL, ...) reports an exit code above 128 on Unix;
        // on Windows this is a defensive-only branch (no signal-based termination), preserved for
        // parity with the Rust source rather than made Windows-conditional.
        if (result.ExitCode != 0 && result.ExitCode > 128)
        {
            Console.Error.Write("[warn] Linter process terminated abnormally (possibly out of memory)\n");
            if (!string.IsNullOrEmpty(result.Stderr))
            {
                var lines = ReadCommand.SplitLines(result.Stderr).Take(5);
                Console.Error.Write($"stderr: {string.Join('\n', lines)}\n");
            }

            return result.ExitCode;
        }

        var raw = $"{result.Stdout}\n{result.Stderr}";

        var filtered = linter switch
        {
            "eslint" => FilterEslintJson(result.Stdout),
            "pylint" => FilterPylintJson(result.Stdout),
            // ruff/mypy: unfiltered passthrough — see class remarks on the disclosed scope cut.
            "ruff" or "mypy" => raw.Trim(),
            _ => FilterGenericLint(raw),
        };

        var hint = Tee.TeeAndHint(raw, "lint", result.ExitCode);
        Console.Out.Write(hint is not null ? $"{filtered}\n{hint}\n" : $"{filtered}\n");

        var argsJoined = string.Join(' ', args);
        timer.Track($"{linter} {argsJoined}", $"rtk lint {linter} {argsJoined}", raw, filtered);

        return result.ExitCode;
    }

    /// <summary>
    /// Checks if a linter name is Python-based (uses pip/pipx, resolved directly via <c>PATH</c>, not
    /// npm/pnpm). Faithful port of <c>is_python_linter</c> (<c>lint_cmd.rs</c>:56-58).
    /// </summary>
    /// <param name="linter">The linter name to check.</param>
    /// <returns><see langword="true"/> for <c>ruff</c>/<c>pylint</c>/<c>mypy</c>/<c>flake8</c>.</returns>
    internal static bool IsPythonLinter(string linter) => linter is "ruff" or "pylint" or "mypy" or "flake8";

    /// <summary>
    /// Counts how many leading tokens are a package-manager wrapper prefix (<c>npx</c>, <c>bunx</c>,
    /// <c>pnpm</c>, <c>yarn</c>, <c>exec</c>) to skip. Faithful port of <c>strip_pm_prefix</c>
    /// (<c>lint_cmd.rs</c>:62-73).
    /// </summary>
    /// <param name="args">The raw arguments following the <c>lint</c> verb.</param>
    /// <returns>The number of leading tokens to skip.</returns>
    internal static int StripPmPrefix(IReadOnlyList<string> args)
    {
        string[] pmNames = ["npx", "bunx", "pnpm", "yarn"];
        var skip = 0;
        foreach (var arg in args)
        {
            if (pmNames.Contains(arg) || arg == "exec")
            {
                skip++;
            }
            else
            {
                break;
            }
        }

        return skip;
    }

    /// <summary>
    /// Detects the linter name from arguments (after <see cref="StripPmPrefix"/>). Faithful port of
    /// <c>detect_linter</c> (<c>lint_cmd.rs</c>:77-88).
    /// </summary>
    /// <param name="args">The arguments after stripping any package-manager prefix.</param>
    /// <returns>The linter name and whether it was explicitly specified (vs. defaulted to <c>eslint</c>).</returns>
    internal static (string Linter, bool IsExplicit) DetectLinter(IReadOnlyList<string> args)
    {
        var isPathOrFlag = args.Count == 0 || args[0].StartsWith('-') || args[0].Contains('/') || args[0].Contains('.');

        return isPathOrFlag ? ("eslint", false) : (args[0], true);
    }

    /// <summary>
    /// Groups ESLint JSON output (<c>-f json</c>) by rule and by file. Delegates to
    /// <see cref="LintFilters.FilterEslintJson"/> (moved to <c>RtkSharp.Filters</c> in Task 7 of the
    /// filters-library extraction — pure text/JSON filtering, no process execution or file I/O).
    /// </summary>
    /// <param name="output">The raw ESLint JSON stdout.</param>
    /// <returns>The grouped summary text.</returns>
    internal static string FilterEslintJson(string output) => LintFilters.FilterEslintJson(output);

    /// <summary>
    /// Groups Pylint JSON2 output by symbol and by file. Delegates to
    /// <see cref="LintFilters.FilterPylintJson"/>.
    /// </summary>
    /// <param name="output">The raw Pylint JSON2 stdout.</param>
    /// <returns>The grouped summary text.</returns>
    internal static string FilterPylintJson(string output) => LintFilters.FilterPylintJson(output);

    /// <summary>
    /// Fallback for non-JSON linters: a naive line-scan counting "warning"/"error" substrings.
    /// Delegates to <see cref="LintFilters.FilterGenericLint"/>.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr.</param>
    /// <returns>The summarized issue count and (capped) issue lines.</returns>
    internal static string FilterGenericLint(string output) => LintFilters.FilterGenericLint(output);

    /// <summary>
    /// Shortens a file path by keeping only the portion from the last <c>src/</c>/<c>lib/</c> segment
    /// onward, or just the file name if neither is present. Delegates to
    /// <see cref="LintFilters.CompactPath"/>.
    /// </summary>
    /// <param name="path">The file path to shorten.</param>
    /// <returns>The shortened path.</returns>
    internal static string CompactPath(string path) => LintFilters.CompactPath(path);
}
