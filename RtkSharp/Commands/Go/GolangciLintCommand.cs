using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Go;

/// <summary>
/// golangci-lint's <c>run --out-format=json</c>/<c>--output.json.path stdout</c> issue position shape.
/// Faithful port of Rust's <c>Position</c> (<c>src/cmds/go/golangci_cmd.rs</c>:48-61).
/// </summary>
internal sealed class GolangciPosition
{
    [JsonPropertyName("Filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("Line")]
    public int Line { get; set; }

    [JsonPropertyName("Column")]
    public int Column { get; set; }

    [JsonPropertyName("Offset")]
    public int Offset { get; set; }
}

/// <summary>golangci-lint's per-issue shape. Faithful port of Rust's <c>Issue</c> (<c>golangci_cmd.rs</c>:63-77).</summary>
internal sealed class GolangciIssue
{
    [JsonPropertyName("FromLinter")]
    public string FromLinter { get; set; } = "";

    [JsonPropertyName("Text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("Pos")]
    public GolangciPosition Pos { get; set; } = new();

    [JsonPropertyName("SourceLines")]
    public List<string> SourceLines { get; set; } = [];

    [JsonPropertyName("Severity")]
    public string Severity { get; set; } = "";
}

/// <summary>golangci-lint's top-level JSON output shape. Faithful port of Rust's <c>GolangciOutput</c> (<c>golangci_cmd.rs</c>:79-83).</summary>
internal sealed class GolangciOutput
{
    [JsonPropertyName("Issues")]
    public List<GolangciIssue> Issues { get; set; } = [];
}

/// <summary>Source-generated JSON context for <see cref="GolangciOutput"/>, avoiding reflection-based (de)serialization under <c>PublishAot</c>.</summary>
[JsonSerializable(typeof(GolangciOutput))]
internal sealed partial class GolangciJsonContext : JsonSerializerContext;

/// <summary>
/// A <c>golangci-lint run</c> invocation split into the global flags preceding <c>run</c> and the
/// arguments following it. Faithful port of Rust's <c>RunInvocation</c> (<c>golangci_cmd.rs</c>:36-40).
/// </summary>
/// <param name="GlobalArgs">Flags/values that appeared before the <c>run</c> subcommand token.</param>
/// <param name="RunArgs">Arguments that appeared after the <c>run</c> subcommand token.</param>
internal sealed record GolangciRunInvocation(IReadOnlyList<string> GlobalArgs, IReadOnlyList<string> RunArgs);

/// <summary>
/// How a <c>golangci-lint</c> invocation should be executed: filtered (a <c>run</c> subcommand RTK
/// can inject JSON output flags into) or raw passthrough (any other invocation — <c>version</c>,
/// <c>help</c>, no subcommand, etc.). Faithful port of Rust's <c>Invocation</c> enum (<c>golangci_cmd.rs</c>:42-46).
/// </summary>
internal abstract record GolangciInvocation
{
    /// <summary>A <c>run</c> invocation RTK will filter through the JSON summary.</summary>
    /// <param name="Invocation">The split global/run arguments.</param>
    internal sealed record FilteredRun(GolangciRunInvocation Invocation) : GolangciInvocation;

    /// <summary>Any invocation RTK does not filter — run raw with inherited stdio.</summary>
    internal sealed record Passthrough : GolangciInvocation
    {
        internal static readonly Passthrough Instance = new();
    }
}

/// <summary>
/// Filters <c>golangci-lint</c> output, grouping issues by rule and file. Faithful port of
/// <c>src/cmds/go/golangci_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <b>Registered under the <c>golangci-lint</c> verb name.</b> Rust's clap declares this command as
/// <c>#[command(name = "golangci-lint")] GolangciLint</c>; the dispatch/registry wiring (done centrally,
/// not here) is expected to route the <c>golangci-lint</c> CLI token to <see cref="RunAsync"/>.
/// </remarks>
public static class GolangciLintCommand
{
    private static readonly string[] GolangciSubcommands =
    [
        "cache", "completion", "config", "custom", "fmt", "formatters",
        "help", "linters", "migrate", "run", "version",
    ];

    private static readonly string[] GlobalFlagsWithValue =
    [
        "-c", "--color", "--config", "--cpu-profile-path", "--mem-profile-path", "--trace-path",
    ];

    /// <summary>
    /// Registry entry point for the <c>golangci-lint</c> verb.
    /// </summary>
    /// <param name="args">The arguments following the <c>golangci-lint</c> verb.</param>
    /// <returns>The wrapped process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity, null);

    /// <summary>Test-friendly overload accepting an explicit executor.</summary>
    /// <param name="args">The arguments following the <c>golangci-lint</c> verb.</param>
    /// <param name="verbose">The rtk-level verbosity count (<c>-v</c>/<c>-vv</c>/...).</param>
    /// <param name="executor">The process executor to use, or null for the default.</param>
    /// <returns>The wrapped process's exit code.</returns>
    internal static Task<int> RunAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        return ClassifyInvocation(args) switch
        {
            GolangciInvocation.FilteredRun filtered => RunFilteredAsync(args, filtered.Invocation, verbose, executor),
            _ => RunPassthroughAsync(args, verbose, executor),
        };
    }

    private static async Task<int> RunFilteredAsync(
        string[] originalArgs, GolangciRunInvocation invocation, int verbose, IProcessExecutor? executor)
    {
        var version = await DetectMajorVersionAsync(executor).ConfigureAwait(false);
        var filteredArgs = BuildFilteredArgs(invocation, version);

        if (RuntimeOptions.Verbosity > 0 || verbose > 0)
        {
            Console.Error.Write($"Running: {FormatCommand("golangci-lint", filteredArgs)}\n");
        }

        var exitCode = await CommandRunner.RunFilteredAsync(
            "golangci-lint",
            filteredArgs,
            "golangci-lint",
            string.Join(' ', originalArgs),
            stdout =>
            {
                // v2 outputs JSON on first line + trailing text; v1 outputs just JSON.
                var jsonOutput = version >= 2 ? FirstLine(stdout) : stdout;
                return FilterGolangciJson(jsonOutput, version);
            },
            new RunOptions(FilterStdoutOnly: true)
        ).ConfigureAwait(false);

        // golangci-lint: exit 0 = clean, exit 1 = lint issues found (not an error),
        // exit 2+ = config/build error, killed-by-signal = N/A on Windows.
        return exitCode == 1 ? 0 : exitCode;
    }

    private static async Task<int> RunPassthroughAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        if ((RuntimeOptions.Verbosity > 0 || verbose > 0) && args.Length > 0)
        {
            Console.Error.Write($"golangci-lint passthrough: {string.Join(", ", args.Select(a => $"\"{a}\""))}\n");
        }

        var timer = TimedExecution.Start();
        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest("golangci-lint", args, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        var cmdLabel = $"golangci-lint {string.Join(' ', args)}".Trim();
        timer.TrackPassthrough(cmdLabel, $"rtk {cmdLabel} (passthrough)");
        return result.ExitCode;
    }

    /// <summary>
    /// Classifies a <c>golangci-lint</c> invocation as a filterable <c>run</c> or raw passthrough.
    /// Faithful port of Rust's <c>classify_invocation</c> (<c>golangci_cmd.rs</c>:168-176).
    /// </summary>
    /// <param name="args">The full argument list.</param>
    /// <returns>The classified invocation.</returns>
    internal static GolangciInvocation ClassifyInvocation(IReadOnlyList<string> args)
    {
        var idx = FindSubcommandIndex(args);
        if (idx is int i && args[i] == "run")
        {
            return new GolangciInvocation.FilteredRun(
                new GolangciRunInvocation(args.Take(i).ToArray(), args.Skip(i + 1).ToArray()));
        }

        return GolangciInvocation.Passthrough.Instance;
    }

    private static int? FindSubcommandIndex(IReadOnlyList<string> args)
    {
        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];

            if (arg == "--")
            {
                return null;
            }

            if (!arg.StartsWith('-'))
            {
                return GolangciSubcommands.Contains(arg) ? i : null;
            }

            var flag = SplitFlagName(arg);
            if (flag is not null && GolangciFlagTakesSeparateValue(arg, flag))
            {
                i++;
            }

            i++;
        }

