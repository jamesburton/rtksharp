namespace RtkSharp.Execution;

public sealed record ExecutionResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    TimeSpan TimedDuration,
    bool WasStarted,
    string? Failure,
    bool TimedOut
);
