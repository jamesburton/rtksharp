using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Jvm;

/// <summary>
/// Buffered and per-line filters for <c>rtk gradlew</c>'s build/test/connected-test/lint/dependencies
/// task families. Faithful port of the filter functions in <c>src/cmds/jvm/gradlew_cmd.rs</c>.
/// </summary>
public static partial class GradlewFilters
{
    // Rust CAP_LIST from src/core/truncate.rs (core::truncate::CAP_LIST).
    private const int CapList = 20;

    // ── Shared regex patterns (used across multiple filters) ────────────────

    [GeneratedRegex(@"^> Task :")]
    private static partial Regex TaskLineRegex();

    [GeneratedRegex(@"^\* Try:|^> Run with --|^> Get more help at")]
    private static partial Regex TrySectionRegex();

    [GeneratedRegex(@"^BUILD (SUCCESSFUL|FAILED)")]
    private static partial Regex BuildStatusRegex();

    [GeneratedRegex(@"^\d+ actionable tasks?")]
    private static partial Regex ActionableRegex();

    // ── Build filter regexes ─────────────────────────────────────────────────

    [GeneratedRegex(@"^(Starting a Gradle Daemon|Daemon will be stopped|Reusing configuration cache|Calculating task graph|> Configure project|Deprecated Gradle features|You can use|For more on this|Configuration cache entry)")]
    private static partial Regex DaemonLineRegex();

