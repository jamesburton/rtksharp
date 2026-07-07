using System.Text;
using RtkSharp.Commands.System;
using RtkSharp.Core;

namespace RtkSharp.Commands.Jvm;

/// <summary>
/// Buffered single-pass filters for <c>mvn test</c>/<c>integration-test</c> (Surefire/Failsafe shape)
/// and <c>mvn package</c>/<c>install</c>/<c>verify</c>/<c>deploy</c> (compile+Surefire mode toggle).
/// Both drive the shared <see cref="SurefireBlock"/> state machine. Faithful port of Rust's
/// <c>filter_surefire</c> / <c>filter_package</c> (<c>src/cmds/jvm/mvn_cmd.rs</c>:523-782).
/// </summary>
internal static class MvnSurefireFilter
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

        foreach (var line in ReadCommand.SplitLines(stripped))
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

        foreach (var line in ReadCommand.SplitLines(stripped))
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
}
