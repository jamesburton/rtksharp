using System.Text;
using RtkSharp.Filters.Commands.Go;
using RtkSharp.Filters.Commands.Js;
using RtkSharp.Filters.Commands.Python;
using RtkSharp.Filters.Commands.Rust;
using RtkSharp.Parser;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure filtering logic for the <c>rtk pipe</c> CLI verb: the two pipe-only mini filters
/// (<see cref="GrepWrapper"/>/<see cref="FindWrapper"/>) and the signature-sniffing auto-detect
/// dispatcher (<see cref="AutoDetectFilter"/>). Ported from <c>src/cmds/system/pipe_cmd.rs</c>.
/// The stdin-reading, filter-name resolution (<c>ResolveFilter</c>), and exception-safety
/// wrapper (<c>ApplyFilter</c>) logic lives in
/// <see cref="RtkSharp.Commands.System.PipeCommand"/> (<c>RtkSharp</c>).
/// </summary>
public static class PipeFilters
{
    // Rust CAP_WARNINGS (= 10) from src/core/truncate.rs (pipe_cmd.rs:7-8), reused verbatim as
    // the per-file/per-directory item cap for both mini-filters below.
    private const int MaxPipeMatches = 10;
    private const int MaxPipeFiles = 10;

    // Rust CAP_LIST (= 20) from src/core/truncate.rs (pipe_cmd.rs:9), reused verbatim as
    // find_wrapper's directory-group cap.
    private const int MaxPipeDirs = 20;

    /// <summary>
    /// Groups <c>file:line:content</c> lines by file, capping each file's shown matches at
    /// <see cref="MaxPipeMatches"/> and appending a <c>+N</c> overflow marker for the rest. Ports
    /// <c>grep_wrapper</c> (<c>pipe_cmd.rs</c>:60-96) — a pipe-only helper with no Rust ecosystem
    /// delegation target.
    /// </summary>
    /// <param name="input">The raw grep/ripgrep-style output.</param>
    /// <returns>The grouped, capped summary, or <paramref name="input"/> unchanged if no line matched the <c>file:line:content</c> shape.</returns>
    public static string GrepWrapper(string input)
    {
        var byFile = new Dictionary<string, List<(string LineNum, string Content)>>(StringComparer.Ordinal);
        var total = 0;

        foreach (var line in ReadFilters.SplitLines(input))
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
    public static string FindWrapper(string input)
    {
        var paths = ReadFilters.SplitLines(input).Where(l => l.Trim().Length > 0).ToList();
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
    public static Func<string, string> AutoDetectFilter(string input)
    {
        // NOT a byte-for-byte port of the window size: Rust's 1024 is a BYTE count and a raw
        // `&input[..end]` slice PANICS if it splits a multi-byte UTF-8 sequence, so Rust must floor
        // to a char boundary to avoid crashing. .NET's `input[..end]` here is a 1024 UTF-16
        // *code-unit* count, and .NET range/substring slicing does NOT throw when it splits a
        // surrogate pair (it silently yields a string ending in a lone high surrogate) — so there is
        // no crash hazard to avoid in this code path. `FloorCharBoundary` below is purely defensive
        // (keeps the sniff window free of a lone surrogate, which is harmless either way since every
        // signature checked below is plain ASCII and appears well before any realistic boundary) —
        // it is not preventing a panic the way Rust's floor does. On heavily non-ASCII input the
        // 1024-char vs. 1024-byte windows also cover different amounts of content (e.g. 1024 CJK
        // characters ≈ 3072 UTF-8 bytes, so Rust would see roughly 1/3 as much text) — in practice
        // this doesn't matter since every signature is ASCII and appears at the very start of
        // real tool output, but the window sizes are not an exact byte-for-byte match.
        var end = Math.Min(input.Length, 1024);
        end = FloorCharBoundary(input, end);
        var first1K = input[..end];

        if (first1K.Contains("test result:", StringComparison.Ordinal) &&
            first1K.Contains("passed;", StringComparison.Ordinal))
        {
            return CargoFilters.FilterCargoTest;
        }

        if (first1K.Contains("=== test session starts", StringComparison.Ordinal))
        {
            return PytestFilters.FilterPytestOutput;
        }

        var firstTrimmed = first1K.TrimStart();
        if (firstTrimmed.StartsWith('{') && first1K.Contains("\"Action\"", StringComparison.Ordinal))
        {
            return GoFilters.FilterGoTestJson;
        }

        if (first1K.Contains(": error:", StringComparison.Ordinal) &&
            first1K.Contains(".py:", StringComparison.Ordinal))
        {
            return MypyFilters.FilterMypyOutput;
        }

        // grep/rg: at least one of the first 5 non-empty lines matches file:number:content.
        if (ReadFilters.SplitLines(first1K)
            .Take(5)
            .Where(l => l.Trim().Length > 0)
            .Any(LooksLikeGrepLine))
        {
            return GrepWrapper;
        }

        if (first1K.Contains("\"testResults\"", StringComparison.Ordinal) ||
            first1K.Contains("\"numTotalTests\"", StringComparison.Ordinal))
        {
            return VitestWrapper;
        }

        // find/fd: every non-empty line looks like a file path, minimum 3 lines.
        var lines = ReadFilters.SplitLines(first1K);
        var pathLikeLines = lines.Count(LooksPathLike);
        var nonEmptyLines = lines.Count(l => l.Trim().Length > 0);
        if (nonEmptyLines >= 3 && pathLikeLines == nonEmptyLines)
        {
            return FindWrapper;
        }

        return IdentityFilter;
    }

    /// <summary>
    /// Faithful port of <c>vitest_wrapper</c> (<c>pipe_cmd.rs</c>:48-56): parses via the shared
    /// <see cref="VitestFilters.VitestParser"/> and always renders
    /// <see cref="FormatMode.Compact"/>, regardless of any process-level verbosity (unlike
    /// <c>rtk vitest</c> itself, which scales its format mode with <c>--verbose</c>) — <c>rtk pipe</c>
    /// has no verbosity concept of its own, matching Rust's hardcoded <c>FormatMode::Compact</c> here.
    /// </summary>
    /// <param name="input">The raw vitest/jest JSON reporter output.</param>
    /// <returns>The compact-formatted test summary, or the raw input on a Passthrough-tier parse.</returns>
    internal static string VitestWrapper(string input)
    {
        var parser = new VitestFilters.VitestParser();
        var result = parser.Parse(input);
        return result switch
        {
            ParseResult<TestResult>.Full full => full.Data.FormatCompact(),
            ParseResult<TestResult>.Degraded degraded => degraded.Data.FormatCompact(),
            ParseResult<TestResult>.Passthrough passthrough => passthrough.Raw,
            _ => input,
        };
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
    /// Never-blocks identity passthrough: used both for the deliberate ecosystem-filter gap and as
    /// the auto-detect fallback when no signature matched. Ports Rust's <c>identity_filter</c>
    /// (<c>pipe_cmd.rs</c>:200-202).
    /// </summary>
    /// <param name="input">The input text.</param>
    /// <returns><paramref name="input"/>, unchanged.</returns>
    private static string IdentityFilter(string input) => input;
}
