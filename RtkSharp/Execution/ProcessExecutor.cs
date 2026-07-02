using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RtkSharp.Execution;

public sealed class ProcessExecutor : IProcessExecutor
{
    public async ValueTask<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process();
        process.StartInfo = CreateStartInfo(request);
        process.EnableRaisingEvents = true;

        try
        {
            if (!process.Start())
            {
                stopwatch.Stop();
                return new ExecutionResult("", "", 127, stopwatch.Elapsed, false, "Process did not start.", false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            stopwatch.Stop();
            return new ExecutionResult("", "", 127, stopwatch.Elapsed, false, ex.Message, false);
        }

        Task<string> stdoutTask = process.StartInfo.RedirectStandardOutput
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : Task.FromResult("");
        Task<string> stderrTask = process.StartInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync(cancellationToken)
            : Task.FromResult("");

        using var timeoutCts = request.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : null;
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ExecutionResult(stdout, stderr, process.ExitCode, stopwatch.Elapsed, true, null, false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            stopwatch.Stop();

            var stdout = await ReadCompletedOrEmptyAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadCompletedOrEmptyAsync(stderrTask).ConfigureAwait(false);
            return new ExecutionResult(
                stdout,
                stderr,
                -1,
                stopwatch.Elapsed,
                true,
                $"Process timed out after {request.Timeout!.Value}.",
                true
            );
        }
    }

    private static ProcessStartInfo CreateStartInfo(ExecutionRequest request)
    {
        var capture = request.CaptureMode != ExecutionCaptureMode.Inherit;
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFileName(request),
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            RedirectStandardInput = false,
            StandardOutputEncoding = capture ? Encoding.UTF8 : null,
            StandardErrorEncoding = capture ? Encoding.UTF8 : null
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        return startInfo;
    }

    private static string ResolveFileName(ExecutionRequest request)
    {
        string? requestPath = null;
        string? requestPathExt = null;
        request.Environment?.TryGetValue("PATH", out requestPath);
        request.Environment?.TryGetValue("PATHEXT", out requestPathExt);

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Environment.CurrentDirectory
            : request.WorkingDirectory;

        return PathResolver.Resolve(
            request.FileName,
            requestPath ?? Environment.GetEnvironmentVariable("PATH"),
            requestPathExt ?? Environment.GetEnvironmentVariable("PATHEXT"),
            workingDirectory
        );
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Preserve timeout reporting even if process cleanup races with exit.
        }
    }

    private static async Task<string> ReadCompletedOrEmptyAsync(Task<string> readTask)
    {
        try
        {
            return readTask.IsCompleted ? await readTask.ConfigureAwait(false) : "";
        }
        catch
        {
            return "";
        }
    }
}
