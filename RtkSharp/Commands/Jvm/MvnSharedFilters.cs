using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Commands.System;

namespace RtkSharp.Commands.Jvm;

/// <summary>
/// Shared regex patterns, stack-frame/boilerplate deny-lists, and the <see cref="SurefireBlock"/> /
/// <see cref="FailuresSummaryCap"/> state machines used by <see cref="MvnSurefireFilter"/> and
/// <see cref="MvnCompileQuietFilters"/>. Faithful port of the module-level items in
/// <c>src/cmds/jvm/mvn_cmd.rs</c> (lines 1-521).
/// </summary>
internal static partial class MvnSharedFilters
{
    /// <summary>Cap on emitted failing test-class blocks and <c>[ERROR] Failures:</c> summary entries —
    /// same binding as the test-failure cap class used by pytest/rspec/rake/runner. Rust <c>CAP_WARNINGS</c>
    /// from <c>src/core/truncate.rs</c>.</summary>
    public const int MaxMvnFailingClasses = 10;

    // ── Shared regex patterns ────────────────────────────────────────────────

    /// <summary><c>[INFO] Running com.example.app.FooTest</c>.</summary>
    [GeneratedRegex(@"^\[INFO\] Running ")]
    public static partial Regex RunningRegex();

    /// <summary>
    /// Surefire/Failsafe per-class close line. Captures Failures and Errors. Tolerates the optional
    /// <c>&lt;&lt;&lt; FAILURE!</c> / <c>&lt;&lt;&lt; ERROR!</c> marker (3.5.5 emits <c>&lt;&lt;&lt; FAILURE!</c>
    /// even for errors-only classes; <c>ERROR!</c> accepted defensively for other Surefire versions;
    /// failure detection is via the captured counts, not the marker). Separator is <c>-</c> (Surefire
    /// 2.x) or <c>--</c> (Surefire 3.x). Prefix INFO/ERROR/WARNING (3.x emits WARNING for classes with
    /// only skipped tests).
    /// </summary>
    [GeneratedRegex(@"^\[(?:INFO|ERROR|WARNING)\] Tests run: \d+, Failures: (\d+), Errors: (\d+), Skipped: \d+, Time elapsed: [^ ]+ s(?:\s+<<<\s*(?:FAILURE|ERROR)!)?\s+--?\s+in (.+)$")]
    public static partial Regex CloseRegex();

    /// <summary>Final BUILD footer.</summary>
    [GeneratedRegex(@"^\[(?:INFO|ERROR)\] BUILD (?:SUCCESS|FAILURE)$")]
    public static partial Regex BuildFootRegex();

    /// <summary><c>[INFO] Results:</c> separator before the aggregate.</summary>
    [GeneratedRegex(@"^\[INFO\] Results:\s*$")]
    public static partial Regex ResultsRegex();

    /// <summary>Aggregate counts line (no <c>Time elapsed</c>, no <c>- in</c>).</summary>
    [GeneratedRegex(@"^\[(?:INFO|ERROR)\] Tests run: \d+, Failures: \d+, Errors: \d+, Skipped: \d+\s*$")]
    public static partial Regex AggRegex();

    /// <summary>Plugin banner line: <c>[INFO] --- plugin:goal (id) @ module ---</c>.</summary>
    [GeneratedRegex(@"^\[INFO\] --- .* @ .* ---$")]
    public static partial Regex PluginBannerRegex();

    /// <summary>Module banner with project name in brackets.</summary>
    [GeneratedRegex(@"^\[INFO\] -+< .+ >-+$")]
    public static partial Regex ModuleBannerRegex();

    /// <summary>Reactor summary header that opens the per-module pass/fail block at the end of a
    /// multi-module build.</summary>
    [GeneratedRegex(@"^\[INFO\] Reactor Summary for ")]
    public static partial Regex ReactorSummaryRegex();

    /// <summary>Compile-error coordinate substring to strip when deduping warnings/errors.</summary>
    [GeneratedRegex(@"/[^:]+\.java:\[\d+,\d+\]")]
    public static partial Regex FileCoordRegex();

    // ── Stack-frame deny-list ─────────────────────────────────────────────────

