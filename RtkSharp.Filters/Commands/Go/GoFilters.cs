using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Go;

/// <summary>
/// A single <c>go test -json</c> NDJSON event. Faithful port of Rust's <c>GoTestEvent</c>
/// (<c>src/cmds/go/go_cmd.rs</c>:13-32).
/// </summary>
internal sealed class GoTestEvent
{
    [JsonPropertyName("Time")]
    public string? Time { get; set; }

    [JsonPropertyName("Action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("Package")]
    public string? Package { get; set; }

    [JsonPropertyName("Test")]
    public string? Test { get; set; }

    [JsonPropertyName("Output")]
    public string? Output { get; set; }

    [JsonPropertyName("Elapsed")]
    public double? Elapsed { get; set; }

    [JsonPropertyName("ImportPath")]
    public string? ImportPath { get; set; }

    [JsonPropertyName("FailedBuild")]
    public string? FailedBuild { get; set; }
}

/// <summary>Source-generated JSON context for <see cref="GoTestEvent"/>, avoiding reflection-based (de)serialization under <c>PublishAot</c>.</summary>
[JsonSerializable(typeof(GoTestEvent))]
internal sealed partial class GoTestJsonContext : JsonSerializerContext;

/// <summary>
/// Per-package aggregate accumulated while scanning a <c>go test -json</c> stream. Faithful port of
/// Rust's <c>PackageResult</c> (<c>go_cmd.rs</c>:34-44).
/// </summary>
internal sealed class GoPackageResult
{
    public int Pass { get; set; }

    public int Fail { get; set; }

    public int Skip { get; set; }

    public bool BuildFailed { get; set; }

    public List<string> BuildErrors { get; } = [];

    /// <summary>(test name, collected output lines) pairs, in the order tests failed.</summary>
    public List<(string Test, List<string> Output)> FailedTests { get; } = [];

    /// <summary>Package-level failure (timeout, signal, etc.) with no specific test or build error.</summary>
    public bool PackageFailed { get; set; }

    public List<string> PackageFailOutput { get; } = [];
}

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
/// Pure filter functions for <c>go test</c>/<c>go build</c>/<c>go vet</c>/<c>golangci-lint</c> output.
/// Faithful ports of Rust's <c>filter_go_test_json</c>, <c>filter_go_build</c>/
/// <c>filter_go_build_with_exit</c>, <c>filter_go_vet</c> (<c>src/cmds/go/go_cmd.rs</c>), and
/// <c>filter_golangci_json</c> (<c>src/cmds/go/golangci_cmd.rs</c>).
/// </summary>
public static class GoFilters
{
    // Rust CAP_ERRORS from src/core/truncate.rs, reused for build errors and vet issues alike.
    private const int CapErrors = 20;

    /// <summary>
    /// The default passthrough character limit, matching <c>Config</c>'s <c>Limits.PassthroughMaxChars</c>
    /// default of 2000. <see cref="RtkSharp.Filters"/> is a pure library with no dependency on
    /// <c>RtkSharp.Core.Config</c> (which lives in the CLI project), so a customized
    /// <c>PassthroughMaxChars</c> in the CLI's local config is not honored here — matching the
    /// established pattern in <c>LintFilters</c>/<c>VitestFilters</c>/<c>OutputParser</c>.
    /// </summary>
    private const int DefaultPassthroughMaxChars = 2000;

    /// <summary>
    /// Parses <c>go test -json</c> NDJSON output into a compact pass/fail summary. Faithful port of
    /// Rust's <c>filter_go_test_json</c> (<c>go_cmd.rs</c>:302-496): 90% token reduction is achieved by
    /// parsing the structured JSON event stream directly rather than block-grouping raw text.
    /// </summary>
    /// <param name="output">The raw <c>go test -json</c> stdout (one JSON object per line).</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGoTestJson(string output)
    {
        var packages = new Dictionary<string, GoPackageResult>(StringComparer.Ordinal);
        var currentTestOutput = new Dictionary<(string Package, string Test), List<string>>();
        var buildOutput = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        GoPackageResult GetPackage(string name)
        {
            if (!packages.TryGetValue(name, out var result))
            {
                result = new GoPackageResult();
                packages[name] = result;
            }

            return result;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            GoTestEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize(trimmed, GoTestJsonContext.Default.GoTestEvent);
            }
            catch (JsonException)
            {
                continue; // Skip non-JSON lines.
            }

            if (evt is null)
            {
                continue;
            }

            if (evt.Action == "build-output")
            {
                if (evt.ImportPath is { } importPath && evt.Output is { } outputText)
                {
                    var text = outputText.TrimEnd();
                    if (text.Length != 0)
                    {
                        if (!buildOutput.TryGetValue(importPath, out var lines))
                        {
                            lines = [];
                            buildOutput[importPath] = lines;
                        }

                        lines.Add(text);
                    }
                }

                continue;
            }

            if (evt.Action == "build-fail")
            {
                // build-fail carries ImportPath — handled when the package-level fail arrives.
                continue;
            }

            var package = evt.Package ?? "unknown";
            var pkgResult = GetPackage(package);

            switch (evt.Action)
            {
                case "pass" when evt.Test is not null:
                    pkgResult.Pass++;
                    break;

                case "fail":
                    if (evt.Test is { } failedTest)
                    {
                        pkgResult.Fail++;
                        var key = (package, failedTest);
                        var outputs = currentTestOutput.Remove(key, out var removed) ? removed : [];
                        pkgResult.FailedTests.Add((failedTest, outputs));
                    }
                    else if (evt.FailedBuild is not null)
                    {
                        pkgResult.BuildFailed = true;
                        if (buildOutput.Remove(evt.FailedBuild, out var errors))
                        {
                            pkgResult.BuildErrors.AddRange(errors);
                        }
                    }
                    else
                    {
                        pkgResult.PackageFailed = true;
                    }

                    break;

                case "skip" when evt.Test is not null:
                    pkgResult.Skip++;
                    break;

                case "output":
                    if (evt.Output is { } outputLine)
                    {
                        if (evt.Test is { } test)
                        {
                            var key = (package, test);
                            if (!currentTestOutput.TryGetValue(key, out var lines))
                            {
                                lines = [];
                                currentTestOutput[key] = lines;
                            }

                            lines.Add(outputLine.TrimEnd());
                        }
                        else
                        {
                            var t = outputLine.Trim();
                            if (t.Length != 0)
                            {
                                pkgResult.PackageFailOutput.Add(t);
                            }
                        }
                    }

                    break;

                default:
                    // run, pause, cont, etc.
                    break;
            }
        }

        var totalPackages = packages.Count;
        var totalPass = packages.Values.Sum(p => p.Pass);
        var totalFail = packages.Values.Sum(p => p.Fail);
        var totalSkip = packages.Values.Sum(p => p.Skip);
        var totalBuildFail = packages.Values.Count(p => p.BuildFailed);
        // Only count package-level fails for packages with no individual test or build failures.
        // go test -json emits a trailing package-level {"action":"fail"} after any test failure
        // too, but that event is just a cascade — the individual test failures are already counted.
        var totalPkgFail = packages.Values.Count(p => p.PackageFailed && p.Fail == 0 && !p.BuildFailed);

        var hasFailures = totalFail > 0 || totalBuildFail > 0 || totalPkgFail > 0;

        if (!hasFailures && totalPass == 0)
        {
            return "Go test: No tests found";
        }

        if (!hasFailures)
        {
            return $"Go test: {totalPass} passed in {totalPackages} packages";
        }

        var result = new StringBuilder();
        result.Append($"Go test: {totalPass} passed, {totalFail + totalBuildFail + totalPkgFail} failed");
        if (totalSkip > 0)
        {
            result.Append($", {totalSkip} skipped");
        }

        result.Append($" in {totalPackages} packages\n");

        // Show package-level failures first (timeouts, signals, panics).
        // Skip packages that already have individual test-level failures — those are displayed
        // in the per-package section below and the package-level event is just a cascade.
        foreach (var (package, pkgResult) in packages)
        {
            if (!pkgResult.PackageFailed || pkgResult.Fail > 0 || pkgResult.BuildFailed)
            {
                continue;
            }

            result.Append($"\n{CompactPackageName(package)} [FAIL]\n");

            foreach (var line in pkgResult.PackageFailOutput)
            {
                var t = line.Trim();
                if (t.Length != 0)
                {
                    result.Append($"  {Utils.Truncate(t, 120)}\n");
                }
            }
        }

        // Show build failures.
        foreach (var (package, pkgResult) in packages)
        {
            if (!pkgResult.BuildFailed)
            {
                continue;
            }

            result.Append($"\n{CompactPackageName(package)} [build failed]\n");

            foreach (var line in pkgResult.BuildErrors)
            {
                var t = line.Trim();
                // Skip the "# package" header line.
                if (!t.StartsWith('#') && t.Length != 0)
                {
                    result.Append($"  {Utils.Truncate(t, 120)}\n");
                }
            }
        }

        // Show failed tests grouped by package.
        foreach (var (package, pkgResult) in packages)
        {
            if (pkgResult.Fail == 0)
            {
                continue;
            }

            result.Append($"\n{CompactPackageName(package)} ({pkgResult.Pass} passed, {pkgResult.Fail} failed)\n");

            foreach (var (test, outputs) in pkgResult.FailedTests)
            {
                result.Append($"  [FAIL] {test}\n");

                foreach (var line in SelectGoTestFailureLines(outputs))
                {
                    result.Append($"     {Utils.Truncate(line, 100)}\n");
                }
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Selects up to 5 relevant lines (file:line locations, failure keywords, and one line of
    /// context following a location) from a failed test's collected output. Faithful port of
    /// Rust's <c>select_go_test_failure_lines</c> (<c>go_cmd.rs</c>:498-541).
    /// </summary>
    private static List<string> SelectGoTestFailureLines(IReadOnlyList<string> outputs)
    {
        var relevant = new List<string>();
        var keepNextContextLine = false;

        foreach (var line in outputs)
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0
                || trimmed.StartsWith("=== RUN", StringComparison.Ordinal)
                || trimmed.StartsWith("--- FAIL", StringComparison.Ordinal)
                || trimmed.StartsWith("--- PASS", StringComparison.Ordinal))
            {
                keepNextContextLine = false;
                continue;
            }

            var isLocation = IsGoTestLocationLine(trimmed);
            var isFailure = IsGoTestFailureLine(trimmed);

            if (isLocation || isFailure || keepNextContextLine)
            {
                relevant.Add(trimmed);
                keepNextContextLine = isLocation;
            }
            else
            {
                keepNextContextLine = false;
            }

            if (relevant.Count >= 5)
            {
                break;
            }
        }

        if (relevant.Count == 0)
        {
            foreach (var raw in outputs)
            {
                var line = raw.Trim();
                if (line.Length != 0
                    && !line.StartsWith("=== RUN", StringComparison.Ordinal)
                    && !line.StartsWith("--- FAIL", StringComparison.Ordinal)
                    && !line.StartsWith("--- PASS", StringComparison.Ordinal))
                {
                    relevant.Add(line);
                    break;
                }
            }
        }

        return relevant;
    }

    private static bool IsGoTestLocationLine(string line)
    {
        var idx = line.IndexOf(".go:", StringComparison.Ordinal);
        if (idx < 0)
        {
            return false;
        }

        var restStart = idx + ".go:".Length;
        return restStart < line.Length && char.IsAsciiDigit(line[restStart]);
    }

    private static bool IsGoTestFailureLine(string line)
    {
        var lower = line.ToLowerInvariant();

        return lower.StartsWith("panic:", StringComparison.Ordinal)
            || lower.StartsWith("error:", StringComparison.Ordinal)
            || lower.Contains(" error:", StringComparison.Ordinal)
            || lower.Contains("expected", StringComparison.Ordinal)
            || lower.Contains("got", StringComparison.Ordinal)
            || lower.Contains("want", StringComparison.Ordinal)
            || lower.Contains("actual", StringComparison.Ordinal)
            || lower.Contains("assert", StringComparison.Ordinal)
            || lower.Contains("mismatch", StringComparison.Ordinal)
            || lower.Contains("unexpected", StringComparison.Ordinal)
            || lower.Contains("fatal", StringComparison.Ordinal)
            || line.StartsWith("at ", StringComparison.Ordinal);
    }

    /// <summary>Buffered equivalent used when <c>go build</c>'s exit code is not yet known (success path). Faithful port of Rust's <c>filter_go_build</c> (<c>go_cmd.rs</c>:572-574).</summary>
    /// <param name="output">The raw <c>go build</c> output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGoBuild(string output) => FilterGoBuildWithExit(output, 0);

    /// <summary>
    /// Filters <c>go build</c> output to show only compiler/config errors, or a failure dump when the
    /// exit code is non-zero and no recognized error lines were found. Faithful port of Rust's
    /// <c>filter_go_build_with_exit</c> (<c>go_cmd.rs</c>:576-611).
    /// </summary>
    /// <param name="output">The raw <c>go build</c> output (stdout+stderr).</param>
    /// <param name="exitCode">The <c>go build</c> process's exit code.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGoBuildWithExit(string output, int exitCode)
    {
        var errors = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            var trimmed = line.Trim();
            if (IsGoBuildErrorLine(trimmed))
            {
                errors.Add(trimmed);
            }
        }

        if (errors.Count == 0)
        {
            return exitCode == 0 ? "Go build: Success" : FormatGoBuildFailure(output, exitCode);
        }

        var result = new StringBuilder();
        result.Append($"Go build: {errors.Count} errors\n");

        const int maxGoBuildErrors = CapErrors;
        for (var i = 0; i < errors.Count && i < maxGoBuildErrors; i++)
        {
            result.Append($"{i + 1}. {Utils.Truncate(errors[i], 120)}\n");
        }

        if (errors.Count > maxGoBuildErrors)
        {
            result.Append($"\n… +{errors.Count - maxGoBuildErrors} more errors\n");
            var allErrors = string.Join('\n', errors);
            var hint = Tee.ForceTeeTailHint(allErrors, "go-build", maxGoBuildErrors + 1);
            if (hint is not null)
            {
                result.Append($"  {hint}\n");
            }
        }

        return result.ToString().Trim();
    }

    private static string FormatGoBuildFailure(string output, int exitCode)
    {
        var lines = SourceFilterLineSplitter.SplitLines(output)
            .Select(l => l.Trim())
            .Where(l => l.Length != 0)
            .ToList();

        if (lines.Count == 0)
        {
            return $"Go build: failed (exit {exitCode})";
        }

        var result = new StringBuilder();
        result.Append($"Go build: failed (exit {exitCode})\n");
        result.Append("═══════════════════════════════════════\n");

        const int maxGoBuildErrors = CapErrors;
        for (var i = 0; i < lines.Count && i < maxGoBuildErrors; i++)
        {
            result.Append($"{i + 1}. {Utils.Truncate(lines[i], 120)}\n");
        }

        if (lines.Count > maxGoBuildErrors)
        {
            result.Append($"\n… +{lines.Count - maxGoBuildErrors} more output lines\n");
        }

        return result.ToString().Trim();
    }

    private static bool IsGoBuildErrorLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var lower = trimmed.ToLowerInvariant();

        // Go download/progress lines often contain package names like pkg/errors,
        // xerrors, or multierror. These are not compilation failures.
        if (lower.StartsWith("go: downloading ", StringComparison.Ordinal)
            || lower.StartsWith("go: finding ", StringComparison.Ordinal)
            || lower.StartsWith("go: extracting ", StringComparison.Ordinal))
        {
            return false;
        }

        // Package headers are context, not errors by themselves.
        if (trimmed.StartsWith('#'))
        {
            return false;
        }

        // Canonical compiler/config error locations: file:line:col: ...
        var isGoConfigLocation = !lower.StartsWith("go: ", StringComparison.Ordinal)
            && (lower.Contains("go.mod:", StringComparison.Ordinal)
                || lower.Contains("go.work:", StringComparison.Ordinal)
                || lower.Contains("go.sum:", StringComparison.Ordinal));
        if (trimmed.Contains(".go:", StringComparison.Ordinal) || isGoConfigLocation)
        {
            return true;
        }

        // Some compiler/module failures do not include a file.go:line:col location.
        string[] nonFileErrorPrefixes =
        [
            "undefined: ",
            "cannot use ",
            "cannot find package ",
            "no required module provides package ",
            "missing go.sum entry for module providing package ",
            "found packages ",
            "go: go.mod file not found in current directory or any parent directory",
            "go: cannot load module ",
            "go: build failed",
            "go: error ",
            "error: ",
            "pattern ",
            "go: updates to go.mod needed",
            "go: inconsistent vendoring",
            "no go files in ",
        ];

        return nonFileErrorPrefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal))
            || lower.Contains("import cycle not allowed", StringComparison.Ordinal)
            || lower.Contains("build constraints exclude all go files", StringComparison.Ordinal)
            || lower.Contains("function main is undeclared in the main package", StringComparison.Ordinal);
    }

    /// <summary>
    /// Filters <c>go vet</c> output down to recognized <c>file.go:line:col</c> issue lines. Faithful
    /// port of Rust's <c>filter_go_vet</c> (<c>go_cmd.rs</c>:700-734).
    /// </summary>
    /// <param name="output">The raw <c>go vet</c> output (stdout+stderr).</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterGoVet(string output)
    {
        var issues = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            var trimmed = line.Trim();

            if (trimmed.Length != 0 && !trimmed.StartsWith('#') && trimmed.Contains(".go:", StringComparison.Ordinal))
            {
                issues.Add(trimmed);
            }
        }

        if (issues.Count == 0)
        {
            return "Go vet: No issues found";
        }

        var result = new StringBuilder();
        result.Append($"Go vet: {issues.Count} issues\n");

        const int maxGoVetIssues = CapErrors;
        for (var i = 0; i < issues.Count && i < maxGoVetIssues; i++)
        {
            result.Append($"{i + 1}. {Utils.Truncate(issues[i], 120)}\n");
        }

        if (issues.Count > maxGoVetIssues)
        {
            result.Append($"\n… +{issues.Count - maxGoVetIssues} more issues\n");
            var allIssues = string.Join('\n', issues);
            var hint = Tee.ForceTeeTailHint(allIssues, "go-vet", maxGoVetIssues + 1);
            if (hint is not null)
            {
                result.Append($"  {hint}\n");
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Compacts a package path down to its final segment (removes long module-path prefixes like
    /// <c>github.com/user/repo/</c>). Faithful port of Rust's <c>compact_package_name</c>
    /// (<c>go_cmd.rs</c>:737-744).
    /// </summary>
    /// <param name="package">The full package import path.</param>
    /// <returns>The compacted package name.</returns>
    public static string CompactPackageName(string package)
    {
        var pos = package.LastIndexOf('/');
        return pos >= 0 ? package[(pos + 1)..] : package;
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
            return $"golangci-lint (JSON parse failed: {e.Message})\n{Utils.Truncate(output, DefaultPassthroughMaxChars)}";
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

        var golangciResult = new StringBuilder();
        golangciResult.Append($"golangci-lint: {totalIssues} issues in {totalFiles} files\n");

        var linterCounts = byLinter.ToList();
        linterCounts.Sort((a, b) => b.Value.CompareTo(a.Value));

        if (linterCounts.Count > 0)
        {
            golangciResult.Append("Top linters:\n");
            foreach (var (linter, count) in linterCounts.Take(10))
            {
                golangciResult.Append($"  {linter} ({count}x)\n");
            }

            golangciResult.Append('\n');
        }

        const int maxGolangciFiles = 10; // Rust CAP_WARNINGS from src/core/truncate.rs.
        golangciResult.Append("Top files:\n");
        foreach (var (file, count) in fileCounts.Take(maxGolangciFiles))
        {
            var shortPath = CompactPath(file);
            golangciResult.Append($"  {shortPath} ({count} issues)\n");

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
                golangciResult.Append($"    {linter} ({linterIssues.Count})\n");

                // v2 only: show first source line for this linter-file group.
                if (version >= 2 && linterIssues.Count > 0)
                {
                    var firstIssue = linterIssues[0];
                    if (firstIssue.SourceLines.Count > 0)
                    {
                        var trimmed = firstIssue.SourceLines[0].Trim();
                        var display = TakeChars(trimmed, 80);
                        golangciResult.Append($"      → {display}\n");
                    }
                }
            }
        }

        if (fileCounts.Count > maxGolangciFiles)
        {
            golangciResult.Append($"\n... +{fileCounts.Count - maxGolangciFiles} more files\n");
        }

        return golangciResult.ToString().Trim();
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
