using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RtkSharp.Learn;

/// <summary>
/// The kind of CLI mistake a <see cref="CorrectionPair"/> or <see cref="CorrectionRule"/> was
/// classified as. Faithful port of Rust <c>ErrorType</c> (<c>learn/detector.rs</c>:6-16).
/// </summary>
public enum ErrorTypeKind
{
    /// <summary>An unrecognized/unknown CLI flag or option.</summary>
    UnknownFlag,

    /// <summary>The invoked command itself was not found (shell/OS level).</summary>
    CommandNotFound,

    /// <summary>Reserved for future use — never produced by <see cref="CorrectionDetector.ClassifyError"/>. Matches Rust's <c>#[allow(dead_code)] WrongSyntax</c> variant, which is likewise never constructed by <c>classify_error</c>.</summary>
    WrongSyntax,

    /// <summary>A referenced file or path did not exist.</summary>
    WrongPath,

    /// <summary>A required argument/value was missing.</summary>
    MissingArg,

    /// <summary>The operation was denied due to insufficient permissions.</summary>
    PermissionDenied,

    /// <summary>Anything not matched by the other patterns — always constructed as <c>"General Error"</c> by <see cref="CorrectionDetector.ClassifyError"/>. Faithful port of Rust's <c>Other(String)</c> variant.</summary>
    Other,
}

/// <summary>
/// The classified error type for a detected CLI mistake. Faithful port of Rust <c>ErrorType</c>
/// (<c>learn/detector.rs</c>:6-30) — modeled as a record (rather than a bare enum) so the
/// <c>Other(String)</c> variant's payload can be represented, mirroring Rust's <c>PartialEq</c>
/// derive (structural equality on both <see cref="Kind"/> and <see cref="OtherLabel"/>).
/// </summary>
/// <param name="Kind">The discriminant.</param>
/// <param name="OtherLabel">The payload for <see cref="ErrorTypeKind.Other"/>; <see langword="null"/> for every other variant.</param>
public sealed record ErrorType(ErrorTypeKind Kind, string? OtherLabel = null)
{
    /// <summary>The <see cref="ErrorTypeKind.UnknownFlag"/> singleton.</summary>
    public static readonly ErrorType UnknownFlag = new(ErrorTypeKind.UnknownFlag);

    /// <summary>The <see cref="ErrorTypeKind.CommandNotFound"/> singleton.</summary>
    public static readonly ErrorType CommandNotFound = new(ErrorTypeKind.CommandNotFound);

    /// <summary>The <see cref="ErrorTypeKind.WrongSyntax"/> singleton.</summary>
    public static readonly ErrorType WrongSyntax = new(ErrorTypeKind.WrongSyntax);

    /// <summary>The <see cref="ErrorTypeKind.WrongPath"/> singleton.</summary>
    public static readonly ErrorType WrongPath = new(ErrorTypeKind.WrongPath);

    /// <summary>The <see cref="ErrorTypeKind.MissingArg"/> singleton.</summary>
    public static readonly ErrorType MissingArg = new(ErrorTypeKind.MissingArg);

    /// <summary>The <see cref="ErrorTypeKind.PermissionDenied"/> singleton.</summary>
    public static readonly ErrorType PermissionDenied = new(ErrorTypeKind.PermissionDenied);

    /// <summary>Constructs an <see cref="ErrorTypeKind.Other"/> instance with the given label.</summary>
    /// <param name="label">The free-form error label.</param>
    public static ErrorType Other(string label) => new(ErrorTypeKind.Other, label);

    /// <summary>
    /// The human-readable label for this error type. Faithful port of Rust <c>ErrorType::as_str</c>
    /// (<c>learn/detector.rs</c>:19-30).
    /// </summary>
    public string AsStr() => Kind switch
    {
        ErrorTypeKind.UnknownFlag => "Unknown Flag",
        ErrorTypeKind.CommandNotFound => "Command Not Found",
        ErrorTypeKind.WrongSyntax => "Wrong Syntax",
        ErrorTypeKind.WrongPath => "Wrong Path",
        ErrorTypeKind.MissingArg => "Missing Argument",
        ErrorTypeKind.PermissionDenied => "Permission Denied",
        ErrorTypeKind.Other => OtherLabel ?? string.Empty,
        _ => throw new InvalidOperationException($"unreachable ErrorTypeKind: {Kind}"),
    };
}