    private static readonly string[] FrameworkFramePrefixes =
    [
        "at org.junit.",
        "at junit.",
        "at org.apache.maven.surefire.",
        "at sun.reflect.",
        "at jdk.internal.reflect.",
        "at jdk.proxy",
        "at java.base/",
        "at java.lang.reflect.",
        "at java.util.",
    ];

    /// <summary>Faithful port of Rust's <c>is_framework_frame</c> (<c>mvn_cmd.rs</c>:123-127).</summary>
    /// <param name="trimmed">The trimmed stack-frame line.</param>
    /// <returns>True if the frame belongs to Surefire/JUnit/JDK internals.</returns>
    public static bool IsFrameworkFrame(string trimmed) =>
        FrameworkFramePrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// Boilerplate <c>[ERROR]</c> lines Maven emits after <c>Failed to execute goal</c> — pure noise
    /// pointing at log files and help URLs, no signal for the user/LLM. Deliberately excludes
    /// <c>[ERROR] After correcting the problems</c> and <c>[ERROR]   mvn &lt;args&gt; -rf :…</c> (the
    /// resume hint is actionable signal for a multi-module build) and <c>[ERROR] Failed to execute
    /// goal</c> (signal).
    /// </summary>
    private static readonly string[] BoilerPrefixes =
    [
        "[ERROR] See ",
        "[ERROR] -> [Help",
        "[ERROR] To see the full stack trace",
        "[ERROR] Re-run Maven",
        "[ERROR] For more information",
        "[ERROR] [Help",
    ];

