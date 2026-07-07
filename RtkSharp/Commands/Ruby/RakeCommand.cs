using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Ruby;

/// <summary>
/// Implements the <c>rtk rake</c> CLI verb: parses standard Minitest output produced by both
/// <c>rake test</c> and <c>rails test</c>, filtering down to failures/errors and the summary line.
/// Faithful port of <c>src/cmds/ruby/rake_cmd.rs</c> and its <c>Commands::Rake</c> dispatch arm in
/// <c>src/main.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat command, no subcommand enum.</b> Unlike <see cref="RtkSharp.Commands.Rust.CargoCommand"/>
/// or <see cref="RtkSharp.Commands.Dotnet.DotnetCommand"/>, Rust's <c>rake_cmd.rs</c> has no
/// nested-subcommand dispatch — it is declared with a single <c>#[arg(trailing_var_arg = true,
/// allow_hyphen_values = true)] args: Vec&lt;String&gt;</c> in <c>main.rs</c>'s <c>Commands::Rake</c>
/// and unconditionally routes through <c>runner::run_filtered</c> (buffered, not streamed). This port
/// therefore has no passthrough branch and no <see cref="CommandArgumentParseException"/> path: clap
/// accepts any token stream for this arm, so there is no clap-equivalent rejection to reproduce.
/// </para>
/// <para>
/// <b><c>rake test</c> vs <c>rails test</c> selection.</b> <c>rake test</c> only supports a single
/// file via <c>TEST=path</c> and ignores positional file args; when any positional test file paths
/// are detected, Rust switches to <c>rails test</c>, which handles single files, multiple files, and
/// line-number syntax (<c>file.rb:15</c>) natively. See <see cref="SelectRunner"/>.
/// </para>
/// </remarks>
public static class RakeCommand
{
    private const int MaxRakeFailures = CapWarnings;

    // Rust CAP_WARNINGS from src/core/truncate.rs.
    private const int CapWarnings = 10;

    private static readonly Regex FailureHeaderRegex = new(@"^\d+\)\s+(Failure|Error):$", RegexOptions.Compiled);

