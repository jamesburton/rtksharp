using System.Linq;
using System.Text;
using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Implements the <c>rtk prettier</c> CLI verb: runs Prettier (resolved directly, or via the detected
/// package manager's exec mechanism) and condenses its output down to a "files needing formatting"
/// summary. Faithful port of <c>src/cmds/js/prettier_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="FilterPrettierOutput"/> is <c>internal</c>, not private — it is a shared dependency.</b>
/// Rust's <c>filter_prettier_output</c> is declared <c>pub</c> specifically so <c>format_cmd.rs</c> can
/// call it directly for the <c>"prettier"</c> branch of its own formatter dispatch
/// (<c>format_cmd.rs</c>:104), rather than duplicating the filtering logic. This port preserves that
/// cross-module reuse: <see cref="RtkSharp.Commands.System.FormatCommand"/> calls this exact method
/// rather than reimplementing prettier output filtering — see that class's remarks for the full
/// shared-logic rationale required by this phase's "don't duplicate within your own new files"
/// constraint.
/// </para>
/// <para>
/// <b><c>FilterStdoutOnly: true</c> — deliberately different from <c>next</c>'s default options.</b>
/// Rust's <c>run()</c> (<c>prettier_cmd.rs</c>:8-20) passes <c>RunOptions::stdout_only()</c> to
/// <c>run_filtered</c>, meaning only captured stdout (not stdout+stderr combined) is fed to the filter —
/// unlike <see cref="NextCommand"/>, which uses the default (stdout+stderr combined). This port matches
/// via <c>new RunOptions(FilterStdoutOnly: true)</c>.
/// </para>
/// <para>
/// <b>#221 regression guard: empty/whitespace-only output is an error, never "all formatted".</b> Rust's
/// leading check (<c>prettier_cmd.rs</c>:23-25) returns the literal string
/// <c>"Error: prettier produced no output"</c> whenever the raw output is empty or all-whitespace —
/// guarding against a silently-broken Prettier invocation being misreported as a clean pass. Ported
/// verbatim as the very first check in <see cref="FilterPrettierOutput"/>.
/// </para>
/// <para>
/// <b>Preserved Rust-source latent bug: write-mode's file count is always <c>filesToFormat</c>'s
/// count, which the extension-scan logic doesn't populate for "modified"/write-mode output lines.</b>
/// When <c>is_check_mode</c> flips to <c>false</c> (raw output contains <c>"modified"</c> or
/// <c>"formatted"</c>), Rust's write-mode branch (<c>prettier_cmd.rs</c>:87-91) still reports
/// <c>files_to_format.len()</c> — the same list built by the check-mode extension-matching logic, which
/// has no write-mode-specific line handling. In practice this means a real <c>prettier --write</c>
/// invocation is likely to report <c>"0 files formatted"</c> even when files were changed. This looks
/// like an incomplete/unfinished feature in the Rust source, not a deliberate design choice, but it is
/// preserved exactly rather than "fixed" — this port does not add write-mode-specific detection Rust
/// itself never implemented.
/// </para>
/// <para>
/// <b>Preserved Rust-source latent bug: the "already formatted" count can go negative.</b>
/// <c>files_checked - files_to_format.len()</c> (<c>prettier_cmd.rs</c>:79-84) is <c>usize</c> arithmetic
/// in Rust, which panics on underflow in debug builds if <c>files_checked &lt; files_to_format.len()</c>
/// (parsing a mismatched leading integer from prettier's own summary text is plausible). This port uses
/// C#'s signed <see cref="int"/> instead, so the equivalent case produces a nonsensical negative count
/// rather than crashing rtk over a display-only computation — a deliberate, disclosed divergence from a
/// literal panic-for-panic port, since crashing the whole command over a cosmetic count is far worse
/// than the Rust oracle's own worst real-world case.
/// </para>
/// </remarks>
public static class PrettierCommand
{
    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs:7</c>) — the per-report file cap (<c>MAX_PRETTIER_FILES</c>, <c>prettier_cmd.rs</c>:60).</summary>
    private const int CapWarnings = 10;

    /// <summary>
    /// Registry entry point for the <c>prettier</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/>
    /// (the registry delegate cannot receive it as an argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>prettier</c> verb.</param>
    /// <returns>Prettier's exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunPrettierSafeAsync(args, RuntimeOptions.Verbosity);

    /// <summary>
    /// Test-friendly overload of the <c>prettier</c> entry point taking an explicit verbosity value
    /// instead of reading <see cref="RuntimeOptions"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>prettier</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <returns>Prettier's exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunPrettierSafeAsync(string[] args, int verbose)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await ExecuteAsync(args, verbose).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Fail-loud, same convention as TscCommand/NpmCommand: an rtk-level failure surfaces as
            // `rtk: {message}`. Guarded proactively, not thrown from this command today — see
            // DockerCommand/PrismaCommand's remarks for the swallowed-exception bug this prevents.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Resolves <c>prettier</c> (directly, or via the detected package manager's exec mechanism), runs
    /// it with <paramref name="args"/> appended verbatim through
    /// <see cref="CommandRunner.RunFilteredAsync"/> with <see cref="FilterPrettierOutput"/>, and returns
    /// the child's exit code. Ports the body of Rust's <c>run()</c> (<c>prettier_cmd.rs</c>:8-20).
    /// </summary>
    /// <param name="args">The arguments following the <c>prettier</c> verb.</param>
    /// <param name="verbose">The verbosity level; a nonzero value logs the arguments being run.</param>
    /// <returns>Prettier's exit code.</returns>
    internal static Task<int> ExecuteAsync(string[] args, int verbose)
    {
        var pm = PackageManagerDetection.PackageManagerExec("prettier");
        var cmdArgs = new List<string>(pm.BaseArguments);
        cmdArgs.AddRange(args);

        var argsDisplay = string.Join(' ', args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: prettier {argsDisplay}\n");
        }

        return CommandRunner.RunFilteredAsync(
            pm.FileName,
            cmdArgs,
            "prettier",
            argsDisplay,
            FilterPrettierOutput,
            new RunOptions(FilterStdoutOnly: true)
        );
    }

    /// <summary>
    /// Filters Prettier output down to a "files needing formatting" summary (check mode) or a
    /// "files formatted" count (write mode). Faithful port of <c>filter_prettier_output</c>
    /// (<c>prettier_cmd.rs</c>:22-95). <c>internal</c>, not <c>private</c>, because
    /// <see cref="RtkSharp.Commands.System.FormatCommand"/> calls this directly for its own
    /// <c>"prettier"</c> dispatch branch — see class remarks.
    /// </summary>
    /// <param name="output">The raw Prettier stdout (per Rust's <c>stdout_only()</c> capture mode) to filter.</param>
    /// <returns>The condensed summary.</returns>
    internal static string FilterPrettierOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // #221: empty or whitespace-only output means prettier didn't run.
        if (output.Trim().Length == 0)
        {
            return "Error: prettier produced no output";
        }

        var filesToFormat = new List<string>();
        var filesChecked = 0;
        var isCheckMode = true;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            var trimmed = line.Trim();

            // Detect check mode vs write mode.
            if (trimmed.Contains("Checking formatting", StringComparison.Ordinal))
            {
                isCheckMode = true;
            }

            // Count files that need formatting (check mode).
            if (trimmed.Length > 0
                && !trimmed.StartsWith("Checking", StringComparison.Ordinal)
                && !trimmed.StartsWith("All matched", StringComparison.Ordinal)
                && !trimmed.StartsWith("Code style", StringComparison.Ordinal)
                && !trimmed.Contains("[warn]", StringComparison.Ordinal)
                && !trimmed.Contains("[error]", StringComparison.Ordinal)
                && (trimmed.EndsWith(".ts", StringComparison.Ordinal)
                    || trimmed.EndsWith(".tsx", StringComparison.Ordinal)
                    || trimmed.EndsWith(".js", StringComparison.Ordinal)
                    || trimmed.EndsWith(".jsx", StringComparison.Ordinal)
                    || trimmed.EndsWith(".json", StringComparison.Ordinal)
                    || trimmed.EndsWith(".md", StringComparison.Ordinal)
                    || trimmed.EndsWith(".css", StringComparison.Ordinal)
                    || trimmed.EndsWith(".scss", StringComparison.Ordinal)))
            {
                filesToFormat.Add(trimmed);
            }

            // Count total files checked.
            if (trimmed.Contains("All matched files use Prettier", StringComparison.Ordinal))
            {
                var firstToken = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstToken is not null && int.TryParse(firstToken, out var count))
                {
                    filesChecked = count;
                }
            }
        }

        // Check if all files are formatted.
        if (filesToFormat.Count == 0 && output.Contains("All matched files use Prettier", StringComparison.Ordinal))
        {
            return "Prettier: All files formatted correctly";
        }

        // Check if files were written (write mode).
        if (output.Contains("modified", StringComparison.Ordinal) || output.Contains("formatted", StringComparison.Ordinal))
        {
            isCheckMode = false;
        }

        var result = new StringBuilder();

        if (isCheckMode)
        {
            // Check mode: show files that need formatting.
            if (filesToFormat.Count == 0)
            {
                result.Append("Prettier: All files formatted correctly\n");
            }
            else
            {
                result.Append($"Prettier: {filesToFormat.Count} files need formatting\n");

                var index = 0;
                foreach (var file in filesToFormat.Take(CapWarnings))
                {
                    index++;
                    result.Append($"{index}. {file}\n");
                }

                if (filesToFormat.Count > CapWarnings)
                {
                    result.Append($"\n... +{filesToFormat.Count - CapWarnings} more files\n");
                }

                if (filesChecked > 0)
                {
                    // See class remarks: this can go negative if files_checked was parsed smaller than
                    // files_to_format.Count - preserved as a disclosed divergence from Rust's usize
                    // underflow panic, not "fixed" toward clamping.
                    result.Append($"\n{filesChecked - filesToFormat.Count} files already formatted\n");
                }
            }
        }
        else
        {
            // Write mode: show what was formatted.
            result.Append($"Prettier: {filesToFormat.Count} files formatted\n");
        }

        return result.ToString().Trim();
    }
}