        return null;
    }

    private static string? SplitFlagName(string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            var eqIdx = arg.IndexOf('=');
            return eqIdx >= 0 ? arg[..eqIdx] : arg;
        }

        return arg.StartsWith('-') ? arg : null;
    }

    private static bool GolangciFlagTakesSeparateValue(string arg, string flag)
    {
        if (!GlobalFlagsWithValue.Contains(flag))
        {
            return false;
        }

        return !(arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('='));
    }

    /// <summary>
    /// Builds the args passed to the real <c>golangci-lint</c> binary for a filtered <c>run</c>
    /// invocation, injecting JSON-output flags unless the user already specified an output format.
    /// Faithful port of Rust's <c>build_filtered_args</c> (<c>golangci_cmd.rs</c>:230-245).
    /// </summary>
    internal static List<string> BuildFilteredArgs(GolangciRunInvocation invocation, uint version)
    {
        var args = new List<string>(invocation.GlobalArgs) { "run" };

        if (!HasOutputFlag(invocation.RunArgs))
        {
            if (version >= 2)
            {
                args.Add("--output.json.path");
                args.Add("stdout");
            }
            else
            {
                args.Add("--out-format=json");
            }
        }

        args.AddRange(invocation.RunArgs);
        return args;
    }

    private static bool HasOutputFlag(IReadOnlyList<string> args) => args.Any(a =>
        a == "--out-format"
        || a.StartsWith("--out-format=", StringComparison.Ordinal)
        || a == "--output.json.path"
        || a.StartsWith("--output.json.path=", StringComparison.Ordinal));

    private static string FormatCommand(string baseCmd, IReadOnlyList<string> args) =>
        args.Count == 0 ? baseCmd : $"{baseCmd} {string.Join(' ', args)}";

    private static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        return idx >= 0 ? text[..idx] : text;
    }

    /// <summary>
    /// Parses the major version number out of <c>golangci-lint --version</c> output. Faithful port of
    /// Rust's <c>parse_major_version</c> (<c>golangci_cmd.rs</c>:85-99): handles both
    /// <c>"golangci-lint version 1.59.1"</c> (v1) and <c>"golangci-lint has version 2.10.0 built with ..."</c>
    /// (v2). Returns 1 on any failure to parse — the safe v1-behaviour fallback.
    /// </summary>
    /// <param name="versionOutput">The raw <c>--version</c> output.</param>
    /// <returns>The parsed major version, or 1 if it could not be determined.</returns>
    public static uint ParseMajorVersion(string versionOutput)
    {
        foreach (var word in versionOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var dotIdx = word.IndexOf('.');
            var firstPart = dotIdx >= 0 ? word[..dotIdx] : word;
            if (word.Contains('.') && uint.TryParse(firstPart, out var major))
            {
                return major;
            }
        }

        return 1;
    }

    /// <summary>
    /// Runs <c>golangci-lint --version</c> and returns its major version number. Faithful port of
    /// Rust's <c>detect_major_version</c> (<c>golangci_cmd.rs</c>:101-118). Returns 1 on any failure.
    /// </summary>
    /// <param name="executor">The process executor to use, or null for the default.</param>
    /// <returns>The detected major version, or 1 on failure.</returns>
    internal static async Task<uint> DetectMajorVersionAsync(IProcessExecutor? executor)
    {
        try
        {
            var exec = executor ?? new ProcessExecutor();
            var request = new ExecutionRequest("golangci-lint", ["--version"], CaptureMode: ExecutionCaptureMode.Separate);
            var result = await exec.ExecuteAsync(request).ConfigureAwait(false);
            var versionText = string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout;
            return ParseMajorVersion(versionText);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Groups golangci-lint JSON output by linter and file. Faithful port of Rust's
    /// <c>filter_golangci_json</c> (<c>golangci_cmd.rs</c>:264-372).
    /// </summary>
    /// <param name="output">The raw golangci-lint JSON stdout (or its first line, for v2).</param>
    /// <param name="version">The detected golangci-lint major version — controls whether source-line previews are shown.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGolangciJson(string output, uint version)
    {
        GolangciOutput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(output, GolangciJsonContext.Default.GolangciOutput);
        }
        catch (JsonException e)
        {
            return $"golangci-lint (JSON parse failed: {e.Message})\n{Utils.Truncate(output, Config.LoadOrDefault().Limits.PassthroughMaxChars)}";
        }

        var issues = parsed?.Issues ?? [];

        if (issues.Count == 0)
        {
            return "golangci-lint: No issues found";
        }

        var totalIssues = issues.Count;
        var uniqueFiles = new HashSet<string>(issues.Select(i => i.Pos.Filename), StringComparer.Ordinal);
        var totalFiles = uniqueFiles.Count;

        var byLinter = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            byLinter[issue.FromLinter] = byLinter.GetValueOrDefault(issue.FromLinter) + 1;
        }

        var byFile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            byFile[issue.Pos.Filename] = byFile.GetValueOrDefault(issue.Pos.Filename) + 1;
        }

        var fileCounts = byFile.ToList();
        fileCounts.Sort((a, b) => b.Value.CompareTo(a.Value));

        var result = new StringBuilder();
        result.Append($"golangci-lint: {totalIssues} issues in {totalFiles} files\n");

        var linterCounts = byLinter.ToList();
        linterCounts.Sort((a, b) => b.Value.CompareTo(a.Value));

        if (linterCounts.Count > 0)
        {
            result.Append("Top linters:\n");
            foreach (var (linter, count) in linterCounts.Take(10))
            {
                result.Append($"  {linter} ({count}x)\n");
            }

            result.Append('\n');
        }

        const int maxGolangciFiles = 10; // Rust CAP_WARNINGS from src/core/truncate.rs.
        result.Append("Top files:\n");
        foreach (var (file, count) in fileCounts.Take(maxGolangciFiles))
        {
            var shortPath = CompactPath(file);
            result.Append($"  {shortPath} ({count} issues)\n");

            var fileLinters = new Dictionary<string, List<GolangciIssue>>(StringComparer.Ordinal);
            foreach (var issue in issues.Where(i => i.Pos.Filename == file))
            {
                if (!fileLinters.TryGetValue(issue.FromLinter, out var list))
                {
                    list = [];
                    fileLinters[issue.FromLinter] = list;
                }

                list.Add(issue);
            }

            var fileLinterCounts = fileLinters.ToList();
            fileLinterCounts.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

            foreach (var (linter, linterIssues) in fileLinterCounts.Take(3))
            {
                result.Append($"    {linter} ({linterIssues.Count})\n");

                // v2 only: show first source line for this linter-file group.
                if (version >= 2 && linterIssues.Count > 0)
                {
                    var firstIssue = linterIssues[0];
                    if (firstIssue.SourceLines.Count > 0)
                    {
                        var trimmed = firstIssue.SourceLines[0].Trim();
                        var display = TakeChars(trimmed, 80);
                        result.Append($"      → {display}\n");
                    }
                }
            }
        }

        if (fileCounts.Count > maxGolangciFiles)
        {
            result.Append($"\n... +{fileCounts.Count - maxGolangciFiles} more files\n");
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Takes at most <paramref name="maxChars"/> Unicode scalar values from <paramref name="s"/>,
    /// without appending an ellipsis (unlike <see cref="Utils.Truncate"/>). Faithful port of the
    /// <c>char_indices().nth(80)</c> slicing in Rust's <c>filter_golangci_json</c>
    /// (<c>golangci_cmd.rs</c>:353-356), which is scalar-value-safe (never splits a surrogate pair).
    /// </summary>
    private static string TakeChars(string s, int maxChars)
    {
        var sb = new StringBuilder();
        var taken = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            if (taken >= maxChars)
            {
                break;
            }

            sb.Append(rune.ToString());
            taken++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Compacts a file path down to a short, package-relative display form (<c>pkg/</c>, <c>cmd/</c>,
    /// <c>internal/</c> prefixes preferred; otherwise just the file name). Faithful port of Rust's
    /// <c>compact_path</c> (<c>golangci_cmd.rs</c>:375-389).
    /// </summary>
    /// <param name="path">The raw file path from a golangci-lint issue.</param>
    /// <returns>The compacted path.</returns>
    public static string CompactPath(string path)
    {
        var normalized = path.Replace('\\', '/');

        var pkgPos = normalized.LastIndexOf("/pkg/", StringComparison.Ordinal);
        if (pkgPos >= 0)
        {
            return "pkg/" + normalized[(pkgPos + 5)..];
        }

        var cmdPos = normalized.LastIndexOf("/cmd/", StringComparison.Ordinal);
        if (cmdPos >= 0)
        {
            return "cmd/" + normalized[(cmdPos + 5)..];
        }

        var internalPos = normalized.LastIndexOf("/internal/", StringComparison.Ordinal);
        if (internalPos >= 0)
        {
            return "internal/" + normalized[(internalPos + 10)..];
        }

        var slashPos = normalized.LastIndexOf('/');
        return slashPos >= 0 ? normalized[(slashPos + 1)..] : normalized;
    }
}
