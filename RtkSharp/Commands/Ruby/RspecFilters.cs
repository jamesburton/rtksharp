using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RtkSharp.Core;

namespace RtkSharp.Commands.Ruby;

/// <summary>
/// Buffered filters for <c>rtk rspec</c>: JSON parsing (the default, structured path) and a
/// noise-stripping state-machine text fallback (custom <c>--format</c>, or JSON parse failure).
/// Faithful port of <c>filter_rspec_output</c>/<c>filter_rspec_text</c>/<c>strip_noise</c> and their
/// helpers (<c>src/cmds/ruby/rspec_cmd.rs</c>).
/// </summary>
internal static partial class RspecFilters
{
    // rspec failures carry full backtraces -- show fewer than a generic warning list.
    // Rust: reduced(CAP_WARNINGS, 5) where CAP_WARNINGS = 10.
    private const int MaxRspecFailures = 5;

    [GeneratedRegex("running via spring preloader", RegexOptions.IgnoreCase)]
    private static partial Regex SpringRegex();

    [GeneratedRegex(@"(coverage report|simplecov|coverage/|\.simplecov|All Files.*Lines)", RegexOptions.IgnoreCase)]
    private static partial Regex SimplecovRegex();

    [GeneratedRegex("^DEPRECATION WARNING:")]
    private static partial Regex DeprecationRegex();

    [GeneratedRegex(@"^Finished in \d")]
    private static partial Regex FinishedInRegex();

    [GeneratedRegex("saved screenshot to (.+)")]
    private static partial Regex ScreenshotRegex();

    [GeneratedRegex(@"(\d+) examples?, (\d+) failures?")]
    private static partial Regex RspecSummaryRegex();

    // ── Noise stripping ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes noise lines: Spring preloader, SimpleCov, DEPRECATION warnings, the "Finished in"
    /// timing line, and Capybara screenshot details (keeping only the path). Faithful port of Rust
    /// <c>strip_noise</c> (<c>rspec_cmd.rs</c>:110-156).
    /// </summary>
    /// <param name="output">The raw, unfiltered command output.</param>
    /// <returns>The output with noise lines removed.</returns>
    public static string StripNoise(string output)
    {
        var result = new List<string>();
        var inSimplecovBlock = false;

        foreach (var line in Core.SourceFilterLineSplitter.SplitLines(output))
        {
            var trimmed = line.Trim();

            if (SpringRegex().IsMatch(trimmed))
            {
                continue;
            }

            if (DeprecationRegex().IsMatch(trimmed))
            {
                continue;
            }

            if (FinishedInRegex().IsMatch(trimmed))
            {
                continue;
            }

            if (SimplecovRegex().IsMatch(trimmed))
            {
                inSimplecovBlock = true;
                continue;
            }

            if (inSimplecovBlock)
            {
                if (trimmed.Length == 0)
                {
                    inSimplecovBlock = false;
                }

                continue;
            }

            var screenshotMatch = ScreenshotRegex().Match(trimmed);
            if (screenshotMatch.Success)
            {
                result.Add($"[screenshot: {screenshotMatch.Groups[1].Value.Trim()}]");
                continue;
            }

            result.Add(line);
        }

        return string.Join('\n', result);
    }

    // ── Output filtering ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Filters RSpec's <c>--format json</c> output into a compact pass/fail summary with failure
    /// details, retrying after noise-stripping and falling back to the text parser if JSON parsing
    /// fails. Faithful port of Rust <c>filter_rspec_output</c> (<c>rspec_cmd.rs</c>:160-183).
    /// </summary>
    /// <param name="output">The raw <c>rspec --format json</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterRspecOutput(string output)
    {
        if (output.Trim().Length == 0)
        {
            return "RSpec: No output";
        }

        // Try parsing as JSON first (happy path when --format json is injected).
        var direct = TryDeserialize(output);
        if (direct is not null)
        {
            return BuildRspecSummary(direct);
        }

        // Strip noise (Spring, SimpleCov, etc.) and retry JSON parse.
        var stripped = StripNoise(output);
        var retried = TryDeserialize(stripped);
        if (retried is not null)
        {
            return BuildRspecSummary(retried);
        }

        Console.Error.Write("[rtk] rspec: JSON parse failed, using text fallback\n");
        return FilterRspecText(stripped);
    }