    /// <summary>
    /// Post-failure help boilerplate, plus the bare <c>[ERROR]</c> divider lines Maven emits between
    /// boilerplate blocks (same drop rules as <see cref="MvnCompileQuietFilters.FilterQuiet"/>).
    /// Faithful port of Rust's <c>is_boilerplate</c> (<c>mvn_cmd.rs</c>:145-147).
    /// </summary>
    /// <param name="line">The raw output line.</param>
    /// <returns>True if the line is post-failure help boilerplate.</returns>
    public static bool IsBoilerplate(string line) =>
        BoilerPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)) || line.TrimEnd() == "[ERROR]";

    /// <summary>
    /// <c>[ERROR] FQN.method -- Time elapsed: 0.030 s &lt;&lt;&lt; FAILURE!</c> (or <c>&lt;&lt;&lt; ERROR!</c>).
    /// Distinguished from <see cref="CloseRegex"/> by call position: only consulted when not already
    /// inside a Surefire block. Faithful port of Rust's <c>is_per_test_subline</c>
    /// (<c>mvn_cmd.rs</c>:156-159).
    /// </summary>
    /// <param name="line">The raw output line.</param>
    /// <returns>True if the line is a per-test failure/error subline.</returns>
    public static bool IsPerTestSubline(string line) =>
        line.StartsWith("[ERROR] ", StringComparison.Ordinal)
        && (line.Contains("<<< FAILURE!", StringComparison.Ordinal) || line.Contains("<<< ERROR!", StringComparison.Ordinal));

    // ── English-footer guard ─────────────────────────────────────────────────

    /// <summary>Faithful port of Rust's <c>has_english_footer</c> (<c>mvn_cmd.rs</c>:163-168).</summary>
    /// <param name="stripped">The ANSI-stripped raw output.</param>
    /// <returns>True if an English <c>BUILD SUCCESS</c>/<c>BUILD FAILURE</c> footer line is present.</returns>
    public static bool HasEnglishFooter(string stripped) =>
        ReadCommand.SplitLines(stripped).Any(l =>
        {
            var t = l.Trim();
            return t.EndsWith(" BUILD SUCCESS", StringComparison.Ordinal) || t.EndsWith(" BUILD FAILURE", StringComparison.Ordinal);
        });

    // ── Outside-block keep list (shared by surefire + package) ──────────────

    /// <summary>
    /// Multi-module reactor summary keeper. Toggles <paramref name="inReactorSummary"/> on
    /// <c>[INFO] Reactor Summary for …</c> (enter) and <c>BUILD SUCCESS</c>/<c>BUILD FAILURE</c>
    /// (exit). Returns true for every line while the flag is set so the per-module status rows
    /// survive. Must be called before <see cref="KeepOutsideBlock"/> so the BUILD_FOOT clears-flag
    /// side effect always runs regardless of short-circuiting. Faithful port of Rust's
    /// <c>reactor_summary_keep</c> (<c>mvn_cmd.rs</c>:181-191).
    /// </summary>
    /// <param name="line">The raw output line.</param>
    /// <param name="inReactorSummary">Whether a reactor summary block is currently open.</param>
    /// <returns>True if the line should be kept as part of the reactor summary.</returns>
    public static bool ReactorSummaryKeep(string line, ref bool inReactorSummary)
    {
        if (ReactorSummaryRegex().IsMatch(line))
        {
            inReactorSummary = true;
            return true;
        }

        if (BuildFootRegex().IsMatch(line))
        {
            inReactorSummary = false;
            return false;
        }

        return inReactorSummary;
    }

    /// <summary>Faithful port of Rust's <c>keep_outside_block</c> (<c>mvn_cmd.rs</c>:193-214).</summary>
    /// <param name="line">The raw output line.</param>
    /// <returns>True if the line should be kept while not inside a Surefire block.</returns>
    public static bool KeepOutsideBlock(string line)
    {
        // Help boilerplate must be rejected before the `[ERROR]` catch-all below (non-quiet parity
        // with filter_quiet's boilerplate stripping).
        if (IsBoilerplate(line))
        {
            return false;
        }

        return ResultsRegex().IsMatch(line)
            || AggRegex().IsMatch(line)
            || BuildFootRegex().IsMatch(line)
            || ModuleBannerRegex().IsMatch(line)
            || line.StartsWith("[INFO] Total time:", StringComparison.Ordinal)
            || line.StartsWith("[INFO] Finished at:", StringComparison.Ordinal)
            || line.StartsWith("[INFO] Building ", StringComparison.Ordinal)
            || line.StartsWith("[INFO] Scanning ", StringComparison.Ordinal)
            || line.StartsWith("[INFO] Installing ", StringComparison.Ordinal)
            || line.StartsWith("[ERROR] Failures:", StringComparison.Ordinal)
            || line.StartsWith("[ERROR] Errors:", StringComparison.Ordinal)
            || (line.StartsWith("[ERROR]", StringComparison.Ordinal) && !line.StartsWith("[ERROR] Tests run:", StringComparison.Ordinal))
            || line.StartsWith("[INFO] Building war:", StringComparison.Ordinal)
            || line.StartsWith("[INFO] Building jar:", StringComparison.Ordinal)
            || line.StartsWith("[INFO] Building ear:", StringComparison.Ordinal);
    }
}

// ── Surefire block filter ────────────────────────────────────────────────────

/// <summary>The kind of result produced by a single <see cref="SurefireBlock.Step"/> call.</summary>
internal enum SurefireStepKind
{
    /// <summary>The inner machine consumed the line; the outer loop should continue.</summary>
    Consumed,

    /// <summary>A CLOSE line with Failures &gt; 0 or Errors &gt; 0 was reached; the outer loop decides
    /// whether to commit.</summary>
    FailingClose,

    /// <summary>The inner machine did not handle the line; the outer loop applies its own
    /// outside-block keep logic.</summary>
    Passthrough,
}

/// <summary>The result of a single <see cref="SurefireBlock.Step"/> call. Faithful port of Rust's
/// <c>SurefireStep</c> enum (<c>mvn_cmd.rs</c>:260-273).</summary>
internal readonly struct SurefireStepResult
{
    private SurefireStepResult(SurefireStepKind kind, string? running, IReadOnlyList<string>? lines, string? close)
    {
        Kind = kind;
        Running = running;
        Lines = lines;
        Close = close;
    }

    public SurefireStepKind Kind { get; }

    public string? Running { get; }

    public IReadOnlyList<string>? Lines { get; }

    public string? Close { get; }

    public static SurefireStepResult Consumed() => new(SurefireStepKind.Consumed, null, null, null);

    public static SurefireStepResult Passthrough() => new(SurefireStepKind.Passthrough, null, null, null);

    public static SurefireStepResult FailingClose(string? running, IReadOnlyList<string> lines, string close) =>
        new(SurefireStepKind.FailingClose, running, lines, close);
}

