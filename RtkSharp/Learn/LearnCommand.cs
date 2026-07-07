using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Discover;

namespace RtkSharp.Learn;

/// <summary>
/// Implements the <c>rtk learn</c> CLI verb: scans Claude Code session history for recurring CLI
/// mistakes (fail-then-succeed command pairs), classifies and scores them, deduplicates into rules,
/// and reports or persists them. Faithful port of Rust <c>src/learn/mod.rs</c>'s <c>run</c>
/// (121 lines) plus the <c>Commands::Learn</c> clap arm (<c>main.rs</c>:587-610, 2142-2161).
/// </summary>
/// <remarks>
/// <para>
/// <b>RTK_META_COMMANDS parity.</b> Rust's <c>main.rs</c> lists <c>"learn"</c> in
/// <c>RTK_META_COMMANDS</c> (<c>main.rs</c>:1170-1174) so a Clap parse failure shows Clap's own error
/// instead of falling back to raw shell execution. Following the established
/// <see cref="RtkSharp.Commands.Analytics.GainCommand"/> convention for meta-commands, <see cref="ParseArgs"/>
/// throws a private <see cref="LearnArgsException"/> (not
/// <see cref="RtkSharp.Cli.CommandArgumentParseException"/>, which re-routes to the
/// TOML-fallback/raw-passthrough path reserved for PASSTHROUGH-classified commands) — <see cref="Run"/>
/// catches it, prints a short <c>error: ...</c> message to stderr, and exits 2, matching Clap's own
/// usage-error exit code without reproducing its multi-line banner verbatim (same disclosed
/// simplification as <c>GainCommand</c>).
/// </para>
/// <para>
/// <b>Disclosed simplification: flag-parse error text.</b> As with <c>GainCommand</c>, Clap's exact
/// multi-line usage banner is not reproduced; a short single-purpose message is emitted instead,
/// preserving exit-2 fail-fast behavior without chasing Clap's exact prose.
/// </para>
/// </remarks>
public static class LearnCommand
{
    /// <summary>
    /// Registry entry point. Runs <c>rtk learn</c> with the given arguments (the remainder after the
    /// <c>learn</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>learn</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        LearnArgs parsed;
        try
        {
            parsed = LearnArgs.Parse(args);
        }
        catch (LearnArgsException ex)
        {
            Console.Error.Write(ex.Message + "\n");
            return 2;
        }

