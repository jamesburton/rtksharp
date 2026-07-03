using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RtkSharp.Execution;

/// <summary>
/// Default <see cref="IProcessExecutor"/> implementation backed by <see cref="System.Diagnostics.Process"/>.
/// Supports separate, merged, and inherited stdout/stderr capture, timeout-based cancellation, and
/// optional stdin piping via <see cref="ExecutionRequest.StdinContent"/>.
/// </summary>
public sealed class ProcessExecutor : IProcessExecutor
{
    /// <summary>
    /// Executes a child process as described by <paramref name="request"/>, capturing its
    /// output according to <see cref="ExecutionRequest.CaptureMode"/> and enforcing
    /// <see cref="ExecutionRequest.Timeout"/> if set.
    /// </summary>
    /// <param name="request">The process execution request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The execution result.</returns>
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

        var merged = request.CaptureMode == ExecutionCaptureMode.Merged;
        StringBuilder? mergedBuffer = merged ? new StringBuilder() : null;
        var mergedLock = new object();

        if (merged)
        {
            process.OutputDataReceived += (_, e) => AppendMergedLine(mergedBuffer!, mergedLock, e.Data);
            process.ErrorDataReceived += (_, e) => AppendMergedLine(mergedBuffer!, mergedLock, e.Data);
        }

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

        if (request.StdinContent is not null)
        {
            // Write and close eagerly (fire-and-forget-then-await pattern via a background task)
            // so a child that reads stdin before producing output doesn't deadlock against the
            // stdout/stderr reads below.
            _ = WriteStdinAsync(process, request.StdinContent, cancellationToken);
        }

        Task<string> stdoutTask;
        Task<string> stderrTask;

        if (merged)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            stdoutTask = Task.FromResult("");
            stderrTask = Task.FromResult("");
        }
        else
        {
            stdoutTask = process.StartInfo.RedirectStandardOutput
                ? process.StandardOutput.ReadToEndAsync(cancellationToken)
                : Task.FromResult("");
            stderrTask = process.StartInfo.RedirectStandardError
                ? process.StandardError.ReadToEndAsync(cancellationToken)
                : Task.FromResult("");
        }

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

            if (merged)
            {
                var mergedOutput = ReadMerged(mergedBuffer!, mergedLock);
                return new ExecutionResult(mergedOutput, "", process.ExitCode, stopwatch.Elapsed, true, null, false);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ExecutionResult(stdout, stderr, process.ExitCode, stopwatch.Elapsed, true, null, false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            stopwatch.Stop();

            if (merged)
            {
                var mergedOutput = ReadMerged(mergedBuffer!, mergedLock);
                return new ExecutionResult(
                    mergedOutput,
                    "",
                    -1,
                    stopwatch.Elapsed,
                    true,
                    $"Process timed out after {request.Timeout!.Value}.",
                    true
                );
            }

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

    /// <summary>
    /// Writes <paramref name="content"/> to the child's redirected standard input (UTF-8, no
    /// byte-order mark) and closes the stream so the child observes EOF. Broken-pipe failures
    /// (the child exits or closes its stdin before consuming everything) are swallowed — this
    /// mirrors piping into a real shell command, where a short-lived reader on the other end of
    /// a pipe does not fail the writer.
    /// </summary>
    /// <param name="process">The started child process, with <c>RedirectStandardInput</c> set.</param>
    /// <param name="content">The text to write to the child's standard input.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    private static async Task WriteStdinAsync(Process process, string content, CancellationToken cancellationToken)
    {
        try
        {
            var writer = process.StandardInput;
            await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            writer.Close();
        }
        catch (IOException)
        {
            // Broken pipe: the child closed stdin (or exited) before reading everything.
        }
        catch (ObjectDisposedException)
        {
            // The process (and its stdin stream) already exited/disposed.
        }
    }

    private static void AppendMergedLine(StringBuilder buffer, object lockObj, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (lockObj)
        {
            buffer.AppendLine(line);
        }
    }

    private static string ReadMerged(StringBuilder buffer, object lockObj)
    {
        lock (lockObj)
        {
            return buffer.ToString();
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
            RedirectStandardInput = request.StdinContent is not null,
            StandardOutputEncoding = capture ? Encoding.UTF8 : null,
            StandardErrorEncoding = capture ? Encoding.UTF8 : null,
            StandardInputEncoding = request.StdinContent is not null ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) : null
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