    private static RspecOutput? TryDeserialize(string input)
    {
        try
        {
            return JsonSerializer.Deserialize(input, RspecJsonContext.Default.RspecOutput);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the compact summary string from a parsed <see cref="RspecOutput"/>. Faithful port of
    /// Rust <c>build_rspec_summary</c> (<c>rspec_cmd.rs</c>:185-270).
    /// </summary>
    /// <param name="rspec">The parsed RSpec JSON output.</param>
    /// <returns>The filtered summary.</returns>
    private static string BuildRspecSummary(RspecOutput rspec)
    {
        var s = rspec.Summary;

        if (s.ExampleCount == 0 && s.ErrorsOutsideOfExamplesCount == 0)
        {
            return "RSpec: No examples found";
        }

        if (s.ExampleCount == 0 && s.ErrorsOutsideOfExamplesCount > 0)
        {
            return $"RSpec: {s.ErrorsOutsideOfExamplesCount} errors outside of examples ({s.Duration.ToString("F2", CultureInfo.InvariantCulture)}s)";
        }

        if (s.FailureCount == 0 && s.ErrorsOutsideOfExamplesCount == 0)
        {
            var passed = Math.Max(0, s.ExampleCount - s.PendingCount);
            var okResult = $"✓ RSpec: {passed} passed";
            if (s.PendingCount > 0)
            {
                okResult += $", {s.PendingCount} pending";
            }

            okResult += $" ({s.Duration.ToString("F2", CultureInfo.InvariantCulture)}s)";
            return okResult;
        }

        var passedCount = Math.Max(0, s.ExampleCount - (s.FailureCount + s.PendingCount));
        var result = new StringBuilder($"RSpec: {passedCount} passed, {s.FailureCount} failed");
        if (s.PendingCount > 0)
        {
            result.Append($", {s.PendingCount} pending");
        }

        result.Append($" ({s.Duration.ToString("F2", CultureInfo.InvariantCulture)}s)\n");

        var failures = rspec.Examples.Where(e => e.Status == "failed").ToList();

        if (failures.Count == 0)
        {
            return result.ToString().Trim();
        }

        result.Append("\nFailures:\n");

        var shown = Math.Min(failures.Count, MaxRspecFailures);
        for (var i = 0; i < shown; i++)
        {
            var example = failures[i];
            result.Append($"{i + 1}. ✗ {example.FullDescription}\n   {example.FilePath}:{example.LineNumber}\n");

            if (example.Exception is { } exc)
            {
                var shortClass = exc.Class.Split("::").LastOrDefault() ?? exc.Class;
                var firstMsg = exc.Message.Split('\n').FirstOrDefault() ?? string.Empty;
                result.Append($"   {shortClass}: {Utils.Truncate(firstMsg, 120)}\n");

                // First backtrace line not from gems/rspec internals.
                foreach (var bt in exc.Backtrace)
                {
                    if (!bt.Contains("/gems/", StringComparison.Ordinal) && !bt.Contains("lib/rspec", StringComparison.Ordinal))
                    {
                        result.Append($"   {Utils.Truncate(bt, 120)}\n");
                        break;
                    }
                }
            }

            if (i < shown - 1)
            {
                result.Append('\n');
            }
        }

        if (failures.Count > MaxRspecFailures)
        {
            result.Append($"\n... +{failures.Count - MaxRspecFailures} more failures\n");
        }

        return result.ToString().Trim();
    }

    // ── Text fallback ────────────────────────────────────────────────────────────────────────────

    private enum TextState
    {
        Header,
        Failures,
        FailedExamples,
        Summary,
    }

    /// <summary>
    /// State-machine text fallback parser for when JSON is unavailable. Faithful port of Rust
    /// <c>filter_rspec_text</c> (<c>rspec_cmd.rs</c>:273-380).
    /// </summary>
    /// <param name="output">The (noise-stripped) raw output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterRspecText(string output)
    {
        var state = TextState.Header;
        var failures = new List<string>();
        var currentFailure = new StringBuilder();
        var summaryLine = string.Empty;

        foreach (var line in Core.SourceFilterLineSplitter.SplitLines(output))
        {
            var trimmed = line.Trim();

            switch (state)
            {
                case TextState.Header:
                    if (trimmed == "Failures:")
                    {
                        state = TextState.Failures;
                    }
                    else if (trimmed == "Failed examples:")
                    {
                        state = TextState.FailedExamples;
                    }
                    else if (RspecSummaryRegex().IsMatch(trimmed))
                    {
                        summaryLine = trimmed;
                        state = TextState.Summary;
                    }

                    break;

                case TextState.Failures:
                    // New failure block starts with a numbered pattern like "  1) ...".
                    if (IsNumberedFailure(trimmed))
                    {
                        if (currentFailure.ToString().Trim().Length > 0)
                        {
                            failures.Add(CompactFailureBlock(currentFailure.ToString()));
                        }

                        currentFailure.Clear();
                        currentFailure.Append(trimmed).Append('\n');
                    }
                    else if (trimmed == "Failed examples:")
                    {
                        if (currentFailure.ToString().Trim().Length > 0)
                        {
                            failures.Add(CompactFailureBlock(currentFailure.ToString()));
                        }

                        currentFailure.Clear();
                        state = TextState.FailedExamples;
                    }
                    else if (RspecSummaryRegex().IsMatch(trimmed))
                    {
                        if (currentFailure.ToString().Trim().Length > 0)
                        {
                            failures.Add(CompactFailureBlock(currentFailure.ToString()));
                        }

                        currentFailure.Clear();
                        summaryLine = trimmed;
                        state = TextState.Summary;
                    }
                    else if (trimmed.Length != 0)
                    {
                        // Skip gem-internal backtrace lines.
                        if (IsGemBacktrace(trimmed))
                        {
                            continue;
                        }

                        currentFailure.Append(trimmed).Append('\n');
                    }

                    break;

                case TextState.FailedExamples:
                    if (RspecSummaryRegex().IsMatch(trimmed))
                    {
                        summaryLine = trimmed;
                        state = TextState.Summary;
                    }

                    // Skip "Failed examples:" section (just rspec commands to re-run).
                    break;

                case TextState.Summary:
                    goto doneReadingLines;
            }
        }

    doneReadingLines:

        // Capture remaining failure.
        if (currentFailure.ToString().Trim().Length > 0 && state == TextState.Failures)
        {
            failures.Add(CompactFailureBlock(currentFailure.ToString()));
        }

        // If we found a summary line, build result.
        if (summaryLine.Length != 0)
        {
            if (failures.Count == 0)
            {
                return $"RSpec: {summaryLine}";
            }

            var result = new StringBuilder($"RSpec: {summaryLine}\n");
            var shown = Math.Min(failures.Count, MaxRspecFailures);
            for (var i = 0; i < shown; i++)
            {
                result.Append($"{i + 1}. ✗ {failures[i]}\n");
                if (i < shown - 1)
                {
                    result.Append('\n');
                }
            }

            if (failures.Count > MaxRspecFailures)
            {
                result.Append($"\n... +{failures.Count - MaxRspecFailures} more failures\n");
            }

            return result.ToString().Trim();
        }

        // Fallback: look for summary anywhere.
        var allLines = Core.SourceFilterLineSplitter.SplitLines(output);
        for (var i = allLines.Count - 1; i >= 0; i--)
        {
            var t = allLines[i].Trim();
            if (t.Contains("example", StringComparison.Ordinal) && (t.Contains("failure", StringComparison.Ordinal) || t.Contains("pending", StringComparison.Ordinal)))
            {
                return $"RSpec: {t}";
            }
        }

        // Last resort: last 5 lines.
        return RubySupport.FallbackTail(output, "rspec", 5);
    }

    /// <summary>
    /// Checks whether a line is a numbered failure header like <c>"1) User#full_name..."</c>.
    /// Faithful port of Rust <c>is_numbered_failure</c> (<c>rspec_cmd.rs</c>:383-391).
    /// </summary>
    /// <param name="line">The trimmed candidate line.</param>
    /// <returns>True if the line is a numbered failure header.</returns>
    internal static bool IsNumberedFailure(string line)
    {
        var trimmed = line.Trim();
        var pos = trimmed.IndexOf(')');
        if (pos < 0)
        {
            return false;
        }

        var prefix = trimmed[..pos];
        return prefix.Length > 0 && prefix.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Checks whether a backtrace line originates from gems/rspec internals. Faithful port of Rust
    /// <c>is_gem_backtrace</c> (<c>rspec_cmd.rs</c>:394-399).
    /// </summary>
    /// <param name="line">The candidate line.</param>
    /// <returns>True if the line looks like gem/rspec-internal backtrace noise.</returns>
    internal static bool IsGemBacktrace(string line) =>
        line.Contains("/gems/", StringComparison.Ordinal)
        || line.Contains("lib/rspec", StringComparison.Ordinal)
        || line.Contains("lib/ruby/", StringComparison.Ordinal)
        || line.Contains("vendor/bundle", StringComparison.Ordinal);

    /// <summary>
    /// Compacts a raw failure block: extracts the spec file:line reference, strips verbose gem
    /// backtrace lines. Faithful port of Rust <c>compact_failure_block</c> (<c>rspec_cmd.rs</c>:401-429).
    /// </summary>
    /// <param name="block">The raw, multi-line failure block.</param>
    /// <returns>The compacted failure block.</returns>
    internal static string CompactFailureBlock(string block)
    {
        var lines = block.Split('\n').Where(l => l.Trim().Length != 0).ToList();

        var specFile = string.Empty;
        var keptLines = new List<string>();

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("# ./spec/", StringComparison.Ordinal) || t.StartsWith("# ./test/", StringComparison.Ordinal))
            {
                specFile = t[2..];
            }
            else if (t.StartsWith('#') && (t.Contains("/gems/", StringComparison.Ordinal) || t.Contains("lib/rspec", StringComparison.Ordinal)))
            {
                // Skip gem backtrace.
                continue;
            }
            else
            {
                keptLines.Add(t);
            }
        }

        var result = string.Join("\n   ", keptLines);
        if (specFile.Length != 0)
        {
            result += $"\n   {specFile}";
        }

        return result;
    }

    // ── JSON structures matching RSpec's --format json output ──────────────────────────────────

    /// <summary>Faithful port of <c>RspecOutput</c> (<c>rspec_cmd.rs</c>:34-38).</summary>
    private sealed class RspecOutput
    {
        [JsonPropertyName("examples")]
        public List<RspecExample> Examples { get; set; } = [];

        [JsonPropertyName("summary")]
        public required RspecSummary Summary { get; set; }
    }

    /// <summary>Faithful port of <c>RspecExample</c> (<c>rspec_cmd.rs</c>:40-47).</summary>
    private sealed class RspecExample
    {
        [JsonPropertyName("full_description")]
        public required string FullDescription { get; set; }

        [JsonPropertyName("status")]
        public required string Status { get; set; }

        [JsonPropertyName("file_path")]
        public required string FilePath { get; set; }

        [JsonPropertyName("line_number")]
        public uint LineNumber { get; set; }

        [JsonPropertyName("exception")]
        public RspecException? Exception { get; set; }
    }

    /// <summary>Faithful port of <c>RspecException</c> (<c>rspec_cmd.rs</c>:49-55).</summary>
    private sealed class RspecException
    {
        [JsonPropertyName("class")]
        public required string Class { get; set; }

        [JsonPropertyName("message")]
        public required string Message { get; set; }

        [JsonPropertyName("backtrace")]
        public List<string> Backtrace { get; set; } = [];
    }

    /// <summary>Faithful port of <c>RspecSummary</c> (<c>rspec_cmd.rs</c>:57-65).</summary>
    private sealed class RspecSummary
    {
        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("example_count")]
        public int ExampleCount { get; set; }

        [JsonPropertyName("failure_count")]
        public int FailureCount { get; set; }

        [JsonPropertyName("pending_count")]
        public int PendingCount { get; set; }

        [JsonPropertyName("errors_outside_of_examples_count")]
        public int ErrorsOutsideOfExamplesCount { get; set; }
    }

    /// <summary>
    /// Source-generated JSON metadata for RSpec's <c>--format json</c> schema, required because
    /// <c>RtkSharp.csproj</c> publishes with <c>PublishAot=true</c> — reflection-based
    /// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming, matching the
    /// convention established elsewhere (e.g. <c>VitestCommand.VitestJsonContext</c>).
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
    [JsonSerializable(typeof(RspecOutput))]
    private sealed partial class RspecJsonContext : JsonSerializerContext;
}
