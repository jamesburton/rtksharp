using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Jvm;

/// <summary>
/// Buffered single-pass filters for <c>mvn test</c>/<c>integration-test</c> (Surefire/Failsafe shape),
/// <c>mvn package</c>/<c>install</c>/<c>verify</c>/<c>deploy</c> (compile+Surefire mode toggle),
/// <c>mvn compile</c>/<c>test-compile</c>, and any goal invoked under <c>-q</c>/<c>--quiet</c>. The
/// Surefire-shaped filters drive the shared <see cref="SurefireBlock"/> state machine. Faithful port
/// of Rust's <c>filter_surefire</c> / <c>filter_package</c> / <c>filter_compile</c> /
/// <c>filter_quiet</c> (<c>src/cmds/jvm/mvn_cmd.rs</c>:523-870).
/// </summary>
public static class MvnFilters
{
    /// <summary>
    /// Filters <c>mvn test</c>/<c>integration-test</c> output. English-footer guard: if no
    /// <c>BUILD SUCCESS</c>/<c>BUILD FAILURE</c> line is present, returns the ANSI-stripped raw input
    /// (non-English locale or truncated output). Faithful port of Rust's <c>filter_surefire</c>
    /// (<c>mvn_cmd.rs</c>:532-534).
    /// </summary>
    /// <param name="raw">The raw <c>mvn test</c> output.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterSurefire(string raw) => FilterSurefireWithCap(raw, MvnSharedFilters.MaxMvnFailingClasses);

    /// <summary>
    /// <see cref="FilterSurefire"/> with an explicit failing-class cap (test-only seam). Faithful port
    /// of Rust's <c>filter_surefire_with_cap</c> (<c>mvn_cmd.rs</c>:536-615).
    /// </summary>
    /// <param name="raw">The raw <c>mvn test</c> output.</param>
    /// <param name="cap">The maximum number of failing test-class blocks to emit in full.</param>
    /// <returns>The filtered output.</returns>
    internal static string FilterSurefireWithCap(string raw, int cap)
    {
        var stripped = Utils.StripAnsi(raw);
        if (!MvnSharedFilters.HasEnglishFooter(stripped))
        {
            return stripped;
        }

        var @out = new StringBuilder();
        var block = new SurefireBlock();
        var keepContinuation = false;
        var inReactorSummary = false;
        var emittedFailing = 0;
        var droppedFailing = 0;
        var summary = new FailuresSummaryCap(cap);

        foreach (var line in SourceFilterLineSplitter.SplitLines(stripped))
        {
            var step = block.Step(line, @out);
            if (step.Kind == SurefireStepKind.Consumed)
            {
                continue;
            }

            if (step.Kind == SurefireStepKind.FailingClose)
            {
                if (emittedFailing < cap)
                {
                    block.CommitFailing(@out, step.Running, step.Lines!, step.Close!);
                    emittedFailing++;
                }
                else
                {
                    block.DropFailing();
                    droppedFailing++;
                }

                keepContinuation = false;
                continue;
            }

            if (keepContinuation && (line.StartsWith(' ') || line.StartsWith('\t')))
            {
                @out.Append(line).Append('\n');
                continue;
            }

            // Failures-summary cap: gate `[ERROR]   ` entries, emit `+N more` tail before AGG. The
            // helper consumes only summary entries — other lines (header, AGG) fall through to the
            // keep-list below.
            if (summary.HandleEntry(line, @out))
            {
                continue;
            }

            // Order matters: call ReactorSummaryKeep first so its BUILD_FOOT clears-flag side effect
            // always runs regardless of `||` short-circuit.
            var reactorKeep = MvnSharedFilters.ReactorSummaryKeep(line, ref inReactorSummary);
            if (reactorKeep || MvnSharedFilters.KeepOutsideBlock(line))
            {
                // Pre-emit the summary tail when we're about to write AGG.
                summary.HandleAggregate(line, @out);
                // Detect summary header so subsequent `[ERROR]   ` entries get capped.
                summary.HandleHeader(line);
                @out.Append(line).Append('\n');
                keepContinuation = line.StartsWith("[ERROR]", StringComparison.Ordinal)
                    && !line.StartsWith("[ERROR] Tests run:", StringComparison.Ordinal)
                    && !line.StartsWith("[ERROR] Failures:", StringComparison.Ordinal)
                    && !line.StartsWith("[ERROR] Errors:", StringComparison.Ordinal);
                continue;
            }

            // Dropped line (e.g. help boilerplate): reset so a stale flag can't keep an indented line
            // that follows a dropped `[ERROR]` line. Parity with filter_package's fall-through reset.
            keepContinuation = false;
        }

        block.Finish(@out);
        summary.Finish(@out);
        if (droppedFailing > 0)
        {
            @out.Append($"\n… +{droppedFailing} more failing test classes\n");
        }

        return @out.ToString();
    }

