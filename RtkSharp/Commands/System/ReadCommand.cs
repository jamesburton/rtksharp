using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Reads one or more files (or stdin via <c>-</c>) and prints their contents, optionally
/// windowed to the first N lines (<c>--max-lines</c>/<c>-m</c>) or last N lines
/// (<c>--tail-lines</c>) and optionally prefixed with right-aligned line numbers
/// (<c>-n</c>/<c>--line-numbers</c>). Multiple files are concatenated with no separator
/// headers, exactly like <c>cat</c>. Unlike the other Phase 5a system commands this reads
/// files natively and prints directly rather than wrapping an external tool.
/// </summary>
/// <remarks>
/// Ported from <c>src/cmds/system/read.rs</c> together with the multi-file dispatch loop in
/// <c>src/main.rs</c> (<c>Commands::Read</c>), including all three <c>--level</c> filter tiers
/// (<c>none</c>/<c>minimal</c>/<c>aggressive</c>) via <see cref="Core.SourceFilter"/>
/// (<c>src/core/filter.rs</c>), language detection by file extension, and the
/// filter-emptied-non-empty-content safety fallback (read.rs's own
/// <c>if filtered.trim().is_empty() &amp;&amp; !content.trim().is_empty()</c> guard). One
/// intentional scope limit remains:
/// <list type="bullet">
///   <item>
///     <b>Verbose diagnostics.</b> The registered handler signature is
///     <c>RunAsync(string[])</c> and receives no verbosity level, so read.rs's stderr
///     <c>eprintln!</c> diagnostics (reduction stats, detected language, "Reading: ...") are
///     omitted; they never affect printed output. The empty-output safety warning IS ported
///     (unconditional in Rust, not gated on verbosity).
///   </item>
/// </list>
/// Token-savings tracking (read.rs's <c>TimedExecution</c>) is also omitted: native commands
/// are not wired to a tracker through the registry, and tracking is a metrics side effect that
/// does not change output.
/// </remarks>
public static class ReadCommand
{
    /// <summary>
    /// Parses the arguments following the <c>read</c> verb, reads each file (or stdin for
    /// <c>-</c>), and prints the (optionally windowed and numbered) contents. Returns 1 if any
    /// file could not be read, otherwise 0. A malformed invocation (unknown flag, missing value,
    /// invalid <c>--level</c>/count value, conflicting window flags, or no files) throws
    /// <see cref="CommandArgumentParseException"/> rather than handling it here — <c>read</c> is
    /// Rust-classified PASSTHROUGH, so the oracle's own clap-level parse failure never reaches
    /// read.rs's body at all; RtkProgram's dispatch layer catches this and re-routes through the
    /// same TOML-fallback/raw-passthrough path used for an unregistered verb.
    /// </summary>
    /// <param name="args">The arguments following the <c>read</c> verb.</param>
    /// <returns>0 on success, 1 if any file failed to read.</returns>
    /// <exception cref="CommandArgumentParseException">The arguments failed to parse.</exception>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // ParseArgs throws CommandArgumentParseException (not caught here) for anything Rust's
        // clap would reject at the top-level parse — RtkProgram's dispatch layer catches it and
        // re-routes through the same TOML-fallback/raw-passthrough path used for an unregistered
        // verb, since `read` is Rust-classified PASSTHROUGH (not RTK_META_COMMANDS): the oracle's
        // own parse failure here never reaches read.rs's body at all. See
        // CommandArgumentParseException's remarks and the compatibility-ledger entry documenting
        // this discovery.
        var parsed = ParseArgs(args);

        var hadError = false;
        var stdinSeen = false;

        foreach (var file in parsed.Files)
        {
            if (file == "-")
            {
                if (stdinSeen)
                {
                    Console.Error.WriteLine("rtk: warning: stdin specified more than once");
                    continue;
                }

                stdinSeen = true;
                var stdinContent = Console.In.ReadToEnd();
                // stdin has no extension and, per read.rs's run_stdin, no empty-output safety
                // fallback (that guard exists only in run(), the file path) — filePath: null.
                var rendered = ReadFilters.Render(
                    stdinContent, Language.Unknown, parsed.Level, parsed.MaxLines, parsed.TailLines,
                    parsed.LineNumbers, filePath: null);
                Console.Out.Write(rendered);
                continue;
            }

            try
            {
                var content = File.ReadAllText(file);
                var language = LanguageExtensions.FromExtension(ReadFilters.GetExtension(file));
                var rendered = ReadFilters.Render(
                    content, language, parsed.Level, parsed.MaxLines, parsed.TailLines, parsed.LineNumbers,
                    filePath: file);
                Console.Out.Write(rendered);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
            {
                // Mirrors main.rs's `eprintln!("cat: {}: {}", file, root_cause)`. The exact OS
                // error text is runtime-specific and not oracle-matched; the shape is.
                Console.Error.WriteLine($"cat: {file}: {ex.Message}");
                hadError = true;
            }
        }

        return Task.FromResult(hadError ? 1 : 0);
    }

