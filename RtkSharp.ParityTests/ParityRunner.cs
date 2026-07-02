using RtkSharp.Execution;

namespace RtkSharp.ParityTests;

/// <summary>
/// Result of executing the same command against both the reference Rust rtk binary
/// and the RtkSharp .NET port, for parity verification.
/// </summary>
/// <param name="Command">The command that was executed against both tools.</param>
/// <param name="RustOutput">Captured stdout from the Rust binary.</param>
/// <param name="DotNetOutput">Captured stdout from the RtkSharp binary.</param>
/// <param name="OutputsMatch">Whether <see cref="RustOutput"/> and <see cref="DotNetOutput"/> are identical.</param>
/// <param name="RustExitCode">Exit code from the Rust binary.</param>
/// <param name="DotNetExitCode">Exit code from the RtkSharp binary.</param>
/// <param name="ExitCodesMatch">Whether <see cref="RustExitCode"/> and <see cref="DotNetExitCode"/> match.</param>
public sealed record ParityComparison(
    string Command,
    string RustOutput,
    string DotNetOutput,
    bool OutputsMatch,
    int RustExitCode,
    int DotNetExitCode,
    bool ExitCodesMatch
);

/// <summary>
/// Executes the same command against a Rust rtk binary and a RtkSharp binary and
/// compares their output, for building parity confidence between the two implementations.
/// </summary>
public static class ParityRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> with <paramref name="args"/> against both binaries and
    /// returns a comparison of their captured stdout and exit codes.
    /// </summary>
    /// <param name="rustRtkPath">Full path to the reference Rust rtk executable.</param>
    /// <param name="dotnetRtkPath">Full path to the RtkSharp executable under test.</param>
    /// <param name="command">The subcommand or first argument to pass to both binaries.</param>
    /// <param name="args">Additional arguments to pass to both binaries.</param>
    /// <param name="cancellationToken">Token to cancel both executions.</param>
    /// <returns>A <see cref="ParityComparison"/> describing whether the two tools agree.</returns>
    public static async Task<ParityComparison> CompareAsync(
        string rustRtkPath,
        string dotnetRtkPath,
        string command,
        string[] args,
        CancellationToken cancellationToken = default
    )
    {
        var executor = new ProcessExecutor();
        var fullArgs = new[] { command }.Concat(args).ToArray();

        var rustResult = await executor.ExecuteAsync(
            new ExecutionRequest(rustRtkPath, fullArgs),
            cancellationToken
        ).ConfigureAwait(false);

        var dotnetResult = await executor.ExecuteAsync(
            new ExecutionRequest(dotnetRtkPath, fullArgs),
            cancellationToken
        ).ConfigureAwait(false);

        return new ParityComparison(
            $"{command} {string.Join(' ', args)}".Trim(),
            rustResult.Stdout,
            dotnetResult.Stdout,
            rustResult.Stdout == dotnetResult.Stdout,
            rustResult.ExitCode,
            dotnetResult.ExitCode,
            rustResult.ExitCode == dotnetResult.ExitCode
        );
    }

    /// <summary>
    /// Runs a single binary once and returns its captured stdout and exit code.
    /// </summary>
    /// <remarks>
    /// Used by the rewrite-parity harness to drive each side independently (the two
    /// binaries live in different locations and, for the oracle, need an isolated
    /// environment). Stderr is intentionally ignored: the oracle emits a
    /// <c>[rtk] /!\ No hook installed</c> warning to stderr that is not part of the
    /// <c>rewrite</c> contract.
    /// </remarks>
    /// <param name="fileName">Executable to run (or the <c>dotnet</c> muxer).</param>
    /// <param name="args">Arguments to pass to the executable.</param>
    /// <param name="workingDirectory">Working directory for the child process, or null.</param>
    /// <param name="environment">Environment overrides to apply, or null to inherit.</param>
    /// <param name="cancellationToken">Token to cancel the execution.</param>
    /// <returns>A tuple of the captured stdout and the process exit code.</returns>
    public static async Task<(string Stdout, int ExitCode)> RunAsync(
        string fileName,
        string[] args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default
    )
    {
        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(
            new ExecutionRequest(fileName, args, workingDirectory, environment),
            cancellationToken
        ).ConfigureAwait(false);

        return (result.Stdout, result.ExitCode);
    }
}