/// <summary>
/// Shared state machine driving the inner Surefire block + failure-trail behaviour for
/// <see cref="MvnSurefireFilter.FilterSurefire"/> and <see cref="MvnSurefireFilter.FilterPackage"/>.
/// Faithful port of Rust's <c>SurefireBlock</c> (<c>mvn_cmd.rs</c>:243-435).
/// </summary>
/// <remarks>
/// Inner machine responsibilities:
/// <list type="bullet">
/// <item><c>[INFO] --- … @ … ---</c> plugin banner skip.</item>
/// <item><c>[INFO] Running &lt;FQN&gt;</c> opens a buffered block (flushes any prior open block as
/// keep — happens on truncated output).</item>
/// <item>In-block buffering until the next CLOSE line.</item>
/// <item>CLOSE with <c>Failures &gt; 0</c> or <c>Errors &gt; 0</c> yields <c>FailingClose</c> so the
/// outer loop can decide whether to emit (enforces the failing-class cap).</item>
/// <item>Failure-trail handling for the exception/user-frame trail Surefire 3.x emits after the close
/// line, terminated by a blank line. Framework frames are stripped; user-code frames are preserved.</item>
/// <item>Multi-failure classes: Surefire 3.x emits one blank-separated detail block per failing test
/// under a single CLOSE line. When a trail ends at a blank line, <c>trailRearm</c> remembers the
/// keep/drop decision so the next per-test subline re-enters the trail with the same decision.</item>
/// </list>
/// </remarks>
internal sealed class SurefireBlock
{
    private readonly List<string> _blockLines = [];
    private string? _blockRunning;
    private bool _inBlock;
    private bool _failureTrail;

    /// <summary>When set together with <c>failureTrail</c>, consumes the trail without writing it to
    /// <c>out</c>. Used when the caller capped a failing block via <see cref="DropFailing"/>.</summary>
    private bool _dropTrail;

    /// <summary>Set when a trail ends at a blank line; holds the <c>dropTrail</c> value so the next
    /// per-test subline of the same class re-enters the trail with the same keep/drop decision.
    /// Cleared by any non-blank non-subline line, by RUNNING, and by commit/drop.</summary>
    private bool? _trailRearm;

    /// <summary>Faithful port of Rust's <c>SurefireBlock::step</c> (<c>mvn_cmd.rs</c>:287-373).</summary>
    /// <param name="line">The raw output line.</param>
    /// <param name="out">The output buffer to append kept content to.</param>
    /// <returns>The step result directing the outer loop's next action.</returns>
    public SurefireStepResult Step(string line, StringBuilder @out)
    {
        if (MvnSharedFilters.PluginBannerRegex().IsMatch(line))
        {
            return SurefireStepResult.Consumed();
        }

        if (MvnSharedFilters.RunningRegex().IsMatch(line))
        {
            if (_inBlock)
            {
                FlushOpenBlockAsKeep(@out);
            }

            _blockLines.Clear();
            _blockRunning = line;
            _inBlock = true;
            _failureTrail = false;
            // Load-bearing: a capped multi-failure class followed by a kept class must not re-arm
            // into the new class's trail decision.
            _trailRearm = null;
            return SurefireStepResult.Consumed();
        }

        if (_inBlock)
        {
            var caps = MvnSharedFilters.CloseRegex().Match(line);
            if (caps.Success)
            {
                var fail = caps.Groups[1].Value != "0";
                var err = caps.Groups[2].Value != "0";
                if (fail || err)
                {
                    var lines = new List<string>(_blockLines);
                    var running = _blockRunning;
                    _blockLines.Clear();
                    _blockRunning = null;
                    _inBlock = false;
                    return SurefireStepResult.FailingClose(running, lines, line);
                }

                _blockLines.Clear();
                _blockRunning = null;
                _inBlock = false;
                return SurefireStepResult.Consumed();
            }

            _blockLines.Add(line);
            return SurefireStepResult.Consumed();
        }

        if (_failureTrail)
        {
            if (line.Length == 0)
            {
                if (!_dropTrail)
                {
                    @out.Append('\n');
                }

                // Arm re-entry: a following per-test subline belongs to the same class and must
                // inherit this trail's keep/drop decision.
                _trailRearm = _dropTrail;
                _failureTrail = false;
                _dropTrail = false;
                return SurefireStepResult.Consumed();
            }

            var t = line.TrimStart();
            if (t.StartsWith("at ", StringComparison.Ordinal) && MvnSharedFilters.IsFrameworkFrame(t))
            {
                return SurefireStepResult.Consumed();
            }

            if (_dropTrail)
            {
                return SurefireStepResult.Consumed();
            }

            @out.Append(line).Append('\n');
            return SurefireStepResult.Consumed();
        }

        if (_trailRearm is { } dropped)
        {
            if (line.Length == 0)
            {
                // Tolerate extra blanks between per-test blocks: stay armed, let the blank fall
                // through (outer keep-lists drop it).
                return SurefireStepResult.Passthrough();
            }

            _trailRearm = null; // disarm unconditionally on non-blank (load-bearing)
            if (MvnSharedFilters.IsPerTestSubline(line))
            {
                _failureTrail = true;
                _dropTrail = dropped;
                if (!dropped)
                {
                    @out.Append(line).Append('\n');
                }

                return SurefireStepResult.Consumed();
            }

            // Non-subline: trail is over; already disarmed — fall through.
        }

        return SurefireStepResult.Passthrough();
    }

