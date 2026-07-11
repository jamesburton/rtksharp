using System.Globalization;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk json</c> CLI verb: shows JSON compactly with values preserved by
/// default, or a keys-only schema view with <c>--keys-only</c>. Faithful port of Rust
/// <c>src/cmds/system/json_cmd.rs</c> (including its inline test module). The pure
/// compact/schema rendering logic lives in <see cref="JsonFilters"/> (<c>RtkSharp.Filters</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>RTK_META_COMMANDS classification.</b> Rust's <c>RTK_META_COMMANDS</c> (<c>main.rs</c>:1170-1191)
/// includes <c>"json"</c> — a clap-layer parse failure (missing the required <c>file</c> positional, an
/// unrecognized flag, or a non-numeric <c>--depth</c>) exits directly via clap's own error, never
/// falling back to a raw PATH-exec of a real <c>json</c> binary. Following the established
/// <see cref="Analytics.SessionCommand"/>/<see cref="Hooks.HookAuditCommand"/> convention for ported
/// meta-commands, <see cref="ParseArgs"/> throws the internal <see cref="JsonArgsException"/> (never
/// <see cref="Cli.CommandArgumentParseException"/>, which routes to the PASSTHROUGH fallback path —
/// wrong for a meta-command) and <see cref="RunAsync"/> catches it, prints a short
/// <c>error: ...</c> message, and returns 2 (clap's usage-error exit code).
/// </para>
/// <para>
/// <b><c>file == "-"</c> reads stdin and skips extension validation entirely.</b>
/// <c>json_cmd.rs</c>:39-40 only calls <c>validate_json_extension</c> from <c>run()</c>; the
/// <c>run_stdin()</c> path (taken when the file argument is the literal <c>"-"</c>, main.rs:1743-1744)
/// never validates an extension, since there is no file path to inspect. This port preserves that
/// asymmetry in <see cref="RunAsync"/>.
/// </para>
/// <para>
/// <b>Byte-length checks, Rune-based indent/tree walking.</b> Rust's <c>s.len() &gt; 80</c> /
/// <c>&gt; 50</c> / <c>== 10</c> checks in <c>compact_json</c>/<c>extract_schema</c> operate on UTF-8
/// byte length, and the compact-string truncation point (<c>s.floor_char_boundary(77)</c>) rounds
/// down to the nearest UTF-8 character boundary at or before byte offset 77 — never splitting a
/// multibyte character. This port mirrors both: <see cref="JsonFilters.Utf8ByteLength"/> for the
/// length checks, and <see cref="JsonFilters.FloorCharBoundaryTruncate"/> for the truncation,
/// which walks the string's <see cref="System.Text.Rune"/>s accumulating UTF-8 byte length
/// exactly the way Rust's own byte-indexed string does.
/// </para>
/// <para>
/// <b>Object keys are sorted lexicographically (ordinal), not by insertion order.</b> Both
/// <c>compact_json</c> and <c>extract_schema</c> collect <c>map.keys()</c> into a <c>Vec</c> and
/// <c>.sort()</c> it (Rust's default string ordering — byte-wise/ordinal) before iterating; this port
/// uses <see cref="StringComparer.Ordinal"/> to match exactly.
/// </para>
/// </remarks>
public static class JsonCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk json</c> with the given arguments (the remainder after the
    /// <c>json</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>json</c>.</param>
    /// <returns>0 on success, 2 on a clap-equivalent argument parse failure, 1 on any other failure.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        JsonArgs parsed;
        try
        {
            parsed = ParseArgs(args);
        }
        catch (JsonArgsException ex)
        {
            Console.Error.Write(ex.Message + "\n");
            return Task.FromResult(2);
        }

        try
        {
            return Task.FromResult(RunCore(parsed, RuntimeOptions.Verbosity, Console.Out, Console.Error));
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return Task.FromResult(1);
        }
    }

    /// <summary>
    /// The testable core: reads the file (or stdin, when <see cref="JsonArgs.File"/> is <c>"-"</c>),
    /// applies the compact or keys-only filter, and prints the result. Faithful port of
    /// <c>json_cmd::run</c>/<c>run_stdin</c> (<c>json_cmd.rs</c>:39-87).
    /// </summary>
    /// <param name="args">The parsed CLI arguments.</param>
    /// <param name="verbose">The global verbosity level (mirrors Rust's <c>cli.verbose: u8</c>).</param>
    /// <param name="stdout">The destination for the rendered JSON view.</param>
    /// <param name="stderr">The destination for the verbose diagnostic line.</param>
    /// <returns>0 on success.</returns>
    internal static int RunCore(JsonArgs args, int verbose, TextWriter stdout, TextWriter stderr)
    {
        var timer = TimedExecution.Start();
        string content;
        string originalCmd;

        if (args.File == "-")
        {
            if (verbose > 0)
            {
                stderr.Write("Analyzing JSON from stdin\n");
            }

            content = Console.In.ReadToEnd();
            originalCmd = "cat - (stdin)";
        }
        else
        {
            ValidateJsonExtension(args.File);

            if (verbose > 0)
            {
                stderr.Write($"Analyzing JSON: {args.File}\n");
            }

            content = File.ReadAllText(args.File);
            originalCmd = $"cat {args.File}";
        }

        var output = args.KeysOnly
            ? JsonFilters.FilterJsonSchema(content, args.MaxDepth)
            : JsonFilters.FilterJsonCompact(content, args.MaxDepth);

        stdout.Write(output + "\n");

        var rtkCmd = args.File == "-" ? "rtk json -" : "rtk json";
        timer.Track(originalCmd, rtkCmd, content, output);

        return 0;
    }

    /// <summary>
    /// Rejects non-JSON files by extension with a clear error before any I/O beyond the check itself.
    /// Faithful port of <c>validate_json_extension</c> (<c>json_cmd.rs</c>:11-36).
    /// </summary>
    /// <param name="file">The file path to validate.</param>
    /// <exception cref="InvalidOperationException">The file's extension indicates a non-JSON format.</exception>
    internal static void ValidateJsonExtension(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var ext = Path.GetExtension(file).TrimStart('.');
        if (ext.Length == 0)
        {
            return;
        }

        var formatName = ext.ToLowerInvariant() switch
        {
            "toml" => "TOML",
            "yaml" or "yml" => "YAML",
            "xml" => "XML",
            "csv" => "CSV",
            "ini" => "INI",
            "env" => "env",
            "txt" => "plain text",
            _ => null,
        };

        if (formatName is null)
        {
            return;
        }

        var msg = $"{file} is not a JSON file (detected {formatName}). Use `rtk read` for non-JSON files.";
        if (string.Equals(ext, "toml", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(file), "Cargo.toml", StringComparison.Ordinal))
        {
            msg += " Tip: use `rtk deps` for Cargo.toml.";
        }

        throw new InvalidOperationException(msg);
    }

    /// <summary>Parsed <c>rtk json</c> arguments.</summary>
    /// <param name="File">The JSON file to read, or <c>"-"</c> for stdin.</param>
    /// <param name="MaxDepth">The maximum nesting depth (default 5).</param>
    /// <param name="KeysOnly">Whether <c>--keys-only</c> was given (schema view instead of compact-with-values).</param>
    internal readonly record struct JsonArgs(string File, int MaxDepth, bool KeysOnly);

    /// <summary>
    /// Parses <c>rtk json</c>'s arguments: a required <c>file</c> positional, an optional
    /// <c>-d</c>/<c>--depth &lt;n&gt;</c> (default 5), and an optional <c>--keys-only</c> flag. Faithful
    /// port of the clap <c>Commands::Json</c> variant (<c>main.rs</c>:229-238).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>json</c>.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="JsonArgsException">The arguments are malformed or unrecognized.</exception>
    internal static JsonArgs ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? file = null;
        var depth = 5;
        var keysOnly = false;

        var i = 0;
        while (i < args.Count)
        {
            var a = args[i];

            if (a is "-d" or "--depth")
            {
                if (i + 1 >= args.Count)
                {
                    throw new JsonArgsException("error: a value is required for '--depth <DEPTH>' but none was supplied");
                }

                depth = ParseDepthValue(args[i + 1]);
                i += 2;
                continue;
            }

            if (a.StartsWith("--depth=", StringComparison.Ordinal))
            {
                depth = ParseDepthValue(a["--depth=".Length..]);
                i++;
                continue;
            }

            if (a == "--keys-only")
            {
                keysOnly = true;
                i++;
                continue;
            }

            if (a.StartsWith('-') && a != "-")
            {
                throw new JsonArgsException($"error: unexpected argument '{a}' found");
            }

            if (file is not null)
            {
                throw new JsonArgsException($"error: unexpected argument '{a}' found");
            }

            file = a;
            i++;
        }

        if (file is null)
        {
            throw new JsonArgsException("error: the following required arguments were not provided: <FILE>");
        }

        return new JsonArgs(file, depth, keysOnly);
    }

    private static int ParseDepthValue(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new JsonArgsException($"error: invalid value '{value}' for '--depth <DEPTH>': invalid digit found in string");
        }

        return parsed;
    }
}

/// <summary>
/// A usage error from <see cref="JsonCommand.ParseArgs"/>, carrying the message to print verbatim to
/// stderr before exiting 2 (clap's usage-error exit code).
/// </summary>
internal sealed class JsonArgsException(string message) : Exception(message);
