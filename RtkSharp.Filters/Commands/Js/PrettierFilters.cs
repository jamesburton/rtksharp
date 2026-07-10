using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic for the <c>prettier</c> CLI proxy. Extracted from
/// <c>RtkSharp.Commands.Js.PrettierCommand</c> (Task 7 of the filters-library extraction) —
/// everything here is a pure function of already-captured text, with no process execution or file
/// I/O.
/// </summary>
/// <remarks>
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
/// has no write-mode-specific line handling. This is preserved exactly rather than "fixed" — this port
/// does not add write-mode-specific detection Rust itself never implemented.
/// </para>
/// <para>
/// <b>Preserved Rust-source latent bug: the "already formatted" count can go negative.</b>
/// <c>files_checked - files_to_format.len()</c> (<c>prettier_cmd.rs</c>:79-84) is <c>usize</c> arithmetic
/// in Rust, which panics on underflow in debug builds if <c>files_checked &lt; files_to_format.len()</c>.
/// This port uses C#'s signed <see cref="int"/> instead, so the equivalent case produces a nonsensical
/// negative count rather than crashing rtk over a display-only computation — a deliberate, disclosed
/// divergence from a literal panic-for-panic port.
/// </para>
/// </remarks>
public static class PrettierFilters
{
    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs:7</c>) — the per-report file cap (<c>MAX_PRETTIER_FILES</c>, <c>prettier_cmd.rs</c>:60).</summary>
    private const int CapWarnings = 10;

    /// <summary>
    /// Filters Prettier output down to a "files needing formatting" summary (check mode) or a
    /// "files formatted" count (write mode). Faithful port of <c>filter_prettier_output</c>
    /// (<c>prettier_cmd.rs</c>:22-95).
    /// </summary>
    /// <param name="output">The raw Prettier stdout (per Rust's <c>stdout_only()</c> capture mode) to filter.</param>
    /// <returns>The condensed summary.</returns>
    public static string FilterPrettierOutput(string output)
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

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
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