    /// <summary>Parsed <c>read</c> arguments.</summary>
    /// <param name="Files">The files to read, in order; <c>-</c> denotes stdin.</param>
    /// <param name="Level">The requested filter level.</param>
    /// <param name="MaxLines">The <c>--max-lines</c> value, or null.</param>
    /// <param name="TailLines">The <c>--tail-lines</c> value, or null.</param>
    /// <param name="LineNumbers">Whether <c>-n</c>/<c>--line-numbers</c> was given.</param>
    internal sealed record ReadArgs(
        IReadOnlyList<string> Files,
        FilterLevel Level,
        int? MaxLines,
        int? TailLines,
        bool LineNumbers
    );

    /// <summary>
    /// Parses the <c>read</c> argument vector into a <see cref="ReadArgs"/>. Supports
    /// <c>--level</c>/<c>-l</c>, <c>--max-lines</c>/<c>-m</c>, <c>--tail-lines</c>, and
    /// <c>-n</c>/<c>--line-numbers</c>, in both <c>--flag value</c> and <c>--flag=value</c>
    /// forms, mirroring the clap definition in main.rs.
    /// </summary>
    /// <param name="args">The raw argument vector following the verb.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="CommandArgumentParseException">On unknown flags, missing values, conflicting window flags, or no files.</exception>
    internal static ReadArgs ParseArgs(string[] args)
    {
        var files = new List<string>();
        var level = FilterLevel.None;
        int? maxLines = null;
        int? tailLines = null;
        var lineNumbers = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (TryTakeValue(args, ref i, arg, "--level", "-l", out var levelValue))
            {
                level = ParseLevel(levelValue);
            }
            else if (TryTakeValue(args, ref i, arg, "--max-lines", "-m", out var maxValue))
            {
                maxLines = ParseCount(maxValue, "--max-lines");
            }
            else if (TryTakeValue(args, ref i, arg, "--tail-lines", null, out var tailValue))
            {
                tailLines = ParseCount(tailValue, "--tail-lines");
            }
            else if (arg is "-n" or "--line-numbers")
            {
                lineNumbers = true;
            }
            else if (arg.Length > 1 && arg.StartsWith('-') && arg != "-")
            {
                throw new CommandArgumentParseException($"unexpected argument '{arg}'");
            }
            else
            {
                files.Add(arg);
            }
        }

        if (maxLines is not null && tailLines is not null)
        {
            throw new CommandArgumentParseException("the argument '--max-lines' cannot be used with '--tail-lines'");
        }

        if (files.Count == 0)
        {
            throw new CommandArgumentParseException("the following required arguments were not provided: <FILES>");
        }

        return new ReadArgs(files, level, maxLines, tailLines, lineNumbers);
    }

    /// <summary>
    /// Attempts to consume an option that takes a value at position <paramref name="i"/>,
    /// supporting both <c>--flag=value</c> and <c>--flag value</c> (and the short <c>-x value</c>)
    /// forms. Advances <paramref name="i"/> past a consumed separate value.
    /// </summary>
    private static bool TryTakeValue(
        string[] args, ref int i, string arg, string longName, string? shortName, out string value)
    {
        if (arg == longName || (shortName is not null && arg == shortName))
        {
            if (i + 1 >= args.Length)
            {
                throw new CommandArgumentParseException($"a value is required for '{longName}' but none was supplied");
            }

            value = args[++i];
            return true;
        }

        var prefix = longName + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static FilterLevel ParseLevel(string value) =>
        value.ToLowerInvariant() switch
        {
            "none" => FilterLevel.None,
            "minimal" => FilterLevel.Minimal,
            "aggressive" => FilterLevel.Aggressive,
            // RtkSharp-only extra tier, on top of the three Rust recognizes — see FilterLevel.Ast.
            "ast" => FilterLevel.Ast,
            _ => throw new CommandArgumentParseException($"invalid value '{value}' for '--level'")
        };

    private static int ParseCount(string value, string flag)
    {
        if (!int.TryParse(value, global::System.Globalization.NumberStyles.None,
                global::System.Globalization.CultureInfo.InvariantCulture, out var count))
        {
            throw new CommandArgumentParseException($"invalid value '{value}' for '{flag}': not a non-negative integer");
        }

        return count;
    }
}
