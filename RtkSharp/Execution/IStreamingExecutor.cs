namespace RtkSharp.Execution;

/// <summary>
/// Abstraction over dual-mode "live passthrough + capped capture" child-process execution,
/// allowing commands and tests to substitute custom streaming behavior.
/// </summary>
public interface IStreamingExecutor
{
    /// <summary>
    /// Executes a child process as described by <paramref name="request"/>, writing its
    /// stdout/stderr to the real console as it arrives while simultaneously accumulating a
    /// capped copy of each stream for later inspection (tracking, filtering, tee-to-disk, etc.).
    /// </summary>
    /// <param name="request">The process execution request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The execution result. <see cref="ExecutionResult.Stdout"/>/<see cref="ExecutionResult.Stderr"/>
    /// hold the captured (possibly capped) text; the full, uncapped text was already written live
    /// to <see cref="Console.Out"/>/<see cref="Console.Error"/> while the child ran.
    /// </returns>
    ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
}
