namespace RtkSharp.Execution;

/// <summary>
/// Abstraction over child-process execution, allowing filters and tests to substitute
/// custom process execution behavior.
/// </summary>
public interface IProcessExecutor
{
    /// <summary>
    /// Executes a child process as described by <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The process execution request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The execution result.</returns>
    ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
}
