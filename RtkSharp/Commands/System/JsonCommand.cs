using System.Globalization;
using System.Text;
using System.Text.Json;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk json</c> CLI verb: shows JSON compactly with values preserved by
/// default, or a keys-only schema view with <c>--keys-only</c>. Faithful port of Rust
/// <c>src/cmds/system/json_cmd.rs</c> (including its inline test module).
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
/// multibyte character. This port mirrors both: <see cref="Utf8ByteLength"/> for the length checks,
/// and <see cref="FloorCharBoundaryTruncate"/> for the truncation, which walks the string's
/// <see cref="System.Text.Rune"/>s accumulating UTF-8 byte length exactly the way Rust's own
/// byte-indexed string does.
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
    private const int MaxObjectKeysCompact = 20;
    private const int MaxObjectKeysSchema = 15;
    private const int MaxArrayItemsInline = 5;
    private const int CompactStringByteThreshold = 80;
    private const int CompactStringTruncateByteOffset = 77;
    private const int SchemaStringByteThreshold = 50;
    private const int SchemaDateLikeByteLength = 10;

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
            ? FilterJsonSchema(content, args.MaxDepth)
            : FilterJsonCompact(content, args.MaxDepth);

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

    /// <summary>
    /// Parses a JSON string and returns a compact representation with values preserved (long strings
    /// truncated, arrays summarized). Faithful port of <c>filter_json_compact</c> (<c>json_cmd.rs</c>:91-94).
    /// </summary>
    /// <param name="jsonStr">The raw JSON text.</param>
    /// <param name="maxDepth">The maximum nesting depth to expand before eliding with <c>...</c>.</param>
    /// <returns>The compact rendering.</returns>
    /// <exception cref="JsonException">The input is not valid JSON.</exception>
    internal static string FilterJsonCompact(string jsonStr, int maxDepth)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        return CompactJson(doc.RootElement, 0, maxDepth);
    }

    private static string CompactJson(JsonElement value, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);

        if (depth > maxDepth)
        {
            return $"{indent}...";
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return $"{indent}null";

            case JsonValueKind.True:
                return $"{indent}true";

            case JsonValueKind.False:
                return $"{indent}false";

            case JsonValueKind.Number:
                return $"{indent}{value.GetRawText()}";

            case JsonValueKind.String:
            {
                var s = value.GetString() ?? string.Empty;
                if (Utf8ByteLength(s) > CompactStringByteThreshold)
                {
                    var truncated = FloorCharBoundaryTruncate(s, CompactStringTruncateByteOffset);
                    return $"{indent}\"{truncated}...\"";
                }

                return $"{indent}\"{s}\"";
            }

            case JsonValueKind.Array:
            {
                var items = value.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    return $"{indent}[]";
                }

                if (items.Count > MaxArrayItemsInline)
                {
                    var first = CompactJson(items[0], depth + 1, maxDepth);
                    return $"{indent}[{first.Trim()}, ... +{items.Count - 1} more]";
                }

                var allSimple = items.All(IsSimpleValue);
                if (allSimple)
                {
                    var inline = items.Select(v => CompactJson(v, depth + 1, maxDepth).Trim());
                    return $"{indent}[{string.Join(", ", inline)}]";
                }

                var lines = new List<string> { $"{indent}[" };
                foreach (var item in items)
                {
                    lines.Add($"{CompactJson(item, depth + 1, maxDepth)},");
                }

                lines.Add($"{indent}]");
                return string.Join("\n", lines);
            }

            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject().ToList();
                if (properties.Count == 0)
                {
                    return $"{indent}{{}}";
                }

                var lines = new List<string> { $"{indent}{{" };
                var keys = properties.Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();

                for (var i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    var val = value.GetProperty(key);
                    var isSimple = IsSimpleValue(val);

                    if (isSimple)
                    {
                        var valStr = CompactJson(val, 0, maxDepth);
                        lines.Add($"{indent}  {key}: {valStr.Trim()}");
                    }
                    else
                    {
                        lines.Add($"{indent}  {key}:");
                        lines.Add(CompactJson(val, depth + 1, maxDepth));
                    }

                    if (i >= MaxObjectKeysCompact)
                    {
                        lines.Add($"{indent}  ... +{keys.Count - i - 1} more keys");
                        break;
                    }
                }

                lines.Add($"{indent}}}");
                return string.Join("\n", lines);
            }

            default:
                return $"{indent}null";
        }
    }

    /// <summary>
    /// Parses a JSON string and returns its schema representation (types only, no values). Faithful
    /// port of <c>filter_json_string</c> (<c>json_cmd.rs</c>:182-185).
    /// </summary>
    /// <param name="jsonStr">The raw JSON text.</param>
    /// <param name="maxDepth">The maximum nesting depth to expand before eliding with <c>...</c>.</param>
    /// <returns>The schema rendering.</returns>
    /// <exception cref="JsonException">The input is not valid JSON.</exception>
    internal static string FilterJsonSchema(string jsonStr, int maxDepth)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        return ExtractSchema(doc.RootElement, 0, maxDepth);
    }

    private static string ExtractSchema(JsonElement value, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);

        if (depth > maxDepth)
        {
            return $"{indent}...";
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return $"{indent}null";

            case JsonValueKind.True:
            case JsonValueKind.False:
                return $"{indent}bool";

            case JsonValueKind.Number:
                return $"{indent}{(IsI64(value) ? "int" : "float")}";

            case JsonValueKind.String:
            {
                var s = value.GetString() ?? string.Empty;
                var byteLen = Utf8ByteLength(s);
                if (byteLen > SchemaStringByteThreshold)
                {
                    return $"{indent}string[{byteLen}]";
                }

                if (s.Length == 0)
                {
                    return $"{indent}string";
                }

                if (s.StartsWith("http", StringComparison.Ordinal))
                {
                    return $"{indent}url";
                }

                if (s.Contains('-', StringComparison.Ordinal) && byteLen == SchemaDateLikeByteLength)
                {
                    return $"{indent}date?";
                }

                return $"{indent}string";
            }

            case JsonValueKind.Array:
            {
                var items = value.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    return $"{indent}[]";
                }

                var firstSchema = ExtractSchema(items[0], depth + 1, maxDepth);
                var trimmed = firstSchema.Trim();
                return items.Count == 1
                    ? $"{indent}[\n{firstSchema}\n{indent}]"
                    : $"{indent}[{trimmed}] ({items.Count})";
            }

            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject().ToList();
                if (properties.Count == 0)
                {
                    return $"{indent}{{}}";
                }

                var lines = new List<string> { $"{indent}{{" };
                var keys = properties.Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();

                for (var i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    var val = value.GetProperty(key);
                    var valSchema = ExtractSchema(val, depth + 1, maxDepth);
                    var valTrimmed = valSchema.Trim();
                    var isSimple = IsSimpleValue(val);

                    if (isSimple)
                    {
                        lines.Add(i < keys.Count - 1
                            ? $"{indent}  {key}: {valTrimmed},"
                            : $"{indent}  {key}: {valTrimmed}");
                    }
                    else
                    {
                        lines.Add($"{indent}  {key}:");
                        lines.Add(valSchema);
                    }

                    if (i >= MaxObjectKeysSchema)
                    {
                        lines.Add($"{indent}  ... +{keys.Count - i - 1} more keys");
                        break;
                    }
                }

                lines.Add($"{indent}}}");
                return string.Join("\n", lines);
            }

            default:
                return $"{indent}null";
        }
    }

    private static bool IsSimpleValue(JsonElement value) => value.ValueKind is
        JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String;

    /// <summary>Mirrors Rust's <c>serde_json::Number::is_i64</c>: true when the number parses as a 64-bit integer.</summary>
    private static bool IsI64(JsonElement value) => value.TryGetInt64(out _);

    /// <summary>The UTF-8 byte length of <paramref name="s"/>, mirroring Rust's <c>str::len()</c>.</summary>
    internal static int Utf8ByteLength(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// Truncates <paramref name="s"/> to the longest prefix whose UTF-8 byte length does not exceed
    /// <paramref name="maxBytes"/>, never splitting a multibyte character. Mirrors Rust's
    /// <c>s.floor_char_boundary(maxBytes)</c> followed by slicing.
    /// </summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="maxBytes">The maximum UTF-8 byte length of the returned prefix.</param>
    /// <returns>The truncated prefix.</returns>
    internal static string FloorCharBoundaryTruncate(string s, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(s);

        var sb = new StringBuilder();
        var byteCount = 0;

        foreach (var rune in s.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes)
            {
                break;
            }

            sb.Append(rune.ToString());
            byteCount += runeBytes;
        }

        return sb.ToString();
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
