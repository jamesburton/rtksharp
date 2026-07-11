using System.Globalization;
using System.Text;
using RtkSharp.Commands.Go;
using RtkSharp.Commands.Js;
using RtkSharp.Commands.Python;
using RtkSharp.Commands.Rust;
using RtkSharp.Filters.Commands.Git;
using RtkSharp.Filters.Commands.Go;
using RtkSharp.Filters.Commands.Js;
using RtkSharp.Filters.Commands.Python;
using RtkSharp.Filters.Commands.Rust;
using RtkSharp.Filters.Commands.System;
using RtkSharp.Parser;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk pipe</c> CLI verb: a stdin-filter dispatcher that lets a caller run
/// arbitrary command output through one of RTK's compaction filters without RTK itself invoking
/// the underlying tool (e.g. <c>some-runner | rtk pipe --filter pytest</c>). Faithful port of
/// Rust <c>src/cmds/system/pipe_cmd.rs</c> (549 lines including its inline test module).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ecosystem-filter delegation — now fully wired (was disclosed-gap, resolved).</b> Every
/// alias delegates to its real ported ecosystem filter: <c>cargo-test</c>/<c>cargo</c> →
/// <see cref="CargoFilters.FilterCargoTest"/>, <c>pytest</c> →
/// <see cref="PytestFilters.FilterPytestOutput"/>, <c>mypy</c> →
/// <see cref="MypyFilters.FilterMypyOutput"/>, <c>ruff-check</c>/<c>ruff-format</c> →
/// <see cref="RuffFilters.FilterRuffCheckJson"/>/<see cref="RuffFilters.FilterRuffFormat"/>,
/// <c>go-test</c>/<c>go-build</c> → <see cref="GoFilters.FilterGoTestJson"/>/
/// <see cref="GoFilters.FilterGoBuild"/>, <c>tsc</c> → <see cref="TscCommand.FilterTscOutput"/>,
/// <c>vitest</c> → <see cref="PipeFilters.VitestWrapper"/> (mirrors Rust's own
/// <c>vitest_wrapper</c>: parses via the shared <see cref="VitestFilters.VitestParser"/> then
/// always formats <see cref="FormatMode.Compact"/>, regardless of the process's own verbosity —
/// pipe has no verbosity concept of its own), <c>prettier</c> →
/// <see cref="PrettierCommand.FilterPrettierOutput"/>, and <c>log</c> →
/// <see cref="LogFilters.AnalyzeLogs"/> (Rust's <c>run_stdin_str</c> equivalent).
/// <c>git-log</c>/<c>git-diff</c>/<c>git-status</c> already delegated to <see cref="GitCommand"/>'s
/// filters. The two pipe-specific mini filters (<see cref="PipeFilters.GrepWrapper"/>/
/// <see cref="PipeFilters.FindWrapper"/>) have no ecosystem-module delegation target in Rust
/// either — they're pipe-only helpers there too — and were already fully ported, now living in
/// <see cref="PipeFilters"/> (<c>RtkSharp.Filters</c>).
/// </para>
/// <para>
/// <b>Exception-safety is a deliberate feature, not a bug.</b> <see cref="ApplyFilter"/> wraps
/// every filter invocation in a try/catch mirroring Rust's <c>catch_unwind</c>
/// (<c>pipe_cmd.rs</c>:204-210): any exception thrown by a filter function is swallowed, a
/// warning is printed to stderr, and the raw unfiltered input is returned instead of propagating
/// — so a bug in one filter can never turn a pipe invocation into a hard failure.
/// </para>
/// <para>
/// <b>Hard stdin-size bail, not truncation.</b> Unlike this codebase's usual truncate-and-append-
/// a-marker convention, reading more than <see cref="RawCap"/> bytes from stdin (outside
/// <c>--passthrough</c> mode) is a hard failure with a non-zero exit — mirrored verbatim from
/// Rust's <c>anyhow::bail!("stdin exceeds {} byte limit", RAW_CAP)</c> (<c>pipe_cmd.rs</c>:224-
/// 226). This is unusual for RTK and deliberate; do not "fix" it into a truncation.
/// </para>
/// <para>
/// <b>No tracking.</b> Like Rust's <c>pipe_cmd::run</c>, this command never calls into any
/// <c>Tracker</c>/<c>TimedExecution</c> — it has no underlying process invocation to time, and
/// the Rust source records no usage metrics for this command either.
/// </para>
/// </remarks>
public static class PipeCommand
{
    // Rust `RAW_CAP` (src/core/stream.rs), reused verbatim as the hard stdin size ceiling.
    // Exceeding it is a HARD FAILURE (pipe_cmd.rs:224-226), unlike the usual truncate-and-
    // continue convention used elsewhere in this codebase.
    private const int RawCap = 10_485_760;