/// <summary>
/// A raw fail-then-succeed detection: a wrong command, the command believed to have corrected it,
/// the wrong command's error output, its classified error type, and a confidence score. Faithful
/// port of Rust <c>CorrectionPair</c> (<c>learn/detector.rs</c>:32-39).
/// </summary>
public sealed record CorrectionPair(
    string WrongCommand,
    string RightCommand,
    string ErrorOutput,
    ErrorType ErrorType,
    double Confidence);

/// <summary>
/// A deduplicated correction pattern: the (best-confidence) wrong/right command pair for a group of
/// <see cref="CorrectionPair"/>s that share the same base command, error type, and diff token, plus
/// how many times the pattern was observed. Faithful port of Rust <c>CorrectionRule</c>
/// (<c>learn/detector.rs</c>:41-49).
/// </summary>
public sealed record CorrectionRule(
    string WrongPattern,
    string RightPattern,
    ErrorType ErrorType,
    int Occurrences,
    string BaseCommand,
    string ExampleError);

/// <summary>
/// A single command's execution result, as consumed by <see cref="CorrectionDetector"/>. Faithful
/// port of Rust <c>CommandExecution</c> (<c>learn/detector.rs</c>:117-121) — a Learn-module-local
/// type distinct from <see cref="RtkSharp.Discover.ExtractedCommand"/> (the mapping from one to the
/// other lives in <see cref="LearnCommand"/>, matching Rust's <c>learn::run</c>).
/// </summary>
public sealed record CommandExecution(string Command, bool IsError, string Output);

/// <summary>
/// Pattern-matches CLI errors to detect and classify fail-then-succeed correction pairs, then
/// deduplicates them into rules. Faithful port of Rust <c>src/learn/detector.rs</c> (629 lines
/// including its inline test module).
/// </summary>
public static class CorrectionDetector
{
    // ------------------------------------------------------------------
    // Classification regexes (detector.rs:51-76 lazy_static! block).
    // ------------------------------------------------------------------

