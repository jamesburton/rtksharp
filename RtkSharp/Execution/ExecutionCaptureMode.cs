namespace RtkSharp.Execution;

/// <summary>
/// Controls how a child process's standard output and standard error streams are captured.
/// </summary>
public enum ExecutionCaptureMode
{
    /// <summary>Capture stdout and stderr independently.</summary>
    Separate,

    /// <summary>Capture stdout and stderr interleaved into a single merged stream, in emission order.</summary>
    Merged,

    /// <summary>Do not redirect; the child process inherits the parent's console streams.</summary>
    Inherit
}