    [GeneratedRegex(@"^\s*\d+%|^Downloading|^Configuring|^Resolving|^\[Incubating\]|^Wrote HTML report|^class \S+ could not|^\[android-")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"(?i)(^FAILURE:|^\* What went wrong:|^\* Where:|> Could not|e: |error:|^Execution failed|Lint found \d+ error)")]
    private static partial Regex ErrorLineRegex();

    [GeneratedRegex(@"^(w: |warning:|Warning:|WARNING:)")]
    private static partial Regex WarnLineRegex();

    [GeneratedRegex(@"gradle\.com/s/|Publishing build scan")]
    private static partial Regex BuildScanRegex();

    // ── Test filter regexes ──────────────────────────────────────────────────

    [GeneratedRegex(@"FAILED$| FAILED ")]
    private static partial Regex FailedLineRegex();

    [GeneratedRegex(@" PASSED$| SKIPPED$")]
    private static partial Regex PassedSkippedRegex();

    [GeneratedRegex(@"\d+ tests? completed|\d+ tests? failed|There were failing tests|See the report at")]
    private static partial Regex TestSummaryLineRegex();

    // ── Connected test filter regexes ────────────────────────────────────────

    [GeneratedRegex(@"^INSTRUMENTATION_STATUS[_CODE]*:")]
    private static partial Regex InstrumentationStatusRegex();

    [GeneratedRegex(@"^INSTRUMENTATION_RESULT:")]
    private static partial Regex InstrumentationResultRegex();

    [GeneratedRegex(@"^INSTRUMENTATION_CODE:")]
    private static partial Regex InstrumentationCodeRegex();

    [GeneratedRegex(@"^Starting \d+ tests? on ")]
    private static partial Regex StartingTestsRegex();

    [GeneratedRegex(@"^Installing APK")]
    private static partial Regex InstallingApkRegex();

    // ── Lint filter regexes ──────────────────────────────────────────────────

    [GeneratedRegex(@"[^:]+:\d+:.*[Ee]rror:.*\[")]
    private static partial Regex AndroidLintErrorRegex();

    [GeneratedRegex(@"[^:]+:\d+:.*[Ww]arning:.*\[")]
    private static partial Regex AndroidLintWarningRegex();

    [GeneratedRegex(@"[^:]+:\d+:\d+:.*[Ll]int")]
    private static partial Regex KtlintViolationRegex();

    [GeneratedRegex(@"[^:]+:\d+:\d+:.*error")]
    private static partial Regex DetektViolationRegex();

    [GeneratedRegex(@"\d+ (issues?|errors?|warnings?)")]
    private static partial Regex LintSummaryLineRegex();

    [GeneratedRegex(@"Wrote (HTML|XML|text) report|file://|/build/reports/lint")]
    private static partial Regex ReportLineRegex();

    // ── Build filter predicate ───────────────────────────────────────────────

    /// <summary>
    /// Predicate deciding whether a single line of Gradle build output should be kept. Faithful port
    /// of Rust's <c>filter_build_line</c> (<c>gradlew_cmd.rs</c>:179-216).
    /// </summary>
    /// <param name="line">The raw output line.</param>
    /// <returns>True if the line should be kept.</returns>
    public static bool FilterBuildLine(string line)
    {
        // Always strip these.
        if (TaskLineRegex().IsMatch(line)
            || DaemonLineRegex().IsMatch(line)
            || ProgressRegex().IsMatch(line)
            || TrySectionRegex().IsMatch(line))
        {
            return false;
        }

        // Always keep these.
        return BuildStatusRegex().IsMatch(line)
            || ActionableRegex().IsMatch(line)
            || ErrorLineRegex().IsMatch(line)
            || WarnLineRegex().IsMatch(line)
            || BuildScanRegex().IsMatch(line)
            || line.Trim().Length == 0; // preserve blank lines that separate error sections
    }

    // ── Test output filter ───────────────────────────────────────────────────

    /// <summary>
    /// Returns true if an <c>at ...</c> stack frame belongs to a test framework (JUnit, Gradle runner,
    /// reflection) rather than user code. Faithful port of Rust's <c>is_framework_frame</c>
    /// (<c>gradlew_cmd.rs</c>:222-228).
    /// </summary>
    /// <param name="trimmed">The trimmed stack-frame line.</param>
    /// <returns>True if the frame belongs to test-framework internals.</returns>
    public static bool IsFrameworkFrame(string trimmed) =>
        trimmed.StartsWith("at org.junit.", StringComparison.Ordinal)
        || trimmed.StartsWith("at junit.", StringComparison.Ordinal)
        || trimmed.StartsWith("at java.lang.reflect.", StringComparison.Ordinal)
        || trimmed.StartsWith("at sun.reflect.", StringComparison.Ordinal)
        || trimmed.StartsWith("at org.gradle.", StringComparison.Ordinal);

    /// <summary>
    /// Filters <c>testDebugUnitTest</c>-family output: strips PASSED/SKIPPED lines, keeps FAILED lines
    /// plus their exception + first user-code stack frame, and always keeps build-status/summary lines.
    /// Faithful port of Rust's <c>filter_test</c> (<c>gradlew_cmd.rs</c>:230-302).
    /// </summary>
    /// <param name="output">The raw Gradle test output.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterTest(string output)
    {
        if (output.Length == 0)
        {
            return string.Empty;
        }

        var resultLines = new List<string>();
        var inFailureBlock = false;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            // Skip always-noise lines.
            if (TaskLineRegex().IsMatch(line) || TrySectionRegex().IsMatch(line))
            {
                continue;
            }

            // Build summary lines always kept.
            if (BuildStatusRegex().IsMatch(line) || ActionableRegex().IsMatch(line) || TestSummaryLineRegex().IsMatch(line))
            {
                resultLines.Add(line);
                continue;
            }

            // PASSED/SKIPPED per-test lines — strip.
            if (PassedSkippedRegex().IsMatch(line))
            {
                inFailureBlock = false;
                continue;
            }

            // FAILED per-test lines — keep + enter failure block for stack trace.
            if (FailedLineRegex().IsMatch(line))
            {
                inFailureBlock = true;
                resultLines.Add(line);
                continue;
            }

            // Stack trace lines following a failure.
            if (inFailureBlock)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("java.", StringComparison.Ordinal) || trimmed.StartsWith("kotlin.", StringComparison.Ordinal))
                {
                    // Exception class + message — always keep.
                    resultLines.Add(line);
                }
                else if (trimmed.StartsWith("at ", StringComparison.Ordinal))
                {
                    // Skip framework frames, keep first user-code frame.
                    if (!IsFrameworkFrame(trimmed))
                    {
                        resultLines.Add(line);
                        inFailureBlock = false;
                    }
                }
                else if (trimmed.Length != 0)
                {
                    inFailureBlock = false;
                }
            }
        }

        var filtered = string.Join('\n', resultLines);

        // Guarantee non-empty output.
        if (filtered.Trim().Length == 0)
        {
            if (output.Contains("BUILD SUCCESSFUL", StringComparison.Ordinal))
            {
                return "ok ✓ (no test output — add testLogging to build.gradle for details)";
            }

            return output.Trim();
        }

        return filtered;
    }

    // ── Connected / instrumented test filter ─────────────────────────────────

    /// <summary>
    /// Filters <c>connectedDebugAndroidTest</c>-family output: strips instrumentation-status noise,
    /// then delegates to <see cref="FilterTest"/> for the shared PASSED/FAILED line format. Faithful
    /// port of Rust's <c>filter_connected</c> (<c>gradlew_cmd.rs</c>:306-350).
    /// </summary>
    /// <param name="output">The raw connected-test output.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterConnected(string output)
    {
        if (output.Length == 0)
        {
            return string.Empty;
        }

        // Special case: no device.
        if (output.Contains("No connected devices!", StringComparison.Ordinal))
        {
            return "connectedAndroidTest failed: No connected devices! Start an emulator or connect a device.";
        }

        var resultLines = new List<string>();

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (InstrumentationStatusRegex().IsMatch(line)
                || InstrumentationResultRegex().IsMatch(line)
                || InstrumentationCodeRegex().IsMatch(line)
                || StartingTestsRegex().IsMatch(line)
                || InstallingApkRegex().IsMatch(line)
                || TaskLineRegex().IsMatch(line)
                || TrySectionRegex().IsMatch(line))
            {
                continue;
            }

            resultLines.Add(line);
        }

        // After stripping instrumentation noise, connected test output uses the same PASSED/FAILED
        // line format as unit tests — delegate to FilterTest.
        var joined = string.Join('\n', resultLines);
        var filtered = FilterTest(joined);

        if (filtered.Trim().Length == 0)
        {
            return "ok ✓ (connected tests passed)";
        }

        return filtered;
    }

    // ── Lint output filter ────────────────────────────────────────────────────

    /// <summary>
    /// Filters <c>lint</c>/<c>ktlintCheck</c>/<c>detekt</c> output: keeps violation lines plus up to
    /// 3 lines of code-snippet context (Android lint only), and summary lines. Faithful port of Rust's
    /// <c>filter_lint</c> (<c>gradlew_cmd.rs</c>:354-432).
    /// </summary>
    /// <param name="output">The raw lint output.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterLint(string output)
    {
        if (output.Length == 0)
        {
            return string.Empty;
        }

        // Android lint emits violation + code snippet + caret + explanation, separated from the next
        // violation by a blank line. We keep up to 3 non-empty context lines so the LLM sees what code
        // is wrong without having to open the file.
        const int maxContextLines = 3;

        var resultLines = new List<string>();
        var contextRemaining = 0;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (TaskLineRegex().IsMatch(line) || TrySectionRegex().IsMatch(line) || ReportLineRegex().IsMatch(line))
            {
                contextRemaining = 0;
                continue;
            }

            var isAndroidLint = AndroidLintErrorRegex().IsMatch(line) || AndroidLintWarningRegex().IsMatch(line);

            if (BuildStatusRegex().IsMatch(line)
                || ActionableRegex().IsMatch(line)
                || LintSummaryLineRegex().IsMatch(line)
                || isAndroidLint
                || KtlintViolationRegex().IsMatch(line)
                || DetektViolationRegex().IsMatch(line))
            {
                resultLines.Add(line);

                // Only Android lint violations have multi-line context; ktlint/detekt/summary lines
                // are single-line.
                contextRemaining = isAndroidLint ? maxContextLines : 0;
                continue;
            }

            if (contextRemaining > 0)
            {
                if (line.Trim().Length == 0)
                {
                    // Blank line terminates the context block.
                    contextRemaining = 0;
                }
                else
                {
                    resultLines.Add(line);
                    contextRemaining--;
                }
            }
        }

        var filtered = string.Join('\n', resultLines);

        if (filtered.Trim().Length == 0)
        {
            if (output.Contains("BUILD SUCCESSFUL", StringComparison.Ordinal))
            {
                return "ok ✓ lint passed";
            }

            return output.Trim();
        }

        return filtered;
    }

    // ── Dependencies output filter ───────────────────────────────────────────

    /// <summary>
    /// Filters <c>dependencies</c> output: extracts only the top-level (first tree depth) dependency
    /// per configuration, capping the listing at <see cref="CapList"/> entries per configuration.
    /// Faithful port of Rust's <c>filter_dependencies</c> (<c>gradlew_cmd.rs</c>:436-526).
    /// </summary>
    /// <param name="output">The raw <c>dependencies</c> output.</param>
    /// <returns>The filtered output.</returns>
    public static string FilterDependencies(string output)
    {
        if (output.Length == 0)
        {
            return string.Empty;
        }

        var configs = new List<(string Config, List<string> Deps)>();
        var currentConfig = string.Empty;
        var currentDeps = new List<string>();
        var totalDeps = 0;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            var trimmed = line.Trim();

            // Skip noise.
            if (trimmed.Length == 0
                || TaskLineRegex().IsMatch(trimmed)
                || TrySectionRegex().IsMatch(trimmed)
                || BuildStatusRegex().IsMatch(trimmed)
                || ActionableRegex().IsMatch(trimmed)
                || trimmed.StartsWith("Downloading", StringComparison.Ordinal)
                || trimmed.StartsWith("Download ", StringComparison.Ordinal)
                || trimmed.StartsWith("Starting a Gradle", StringComparison.Ordinal)
                || trimmed == "No dependencies"
                || trimmed == "(n)")
            {
                continue;
            }

            // Configuration header: "compileClasspath - Compile classpath for source set 'main'."
            // Not indented, not a tree line, contains " - ".
            if (!trimmed.StartsWith('+')
                && !trimmed.StartsWith('|')
                && !trimmed.StartsWith('\\')
                && !trimmed.StartsWith(' ')
                && trimmed.Contains(" - ", StringComparison.Ordinal))
            {
                if (currentConfig.Length != 0 && currentDeps.Count > 0)
                {
                    configs.Add((currentConfig, new List<string>(currentDeps)));
                }

                var dashIdx = trimmed.IndexOf(" - ", StringComparison.Ordinal);
                currentConfig = dashIdx >= 0 ? trimmed[..dashIdx] : trimmed;
                currentDeps = [];
                continue;
            }

            // Top-level dependencies only (first level of the tree). Check the *untrimmed* line —
            // top-level deps start at column 0, transitive deps are indented (e.g. "|    +---" or
            // "     \---").
            if ((line.StartsWith("+---", StringComparison.Ordinal) || line.StartsWith("\\---", StringComparison.Ordinal))
                && currentConfig.Length != 0)
            {
                var dep = StripDepPrefix(trimmed);
                currentDeps.Add(dep);
                totalDeps++;
            }
        }

        // Flush last config.
        if (currentConfig.Length != 0 && currentDeps.Count > 0)
        {
            configs.Add((currentConfig, currentDeps));
        }

        if (configs.Count == 0)
        {
            if (output.Contains("BUILD SUCCESSFUL", StringComparison.Ordinal))
            {
                return "ok ✓ no dependencies";
            }

            return output.Trim();
        }

        var result = new StringBuilder();
        result.Append($"{totalDeps} top-level dependencies across {configs.Count} configurations\n");

        const int maxGradleDeps = CapList;
        foreach (var (config, deps) in configs)
        {
            result.Append($"\n{config} ({deps.Count}):\n");
            foreach (var dep in deps.Take(maxGradleDeps))
            {
                result.Append($"  {dep}\n");
            }

            if (deps.Count > maxGradleDeps)
            {
                result.Append($"  … +{deps.Count - maxGradleDeps} more\n");
            }
        }

        return result.ToString().TrimEnd();
    }

    private static string StripDepPrefix(string trimmed)
    {
        if (trimmed.StartsWith("+--- ", StringComparison.Ordinal))
        {
            return trimmed["+--- ".Length..];
        }

        if (trimmed.StartsWith("\\--- ", StringComparison.Ordinal))
        {
            return trimmed["\\--- ".Length..];
        }

        return trimmed;
    }
}