    /// <summary>
    /// Marks a <c>FailingClose</c> as dropped (cap exceeded). The block itself is already extracted by
    /// <see cref="Step"/>; this sets <c>failureTrail</c> so the post-close trail (per-test subline,
    /// exception, user frames) is consumed and silently dropped until the next blank line. Faithful
    /// port of Rust's <c>SurefireBlock::drop_failing</c> (<c>mvn_cmd.rs</c>:379-385).
    /// </summary>
    public void DropFailing()
    {
        _failureTrail = true;
        _dropTrail = true;
        // Belt-and-suspenders: a CLOSE can only follow a RUNNING (which already cleared trailRearm),
        // but keep the invariant local too.
        _trailRearm = null;
    }

    /// <summary>
    /// Commits a <c>FailingClose</c> to <paramref name="out"/>: writes <paramref name="running"/>,
    /// then <paramref name="lines"/> (with framework frames stripped), then <paramref name="close"/>.
    /// Enables <c>failureTrail</c> so the post-close exception/user-frame trail is preserved. Faithful
    /// port of Rust's <c>SurefireBlock::commit_failing</c> (<c>mvn_cmd.rs</c>:390-414).
    /// </summary>
    /// <param name="out">The output buffer to append to.</param>
    /// <param name="running">The buffered <c>[INFO] Running …</c> line, if any.</param>
    /// <param name="lines">The buffered block body lines.</param>
    /// <param name="close">The CLOSE line itself.</param>
    public void CommitFailing(StringBuilder @out, string? running, IReadOnlyList<string> lines, string close)
    {
        if (running is not null)
        {
            @out.Append(running).Append('\n');
        }

        foreach (var l in lines)
        {
            var t = l.TrimStart();
            if (t.StartsWith("at ", StringComparison.Ordinal) && MvnSharedFilters.IsFrameworkFrame(t))
            {
                continue;
            }

            @out.Append(l).Append('\n');
        }

        @out.Append(close).Append('\n');
        _failureTrail = true;
        // Belt-and-suspenders: see DropFailing.
        _trailRearm = null;
    }

    /// <summary>
    /// End-of-stream flush: if a block opened and never closed (truncated output), surfaces what we
    /// have rather than dropping it silently. Faithful port of Rust's <c>SurefireBlock::finish</c>
    /// (<c>mvn_cmd.rs</c>:418-422).
    /// </summary>
    /// <param name="out">The output buffer to append to.</param>
    public void Finish(StringBuilder @out)
    {
        if (_inBlock)
        {
            FlushOpenBlockAsKeep(@out);
        }
    }