    /// <summary>
    /// Filters <c>mvn package</c>/<c>install</c>/<c>verify</c>/<c>deploy</c> output. Mode toggle:
    /// starts in compile mode, switches to Surefire mode when a <c>[INFO] Running …</c> line is seen,
    /// switches back on <c>Tests run:</c> close. Outside any Surefire block, applies the unified
    /// keep-list (compile keepers + install/artifact lines). Faithful port of Rust's
    /// <c>filter_package</c> (<c>mvn_cmd.rs</c>:695-697).
    /// </summary>
    /// <param name="raw">The raw <c>mvn package</c>/<c>install</c>/<c>verify</c>/<c>deploy</c> output.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterPackage(string raw) => FilterPackageWithCap(raw, MvnSharedFilters.MaxMvnFailingClasses);

    /// <summary>
    /// <see cref="FilterPackage"/> with an explicit failing-class cap (test-only seam). Faithful port
    /// of Rust's <c>filter_package_with_cap</c> (<c>mvn_cmd.rs</c>:699-782).
    /// </summary>
    /// <param name="raw">The raw output.</param>
    /// <param name="cap">The maximum number of failing test-class blocks to emit in full.</param>
    /// <returns>The filtered output.</returns>
    internal static string FilterPackageWithCap(string raw, int cap)
    {
        var stripped = Utils.StripAnsi(raw);
        if (!MvnSharedFilters.HasEnglishFooter(stripped))
        {
            return stripped;
        }

        var @out = new StringBuilder();
        var block = new SurefireBlock();
        var keepContinuation = false;
        var inReactorSummary = false;
        var seenWarnings = new HashSet<string>(StringComparer.Ordinal);
        var emittedFailing = 0;
        var droppedFailing = 0;
        var summary = new FailuresSummaryCap(cap);

        foreach (var line in SourceFilterLineSplitter.SplitLines(stripped))
        {
            var step = block.Step(line, @out);
            if (step.Kind == SurefireStepKind.Consumed)
            {
                continue;
            }

            if (step.Kind == SurefireStepKind.FailingClose)
            {
                if (emittedFailing < cap)
                {
                    block.CommitFailing(@out, step.Running, step.Lines!, step.Close!);
                    emittedFailing++;
                }
                else
                {
                    block.DropFailing();
                    droppedFailing++;
                }

                keepContinuation = false;
                continue;
            }

            // Failures-summary cap (see FilterSurefireWithCap for details).
            if (summary.HandleEntry(line, @out))
            {
                continue;
            }

            // Order matters: call ReactorSummaryKeep first so its BUILD_FOOT clears-flag side effect
            // always runs regardless of `||` short-circuit.
            var reactorKeep = MvnSharedFilters.ReactorSummaryKeep(line, ref inReactorSummary);
            // Outside any Surefire block: compile-keep AND surefire-outside-keep merge.
            if (reactorKeep || MvnSharedFilters.ModuleBannerRegex().IsMatch(line) || MvnSharedFilters.KeepOutsideBlock(line))
            {
                summary.HandleAggregate(line, @out);
                summary.HandleHeader(line);
                @out.Append(line).Append('\n');
                keepContinuation = line.StartsWith("[ERROR]", StringComparison.Ordinal)
                    && !line.StartsWith("[ERROR] Tests run:", StringComparison.Ordinal)
                    && !line.StartsWith("[ERROR] Failures:", StringComparison.Ordinal)
                    && !line.StartsWith("[ERROR] Errors:", StringComparison.Ordinal);
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

            keepContinuation = false;
        }

        block.Finish(@out);
        summary.Finish(@out);
        if (droppedFailing > 0)
        {
            @out.Append($"\n… +{droppedFailing} more failing test classes\n");
        }

        return @out.ToString();
    }

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

        foreach (var line in SourceFilterLineSplitter.SplitLines(stripped))
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

        foreach (var line in SourceFilterLineSplitter.SplitLines(stripped))
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
