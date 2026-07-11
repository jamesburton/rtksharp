using System.Text.RegularExpressions;
using RtkSharp.Core;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk smart</c> CLI verb: a heuristic-based 2-line code summarizer (no external
/// model call, despite Rust's module name <c>local_llm</c>). Faithful port of Rust
/// <c>src/cmds/system/local_llm.rs</c> (<c>run</c>, <c>analyze_code</c>, and every extraction/pattern
/// helper it calls).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>RTK_META_COMMANDS</c> parity.</b> Rust's <c>main.rs</c> lists <c>"smart"</c> in
/// <c>RTK_META_COMMANDS</c> (<c>main.rs:1188</c>), so a Clap parse failure shows Clap's own error
/// instead of falling back to raw shell execution. Following the same convention established by
/// <see cref="RtkSharp.Commands.Analytics.GainCommand"/> for other meta-commands, this uses a
/// hand-rolled <see cref="SmartArgsException"/>-based parser (never
/// <see cref="RtkSharp.Cli.CommandArgumentParseException"/>, which is reserved for
/// PASSTHROUGH-classified commands).
/// </para>
/// <para>
/// <b><c>--model</c>/<c>--force-download</c> are accepted but unused</b>, exactly like Rust's
/// <c>run(file, _model, _force_download, verbose)</c> (<c>local_llm.rs:11</c>) — the only supported
/// "model" is the built-in heuristic; the flags exist for a future real-model integration that was
/// never wired up.
/// </para>
/// <para>
/// <b>Language detection reuses <see cref="Language"/>/<see cref="LanguageExtensions"/></b>
/// (<c>RtkSharp/Core/SourceFilter.cs</c>), the same enum <c>ReadCommand</c> uses for its
/// <c>--level</c> filters — this is the C# equivalent of Rust's <c>crate::core::filter::Language</c>,
/// already ported and shared rather than re-invented here. Extension extraction reuses
/// <see cref="ReadFilters.GetExtension"/> for the same dotfile-safe <c>Path::extension()</c>
/// semantics (a leading-dot-only name like <c>.gitignore</c> has no extension, unlike
/// <see cref="Path.GetExtension(string)"/>).
/// </para>
/// </remarks>
public static partial class SmartCommand
{
    /// <summary>
    /// Runs <c>rtk smart</c> with the given arguments (the remainder after the <c>smart</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>smart</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        try
        {
            return RunCore(args, RuntimeOptions.Verbosity);
        }
        catch (SmartArgsException ex)
        {
            Console.Error.Write(ex.Message + "\n");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>The testable core of <see cref="Run"/>. Faithful port of <c>run</c> (<c>local_llm.rs:11-31</c>).</summary>
    /// <param name="args">The CLI arguments following <c>smart</c>.</param>
    /// <param name="verbose">The global verbosity level (mirrors Rust's <c>cli.verbose: u8</c>).</param>
    /// <returns>0 on success.</returns>
    internal static int RunCore(string[] args, int verbose)
    {
        var parsed = SmartArgs.Parse(args);

        if (verbose > 0)
        {
            Console.Error.Write($"Analyzing: {parsed.File}\n");
        }

        string content;
        try
        {
            content = File.ReadAllText(parsed.File);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to read file: {parsed.File}: {ex.Message}", ex);
        }

        var extension = ReadFilters.GetExtension(parsed.File);
        var lang = extension.Length > 0 ? LanguageExtensions.FromExtension(extension) : Language.Unknown;

        var summary = AnalyzeCode(content, lang);

        Console.Out.Write(summary.Line1 + "\n");
        Console.Out.Write(summary.Line2 + "\n");

        return 0;
    }

    /// <summary>The two-line heuristic summary produced by <see cref="AnalyzeCode"/>.</summary>
    /// <param name="Line1">The "what it is" line: language, main type, and component counts.</param>
    /// <param name="Line2">The "key details" line: imports, detected patterns, and/or defined functions.</param>
    internal readonly record struct CodeSummary(string Line1, string Line2);

    /// <summary>Faithful port of <c>analyze_code</c> (<c>local_llm.rs:38-112</c>).</summary>
    /// <param name="content">The source file's full text.</param>
    /// <param name="lang">The detected source language.</param>
    /// <returns>The two-line summary.</returns>
    internal static CodeSummary AnalyzeCode(string content, Language lang)
    {
        var lines = SplitLines(content);
        var totalLines = lines.Count;

        var imports = ExtractImports(content, lang);
        var functions = ExtractFunctions(content, lang);
        var structs = ExtractStructs(content, lang);
        var traits = ExtractTraits(content, lang);

        var patterns = DetectPatterns(content, lang);

        var langName = LangDisplayName(lang);
        var mainType = structs.Count > 0 && functions.Count > 0
            ? $"{langName} module"
            : structs.Count > 0
                ? $"{langName} data structures"
                : functions.Count > 0
                    ? $"{langName} functions"
                    : $"{langName} code";

        var components = new List<string>();
        if (functions.Count > 0)
        {
            components.Add($"{functions.Count} fn");
        }

        if (structs.Count > 0)
        {
            components.Add($"{structs.Count} struct");
        }

        if (traits.Count > 0)
        {
            components.Add($"{traits.Count} trait");
        }

        var line1 = components.Count == 0
            ? $"{mainType} ({totalLines} lines)"
            : $"{mainType} ({string.Join(", ", components)}) - {totalLines} lines";

        var details = new List<string>();

        if (imports.Count > 0)
        {
            var keyImports = imports.Take(3);
            details.Add($"uses: {string.Join(", ", keyImports)}");
        }

        if (patterns.Count > 0)
        {
            details.Add($"patterns: {string.Join(", ", patterns)}");
        }

        if (functions.Count > 0 && details.Count == 0)
        {
            var keyFns = functions.Take(3);
            details.Add($"defines: {string.Join(", ", keyFns)}");
        }

        var line2 = details.Count == 0 ? "General purpose code file" : string.Join(" | ", details);

        return new CodeSummary(line1, line2);
    }

    /// <summary>Faithful port of <c>lang_display_name</c> (<c>local_llm.rs:114-129</c>).</summary>
    internal static string LangDisplayName(Language lang) => lang switch
    {
        Language.Rust => "Rust",
        Language.Python => "Python",
        Language.JavaScript => "JavaScript",
        Language.TypeScript => "TypeScript",
        Language.Go => "Go",
        Language.C => "C",
        Language.Cpp => "C++",
        Language.Java => "Java",
        Language.Ruby => "Ruby",
        Language.Shell => "Shell",
        Language.Data => "Data",
        _ => "Code",
    };

    // -----------------------------------------------------------------------
    // Extraction helpers (local_llm.rs:131-227)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts up to 5 deduplicated, non-stdlib import/dependency base names. Faithful port of
    /// <c>extract_imports</c> (<c>local_llm.rs:131-160</c>) — the regex is applied per-line (not
    /// across the whole content), matching Rust's <c>content.lines()</c> loop with
    /// <c>re.captures(line)</c>.
    /// </summary>
    internal static List<string> ExtractImports(string content, Language lang)
    {
        Regex? re = lang switch
        {
            Language.Rust => RustImportRegex(),
            Language.Python => PythonImportRegex(),
            Language.JavaScript or Language.TypeScript => JsImportRegex(),
            Language.Go => GoImportRegex(),
            _ => null,
        };

        if (re is null)
        {
            return [];
        }

        var imports = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in SplitLines(content))
        {
            var match = re.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var import = FirstSuccessfulGroup(match);
            if (import is null)
            {
                continue;
            }

            var separatorIndex = import.IndexOf("::", StringComparison.Ordinal);
            var baseName = separatorIndex >= 0 ? import[..separatorIndex] : import;

            // Mirrors Rust's `!seen.contains(&base) && !is_std_import(&base, lang)` guard: since
            // is_std_import is a pure function of (name, lang), whether a rejected std import ends
            // up recorded in `seen` is unobservable — HashSet.Add's own dedup semantics suffice.
            if (seen.Add(baseName) && !IsStdImport(baseName, lang))
            {
                imports.Add(baseName);
            }
        }

        return imports.Take(5).ToList();
    }

    /// <summary>Faithful port of <c>is_std_import</c> (<c>local_llm.rs:162-168</c>).</summary>
    internal static bool IsStdImport(string name, Language lang) => lang switch
    {
        Language.Rust => name is "std" or "core" or "alloc",
        Language.Python => name is "os" or "sys" or "re" or "json" or "typing",
        _ => false,
    };

    /// <summary>
    /// Extracts up to 10 top-level function names (excluding <c>test_*</c>/<c>main</c>/<c>new</c>).
    /// Faithful port of <c>extract_functions</c> (<c>local_llm.rs:170-196</c>) — per-line regex,
    /// matching Rust's <c>content.lines()</c> loop.
    /// </summary>
    internal static List<string> ExtractFunctions(string content, Language lang)
    {
        Regex? re = lang switch
        {
            Language.Rust => RustFunctionRegex(),
            Language.Python => PythonFunctionRegex(),
            Language.JavaScript or Language.TypeScript => JsFunctionRegex(),
            Language.Go => GoFunctionRegex(),
            _ => null,
        };

        if (re is null)
        {
            return [];
        }

        var functions = new List<string>();

        foreach (var line in SplitLines(content))
        {
            var match = re.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = FirstSuccessfulGroup(match);
            if (name is null)
            {
                continue;
            }

            if (!name.StartsWith("test_", StringComparison.Ordinal) && name != "main" && name != "new")
            {
                functions.Add(name);
            }
        }

        return functions.Take(10).ToList();
    }

    /// <summary>
    /// Extracts up to 10 struct/class/enum/interface/type names. Faithful port of
    /// <c>extract_structs</c> (<c>local_llm.rs:198-213</c>) — matched across the whole content via
    /// <c>captures_iter</c>, not per-line.
    /// </summary>
    internal static List<string> ExtractStructs(string content, Language lang)
    {
        Regex? re = lang switch
        {
            Language.Rust => RustStructRegex(),
            Language.Python => PythonClassRegex(),
            Language.TypeScript => TsStructRegex(),
            Language.Go => GoStructRegex(),
            Language.Java => JavaClassRegex(),
            _ => null,
        };

        if (re is null)
        {
            return [];
        }

        return re.Matches(content).Select(m => m.Groups[1].Value).Take(10).ToList();
    }

    /// <summary>
    /// Extracts up to 5 trait/interface names. Faithful port of <c>extract_traits</c>
    /// (<c>local_llm.rs:215-227</c>) — matched across the whole content via <c>captures_iter</c>.
    /// </summary>
    internal static List<string> ExtractTraits(string content, Language lang)
    {
        Regex? re = lang switch
        {
            Language.Rust => RustTraitRegex(),
            Language.TypeScript => TsInterfaceRegex(),
            _ => null,
        };

        if (re is null)
        {
            return [];
        }

        return re.Matches(content).Select(m => m.Groups[1].Value).Take(5).ToList();
    }

    /// <summary>Faithful port of <c>detect_patterns</c> (<c>local_llm.rs:229-275</c>).</summary>
    internal static List<string> DetectPatterns(string content, Language lang)
    {
        var patterns = new List<string>();

        if (content.Contains("async", StringComparison.Ordinal) && content.Contains("await", StringComparison.Ordinal))
        {
            patterns.Add("async");
        }

        switch (lang)
        {
            case Language.Rust:
                if (content.Contains("impl", StringComparison.Ordinal) && content.Contains("for", StringComparison.Ordinal))
                {
                    patterns.Add("trait impl");
                }

                if (content.Contains("#[derive", StringComparison.Ordinal))
                {
                    patterns.Add("derive");
                }

                if (content.Contains("Result<", StringComparison.Ordinal) || content.Contains("anyhow::", StringComparison.Ordinal))
                {
                    patterns.Add("error handling");
                }

                if (content.Contains("#[test]", StringComparison.Ordinal))
                {
                    patterns.Add("tests");
                }

                if (content.Contains("Box<dyn", StringComparison.Ordinal) || content.Contains("&dyn", StringComparison.Ordinal))
                {
                    patterns.Add("dyn dispatch");
                }

                break;

            case Language.Python:
                if (content.Contains("@dataclass", StringComparison.Ordinal))
                {
                    patterns.Add("dataclass");
                }

                if (content.Contains("def __init__", StringComparison.Ordinal))
                {
                    patterns.Add("OOP");
                }

                break;

            case Language.JavaScript:
            case Language.TypeScript:
                if (content.Contains("useState", StringComparison.Ordinal) || content.Contains("useEffect", StringComparison.Ordinal))
                {
                    patterns.Add("React hooks");
                }

                if (content.Contains("export default", StringComparison.Ordinal))
                {
                    patterns.Add("ES modules");
                }

                break;
        }

        return patterns.Take(3).ToList();
    }

    /// <summary>Returns the first successful (non-empty) capture group among group 1 and group 2 (mirrors Rust's <c>caps.get(1).or(caps.get(2))</c>).</summary>
    private static string? FirstSuccessfulGroup(Match match)
    {
        if (match.Groups.Count > 1 && match.Groups[1].Success)
        {
            return match.Groups[1].Value;
        }

        return match.Groups.Count > 2 && match.Groups[2].Success ? match.Groups[2].Value : null;
    }

    /// <summary>Splits text into lines exactly as Rust's <c>str::lines()</c> does, reusing the shared splitter.</summary>
    private static List<string> SplitLines(string text) => SourceFilterLineSplitter.SplitLines(text);

    // -----------------------------------------------------------------------
    // Regexes (one GeneratedRegex per language arm, matching local_llm.rs's patterns verbatim)
    // -----------------------------------------------------------------------

    [GeneratedRegex(@"^use\s+([a-zA-Z_][a-zA-Z0-9_]*(?:::[a-zA-Z_][a-zA-Z0-9_]*)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RustImportRegex();

    [GeneratedRegex(@"^(?:from\s+(\S+)|import\s+(\S+))", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PythonImportRegex();

    [GeneratedRegex(@"(?:import.*from\s+['""]([^'""]+)['""]|require\(['""]([^'""]+)['""]\))", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex JsImportRegex();

    [GeneratedRegex(@"^\s*""([^""]+)""$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex GoImportRegex();

    [GeneratedRegex(@"(?:pub\s+)?(?:async\s+)?fn\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RustFunctionRegex();

    [GeneratedRegex(@"def\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PythonFunctionRegex();

    [GeneratedRegex(@"(?:async\s+)?function\s+([a-zA-Z_][a-zA-Z0-9_]*)|(?:const|let|var)\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*=\s*(?:async\s+)?\(", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex JsFunctionRegex();

    [GeneratedRegex(@"func\s+(?:\([^)]+\)\s+)?([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex GoFunctionRegex();

    [GeneratedRegex(@"(?:pub\s+)?(?:struct|enum)\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RustStructRegex();

    [GeneratedRegex(@"class\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PythonClassRegex();

    [GeneratedRegex(@"(?:interface|class|type)\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TsStructRegex();

    [GeneratedRegex(@"type\s+([a-zA-Z_][a-zA-Z0-9_]*)\s+struct", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex GoStructRegex();

    [GeneratedRegex(@"(?:public\s+)?class\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex JavaClassRegex();

    [GeneratedRegex(@"(?:pub\s+)?trait\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RustTraitRegex();

    [GeneratedRegex(@"interface\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TsInterfaceRegex();

    // -----------------------------------------------------------------------
    // Flag parsing
    // -----------------------------------------------------------------------

    /// <summary>Parsed <c>rtk smart</c> flags. Hand-rolled port of the Clap-derived <c>Commands::Smart</c> arg struct (<c>main.rs:114-123</c>).</summary>
    private sealed class SmartArgs
    {
        public string File { get; private set; } = string.Empty;

        public string Model { get; private set; } = "heuristic";

        public bool ForceDownload { get; private set; }

        public static SmartArgs Parse(string[] args)
        {
            var result = new SmartArgs();
            string? file = null;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-m" or "--model":
                        result.Model = RequireValue(args, ref i, arg);
                        break;
                    case "--force-download":
                        result.ForceDownload = true;
                        break;
                    default:
                        if (arg.StartsWith("--model=", StringComparison.Ordinal))
                        {
                            result.Model = arg["--model=".Length..];
                        }
                        else if (arg.Length > 0 && arg[0] == '-' && arg != "-")
                        {
                            throw new SmartArgsException($"error: unexpected argument '{arg}' found");
                        }
                        else if (file is null)
                        {
                            file = arg;
                        }
                        else
                        {
                            throw new SmartArgsException($"error: unexpected argument '{arg}' found");
                        }

                        break;
                }
            }

            if (file is null)
            {
                throw new SmartArgsException(
                    "error: the following required arguments were not provided:\n  <FILE>\n\nUsage: rtk smart <FILE>\n\nFor more information, try '--help'.");
            }

            result.File = file;
            return result;
        }

        private static string RequireValue(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
            {
                throw new SmartArgsException($"error: a value is required for '{flag}' but none was supplied");
            }

            i++;
            return args[i];
        }
    }

    private sealed class SmartArgsException(string message) : Exception(message);
}