    private void FlushOpenBlockAsKeep(StringBuilder @out)
    {
        if (_blockRunning is not null)
        {
            @out.Append(_blockRunning).Append('\n');
            _blockRunning = null;
        }

        foreach (var l in _blockLines)
        {
            @out.Append(l).Append('\n');
        }

        _blockLines.Clear();
        _inBlock = false;
    }
}

/// <summary>
/// <c>[ERROR] Failures:</c> summary block cap. Maven emits a summary at the end of a failing test run
/// listing every failing test method; on builds with hundreds of failures this can be quite large.
/// Caps entries at the configured cap and emits a <c>… +N more failures</c> tail immediately before
/// the <c>[ERROR] Tests run:</c> aggregate line when entries were dropped. Faithful port of Rust's
/// <c>FailuresSummaryCap</c> (<c>mvn_cmd.rs</c>:453-521).
/// </summary>
/// <param name="cap">The maximum number of <c>[ERROR]   </c> entries to emit before capping.</param>
internal sealed class FailuresSummaryCap(int cap)
{
    private bool _inSummary;
    private int _emitted;
    private int _dropped;

    /// <summary>
    /// If <paramref name="line"/> is an <c>[ERROR]   </c> entry inside the failures summary, writes it
    /// (or counts it as dropped) and returns true so the caller skips its own keep-list. Faithful port
    /// of Rust's <c>FailuresSummaryCap::handle_entry</c> (<c>mvn_cmd.rs</c>:473-486).
    /// </summary>
    /// <param name="line">The raw output line.</param>
    /// <param name="out">The output buffer to append to.</param>
    /// <returns>True if the line was handled as a summary entry.</returns>
    public bool HandleEntry(string line, StringBuilder @out)
    {
        if (!_inSummary || !line.StartsWith("[ERROR]   ", StringComparison.Ordinal))
        {
            return false;
        }

        // Per core cap policy, cap=0 means summary-only: no entries, tail still counts.
        if (_emitted < cap)
        {
            @out.Append(line).Append('\n');
            _emitted++;
        }
        else
        {
            _dropped++;
        }

        return true;
    }

    /// <summary>
    /// Detects the <c>[ERROR] Failures:</c> header so subsequent <c>[ERROR]   </c> lines get capped.
    /// The caller is responsible for writing the header to <c>out</c>. Faithful port of Rust's
    /// <c>FailuresSummaryCap::handle_header</c> (<c>mvn_cmd.rs</c>:490-496).
    /// </summary>
    /// <param name="line">The raw output line.</param>
    public void HandleHeader(string line)
    {
        if (line.StartsWith("[ERROR] Failures:", StringComparison.Ordinal))
        {
            _inSummary = true;
            _emitted = 0;
            _dropped = 0;
        }
    }

    /// <summary>
    /// Pre-emits the <c>… +N more failures</c> tail when the aggregate <c>[ERROR] Tests run:</c> line
    /// is about to be written, then closes the summary. The caller writes the AGG line itself
    /// afterwards. Faithful port of Rust's <c>FailuresSummaryCap::handle_aggregate</c>
    /// (<c>mvn_cmd.rs</c>:501-511).
    /// </summary>
    /// <param name="line">The raw output line.</param>
    /// <param name="out">The output buffer to append to.</param>
    public void HandleAggregate(string line, StringBuilder @out)
    {
        if (!_inSummary || !MvnSharedFilters.AggRegex().IsMatch(line))
        {
            return;
        }

        if (_dropped > 0)
        {
            @out.Append($"\n… +{_dropped} more failures\n");
        }

        _inSummary = false;
        _emitted = 0;
        _dropped = 0;
    }

    /// <summary>
    /// End-of-stream tail emission for cases where the AGG line never arrives (truncated output).
    /// Faithful port of Rust's <c>FailuresSummaryCap::finish</c> (<c>mvn_cmd.rs</c>:516-520).
    /// </summary>
    /// <param name="out">The output buffer to append to.</param>
    public void Finish(StringBuilder @out)
    {
        if (_inSummary && _dropped > 0)
        {
            @out.Append($"\n… +{_dropped} more failures\n");
        }
    }
}