        try
        {
            return RunCore(Console.Out, parsed);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// The testable core of <see cref="Run"/>, taking already-parsed arguments and an injectable
    /// output writer. Faithful port of Rust <c>run</c> (<c>learn/mod.rs</c>:11-121).
    /// </summary>
    /// <param name="stdout">The destination for all output.</param>
    /// <param name="args">The parsed <c>rtk learn</c> arguments.</param>
    /// <returns>0 on success (the only failure path is an exception from session discovery/extraction, which propagates).</returns>
    internal static int RunCore(TextWriter stdout, LearnArgs args)
    {
        // Determine project filter (same logic as discover).
        string? projectFilter;
        if (args.All)
        {
            projectFilter = null;
        }
        else if (args.Project is { } p)
        {
            projectFilter = p;
        }
        else
        {
            // Default: current working directory.
            var cwd = Directory.GetCurrentDirectory();
            projectFilter = ClaudeProvider.EncodeProjectPath(cwd);
        }

        // Discover sessions.
        var sessions = ClaudeProvider.DiscoverSessions(projectFilter, args.Since);

        if (sessions.Count == 0)
        {
            stdout.Write($"No Claude Code sessions found in the last {args.Since} days.\n");
            return 0;
        }

        // Extract commands from all sessions.
        var allCommands = new List<CommandExecution>();

        foreach (var sessionPath in sessions)
        {
            List<ExtractedCommand> extracted;
            try
            {
                extracted = ClaudeProvider.ExtractCommands(sessionPath);
            }
            catch (Exception)
            {
                continue; // Skip malformed sessions.
            }

            foreach (var extCmd in extracted)
            {
                // Only process commands with output content.
                if (extCmd.OutputContent is { } output)
                {
                    allCommands.Add(new CommandExecution(extCmd.Command, extCmd.IsError, output));
                }
            }
        }

        // Sort by sequence index to maintain chronological order
        // (already sorted by extraction order within each session).

        // Find corrections.
        var corrections = CorrectionDetector.FindCorrections(allCommands);

        if (corrections.Count == 0)
        {
            stdout.Write($"No CLI corrections detected in {sessions.Count} sessions.\n");
            return 0;
        }

        // Filter by confidence.
        var filtered = corrections.Where(c => c.Confidence >= args.MinConfidence).ToList();

        // Deduplicate.
        var rules = CorrectionDetector.DeduplicateCorrections(filtered);

        // Filter by occurrences.
        rules = rules.Where(r => (ulong)r.Occurrences >= args.MinOccurrences).ToList();

        // Output.
        if (args.Format == "json")
        {
            var export = new LearnJsonExport
            {
                SessionsScanned = sessions.Count,
                TotalCorrections = filtered.Count,
                Rules = rules.Select(r => new LearnJsonRule
                {
                    Wrong = r.WrongPattern,
                    Right = r.RightPattern,
                    ErrorType = r.ErrorType.AsStr(),
                    Occurrences = r.Occurrences,
                    BaseCommand = r.BaseCommand,
                }).ToList(),
            };

            // Utf8JsonWriter's indented mode emits Environment.NewLine between elements (CRLF on
            // Windows); normalize to "\n" per the house "\n"-only stdout rule (serde_json never
            // emits CRLF either), matching GainCommand's established convention.
            var json = JsonSerializer.Serialize(export, LearnJsonContext.Default.LearnJsonExport)
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            stdout.Write(json + "\n");
        }
        else
        {
            // Text output.
            var report = LearnReport.FormatConsoleReport(rules, filtered.Count, sessions.Count, args.Since);
            stdout.Write(report);

            if (args.WriteRules && rules.Count > 0)
            {
                var rulesPath = Path.Combine(".claude", "rules", "cli-corrections.md");
                LearnReport.WriteRulesFile(rules, rulesPath);
                stdout.Write($"\nWritten to: {rulesPath}\n");
            }
        }

        return 0;
    }

    // -----------------------------------------------------------------------
    // Flag parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parsed <c>rtk learn</c> flags. Hand-rolled port of the Clap-derived <c>Commands::Learn</c> arg
    /// struct (<c>main.rs</c>:587-610).
    /// </summary>
    internal sealed class LearnArgs
    {
        public string? Project { get; private set; }

        public bool All { get; private set; }

        public ulong Since { get; private set; } = 30;

        public string Format { get; private set; } = "text";

        public bool WriteRules { get; private set; }

        public double MinConfidence { get; private set; } = 0.6;

        public ulong MinOccurrences { get; private set; } = 1;

        public static LearnArgs Parse(string[] args)
        {
            var result = new LearnArgs();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-p" or "--project":
                        result.Project = RequireValue(args, ref i, arg);
                        break;
                    case "-a" or "--all":
                        result.All = true;
                        break;
                    case "-s" or "--since":
                        result.Since = ParseULong(RequireValue(args, ref i, arg), arg);
                        break;
                    case "-f" or "--format":
                        result.Format = RequireValue(args, ref i, arg);
                        break;
                    case "-w" or "--write-rules":
                        result.WriteRules = true;
                        break;
                    case "--min-confidence":
                        result.MinConfidence = ParseDouble(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--min-occurrences":
                        result.MinOccurrences = ParseULong(RequireValue(args, ref i, arg), arg);
                        break;
                    default:
                        if (arg.StartsWith("--project=", StringComparison.Ordinal))
                        {
                            result.Project = arg["--project=".Length..];
                        }
                        else if (arg.StartsWith("--since=", StringComparison.Ordinal))
                        {
                            result.Since = ParseULong(arg["--since=".Length..], "--since");
                        }
                        else if (arg.StartsWith("--format=", StringComparison.Ordinal))
                        {
                            result.Format = arg["--format=".Length..];
                        }
                        else if (arg.StartsWith("--min-confidence=", StringComparison.Ordinal))
                        {
                            result.MinConfidence = ParseDouble(arg["--min-confidence=".Length..], "--min-confidence");
                        }
                        else if (arg.StartsWith("--min-occurrences=", StringComparison.Ordinal))
                        {
                            result.MinOccurrences = ParseULong(arg["--min-occurrences=".Length..], "--min-occurrences");
                        }
                        else
                        {
                            throw new LearnArgsException($"error: unexpected argument '{arg}' found");
                        }

                        break;
                }
            }

            return result;
        }

        private static string RequireValue(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
            {
                throw new LearnArgsException($"error: a value is required for '{flag}' but none was supplied");
            }

            i++;
            return args[i];
        }

        private static ulong ParseULong(string value, string flag)
        {
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            {
                throw new LearnArgsException($"error: invalid value '{value}' for '{flag}': invalid digit found in string");
            }

            return result;
        }

        private static double ParseDouble(string value, string flag)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            {
                throw new LearnArgsException($"error: invalid value '{value}' for '{flag}': invalid float literal");
            }

            return result;
        }
    }

    private sealed class LearnArgsException(string message) : Exception(message);

    // -----------------------------------------------------------------------
    // JSON export DTOs (mod.rs:92-105)
    // -----------------------------------------------------------------------

    /// <summary>The top-level <c>rtk learn --format json</c> export shape (Rust's inline <c>serde_json::json!</c> object, <c>learn/mod.rs</c>:94-104).</summary>
    internal sealed class LearnJsonExport
    {
        [JsonPropertyName("sessions_scanned")]
        public required int SessionsScanned { get; init; }

        [JsonPropertyName("total_corrections")]
        public required int TotalCorrections { get; init; }

        [JsonPropertyName("rules")]
        public required List<LearnJsonRule> Rules { get; init; }
    }

    /// <summary>A single rule entry within <see cref="LearnJsonExport"/>.</summary>
    internal sealed class LearnJsonRule
    {
        [JsonPropertyName("wrong")]
        public required string Wrong { get; init; }

        [JsonPropertyName("right")]
        public required string Right { get; init; }

        [JsonPropertyName("error_type")]
        public required string ErrorType { get; init; }

        [JsonPropertyName("occurrences")]
        public required int Occurrences { get; init; }

        [JsonPropertyName("base_command")]
        public required string BaseCommand { get; init; }
    }
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <c>rtk learn --format json</c>'s export
/// shape, avoiding the reflection-based serialization <see cref="JsonSerializer"/> falls back to
/// otherwise — required because <c>RtkSharp.csproj</c> sets <c>PublishAot=true</c> (mirrors
/// <c>GainCommand</c>'s established <c>GainJsonContext</c> convention).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LearnCommand.LearnJsonExport))]
[JsonSerializable(typeof(LearnCommand.LearnJsonRule))]
internal sealed partial class LearnJsonContext : JsonSerializerContext;
