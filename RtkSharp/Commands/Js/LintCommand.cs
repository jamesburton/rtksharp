using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Implements the <c>rtk lint</c> CLI verb: a multi-linter dispatcher (despite the "ESLint with
/// grouped rule violations" doc-comment/command-inventory description) that detects the linter from
/// its arguments and groups violations by rule and file. Faithful port of Rust
/// <c>src/cmds/js/lint_cmd.rs</c> for the <c>eslint</c>, <c>pylint</c>, and generic-fallback paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope cut: <c>ruff</c>/<c>mypy</c> delegate to unported Python-ecosystem modules — not faked,
/// disclosed instead.</b> Rust's <c>lint_cmd.rs</c> imports <c>crate::ruff_cmd</c>/<c>crate::mypy_cmd</c>
/// and calls <c>ruff_cmd::filter_ruff_check_json</c>/<c>mypy_cmd::filter_mypy_output</c> directly for
/// those two linters — neither module exists anywhere in RtkSharp yet (the Python ecosystem hasn't
/// been touched by any phase), the same class of hidden dependency <c>rtk session</c> had on
/// <c>discover</c>. Rather than fabricate a plausible-looking ruff/mypy grouped-output format this
/// port has never verified against the real filters, <see cref="RunCoreAsync"/> still resolves and
/// invokes <c>ruff</c>/<c>mypy</c> with the correct flags (that part of <c>run()</c> is generic and
/// has no Python-specific dependency), but prints the RAW combined stdout+stderr unfiltered for those
/// two linter names — an honest "not grouped yet" passthrough rather than a silently wrong grouping,
/// matching the Phase 8 "genuinely unimplemented ecosystem, don't fake it" convention applied to
/// <c>next</c>/<c>prettier</c>/<c>biome</c>. See <c>docs/parity/compatibility-ledger.md</c>.
/// </para>
/// <para>
/// <b>Preserved Rust-source oddity: the "not installed" hint always suggests <c>pip install</c>, even
/// for JS linters.</b> Rust's error context string (<c>lint_cmd.rs</c>:167-170) is
/// <c>"Failed to run {linter}. Is it installed? Try: pip install {linter} (or npm/pnpm for JS linters)"</c>
/// for every linter, including <c>eslint</c> — a copy-paste artifact from the Python-linter case,
/// preserved verbatim rather than "fixed" to suggest the right installer per linter.
/// </para>
/// </remarks>
public static class LintCommand
{
    /// <summary>Rust's <c>CAP_ERRORS</c> (<c>core/truncate.rs:5</c>) — the generic-fallback issue cap.</summary>
    private const int CapErrors = 20;

    /// <summary>Rust's <c>CAP_WARNINGS</c> (<c>core/truncate.rs:7</c>) — the eslint/pylint per-report file cap.</summary>
    private const int CapWarnings = 10;

    /// <summary>
    /// Registry entry point. Runs <c>rtk lint</c> with the given arguments (the remainder after the
    /// <c>lint</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>lint</c>.</param>
    /// <returns>The linter's real exit code, or 1 on an rtk-level failure.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor());

    /// <summary>
    /// Runs <c>rtk lint</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>lint</c>.</param>
    /// <param name="verbose">The top-level verbosity count (Rust's <c>cli.verbose</c>).</param>
    /// <param name="executor">The process executor to spawn the linter with.</param>
    /// <returns>The linter's real exit code, or 1 on an rtk-level failure.</returns>
    public static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        try
        {
            return await RunCoreAsync(args, verbose, executor).ConfigureAwait(false);
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
    /// The testable core of <see cref="RunAsync(string[],int,IProcessExecutor)"/>. Faithful port of
    /// <c>run</c> (<c>lint_cmd.rs</c>:90-220).
    /// </summary>
    /// <param name="args">The arguments following the <c>lint</c> verb.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn the linter with.</param>
    /// <returns>The linter's real exit code.</returns>
    internal static async Task<int> RunCoreAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var skip = StripPmPrefix(args);
        var effectiveArgs = args[skip..];
        var (linter, isExplicit) = DetectLinter(effectiveArgs);

        // Python linters use resolved_command() directly (they're on PATH via pip/pipx); JS linters
        // use package_manager_exec (npx/pnpm exec).
        string fileName;
        var cmdArgs = new List<string>();
        if (IsPythonLinter(linter))
        {
            fileName = PathResolver.Resolve(linter);
        }
        else
        {
            var pm = PackageManagerDetection.PackageManagerExec(linter);
            fileName = pm.FileName;
            cmdArgs.AddRange(pm.BaseArguments);
        }

        // Format flags per linter.
        if (linter == "eslint")
        {
            cmdArgs.Add("-f");
            cmdArgs.Add("json");
        }
        else if (linter == "ruff" && !effectiveArgs.Contains("--output-format"))
        {
            cmdArgs.Add("check");
            cmdArgs.Add("--output-format=json");
        }
        else if (linter == "pylint" && !effectiveArgs.Contains("--output-format"))
        {
            cmdArgs.Add("--output-format=json2");
        }

        // Determine the start index into effectiveArgs for the user's own arguments (skipping the
        // linter-name token, and "ruff check" if we already injected "check").
        int startIdx;
        if (!isExplicit)
        {
            startIdx = 0;
        }
        else if (linter == "ruff" && effectiveArgs.Length > 0 && effectiveArgs[0] == "ruff")
        {
            startIdx = effectiveArgs.Length > 1 && effectiveArgs[1] == "check" ? 2 : 1;
        }
        else
        {
            startIdx = 1;
        }

        for (var i = startIdx; i < effectiveArgs.Length; i++)
        {
            var arg = effectiveArgs[i];
            if ((linter == "ruff" || linter == "pylint") && arg.StartsWith("--output-format", StringComparison.Ordinal))
            {
                continue;
            }

            cmdArgs.Add(arg);
        }

        // Default to current directory if no path specified.
        if (linter is "ruff" or "pylint" or "mypy" or "eslint")
        {
            var hasPath = effectiveArgs.Skip(startIdx).Any(a => !a.StartsWith('-') && !a.Contains('='));
            if (!hasPath)
            {
                cmdArgs.Add(".");
            }
        }

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {linter} with structured output\n");
        }

        ExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(new ExecutionRequest(fileName, cmdArgs)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Failed to run {linter}. Is it installed? Try: pip install {linter} (or npm/pnpm for JS linters): {ex.Message}", ex);
        }

        if (!result.WasStarted)
        {
            throw new IOException(
                $"Failed to run {linter}. Is it installed? Try: pip install {linter} (or npm/pnpm for JS linters)"
                + (result.Failure is { } f ? $": {f}" : string.Empty));
        }

        // A process killed by a signal (SIGABRT, SIGKILL, ...) reports an exit code above 128 on Unix;
        // on Windows this is a defensive-only branch (no signal-based termination), preserved for
        // parity with the Rust source rather than made Windows-conditional.
        if (result.ExitCode != 0 && result.ExitCode > 128)
        {
            Console.Error.Write("[warn] Linter process terminated abnormally (possibly out of memory)\n");
            if (!string.IsNullOrEmpty(result.Stderr))
            {
                var lines = ReadCommand.SplitLines(result.Stderr).Take(5);
                Console.Error.Write($"stderr: {string.Join('\n', lines)}\n");
            }

            return result.ExitCode;
        }

        var raw = $"{result.Stdout}\n{result.Stderr}";

        var filtered = linter switch
        {
            "eslint" => FilterEslintJson(result.Stdout),
            "pylint" => FilterPylintJson(result.Stdout),
            // ruff/mypy: unfiltered passthrough — see class remarks on the disclosed scope cut.
            "ruff" or "mypy" => raw.Trim(),
            _ => FilterGenericLint(raw),
        };

        var hint = Tee.TeeAndHint(raw, "lint", result.ExitCode);
        Console.Out.Write(hint is not null ? $"{filtered}\n{hint}\n" : $"{filtered}\n");

        var argsJoined = string.Join(' ', args);
        timer.Track($"{linter} {argsJoined}", $"rtk lint {linter} {argsJoined}", raw, filtered);

        return result.ExitCode;
    }

    /// <summary>
    /// Checks if a linter name is Python-based (uses pip/pipx, resolved directly via <c>PATH</c>, not
    /// npm/pnpm). Faithful port of <c>is_python_linter</c> (<c>lint_cmd.rs</c>:56-58).
    /// </summary>
    /// <param name="linter">The linter name to check.</param>
    /// <returns><see langword="true"/> for <c>ruff</c>/<c>pylint</c>/<c>mypy</c>/<c>flake8</c>.</returns>
    internal static bool IsPythonLinter(string linter) => linter is "ruff" or "pylint" or "mypy" or "flake8";

    /// <summary>
    /// Counts how many leading tokens are a package-manager wrapper prefix (<c>npx</c>, <c>bunx</c>,
    /// <c>pnpm</c>, <c>yarn</c>, <c>exec</c>) to skip. Faithful port of <c>strip_pm_prefix</c>
    /// (<c>lint_cmd.rs</c>:62-73).
    /// </summary>
    /// <param name="args">The raw arguments following the <c>lint</c> verb.</param>
    /// <returns>The number of leading tokens to skip.</returns>
    internal static int StripPmPrefix(IReadOnlyList<string> args)
    {
        string[] pmNames = ["npx", "bunx", "pnpm", "yarn"];
        var skip = 0;
        foreach (var arg in args)
        {
            if (pmNames.Contains(arg) || arg == "exec")
            {
                skip++;
            }
            else
            {
                break;
            }
        }

        return skip;
    }

    /// <summary>
    /// Detects the linter name from arguments (after <see cref="StripPmPrefix"/>). Faithful port of
    /// <c>detect_linter</c> (<c>lint_cmd.rs</c>:77-88).
    /// </summary>
    /// <param name="args">The arguments after stripping any package-manager prefix.</param>
    /// <returns>The linter name and whether it was explicitly specified (vs. defaulted to <c>eslint</c>).</returns>
    internal static (string Linter, bool IsExplicit) DetectLinter(IReadOnlyList<string> args)
    {
        var isPathOrFlag = args.Count == 0 || args[0].StartsWith('-') || args[0].Contains('/') || args[0].Contains('.');

        return isPathOrFlag ? ("eslint", false) : (args[0], true);
    }

    /// <summary>
    /// Groups ESLint JSON output (<c>-f json</c>) by rule and by file. Faithful port of
    /// <c>filter_eslint_json</c> (<c>lint_cmd.rs</c>:223-319).
    /// </summary>
    /// <param name="output">The raw ESLint JSON stdout.</param>
    /// <returns>The grouped summary text.</returns>
    internal static string FilterEslintJson(string output)
    {
        List<EslintResult>? results;
        try
        {
            results = JsonSerializer.Deserialize(output, LintJsonContext.Default.ListEslintResult);
        }
        catch (JsonException e)
        {
            return $"ESLint output (JSON parse failed: {e.Message})\n{Utils.Truncate(output, Config.LoadOrDefault().Limits.PassthroughMaxChars)}";
        }

        results ??= [];

        var totalErrors = results.Sum(r => r.ErrorCount);
        var totalWarnings = results.Sum(r => r.WarningCount);
        var totalFiles = results.Count(r => r.Messages.Count > 0);

        if (totalErrors == 0 && totalWarnings == 0)
        {
            return "ESLint: No issues found";
        }

        var byRule = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var msg in results.SelectMany(r => r.Messages))
        {
            if (msg.RuleId is { } rule)
            {
                byRule[rule] = byRule.GetValueOrDefault(rule) + 1;
            }
        }

        var byFile = results
            .Where(r => r.Messages.Count > 0)
            .Select(r => (Result: r, Count: r.Messages.Count))
            .OrderByDescending(x => x.Count)
            .ToList();

        var sb = new global::System.Text.StringBuilder();
        sb.Append($"ESLint: {totalErrors} errors, {totalWarnings} warnings in {totalFiles} files\n");

        var ruleCounts = byRule.OrderByDescending(kv => kv.Value).ToList();
        if (ruleCounts.Count > 0)
        {
            sb.Append("Top rules:\n");
            foreach (var (rule, count) in ruleCounts.Take(10))
            {
                sb.Append($"  {rule} ({count}x)\n");
            }

            sb.Append('\n');
        }

        sb.Append("Top files:\n");
        foreach (var (fileResult, count) in byFile.Take(CapWarnings))
        {
            sb.Append($"  {CompactPath(fileResult.FilePath)} ({count} issues)\n");

            var fileRules = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var msg in fileResult.Messages)
            {
                if (msg.RuleId is { } rule)
                {
                    fileRules[rule] = fileRules.GetValueOrDefault(rule) + 1;
                }
            }

            foreach (var (rule, count2) in fileRules.OrderByDescending(kv => kv.Value).Take(3))
            {
                sb.Append($"    {rule} ({count2})\n");
            }
        }

        if (byFile.Count > CapWarnings)
        {
            sb.Append($"\n… +{byFile.Count - CapWarnings} more files\n");
            var allFileLines = string.Join('\n', byFile.Select(x => $"{CompactPath(x.Result.FilePath)} ({x.Count} issues)"));
            if (Tee.ForceTeeTailHint(allFileLines, "eslint-files", CapWarnings + 1) is { } hint)
            {
                sb.Append($"  {hint}\n");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Groups Pylint JSON2 output by symbol and by file. Faithful port of <c>filter_pylint_json</c>
    /// (<c>lint_cmd.rs</c>:322-446).
    /// </summary>
    /// <param name="output">The raw Pylint JSON2 stdout.</param>
    /// <returns>The grouped summary text.</returns>
    internal static string FilterPylintJson(string output)
    {
        List<PylintDiagnostic>? diagnostics;
        try
        {
            diagnostics = JsonSerializer.Deserialize(output, LintJsonContext.Default.ListPylintDiagnostic);
        }
        catch (JsonException e)
        {
            return $"Pylint output (JSON parse failed: {e.Message})\n{Utils.Truncate(output, Config.LoadOrDefault().Limits.PassthroughMaxChars)}";
        }

        diagnostics ??= [];

        if (diagnostics.Count == 0)
        {
            return "Pylint: No issues found";
        }

        var errors = diagnostics.Count(d => d.MsgType == "error");
        var warnings = diagnostics.Count(d => d.MsgType == "warning");
        var conventions = diagnostics.Count(d => d.MsgType == "convention");
        var refactors = diagnostics.Count(d => d.MsgType == "refactor");

        var totalFiles = diagnostics.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count();

        var bySymbol = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            var key = $"{diag.Symbol} ({diag.MessageId})";
            bySymbol[key] = bySymbol.GetValueOrDefault(key) + 1;
        }

        var byFile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            byFile[diag.Path] = byFile.GetValueOrDefault(diag.Path) + 1;
        }

        var fileCounts = byFile.OrderByDescending(kv => kv.Value).ToList();

        var sb = new global::System.Text.StringBuilder();
        sb.Append($"Pylint: {diagnostics.Count} issues in {totalFiles} files\n");

        if (errors > 0 || warnings > 0)
        {
            sb.Append($"  {errors} errors, {warnings} warnings");
            if (conventions > 0 || refactors > 0)
            {
                sb.Append($", {conventions} conventions, {refactors} refactors");
            }

            sb.Append('\n');
        }

        var symbolCounts = bySymbol.OrderByDescending(kv => kv.Value).ToList();
        if (symbolCounts.Count > 0)
        {
            sb.Append("Top rules:\n");
            foreach (var (symbol, count) in symbolCounts.Take(10))
            {
                sb.Append($"  {symbol} ({count}x)\n");
            }

            sb.Append('\n');
        }

        sb.Append("Top files:\n");
        foreach (var (file, count) in fileCounts.Take(CapWarnings))
        {
            sb.Append($"  {CompactPath(file)} ({count} issues)\n");

            var fileSymbols = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var diag in diagnostics.Where(d => d.Path == file))
            {
                var key = $"{diag.Symbol} ({diag.MessageId})";
                fileSymbols[key] = fileSymbols.GetValueOrDefault(key) + 1;
            }

            foreach (var (symbol, count2) in fileSymbols.OrderByDescending(kv => kv.Value).Take(3))
            {
                sb.Append($"    {symbol} ({count2})\n");
            }
        }

        if (fileCounts.Count > CapWarnings)
        {
            sb.Append($"\n… +{fileCounts.Count - CapWarnings} more files\n");
            var allFileLines = string.Join('\n', fileCounts.Select(x => $"{CompactPath(x.Key)} ({x.Value} issues)"));
            if (Tee.ForceTeeTailHint(allFileLines, "pylint-files", CapWarnings + 1) is { } hint)
            {
                sb.Append($"  {hint}\n");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Fallback for non-JSON linters: a naive line-scan counting "warning"/"error" substrings.
    /// Faithful port of <c>filter_generic_lint</c> (<c>lint_cmd.rs</c>:449-489).
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr.</param>
    /// <returns>The summarized issue count and (capped) issue lines.</returns>
    internal static string FilterGenericLint(string output)
    {
        var warnings = 0;
        var errors = 0;
        var issues = new List<string>();

        // Rust's `output.lines()` (lint_cmd.rs:454) strips a trailing '\r' per line; a naive
        // Split('\n') on CRLF child-process output would leave it in, corrupting both the emitted
        // issue text and the 100-char Truncate budget below.
        foreach (var line in ReadCommand.SplitLines(output))
        {
            var lineLower = line.ToLowerInvariant();
            if (lineLower.Contains("warning", StringComparison.Ordinal))
            {
                warnings++;
                issues.Add(line);
            }

            if (lineLower.Contains("error", StringComparison.Ordinal) && !lineLower.Contains("0 error", StringComparison.Ordinal))
            {
                errors++;
                issues.Add(line);
            }
        }

        if (errors == 0 && warnings == 0)
        {
            return "Lint: No issues found";
        }

        var sb = new global::System.Text.StringBuilder();
        sb.Append($"Lint: {errors} errors, {warnings} warnings\n");

        foreach (var issue in issues.Take(CapErrors))
        {
            sb.Append($"{Utils.Truncate(issue, 100)}\n");
        }

        if (issues.Count > CapErrors)
        {
            sb.Append($"\n… +{issues.Count - CapErrors} more issues\n");
            var allIssues = string.Join('\n', issues);
            if (Tee.ForceTeeTailHint(allIssues, "lint-issues", CapErrors + 1) is { } hint)
            {
                sb.Append($"  {hint}\n");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Shortens a file path by keeping only the portion from the last <c>src/</c>/<c>lib/</c> segment
    /// onward, or just the file name if neither is present. Faithful port of <c>compact_path</c>
    /// (<c>lint_cmd.rs</c>:492-505).
    /// </summary>
    /// <param name="path">The file path to shorten.</param>
    /// <returns>The shortened path.</returns>
    internal static string CompactPath(string path)
    {
        var normalized = path.Replace('\\', '/');

        var srcPos = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
        if (srcPos >= 0)
        {
            return "src/" + normalized[(srcPos + 5)..];
        }

        var libPos = normalized.LastIndexOf("/lib/", StringComparison.Ordinal);
        if (libPos >= 0)
        {
            return "lib/" + normalized[(libPos + 5)..];
        }

        var slashPos = normalized.LastIndexOf('/');
        return slashPos >= 0 ? normalized[(slashPos + 1)..] : normalized;
    }
}

/// <summary>ESLint's <c>-f json</c> per-message shape. Port of Rust <c>EslintMessage</c> (<c>lint_cmd.rs</c>:14-22).</summary>
internal sealed class EslintMessage
{
    [JsonPropertyName("ruleId")]
    public string? RuleId { get; set; }

    [JsonPropertyName("severity")]
    public byte Severity { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>ESLint's <c>-f json</c> per-file shape. Port of Rust <c>EslintResult</c> (<c>lint_cmd.rs</c>:24-33).</summary>
internal sealed class EslintResult
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<EslintMessage> Messages { get; set; } = [];

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }
}

/// <summary>Pylint's <c>--output-format=json2</c> per-diagnostic shape. Port of Rust <c>PylintDiagnostic</c> (<c>lint_cmd.rs</c>:35-53).</summary>
internal sealed class PylintDiagnostic
{
    [JsonPropertyName("type")]
    public string MsgType { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("message-id")]
    public string MessageId { get; set; } = "";
}

/// <summary>Source-generated JSON context for <see cref="LintCommand"/>'s ESLint/Pylint DTOs, avoiding reflection-based (de)serialization under <c>PublishAot</c>.</summary>
[JsonSerializable(typeof(List<EslintResult>))]
[JsonSerializable(typeof(List<PylintDiagnostic>))]
internal sealed partial class LintJsonContext : JsonSerializerContext;
