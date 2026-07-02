namespace RtkSharp.Execution;

public interface IProcessExecutor
{
    ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
}