    // Exact alias list + order for the "Unknown filter" error message (pipe_cmd.rs:230-235).
    private static readonly string[] KnownFilterNames =
    [
        "cargo-test", "pytest", "go-test", "go-build", "tsc", "vitest", "grep", "rg",
        "find", "fd", "git-log", "git-diff", "git-status", "log", "mypy", "ruff-check",
        "ruff-format", "prettier"
    ];

    /// <summary>
    /// Registry entry point. Runs <c>rtk pipe</c> with the given arguments (the remainder after
    /// the <c>pipe</c> verb), reading from the process's real standard input.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>pipe</c>.</param>
    /// <returns>0 on success, 1 on a top-level failure (unknown filter / stdin too large / relay failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, stdin: null);

    /// <summary>
    /// Runs <c>rtk pipe</c> with an injectable stdin <see cref="Stream"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>pipe</c>.</param>
    /// <param name="stdin">The stream to read stdin bytes from, or null for the process's real standard input.</param>
    /// <returns>0 on success, 1 on a top-level failure (unknown filter / stdin too large / relay failure).</returns>
    internal static Task<int> RunAsync(IReadOnlyList<string> args, Stream? stdin) =>
        RunAsync(args, stdin, stdout: null);

    /// <summary>
    /// Runs <c>rtk pipe</c> with injectable stdin/stdout <see cref="Stream"/>s, for testing. The
    /// <c>--passthrough</c> byte-relay path writes directly to a <see cref="Stream"/> (mirroring
    /// Rust's <c>io::copy(&amp;mut stdin, &amp;mut stdout)</c>) rather than through
    /// <see cref="Console.Out"/>, so exercising it in a test requires injecting the destination
    /// stream itself — redirecting <see cref="Console.Out"/> alone would not observe it.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>pipe</c>.</param>
    /// <param name="stdin">The stream to read stdin bytes from, or null for the process's real standard input.</param>
    /// <param name="stdout">The stream <c>--passthrough</c> relays stdin bytes to, or null for the process's real standard output.</param>
    /// <returns>0 on success, 1 on a top-level failure (unknown filter / stdin too large / relay failure).</returns>
    internal static Task<int> RunAsync(IReadOnlyList<string> args, Stream? stdin, Stream? stdout)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return Task.FromResult(RunCore(args, stdin ?? Console.OpenStandardInput(), stdout));
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return Task.FromResult(1);
        }
    }

    /// <summary>
    /// Parses <c>-f</c>/<c>--filter &lt;name&gt;</c>/<c>--filter=&lt;name&gt;</c> and
    /// <c>--passthrough</c> out of the raw pipe arguments.
    /// </summary>
    /// <param name="args">The raw CLI arguments following the <c>pipe</c> verb.</param>
    /// <returns>The resolved filter name (or null if not given) and whether <c>--passthrough</c> was set.</returns>
    internal static (string? FilterName, bool Passthrough) ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? filterName = null;
        var passthrough = false;

        var i = 0;
        while (i < args.Count)
        {
            var a = args[i];

            if (a == "-f" || a == "--filter")
            {
                if (i + 1 < args.Count)
                {
                    filterName = args[i + 1];
                    i += 2;
                }
                else
                {
                    i++;
                }

                continue;
            }

            if (a.StartsWith("--filter=", StringComparison.Ordinal))
            {
                filterName = a["--filter=".Length..];
                i++;
                continue;
            }

            if (a == "--passthrough")
            {
                passthrough = true;
                i++;
                continue;
            }

            i++;
        }

        return (filterName, passthrough);
    }

    private static int RunCore(IReadOnlyList<string> args, Stream stdin, Stream? stdout)
    {
        var (filterName, passthrough) = ParseArgs(args);

        if (passthrough)
        {
            // Only open (and dispose) the real standard-output stream when the caller did not
            // inject one - disposing an injected test MemoryStream here would make its contents
            // inaccessible to the caller afterward.
            if (stdout is not null)
            {
                stdin.CopyTo(stdout);
            }
            else
            {
                using var realStdout = Console.OpenStandardOutput();
                stdin.CopyTo(realStdout);
            }

            return 0;
        }

        // Mirrors Rust's `stdin.take(RAW_CAP + 1).read_to_string(&mut buf)` (pipe_cmd.rs:220-223):
        // read at most one byte past the cap so an oversized input is detected without buffering
        // the caller's entire (potentially unbounded) stream.
        var bytes = ReadUpTo(stdin, RawCap + 1);
        if (bytes.Length > RawCap)
        {
            throw new InvalidOperationException($"stdin exceeds {RawCap} byte limit");
        }

        var buf = Encoding.UTF8.GetString(bytes);

        Func<string, string> filterFn;
        if (filterName is not null)
        {
            filterFn = ResolveFilter(filterName) ?? throw new InvalidOperationException(
                $"Unknown filter '{filterName}'. Available: {string.Join(", ", KnownFilterNames)}");
        }
        else
        {
            filterFn = PipeFilters.AutoDetectFilter(buf);
        }

        var output = ApplyFilter(filterFn, buf);

        // No forced trailing newline beyond whatever the filter naturally produced
        // (pipe_cmd.rs:241's `print!("{}", output)`).
        Console.Out.Write(output);
        return 0;
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes from <paramref name="stream"/>, stopping
    /// early on end-of-stream. Equivalent to Rust's <c>stream.take(maxBytes)</c>.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="maxBytes">The maximum number of bytes to read.</param>
    /// <returns>The bytes read (may be shorter than <paramref name="maxBytes"/> if the stream ended first).</returns>
    private static byte[] ReadUpTo(Stream stream, int maxBytes)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        var totalRead = 0;

        while (totalRead < maxBytes)
        {
            var toRead = Math.Min(buffer.Length, maxBytes - totalRead);
            var read = stream.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            ms.Write(buffer, 0, read);
            totalRead += read;
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Exact-matches <paramref name="name"/> against the named-filter alias table
    /// (<c>pipe_cmd.rs</c>:11-31).
    /// </summary>
    /// <param name="name">The filter name (as given via <c>-f</c>/<c>--filter</c>).</param>
    /// <returns>The resolved filter function, or null if <paramref name="name"/> is not a known alias.</returns>
    internal static Func<string, string>? ResolveFilter(string name) => name switch
    {
        "cargo-test" or "cargo" => CargoFilters.FilterCargoTest,

        "pytest" => PytestFilters.FilterPytestOutput,
        "mypy" => MypyFilters.FilterMypyOutput,
        "ruff-check" => RuffFilters.FilterRuffCheckJson,
        "ruff-format" => RuffFilters.FilterRuffFormat,

        "go-test" => GoFilters.FilterGoTestJson,
        "go-build" => GoFilters.FilterGoBuild,

        "tsc" => TscCommand.FilterTscOutput,
        "vitest" => PipeFilters.VitestWrapper,
        "prettier" => PrettierCommand.FilterPrettierOutput,

        "log" => LogFilters.AnalyzeLogs,

        // Genuinely ported: fresh pipe-only mini filters (no Rust ecosystem delegation either).
        "grep" or "rg" => PipeFilters.GrepWrapper,
        "find" or "fd" => PipeFilters.FindWrapper,

        // Genuinely ported: delegates to GitCommand's already-ported filter logic.
        "git-log" => GitLogWrapper,
        "git-diff" => GitDiffWrapper,
        "git-status" => GitStatusWrapper,

        _ => null,
    };


    private static string GitLogWrapper(string input) =>
        GitFilters.FilterLogOutput(input, limit: 50, userSetLimit: false, userFormat: false);

    private static string GitDiffWrapper(string input) =>
        GitFilters.CompactDiff(input, maxLines: 200);

    private static string GitStatusWrapper(string input) =>
        GitFilters.FormatStatusOutput(input);

    /// <summary>
    /// Invokes <paramref name="filterFn"/>, catching any exception it throws and falling back to
    /// the raw <paramref name="input"/> with a stderr warning instead of propagating — the .NET
    /// equivalent of Rust's <c>catch_unwind</c>-based panic recovery (<c>pipe_cmd.rs</c>:204-210).
    /// This is a deliberate graceful-degradation feature: a bug in one filter must never turn a
    /// pipe invocation into a hard failure for the caller.
    /// </summary>
    /// <param name="filterFn">The filter function to invoke.</param>
    /// <param name="input">The input text to filter.</param>
    /// <returns>The filter's output, or <paramref name="input"/> unchanged if the filter threw.</returns>
    internal static string ApplyFilter(Func<string, string> filterFn, string input)
    {
        try
        {
            return filterFn(input);
        }
        catch (Exception)
        {
            Console.Error.Write("[rtk] warning: filter panicked — passing through raw output\n");
            return input;
        }
    }
}
