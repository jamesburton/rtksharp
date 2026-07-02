namespace RtkSharp.Execution;

/// <summary>
/// Describes the outcome of executing a child process.
/// </summary>
/// <param name="Stdout">The captured standard output (or merged stdout+stderr, depending on capture mode).</param>
/// <param name="Stderr">The captured standard error, empty when using merged capture mode.</param>
/// <param name="ExitCode">The process exit code, or a sentinel value (127 or -1) if the process did not start or was killed.</param>
/// <param name="TimedDuration">How long the process ran for.</param>
/// <param name="WasStarted">Whether the process was successfully started.</param>
/// <param name="Failure">A human-readable failure description, or null if the process completed normally.</param>
/// <param name="TimedOut">Whether the process was killed due to exceeding its configured timeout.</param>
public sealed record ExecutionResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    TimeSpan TimedDuration,
    bool WasStarted,
    string? Failure,
    bool TimedOut
);