    private static readonly Regex UnknownFlagRe = new(
        @"(unexpected argument|unknown (option|flag)|unrecognized (option|flag)|invalid (option|flag))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CmdNotFoundRe = new(
        @"(command not found|not recognized as an internal|no such file or directory.*command)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex WrongPathRe = new(
        @"(no such file or directory|cannot find the path|file not found)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MissingArgRe = new(
        @"(requires a value|requires an argument|missing (required )?argument|expected.*argument)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex PermissionDeniedRe = new(
        @"(permission denied|access denied|not permitted)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // User rejection patterns - NOT actual errors.
    private static readonly Regex UserRejectionRe = new(
        @"(user (doesn't want|declined|rejected|cancelled)|operation (cancelled|aborted) by user)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Corrections must appear within this many subsequent commands to count as a fail-then-succeed
    /// pair. Faithful port of Rust <c>CORRECTION_WINDOW</c> (<c>learn/detector.rs</c>:123).
    /// </summary>
    private const int CorrectionWindow = 3;

    /// <summary>
    /// The minimum confidence score a candidate correction must reach to be recorded. Faithful port
    /// of Rust <c>MIN_CONFIDENCE</c> (<c>learn/detector.rs</c>:124).
    /// </summary>
    private const double MinConfidence = 0.6;

    /// <summary>
    /// Filters out user rejections — requires actual error-indicating content. Faithful port of Rust
    /// <c>is_command_error</c> (<c>learn/detector.rs</c>:79-98).
    /// </summary>
    /// <param name="isError">Whether the tool result reported <c>is_error</c>.</param>
    /// <param name="output">The command's output text.</param>
    /// <returns>True if <paramref name="output"/> represents a genuine command error.</returns>
    public static bool IsCommandError(bool isError, string output)
    {
        if (!isError)
        {
            return false;
        }

        // Reject if it's a user rejection.
        if (UserRejectionRe.IsMatch(output))
        {
            return false;
        }

        // Must contain error-indicating content.
        var outputLower = output.ToLowerInvariant();
        return outputLower.Contains("error", StringComparison.Ordinal)
            || outputLower.Contains("failed", StringComparison.Ordinal)
            || outputLower.Contains("unknown", StringComparison.Ordinal)
            || outputLower.Contains("invalid", StringComparison.Ordinal)
            || outputLower.Contains("not found", StringComparison.Ordinal)
            || outputLower.Contains("permission denied", StringComparison.Ordinal)
            || outputLower.Contains("cannot", StringComparison.Ordinal);
    }

    /// <summary>
    /// Classifies a command's error output into an <see cref="ErrorType"/>, checked in the fixed
    /// priority order: unknown flag, command not found, missing argument, permission denied, wrong
    /// path, then a generic fallback. Faithful port of Rust <c>classify_error</c>
    /// (<c>learn/detector.rs</c>:100-114).
    /// </summary>
    /// <param name="output">The command's error output text.</param>
    /// <returns>The classified error type.</returns>
    public static ErrorType ClassifyError(string output)
    {
        if (UnknownFlagRe.IsMatch(output))
        {
            return ErrorType.UnknownFlag;
        }

        if (CmdNotFoundRe.IsMatch(output))
        {
            return ErrorType.CommandNotFound;
        }

        if (MissingArgRe.IsMatch(output))
        {
            return ErrorType.MissingArg;
        }

        if (PermissionDeniedRe.IsMatch(output))
        {
            return ErrorType.PermissionDenied;
        }

        if (WrongPathRe.IsMatch(output))
        {
            return ErrorType.WrongPath;
        }

        return ErrorType.Other("General Error");
    }

    /// <summary>
    /// Extracts a command's base command (its first 1-2 whitespace-separated tokens), first stripping
    /// a small set of hardcoded environment-variable-assignment prefixes. Faithful port of Rust
    /// <c>extract_base_command</c> (<c>learn/detector.rs</c>:127-144).
    /// </summary>
    /// <param name="cmd">The raw command string.</param>
    /// <returns>The extracted base command (empty string if <paramref name="cmd"/> is blank).</returns>
    public static string ExtractBaseCommand(string cmd)
    {
        var trimmed = cmd.Trim();

        // Strip common env prefixes.
        var stripped = trimmed;
        if (trimmed.StartsWith("RUST_BACKTRACE=1 ", StringComparison.Ordinal))
        {
            stripped = trimmed["RUST_BACKTRACE=1 ".Length..];
        }
        else if (trimmed.StartsWith("NODE_ENV=production ", StringComparison.Ordinal))
        {
            stripped = trimmed["NODE_ENV=production ".Length..];
        }
        else if (trimmed.StartsWith("DEBUG=* ", StringComparison.Ordinal))
        {
            stripped = trimmed["DEBUG=* ".Length..];
        }

        // Get first 1-2 tokens.
        var parts = stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => string.Empty,
            1 => parts[0],
            _ => $"{parts[0]} {parts[1]}",
        };
    }

    /// <summary>
    /// Calculates the similarity between two commands using Jaccard similarity over their argument
    /// tokens: differing base commands score 0.0; identical commands score 1.0; otherwise the same
    /// base command contributes a flat 0.5, plus up to 0.5 more from argument-set overlap. Faithful
    /// port of Rust <c>command_similarity</c> (<c>learn/detector.rs</c>:146-182).
    /// </summary>
    /// <param name="a">The first command.</param>
    /// <param name="b">The second command.</param>
    /// <returns>The similarity score in [0.0, 1.0].</returns>
    public static double CommandSimilarity(string a, string b)
    {
        var baseA = ExtractBaseCommand(a);
        var baseB = ExtractBaseCommand(b);

        if (baseA != baseB)
        {
            return 0.0;
        }

        // Extract args (everything after base command).
        var argsA = SplitArgsAfterBase(a, baseA);
        var argsB = SplitArgsAfterBase(b, baseB);

        if (argsA.Count == 0 && argsB.Count == 0)
        {
            return 1.0; // Identical commands.
        }

        var intersection = argsA.Intersect(argsB).Count();
        var union = argsA.Union(argsB).Count();

        if (union == 0)
        {
            return 0.5; // Same base, no args.
        }

        // 0.5 for same base + up to 0.5 for arg similarity.
        return 0.5 + (intersection / (double)union * 0.5);
    }

    private static HashSet<string> SplitArgsAfterBase(string cmd, string baseCmd)
    {
        var rest = cmd.StartsWith(baseCmd, StringComparison.Ordinal) ? cmd[baseCmd.Length..] : string.Empty;
        return rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Checks whether an error looks like a compilation/test error (a TDD red-green cycle), not a
    /// genuine CLI mistake. Faithful port of Rust <c>is_tdd_cycle_error</c>
    /// (<c>learn/detector.rs</c>:185-199).
    /// </summary>
    private static bool IsTddCycleError(ErrorType errorType, string output)
    {
        // Compilation errors.
        if (output.Contains("error[E", StringComparison.Ordinal) || output.Contains("aborting due to", StringComparison.Ordinal))
        {
            return true;
        }

        // Test failures.
        if (output.Contains("test result: FAILED", StringComparison.Ordinal) || output.Contains("tests failed", StringComparison.Ordinal))
        {
            return true;
        }

        // Only syntax errors are CLI corrections.
        return (errorType.Kind is ErrorTypeKind.CommandNotFound or ErrorTypeKind.Other)
            && (output.Contains("error[E", StringComparison.Ordinal) || output.Contains("FAILED", StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks whether two commands differ only by path (mere exploration, not a correction). Faithful
    /// port of Rust <c>differs_only_by_path</c> (<c>learn/detector.rs</c>:202-214).
    /// </summary>
    private static bool DiffersOnlyByPath(string a, string b)
    {
        var baseA = ExtractBaseCommand(a);
        var baseB = ExtractBaseCommand(b);

        if (baseA != baseB)
        {
            return false;
        }

        // Simple heuristic: if similarity is very high (>0.9) but not identical, likely just path
        // differences.
        var sim = CommandSimilarity(a, b);
        return sim is > 0.9 and < 1.0;
    }

    /// <summary>
    /// Scans a chronological command list for fail-then-succeed pairs: an actual error, followed
    /// within <see cref="CorrectionWindow"/> commands by a similar-enough candidate that isn't mere
    /// path exploration or an identical repeat, boosted in confidence if the candidate itself
    /// succeeded. Faithful port of Rust <c>find_corrections</c> (<c>learn/detector.rs</c>:216-281).
    /// </summary>
    /// <param name="commands">The chronologically-ordered command executions to scan.</param>
    /// <returns>The detected correction pairs, in the order their wrong command occurred.</returns>
    public static List<CorrectionPair> FindCorrections(IReadOnlyList<CommandExecution> commands)
    {
        var corrections = new List<CorrectionPair>();

        for (var i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];

            // Must be an actual error.
            if (!IsCommandError(cmd.IsError, cmd.Output))
            {
                continue;
            }

            var errorType = ClassifyError(cmd.Output);

            // Skip TDD cycle errors.
            if (IsTddCycleError(errorType, cmd.Output))
            {
                continue;
            }

            // Look ahead for correction within CorrectionWindow.
            var upperBound = Math.Min(i + 1 + CorrectionWindow, commands.Count);
            for (var j = i + 1; j < upperBound; j++)
            {
                var candidate = commands[j];
                var similarity = CommandSimilarity(cmd.Command, candidate.Command);

                // Must meet minimum similarity.
                if (similarity < 0.5)
                {
                    continue;
                }

                // Skip if only path differs (exploration).
                if (DiffersOnlyByPath(cmd.Command, candidate.Command))
                {
                    continue;
                }

                // Skip if identical commands (same error repeated).
                if (cmd.Command == candidate.Command)
                {
                    continue;
                }

                // Calculate confidence.
                var confidence = similarity;

                // Boost confidence if correction succeeded.
                if (!IsCommandError(candidate.IsError, candidate.Output))
                {
                    confidence = Math.Min(confidence + 0.2, 1.0);
                }

                // Must meet minimum confidence.
                if (confidence < MinConfidence)
                {
                    continue;
                }

                // Found a correction!
                corrections.Add(new CorrectionPair(
                    cmd.Command,
                    candidate.Command,
                    new string(cmd.Output.Take(500).ToArray()),
                    errorType,
                    confidence));

                // Take first match only.
                break;
            }
        }

        return corrections;
    }

    /// <summary>
    /// Extracts the specific token that changed between the wrong and right commands, as a short
    /// human-readable summary. Faithful port of Rust <c>extract_diff_token</c>
    /// (<c>learn/detector.rs</c>:284-304).
    /// </summary>
    private static string ExtractDiffToken(string wrong, string right)
    {
        var wrongParts = wrong.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightParts = right.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        // Find tokens in wrong but not in right (removed).
        var removed = wrongParts.Except(rightParts).ToList();

        // Find tokens in right but not in wrong (added).
        var added = rightParts.Except(wrongParts).ToList();

        // Return the most distinctive change.
        if (removed.Count > 0 && added.Count > 0)
        {
            return $"{removed[0]} → {added[0]}";
        }

        if (removed.Count > 0)
        {
            return $"removed {removed[0]}";
        }

        if (added.Count > 0)
        {
            return $"added {added[0]}";
        }

        return "unknown";
    }

    /// <summary>
    /// Groups correction pairs by (base command, error type, diff token), keeping the
    /// highest-confidence example per group and recording each group's occurrence count. Faithful
    /// port of Rust <c>deduplicate_corrections</c> (<c>learn/detector.rs</c>:306-351).
    /// </summary>
    /// <param name="pairs">The raw correction pairs to deduplicate.</param>
    /// <returns>The deduplicated rules, sorted by occurrence count descending.</returns>
    public static List<CorrectionRule> DeduplicateCorrections(List<CorrectionPair> pairs)
    {
        var groupOrder = new List<(string Base, string ErrorTypeStr, string DiffToken)>();
        var groups = new Dictionary<(string Base, string ErrorTypeStr, string DiffToken), List<CorrectionPair>>();

        // Group by (base_command, error_type, diff_token).
        foreach (var pair in pairs)
        {
            var baseCmd = ExtractBaseCommand(pair.WrongCommand);
            var errorTypeStr = pair.ErrorType.AsStr();
            var diffToken = ExtractDiffToken(pair.WrongCommand, pair.RightCommand);

            var key = (baseCmd, errorTypeStr, diffToken);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                groupOrder.Add(key);
            }

            list.Add(pair);
        }

        // For each group, keep the best confidence example.
        var rules = new List<CorrectionRule>();
        foreach (var key in groupOrder)
        {
            // Sort by confidence descending — OrderByDescending is a stable sort, matching Rust's
            // Vec::sort_by (List<T>.Sort itself is NOT stable, so it is deliberately avoided here).
            var group = groups[key].OrderByDescending(p => p.Confidence).ToList();

            var best = group[0];
            var occurrences = group.Count;

            rules.Add(new CorrectionRule(
                best.WrongCommand,
                best.RightCommand,
                best.ErrorType,
                occurrences,
                key.Base,
                best.ErrorOutput));
        }

        // Sort by occurrences descending (most common mistakes first); stable, matching Rust's
        // Vec::sort_by_key.
        return rules.OrderByDescending(r => r.Occurrences).ToList();
    }
}
