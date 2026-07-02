namespace RtkSharp.Execution;

public sealed record ExecutionRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    ExecutionCaptureMode CaptureMode = ExecutionCaptureMode.Separate
);