    /// <summary>
    /// Registry entry point. Selects <c>rake</c> or <c>rails</c> per <see cref="SelectRunner"/>, runs
    /// it via <see cref="RubySupport.RubyExec"/> (auto-detecting <c>bundle exec</c>), and filters the
    /// buffered output through <see cref="FilterMinitestOutput"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>rake</c> verb.</param>
    /// <returns>The wrapped process's exit code.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var (tool, effectiveArgs) = SelectRunner(args);
        var ruby = RubySupport.RubyExec(tool);

        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running: {ruby.FileName} {string.Join(' ', effectiveArgs)}\n");
        }

        var invocation = new List<string>(ruby.BaseArguments);
        invocation.AddRange(effectiveArgs);

        return CommandRunner.RunFilteredAsync(
            ruby.FileName,
            invocation,
            "rake",
            string.Join(' ', args),
            FilterMinitestOutput,
            new RunOptions(TeeLabel: "rake"));
    }

    /// <summary>
    /// Decides whether to invoke <c>rake test</c> or <c>rails test</c> based on <paramref
    /// name="args"/>. Faithful port of Rust <c>select_runner</c> (<c>rake_cmd.rs</c>:20-41). Both
    /// branches return the argument vector unchanged — only the tool name differs.
    /// </summary>
    /// <param name="args">The arguments following the <c>rake</c> verb.</param>
    /// <returns>The tool name (<c>"rake"</c> or <c>"rails"</c>) and the (unchanged) effective arguments.</returns>
    internal static (string Tool, IReadOnlyList<string> Args) SelectRunner(IReadOnlyList<string> args)
    {
        var hasTestSubcommand = args.Count > 0 && args[0] == "test";
        if (!hasTestSubcommand)
        {
            return ("rake", args);
        }

        var afterTest = args.Skip(1);

        var needsRails = afterTest
            .Where(a => !a.Contains('=') && !a.StartsWith('-'))
            .Any(LooksLikeTestPath);

        return (needsRails ? "rails" : "rake", args);
    }

    /// <summary>
    /// Heuristically detects whether <paramref name="arg"/> looks like a positional test-file path
    /// (as opposed to a flag or <c>KEY=value</c> pair). Faithful port of Rust
    /// <c>looks_like_test_path</c> (<c>rake_cmd.rs</c>:43-50).
    /// </summary>
    /// <param name="arg">The candidate argument.</param>
    /// <returns>True if <paramref name="arg"/> looks like a test-file path.</returns>
    internal static bool LooksLikeTestPath(string arg)
    {
        var path = arg.Split(':')[0];
        return path.EndsWith(".rb", StringComparison.Ordinal)
            || path.StartsWith("test/", StringComparison.Ordinal)
            || path.StartsWith("spec/", StringComparison.Ordinal)
            || path.Contains("_test.rb", StringComparison.Ordinal)
            || path.Contains("_spec.rb", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses Minitest output (from both <c>rake test</c> and <c>rails test</c>, plus
    /// minitest-reporters' alternate framing) using a state machine, filtering down to
    /// failures/errors and the summary line. Faithful port of Rust <c>filter_minitest_output</c>
    /// (<c>rake_cmd.rs</c>:104-163).
    /// </summary>
    /// <param name="output">The raw, unfiltered command output.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterMinitestOutput(string output)
    {
        var clean = Utils.StripAnsi(output);

        var state = ParseState.Header;
        var failures = new List<string>();
        var currentFailure = new List<string>();
        var summaryLine = string.Empty;

        foreach (var line in Core.SourceFilterLineSplitter.SplitLines(clean))
        {
            var trimmed = line.Trim();

            // Detect summary line anywhere (it's always the last meaningful line). Handles both
            // "N runs, N assertions, ..." and "N tests, N assertions, ..." (minitest-reporters).
            if ((trimmed.Contains(" runs,", StringComparison.Ordinal) || trimmed.Contains(" tests,", StringComparison.Ordinal))
                && trimmed.Contains(" assertions,", StringComparison.Ordinal))
            {
                summaryLine = trimmed;
                continue;
            }

            // State transitions -- handle both standard Minitest and minitest-reporters.
            if (trimmed == "# Running:" || trimmed.StartsWith("Started with run options", StringComparison.Ordinal))
            {
                state = ParseState.Running;
                continue;
            }

            if (trimmed.StartsWith("Finished in ", StringComparison.Ordinal))
            {
                state = ParseState.Failures;
                continue;
            }

            switch (state)
            {
                case ParseState.Header:
                case ParseState.Running:
                    // Skip seed line, blank lines, progress dots.
                    continue;
                case ParseState.Failures:
                    if (IsFailureHeader(trimmed))
                    {
                        if (currentFailure.Count > 0)
                        {
                            failures.Add(string.Join('\n', currentFailure));
                            currentFailure.Clear();
                        }

                        currentFailure.Add(trimmed);
                    }
                    else if (trimmed.Length == 0 && currentFailure.Count > 0)
                    {
                        failures.Add(string.Join('\n', currentFailure));
                        currentFailure.Clear();
                    }
                    else if (trimmed.Length != 0)
                    {
                        currentFailure.Add(line);
                    }

                    break;
            }
        }

        // Save last failure if any.
        if (currentFailure.Count > 0)
        {
            failures.Add(string.Join('\n', currentFailure));
        }

        return BuildMinitestSummary(summaryLine, failures);
    }

    private static bool IsFailureHeader(string line) => FailureHeaderRegex.IsMatch(line);

    private static string BuildMinitestSummary(string summary, IReadOnlyList<string> failures)
    {
        var (runs, _, failCount, errorCount, skips) = ParseMinitestSummary(summary);

        if (runs == 0 && summary.Length == 0)
        {
            return "rake test: no tests ran";
        }

        if (failCount == 0 && errorCount == 0)
        {
            var msg = $"ok rake test: {runs} runs, 0 failures";
            if (skips > 0)
            {
                msg += $", {skips} skips";
            }

            return msg;
        }

        var result = new StringBuilder();
        result.Append($"rake test: {runs} runs, {failCount} failures, {errorCount} errors");
        if (skips > 0)
        {
            result.Append($", {skips} skips");
        }

        result.Append('\n');

        if (failures.Count == 0)
        {
            return result.ToString().Trim();
        }

        result.Append('\n');

        var shown = Math.Min(failures.Count, MaxRakeFailures);
        for (var i = 0; i < shown; i++)
        {
            var lines = failures[i].Split('\n');

            // First line is like "  1) Failure:" or "  1) Error:".
            if (lines.Length > 0)
            {
                result.Append($"{i + 1}. {lines[0].Trim()}\n");
            }

            // Remaining lines contain test name, file:line, assertion message.
            foreach (var line in lines.Skip(1).Take(4))
            {
                var trimmed = line.Trim();
                if (trimmed.Length != 0)
                {
                    result.Append($"   {Utils.Truncate(trimmed, 120)}\n");
                }
            }

            if (i < shown - 1)
            {
                result.Append('\n');
            }
        }

        if (failures.Count > MaxRakeFailures)
        {
            result.Append($"\n... +{failures.Count - MaxRakeFailures} more failures\n");
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Parses a Minitest summary line like <c>"8 runs, 9 assertions, 0 failures, 0 errors, 0
    /// skips"</c> (or the minitest-reporters <c>"tests"</c> variant). Faithful port of Rust
    /// <c>parse_minitest_summary</c> (<c>rake_cmd.rs</c>:235-260).
    /// </summary>
    /// <param name="summary">The summary line.</param>
    /// <returns>The parsed (runs, assertions, failures, errors, skips) tuple.</returns>
    internal static (int Runs, int Assertions, int Failures, int Errors, int Skips) ParseMinitestSummary(string summary)
    {
        var runs = 0;
        var assertions = 0;
        var failures = 0;
        var errors = 0;
        var skips = 0;

        foreach (var rawPart in summary.Split(','))
        {
            var part = rawPart.Trim();
            var words = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && int.TryParse(words[0], out var n))
            {
                switch (words[1].TrimEnd(','))
                {
                    case "runs" or "run" or "tests" or "test":
                        runs = n;
                        break;
                    case "assertions" or "assertion":
                        assertions = n;
                        break;
                    case "failures" or "failure":
                        failures = n;
                        break;
                    case "errors" or "error":
                        errors = n;
                        break;
                    case "skips" or "skip":
                        skips = n;
                        break;
                }
            }
        }

        return (runs, assertions, failures, errors, skips);
    }

    private enum ParseState
    {
        Header,
        Running,
        Failures,
    }
}
