using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Unicode;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk curl</c> CLI verb: runs the real <c>curl</c> binary and condenses long
/// output for human consumption. Faithful port of Rust <c>src/cmds/cloud/curl_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not "auto-JSON detection and schema output" despite that description in
/// <c>docs/parity/command-inventory.md</c>.</b> The Rust source never parses JSON into a schema or
/// summary — it's a much simpler heuristic: pass the full body through unchanged for pipes/redirects,
/// JSON-shaped bodies, and small bodies; truncate to 500 bytes with a tee-file recovery hint only for
/// large non-JSON bodies on an interactive terminal. This port implements exactly that (verified
/// directly against <c>curl_cmd.rs</c>), not schema inference.
/// </para>
/// <para>
/// <b>Raw-byte capture, not the shared <see cref="ProcessExecutor"/>/<see cref="ExecutionResult"/>
/// (string-only) pipeline.</b> Rust's <c>cmd.output()</c> captures stdout as raw <c>Vec&lt;u8&gt;</c>
/// specifically so a binary download's non-UTF-8 bytes are never lossily mangled before the
/// <see cref="IsBinary"/> check (issue #1087) — using this project's shared string-based executor
/// would defeat that check before it ever runs. This command therefore spawns <c>curl</c> directly via
/// <see cref="Process"/>, reading <see cref="Process.StandardOutput"/>/<see cref="Process.StandardError"/>'s
/// underlying <see cref="Stream"/> as raw bytes — matching Rust's own <c>curl_cmd.rs</c>, which
/// likewise bypasses any shared filter-pipeline abstraction and calls <c>resolved_command("curl")</c>
/// directly.
/// </para>
/// <para>
/// <b>Byte length, not UTF-16 code-unit length, for the 500-byte threshold and truncation
/// boundary.</b> Rust's <c>trimmed.len()</c>/<c>is_char_boundary</c> operate on UTF-8 byte length;
/// this port re-encodes the trimmed string to UTF-8 bytes for the length check and walks back over
/// continuation bytes (<c>0b10xxxxxx</c>) to find a safe truncation point, then decodes only the kept
/// prefix back to a string — never truncating a .NET <see cref="string.Length"/> (UTF-16 code units)
/// directly, which would diverge from Rust for any non-ASCII response body (exercised by the port's
/// own multibyte-boundary test, mirroring Rust's <c>test_filter_curl_multibyte_boundary</c>).
/// </para>
/// </remarks>
public static class CurlCommand
{
    private const int MaxResponseSize = 500;

    /// <summary>
    /// Registry entry point. Runs <c>rtk curl</c> with the given arguments (the remainder after the
    /// <c>curl</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>curl</c>, forwarded verbatim to real <c>curl</c>.</param>
    /// <returns>curl's real exit code, or 1 if curl itself could not be spawned.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await RunCoreAsync(args, RuntimeOptions.Verbosity).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Guarded proactively, not thrown from this command today — see DockerCommand/
            // PrismaCommand's remarks for the swallowed-exception bug this prevents.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// The testable core of <see cref="RunAsync"/>. Faithful port of <c>run</c>
    /// (<c>curl_cmd.rs</c>:23-92).
    /// </summary>
    /// <param name="args">The arguments to forward to <c>curl</c> (after a leading <c>-s</c> this port always adds).</param>
    /// <param name="verbose">The top-level verbosity count (Rust's <c>cli.verbose</c>); &gt; 0 prints the resolved command line to stderr.</param>
    /// <param name="binaryStdout">
    /// The destination for the binary-passthrough branch's raw bytes. Defaults to
    /// <see cref="Console.OpenStandardOutput()"/> (the real process stdout, which — unlike
    /// <see cref="Console.Out"/> — is not affected by <see cref="Console.SetOut"/>); tests inject a
    /// <see cref="MemoryStream"/> here to observe the exact bytes written.
    /// </param>
    /// <returns>curl's real exit code.</returns>
    internal static async Task<int> RunCoreAsync(string[] args, int verbose, Stream? binaryStdout = null)
    {
        var timer = TimedExecution.Start();
        var resolvedPath = PathResolver.Resolve("curl");

        var psi = new ProcessStartInfo(resolvedPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-s");
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (verbose > 0)
        {
            Console.Error.Write($"Running: curl -s {string.Join(' ', args)}\n");
        }

        using var process = Process.Start(psi) ?? throw new IOException("Failed to run curl");

        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        var stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream);
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stdoutBytes = await stdoutTask.ConfigureAwait(false);
        var stderrBytes = await stderrTask.ConfigureAwait(false);
        var exitCode = process.ExitCode;

        var argsJoined = string.Join(' ', args);

        // Skip filtering on failure: curl can return HTML error bodies that would be misleading to
        // summarize, and we want the real exit code surfaced (curl_cmd.rs:43-55).
        if (exitCode != 0)
        {
            var stderrStr = Encoding.UTF8.GetString(stderrBytes).Trim();
            var stdoutStr = Encoding.UTF8.GetString(stdoutBytes).Trim();
            var msg = stderrStr.Length == 0 ? stdoutStr : stderrStr;
            Console.Error.Write($"FAILED: curl {msg}\n");
            return exitCode;
        }

        // Binary detection: passthrough raw bytes and skip filtering entirely, tracked as 0%-savings
        // passthrough (curl_cmd.rs:57-73).
        if (IsBinary(stdoutBytes))
        {
            if (binaryStdout is not null)
            {
                await binaryStdout.WriteAsync(stdoutBytes).ConfigureAwait(false);
            }
            else
            {
                using var stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(stdoutBytes).ConfigureAwait(false);
            }

            timer.TrackPassthrough($"curl {argsJoined}", $"rtk curl {argsJoined}");
            return exitCode;
        }

        var raw = Encoding.UTF8.GetString(stdoutBytes);
        var isTty = !Console.IsOutputRedirected;
        var filtered = FilterCurlOutput(raw, isTty);

        Console.Out.Write(filtered.Content + "\n");
        if (filtered.TeeHint is { } hint)
        {
            Console.Out.Write(hint + "\n");
        }

        timer.Track($"curl {argsJoined}", $"rtk curl {argsJoined}", raw, filtered.Content);

        return exitCode;
    }

