using System.Text;
using RtkSharp.Commands.System;
using RtkSharp.Core;

namespace RtkSharp.Commands.Jvm;

/// <summary>
/// Buffered single-pass filters for <c>mvn compile</c>/<c>test-compile</c> and for any goal invoked
/// under <c>-q</c>/<c>--quiet</c>. Faithful port of Rust's <c>filter_compile</c> / <c>filter_quiet</c>
/// (<c>src/cmds/jvm/mvn_cmd.rs</c>:617-870).
/// </summary>
internal static class MvnCompileQuietFilters
{
    /// <summary>
    /// Filters <c>mvn compile</c>/<c>test-compile</c> output: keeps module banners,
    /// <c>[INFO] Building …</c>, <c>[INFO] BUILD …</c>, totals, finish time, scanning line, install
    /// lines, and <c>[ERROR]</c> blocks with indented continuation (<c>  symbol:</c>, <c>  ^</c>,
    /// <c>  required:</c>). Deduplicates <c>[WARNING]</c> lines by normalised message (strip file
    /// coordinates). Faithful port of Rust's <c>filter_compile</c> (<c>mvn_cmd.rs</c>:625-685).
    /// </summary>
    /// <param name="raw">The raw <c>mvn compile</c>/<c>test-compile</c> output.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterCompile(string raw)
    {
        var stripped = Utils.StripAnsi(raw);
        if (!MvnSharedFilters.HasEnglishFooter(stripped))
        {
            return stripped;
        }

        var @out = new StringBuilder();
        var keepContinuation = false;
        var seenWarnings = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in ReadCommand.SplitLines(stripped))
        {
            if (MvnSharedFilters.ModuleBannerRegex().IsMatch(line))
            {
                @out.Append(line).Append('\n');
                keepContinuation = false;
                continue;
            }

            if (MvnSharedFilters.BuildFootRegex().IsMatch(line)
                || line.StartsWith("[INFO] Building ", StringComparison.Ordinal)
                || line.StartsWith("[INFO] Total time:", StringComparison.Ordinal)
                || line.StartsWith("[INFO] Finished at:", StringComparison.Ordinal)
                || line.StartsWith("[INFO] Scanning ", StringComparison.Ordinal))
            {
                @out.Append(line).Append('\n');
                keepContinuation = false;
                continue;
            }

            // Help boilerplate: drop before the `[ERROR]` catch-all (parity with keep_outside_block /
            // filter_quiet).
            if (MvnSharedFilters.IsBoilerplate(line))
            {
                keepContinuation = false;
                continue;
            }

            if (line.StartsWith("[ERROR]", StringComparison.Ordinal))
            {
                @out.Append(line).Append('\n');
                keepContinuation = true;
                continue;
            }

            if (keepContinuation && (line.StartsWith(' ') || line.StartsWith('\t')))
            {
                @out.Append(line).Append('\n');
                continue;
            }

            if (line.StartsWith("[WARNING]", StringComparison.Ordinal))
            {
                var payload = line.StartsWith("[WARNING] ", StringComparison.Ordinal)
                    ? line["[WARNING] ".Length..]
                    : line;
                var norm = MvnSharedFilters.FileCoordRegex().Replace(payload, string.Empty);
                if (seenWarnings.Add(norm))
                {
                    @out.Append(line).Append('\n');
                }

                keepContinuation = false;
                continue;
            }

            // Drop everything else.
            keepContinuation = false;
        }

        return @out.ToString();
    }

    /// <summary>
    /// Filters <c>mvn -q</c> invocations. Under <c>-q</c>, Maven 3.x suppresses all <c>[INFO]</c>
    /// lines, so the standard filters (which key off <c>[INFO]</c> markers and the English footer
    /// guard) can't fire. A passing run emits zero bytes; a failing run emits only <c>[ERROR]</c>-
    /// prefixed lines plus the stack trace. Faithful port of Rust's <c>filter_quiet</c>
    /// (<c>mvn_cmd.rs</c>:801-870).
    /// </summary>
    /// <param name="raw">The raw <c>mvn -q</c> output.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterQuiet(string raw)
    {
        var stripped = Utils.StripAnsi(raw);
        if (stripped.Trim().Length == 0)
        {
            return string.Empty;
        }

        var @out = new StringBuilder();
        var failureTrail = false;

        foreach (var line in ReadCommand.SplitLines(stripped))
        {
            // Surefire close-line for a failed class — keep + enter failure trail.
            if (MvnSharedFilters.CloseRegex().IsMatch(line))
            {
                @out.Append(line).Append('\n');
                failureTrail = line.Contains("<<< FAILURE!", StringComparison.Ordinal) || line.Contains("<<< ERROR!", StringComparison.Ordinal);
                continue;
            }

            // Per-test failure subline: `[ERROR] FQN.method -- Time elapsed: … <<< FAILURE!` (or
            // `<<< ERROR!` for thrown exceptions).
            if (MvnSharedFilters.IsPerTestSubline(line))
            {
                @out.Append(line).Append('\n');
                failureTrail = true;
                continue;
            }

            // Failure-trail body: exception class, user-code frames; drop framework frames.
            if (failureTrail)
            {
                if (line.Trim().Length == 0)
                {
                    @out.Append('\n');
                    failureTrail = false;
                    continue;
                }

                var t = line.TrimStart();
                if (t.StartsWith("at ", StringComparison.Ordinal) && MvnSharedFilters.IsFrameworkFrame(t))
                {
                    continue;
                }

                @out.Append(line).Append('\n');
                continue;
            }

            // Failure summary keepers.
            if (line.StartsWith("[ERROR] Tests run:", StringComparison.Ordinal)
                || line.StartsWith("[ERROR] Failures:", StringComparison.Ordinal)
                || line.StartsWith("[ERROR] Errors:", StringComparison.Ordinal)
                || line.StartsWith("[ERROR]   ", StringComparison.Ordinal)
                || line.StartsWith("[ERROR] Failed to execute goal", StringComparison.Ordinal))
            {
                @out.Append(line).Append('\n');
                continue;
            }

            // Drop post-failure help boilerplate and bare `[ERROR]` dividers (shared with the
            // non-quiet filters).
            if (MvnSharedFilters.IsBoilerplate(line))
            {
                continue;
            }

            // Safety net: keep anything else (unexpected output under `-q` is rare; do not silently
            // drop signal we haven't classified).
            @out.Append(line).Append('\n');
        }

        return @out.ToString();
    }
}
