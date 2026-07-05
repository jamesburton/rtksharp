using System;
using System.Diagnostics;

namespace RtkSharp.Core.Tracking;

/// <summary>
/// Helper for timing command execution and recording token-savings/timing metrics via
/// <see cref="Tracker"/>. Faithful port of Rust <c>TimedExecution</c> (<c>tracking.rs:1306-1399</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Direct <see cref="Tracker"/> construction, not <see cref="ITokenTracker"/>.</b> Unlike other
/// ported filters that report through the <see cref="ITokenTracker"/> abstraction (length-based, no
/// elapsed-time parameter), this type constructs a full <see cref="Tracker"/> directly and calls its
/// <see cref="Tracker.Record"/> overload with an explicit elapsed-milliseconds value — matching
/// Rust's own direct call to <c>Tracker::new()</c> / <c>tracker.record(...)</c> inside
/// <c>TimedExecution::track</c>. <see cref="ITokenTracker"/> has no elapsed-time parameter and is a
/// poor fit here.
/// </para>
/// <para>
/// <b>Silently swallows all tracking failures.</b> Both <see cref="Track"/> and
/// <see cref="TrackPassthrough"/> catch and discard any exception raised while constructing a
/// <see cref="Tracker"/> or calling <see cref="Tracker.Record"/> — matching Rust's
/// <c>if let Ok(tracker) = Tracker::new() { let _ = tracker.record(...); }</c> pattern. Tracking is a
/// "never block the user" hot-path concern: a broken or unwritable database must never surface as a
/// command failure.
/// </para>
/// </remarks>
public sealed class TimedExecution
{
    private readonly Stopwatch _stopwatch;

    /// <summary>Prevents direct construction; use <see cref="Start"/>.</summary>
    private TimedExecution()
    {
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// Starts timing a command execution. Faithful port of Rust <c>TimedExecution::start</c>
    /// (<c>tracking.rs:1326-1330</c>): creates a new timer that begins measuring elapsed time
    /// immediately, to be finished later via <see cref="Track"/> or <see cref="TrackPassthrough"/>.
    /// </summary>
    /// <returns>A new timer.</returns>
    public static TimedExecution Start() => new();

    /// <summary>
    /// Records the command execution with elapsed time and token counts estimated from
    /// <paramref name="input"/> / <paramref name="output"/>. Faithful port of Rust
    /// <c>TimedExecution::track</c> (<c>tracking.rs:1356-1370</c>). Any failure constructing a
    /// <see cref="Tracker"/> or persisting the record is silently swallowed — never blocks the user.
    /// </summary>
    /// <param name="originalCmd">The standard command (e.g., <c>"ls -la"</c>).</param>
    /// <param name="rtkCmd">The RTK command used (e.g., <c>"rtk ls"</c>).</param>
    /// <param name="input">The standard command's output, used for input-token estimation.</param>
    /// <param name="output">RTK's filtered output, used for output-token estimation.</param>
    public void Track(string originalCmd, string rtkCmd, string input, string output)
    {
        var elapsedMs = _stopwatch.ElapsedMilliseconds;
        var inputTokens = Tracker.EstimateTokens(input);
        var outputTokens = Tracker.EstimateTokens(output);

        try
        {
            using var tracker = new Tracker();
            tracker.Record(originalCmd, rtkCmd, inputTokens, outputTokens, elapsedMs);
        }
        catch (Exception)
        {
            // Never block the user over tracking failures - matches Rust's
            // `if let Ok(tracker) = Tracker::new() { let _ = tracker.record(...); }`, which discards
            // both a construction failure and a record() failure without surfacing either to the caller.
        }
    }

    /// <summary>
    /// Records a passthrough command's elapsed time only, with <c>input_tokens=0</c> /
    /// <c>output_tokens=0</c> so it doesn't dilute savings statistics. For commands that stream
    /// output or run interactively where output cannot be captured. Faithful port of Rust
    /// <c>TimedExecution::track_passthrough</c> (<c>tracking.rs:1392-1398</c>). Any failure
    /// constructing a <see cref="Tracker"/> or persisting the record is silently swallowed.
    /// </summary>
    /// <param name="originalCmd">The standard command (e.g., <c>"git tag --list"</c>).</param>
    /// <param name="rtkCmd">The RTK command used (e.g., <c>"rtk git tag --list"</c>).</param>
    public void TrackPassthrough(string originalCmd, string rtkCmd)
    {
        var elapsedMs = _stopwatch.ElapsedMilliseconds;

        try
        {
            using var tracker = new Tracker();
            tracker.Record(originalCmd, rtkCmd, 0, 0, elapsedMs);
        }
        catch (Exception)
        {
            // Never block the user over tracking failures - see Track() above for rationale.
        }
    }
}