    /// <summary>
    /// Reports whether <paramref name="bytes"/> is not valid UTF-8 — exactly the condition under
    /// which a lossy UTF-8 decode would replace invalid bytes with U+FFFD and corrupt binary content
    /// (gzip/zip/png/pdf/elf/...). Faithful port of <c>is_binary</c> (<c>curl_cmd.rs</c>:101-103).
    /// </summary>
    /// <param name="bytes">The byte sequence to check.</param>
    /// <returns><see langword="true"/> if <paramref name="bytes"/> is not valid UTF-8.</returns>
    internal static bool IsBinary(ReadOnlySpan<byte> bytes) => !Utf8.IsValid(bytes);

    /// <summary>
    /// Decides whether to pass a curl response body through unchanged or truncate it with a tee-file
    /// recovery hint. Faithful port of <c>filter_curl_output</c> (<c>curl_cmd.rs</c>:105-154).
    /// </summary>
    /// <param name="raw">The raw, UTF-8-decoded response body.</param>
    /// <param name="isTty">Whether stdout is an interactive terminal.</param>
    /// <returns>The content to print and an optional tee-recovery hint line.</returns>
    internal static FilterResult FilterCurlOutput(string raw, bool isTty)
    {
        var trimmed = raw.Trim();
        var trimmedBytes = Encoding.UTF8.GetBytes(trimmed);

        // Heuristic: looks like a top-level JSON document. Numbers/booleans/null are always under
        // MAX_RESPONSE_SIZE so they don't need detection here.
        var looksLikeJson =
            (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            || (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            || (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2);

        // Pass through unchanged when the body looks like JSON (mid-stream truncation produces
        // invalid JSON, #1536), stdout is not a terminal (pipes/redirects need the full body, #1282),
        // or the body fits under the truncation threshold. Do NOT tee on this path — no recovery file
        // is needed when the consumer already receives the full body.
        if (!isTty || looksLikeJson || trimmedBytes.Length < MaxResponseSize)
        {
            return new FilterResult(trimmed, null);
        }

        // About to truncate for a human reader — write a tee file so the recovery hint can restore
        // the full body.
        var hint = Tee.ForceTeeHint(raw, "curl");
        if (hint is null)
        {
            // Tee disabled (RTK_TEE=0 or below the minimum tee size): nowhere to point a recovery
            // hint, so pass through rather than emit an unrecoverable truncation marker.
            return new FilterResult(trimmed, null);
        }

        // Rust's `is_char_boundary(end)` treats `end == len` as always a valid boundary (there's no
        // byte to inspect there); the `end < trimmedBytes.Length` guard reproduces that instead of
        // indexing past the end of the array when the body is exactly MaxResponseSize bytes long.
        var end = MaxResponseSize;
        while (end > 0 && end < trimmedBytes.Length && IsUtf8ContinuationByte(trimmedBytes[end]))
        {
            end--;
        }

        var truncated = Encoding.UTF8.GetString(trimmedBytes, 0, end);
        var content = $"{truncated}... ({trimmedBytes.Length} bytes total)";
        return new FilterResult(content, hint);
    }

    private static bool IsUtf8ContinuationByte(byte b) => (b & 0b1100_0000) == 0b1000_0000;

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>The result of <see cref="FilterCurlOutput"/>: the content to print and an optional tee-recovery hint.</summary>
    /// <param name="Content">The (possibly truncated) content to print.</param>
    /// <param name="TeeHint">The recovery-hint line to print after <paramref name="Content"/>, or <see langword="null"/> if none.</param>
    internal readonly record struct FilterResult(string Content, string? TeeHint);
}
