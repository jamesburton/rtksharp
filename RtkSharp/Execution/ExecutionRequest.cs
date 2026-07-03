namespace RtkSharp.Execution;

/// <summary>
/// Describes a child process to execute: the executable, arguments, and optional
/// working directory, environment overrides, timeout, and stream capture mode.
/// </summary>
/// <param name="FileName">The executable name or path to run.</param>
/// <param name="Arguments">The arguments to pass to the executable.</param>
/// <param name="WorkingDirectory">The working directory for the child process, or null to use the current directory.</param>
/// <param name="Environment">Environment variable overrides to apply, or null to inherit the parent's environment.</param>
/// <param name="Timeout">The maximum duration to allow the process to run before it is killed, or null for no timeout.</param>
/// <param name="CaptureMode">How stdout/stderr should be captured.</param>
/// <param name="StdinContent">
/// When set, the child process's standard input is redirected, this text is written to it
/// (UTF-8, no BOM), and the stream is then closed so the child observes EOF. When null
/// (the default), standard input is not redirected and the child does not receive any
/// piped input from the parent.
/// </param>
public sealed record ExecutionRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    ExecutionCaptureMode CaptureMode = ExecutionCaptureMode.Separate,
    string? StdinContent = null
);
