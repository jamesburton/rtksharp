using System.Globalization;
using System.Text;
using RtkSharp.Commands.Git;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk pipe</c> CLI verb: a stdin-filter dispatcher that lets a caller run
/// arbitrary command output through one of RTK's compaction filters without RTK itself invoking
/// the underlying tool (e.g. <c>some-runner | rtk pipe --filter pytest</c>). Faithful port of
/// Rust <c>src/cmds/system/pipe_cmd.rs</c> (549 lines including its inline test module).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ecosystem-filter delegation status (Phase 6, Task 5 — resolved, do not re-litigate).</b>
/// Only <c>git-log</c>/<c>git-diff</c>/<c>git-status</c> delegate to a genuinely-ported ecosystem
/// filter (<see cref="GitCommand"/>'s <c>FilterLogOutput</c>/<c>CompactDiff</c>/
/// <c>FormatStatusOutput</c>) — <c>Git</c> is the only ecosystem module ported as of this phase.
/// None of the Rust/Cargo, Python (pytest/mypy/ruff), Go, or JS (tsc/vitest/prettier) filter
/// modules exist in RtkSharp yet (the JS stack is Phase 8; Go/Python/Rust ecosystems have no
/// scoped phase yet), so every alias that would delegate to one of those in Rust instead
/// resolves to <see cref="IdentityFilter"/> here — an intentional, disclosed passthrough (never
/// blocks the caller, matches this codebase's fallback-pattern convention), not a faked filter.
/// See <c>docs/parity/compatibility-ledger.md</c> for the ledgered gap. The two pipe-specific
/// mini filters (<see cref="GrepWrapper"/>/<see cref="FindWrapper"/>) have no ecosystem-module
/// delegation target in Rust either — they're pipe-only helpers there too — and are fully ported.
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

    // Rust CAP_WARNINGS (= 10) from src/core/truncate.rs (pipe_cmd.rs:7-8), reused verbatim as
    // the per-file/per-directory item cap for both mini-filters below.
    private const int MaxPipeMatches = 10;
    private const int MaxPipeFiles = 10;

    // Rust CAP_LIST (= 20) from src/core/truncate.rs (pipe_cmd.rs:9), reused verbatim as
    // find_wrapper's directory-group cap.
    private const int MaxPipeDirs = 20;

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
            filterFn = AutoDetectFilter(buf);
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
        // Gap-disclosed: no Rust/Cargo ecosystem filter module ported in RtkSharp yet.
        "cargo-test" or "cargo" => IdentityFilter,

        // Gap-disclosed: no Python ecosystem filter module ported in RtkSharp yet.
        "pytest" => IdentityFilter,
        "mypy" => IdentityFilter,
        "ruff-check" => IdentityFilter,
        "ruff-format" => IdentityFilter,

        // Gap-disclosed: no Go ecosystem filter module ported in RtkSharp yet.
        "go-test" => IdentityFilter,
        "go-build" => IdentityFilter,

        // Gap-disclosed: JS ecosystem filters are scoped to Phase 8, not yet ported.
        "tsc" => IdentityFilter,
        "vitest" => IdentityFilter,
        "prettier" => IdentityFilter,

        // Gap-disclosed: no dedicated `rtk log` command ported in RtkSharp yet.
        "log" => IdentityFilter,

        // Genuinely ported: fresh pipe-only mini filters (no Rust ecosystem delegation either).
        "grep" or "rg" => GrepWrapper,
        "find" or "fd" => FindWrapper,

        // Genuinely ported: delegates to GitCommand's already-ported filter logic.
        "git-log" => GitLogWrapper,
        "git-diff" => GitDiffWrapper,
        "git-status" => GitStatusWrapper,

        _ => null,
    };

    private static string GitLogWrapper(string input) =>
        GitCommand.FilterLogOutput(input, limit: 50, userSetLimit: false, userFormat: false);

    private static string GitDiffWrapper(string input) =>
        GitCommand.CompactDiff(input, maxLines: 200);

    private static string GitStatusWrapper(string input) =>
        GitCommand.FormatStatusOutput(input);

    /// <summary>
    /// Groups <c>file:line:content</c> lines by file, capping each file's shown matches at
    /// <see cref="MaxPipeMatches"/> and appending a <c>+N</c> overflow marker for the rest. Ports
    /// <c>grep_wrapper</c> (<c>pipe_cmd.rs</c>:60-96) — a pipe-only helper with no Rust ecosystem
    /// delegation target.
    /// </summary>
    /// <param name="input">The raw grep/ripgrep-style output.</param>
    /// <returns>The grouped, capped summary, or <paramref name="input"/> unchanged if no line matched the <c>file:line:content</c> shape.</returns>
    internal static string GrepWrapper(string input)
    {
        var byFile = new Dictionary<string, List<(string LineNum, string Content)>>(StringComparer.Ordinal);
        var total = 0;

        foreach (var line in ReadCommand.SplitLines(input))
        {
            var parts = line.Split(':', 3);
            if (parts.Length == 3 && IsUnsignedInteger(parts[1]))
            {
                total++;
                if (!byFile.TryGetValue(parts[0], out var list))
                {
                    list = [];
                    byFile[parts[0]] = list;
                }

                list.Add((parts[1], parts[2]));
            }
        }

        if (total == 0)
        {
            return input;
        }

        var sb = new StringBuilder();
        sb.Append(total).Append(" matches in ").Append(byFile.Count).Append("F:\n\n");

        foreach (var file in byFile.Keys.OrderBy(f => f, StringComparer.Ordinal))
        {
            var matches = byFile[file];
            sb.Append("[file] ").Append(file).Append(" (").Append(matches.Count).Append("):\n");

            foreach (var (lineNum, content) in matches.Take(MaxPipeMatches))
            {
                sb.Append("  ").Append(lineNum.PadLeft(4)).Append(": ").Append(content.Trim()).Append('\n');
            }

            if (matches.Count > MaxPipeMatches)
            {
                sb.Append("  +").Append(matches.Count - MaxPipeMatches).Append('\n');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Groups file paths by directory, capping the number of directories shown at
    /// <see cref="MaxPipeDirs"/> and each directory's files at <see cref="MaxPipeFiles"/>. Ports
    /// <c>find_wrapper</c> (<c>pipe_cmd.rs</c>:98-140) — a pipe-only helper with no Rust ecosystem
    /// delegation target.
    /// </summary>
    /// <param name="input">The raw find/fd-style path listing.</param>
    /// <returns>The grouped, capped summary, or <paramref name="input"/> unchanged if it contains no non-empty lines.</returns>
    internal static string FindWrapper(string input)
    {
        var paths = ReadCommand.SplitLines(input).Where(l => l.Trim().Length > 0).ToList();
        if (paths.Count == 0)
        {
            return input;
        }

        var byDir = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/');
            var dir = slash >= 0 ? path[..slash] : ".";
            var name = slash >= 0 ? path[(slash + 1)..] : path;

            if (!byDir.TryGetValue(dir, out var list))
            {
                list = [];
                byDir[dir] = list;
            }

            list.Add(name);
        }

        var sb = new StringBuilder();
        sb.Append(paths.Count).Append(" files in ").Append(byDir.Count).Append(" dirs:\n\n");

        var sortedDirs = byDir.Keys.OrderBy(d => d, StringComparer.Ordinal).ToList();

        foreach (var dir in sortedDirs.Take(MaxPipeDirs))
        {
            var files = byDir[dir];
            sb.Append(dir).Append("/  (").Append(files.Count).Append(")\n");

            foreach (var f in files.Take(MaxPipeFiles))
            {
                sb.Append("  ").Append(f).Append('\n');
            }

            if (files.Count > MaxPipeFiles)
            {
                sb.Append("  +").Append(files.Count - MaxPipeFiles).Append('\n');
            }
        }

        if (sortedDirs.Count > MaxPipeDirs)
        {
            sb.Append("\n+").Append(sortedDirs.Count - MaxPipeDirs).Append(" more dirs\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// True if <paramref name="s"/> is a non-empty run of ASCII digits — the .NET equivalent of
    /// Rust's <c>str::parse::&lt;usize&gt;()</c> succeeding (no sign, no whitespace, no overflow
    /// beyond what a line number would ever reach).
    /// </summary>
    private static bool IsUnsignedInteger(string s) =>
        s.Length > 0 && s.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// Signature-sniffs the first 1024 characters of <paramref name="input"/> to pick a filter
    /// when the caller did not supply <c>-f</c>/<c>--filter</c>. Ports <c>auto_detect_filter</c>
    /// (<c>pipe_cmd.rs</c>:142-198).
    /// </summary>
    /// <param name="input">The full stdin content (already decoded to a string).</param>
    /// <returns>The detected filter function, or <see cref="IdentityFilter"/> if no signature matched.</returns>
    internal static Func<string, string> AutoDetectFilter(string input)
    {
        var end = Math.Min(input.Length, 1024);

        // Rust avoids a panic by flooring to the nearest UTF-8 char boundary (a multi-byte
        // sequence could straddle byte 1024). .NET strings are UTF-16, so the equivalent hazard is
        // slicing inside a surrogate pair (an astral character, e.g. an emoji, represented as two
        // UTF-16 code units) — floor by one position when that would happen.
        end = FloorCharBoundary(input, end);
        var first1K = input[..end];

        if (first1K.Contains("test result:", StringComparison.Ordinal) &&
            first1K.Contains("passed;", StringComparison.Ordinal))
        {
            // Gap-disclosed: would be cargo-test in Rust; resolves to identity here (see ResolveFilter).
            return IdentityFilter;
        }

        if (first1K.Contains("=== test session starts", StringComparison.Ordinal))
        {
            // Gap-disclosed: would be pytest in Rust; resolves to identity here (see ResolveFilter).
            return IdentityFilter;
        }

        var firstTrimmed = first1K.TrimStart();
        if (firstTrimmed.StartsWith('{') && first1K.Contains("\"Action\"", StringComparison.Ordinal))
        {
            // Gap-disclosed: would be go-test in Rust; resolves to identity here (see ResolveFilter).
            return IdentityFilter;
        }

        if (first1K.Contains(": error:", StringComparison.Ordinal) &&
            first1K.Contains(".py:", StringComparison.Ordinal))
        {
            // Gap-disclosed: would be mypy in Rust; resolves to identity here (see ResolveFilter).
            return IdentityFilter;
        }

        // grep/rg: at least one of the first 5 non-empty lines matches file:number:content.
        if (ReadCommand.SplitLines(first1K)
            .Take(5)
            .Where(l => l.Trim().Length > 0)
            .Any(LooksLikeGrepLine))
        {
            return GrepWrapper;
        }

        if (first1K.Contains("\"testResults\"", StringComparison.Ordinal) ||
            first1K.Contains("\"numTotalTests\"", StringComparison.Ordinal))
        {
            // Gap-disclosed: would be vitest in Rust; resolves to identity here (see ResolveFilter).
            return IdentityFilter;
        }

        // find/fd: every non-empty line looks like a file path, minimum 3 lines.
        var lines = ReadCommand.SplitLines(first1K);
        var pathLikeLines = lines.Count(LooksPathLike);
        var nonEmptyLines = lines.Count(l => l.Trim().Length > 0);
        if (nonEmptyLines >= 3 && pathLikeLines == nonEmptyLines)
        {
            return FindWrapper;
        }

        return IdentityFilter;
    }

    private static bool LooksLikeGrepLine(string line)
    {
        var parts = line.Split(':', 3);
        return parts.Length == 3 && IsUnsignedInteger(parts[1]);
    }

    private static bool LooksPathLike(string line)
    {
        var t = line.Trim();
        return t.Length > 0 &&
               !t.Contains(':', StringComparison.Ordinal) &&
               (t.StartsWith('.') || t.StartsWith('/') || t.Contains('/', StringComparison.Ordinal));
    }

    /// <summary>
    /// Floors <paramref name="index"/> to the nearest position that does not split a UTF-16
    /// surrogate pair — the .NET analogue of Rust's <c>str::floor_char_boundary</c> for UTF-8.
    /// </summary>
    /// <param name="s">The string being sliced.</param>
    /// <param name="index">The candidate slice endpoint.</param>
    /// <returns>The same index, or one position earlier if it would split a surrogate pair.</returns>
    private static int FloorCharBoundary(string s, int index)
    {
        if (index <= 0 || index >= s.Length)
        {
            return Math.Clamp(index, 0, s.Length);
        }

        if (char.IsLowSurrogate(s[index]) && char.IsHighSurrogate(s[index - 1]))
        {
            return index - 1;
        }

        return index;
    }

    /// <summary>
    /// Never-blocks identity passthrough: used both for the deliberate ecosystem-filter gap (see
    /// the class remarks) and as the auto-detect fallback when no signature matched. Ports Rust's
    /// <c>identity_filter</c> (<c>pipe_cmd.rs</c>:200-202).
    /// </summary>
    /// <param name="input">The input text.</param>
    /// <returns><paramref name="input"/>, unchanged.</returns>
    private static string IdentityFilter(string input) => input;

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
