using System.Text.RegularExpressions;
using RtkSharp.Rewrite;

namespace RtkSharp.Discover;

/// <summary>
/// The result of classifying a single (already chain-split) command against
/// <see cref="DiscoverRules.All"/>. Faithful port of Rust <c>discover::registry::Classification</c>
/// (<c>registry.rs</c>:9-22), modeled as a closed record hierarchy so pattern matching and value
/// equality (used throughout the ported test suite, mirroring Rust's <c>assert_eq!</c> on a
/// <c>#[derive(PartialEq)]</c> enum) both work the same way.
/// </summary>
public abstract record Classification
{
    private Classification()
    {
    }

    /// <summary>A command rtk already handles, with an established rtk-equivalent, category, and estimated savings.</summary>
    /// <param name="RtkEquivalent">The rtk command that handles this, e.g. <c>"rtk git"</c>.</param>
    /// <param name="Category">The matched rule's category, e.g. <c>"Git"</c>.</param>
    /// <param name="EstimatedSavingsPct">The (possibly subcommand-overridden) estimated savings percentage.</param>
    /// <param name="Status">Whether rtk's handler is a dedicated filter, bare passthrough, or unsupported for this specific subcommand.</param>
    public sealed record Supported(string RtkEquivalent, string Category, double EstimatedSavingsPct, RtkStatus Status) : Classification;

    /// <summary>A command with no matching rule and not in the ignored lists — a candidate for a new rtk filter.</summary>
    /// <param name="BaseCommand">The extracted base command (first word, or first two words for subcommand-shaped invocations).</param>
    public sealed record Unsupported(string BaseCommand) : Classification;

    /// <summary>A command that should never be reported (shell builtins, control-flow keywords, already-rtk invocations, etc).</summary>
    public sealed record Ignored : Classification
    {
        /// <summary>The single <see cref="Ignored"/> instance (this variant carries no data).</summary>
        public static readonly Ignored Instance = new();
    }
}

/// <summary>
/// Matches shell commands against <see cref="DiscoverRules.All"/> to decide whether <c>rtk discover</c>
/// should report them as a missed-savings opportunity, an unhandled command, or silently ignore them.
/// Faithful port of Rust <c>discover::registry</c> (<c>registry.rs</c>, lines 1-461 — the
/// classification half of the file; the rewrite-engine half, lines 462-877, is out of this port's
/// scope, having already been ported as <see cref="RtkSharp.Rewrite.RewriteEngine"/>).
/// </summary>
public static class DiscoverRegistry
{
    private const string DoubleQuoted = "\"(?:[^\"\\\\]|\\\\.)*\"";
    private const string SingleQuoted = "'(?:[^'\\\\]|\\\\.)*'";
    private const string Unquoted = @"[^\s]*";

    /// <summary>Matches a leading <c>sudo</c>/<c>env</c>/<c>VAR=val</c> environment prefix. Port of Rust <c>ENV_PREFIX</c> (<c>registry.rs</c>:54-61).</summary>
    private static readonly Regex EnvPrefix = new(
        $@"^(?:sudo\s+|env\s+|[A-Z_][A-Z0-9_]*=(?:{DoubleQuoted}|{SingleQuoted}|{Unquoted})\s+)+",
        RegexOptions.Compiled);

    /// <summary>Matches git global options before the subcommand. Port of Rust <c>GIT_GLOBAL_OPT</c> (<c>registry.rs</c>:64-65).</summary>
    private static readonly Regex GitGlobalOpt = new(
        @"^(?:(?:-C\s+\S+|-c\s+\S+|--git-dir(?:=\S+|\s+\S+)|--work-tree(?:=\S+|\s+\S+)|--no-pager|--no-optional-locks|--bare|--literal-pathspecs)\s+)+",
        RegexOptions.Compiled);

    /// <summary>golangci-lint global flags that consume a following separate value. Port of Rust <c>GOLANGCI_GLOBAL_OPT_WITH_VALUE</c> (<c>registry.rs</c>:78-85).</summary>
    private static readonly string[] GolangciGlobalOptWithValue =
        ["-c", "--color", "--config", "--cpu-profile-path", "--mem-profile-path", "--trace-path"];

    /// <summary>
    /// Average token counts per category for estimation when a command's tool-result output length
    /// isn't available. Faithful port of Rust <c>category_avg_tokens</c> (<c>registry.rs</c>:25-45).
    /// </summary>
    /// <param name="category">The matched rule's category.</param>
    /// <param name="subcmd">The command's subcommand (second whitespace-delimited word), used to distinguish e.g. <c>git log</c> from <c>git add</c>.</param>
    /// <returns>An estimated average output token count for this category/subcommand.</returns>
    public static int CategoryAvgTokens(string category, string subcmd) => category switch
    {
        "Git" => subcmd is "log" or "diff" or "show" ? 200 : 40,
        "Cargo" => subcmd is "test" ? 500 : 150,
        "Tests" => 800,
        "Files" => 100,
        "Build" => 300,
        "Infra" => 120,
        "Network" => 150,
        "GitHub" => 200,
        "GitLab" => 200,
        "PackageManager" => 150,
        _ => 150,
    };

    /// <summary>
    /// Classifies a single (already chain-split) command against <see cref="DiscoverRules.All"/>.
    /// Faithful port of Rust <c>classify_command</c> (<c>registry.rs</c>:93-198).
    /// </summary>
    /// <param name="cmd">The command to classify (a single segment, not a compound <c>&amp;&amp;</c>/<c>;</c>/<c>|</c> chain).</param>
    /// <returns>The classification result.</returns>
    public static Classification ClassifyCommand(string cmd)
    {
        var trimmed = cmd.Trim();
        if (trimmed.Length == 0)
        {
            return Classification.Ignored.Instance;
        }

        foreach (var exact in DiscoverRules.IgnoredExact)
        {
            if (trimmed == exact)
            {
                return Classification.Ignored.Instance;
            }
        }

        foreach (var prefix in DiscoverRules.IgnoredPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Classification.Ignored.Instance;
            }
        }

        var stripped = EnvPrefix.Replace(trimmed, "");
        var cmdClean = stripped.Trim();
        if (cmdClean.Length == 0)
        {
            return Classification.Ignored.Instance;
        }

        // Normalize absolute binary paths, strip git/golangci-lint global options before matching.
        cmdClean = StripAbsolutePath(cmdClean);
        cmdClean = StripGitGlobalOpts(cmdClean);
        cmdClean = StripGolangciGlobalOpts(cmdClean);

        // Exclude cat/head/tail with redirect operators — these are writes, not reads (#315).
        if (cmdClean.StartsWith("cat ", StringComparison.Ordinal)
            || cmdClean.StartsWith("head ", StringComparison.Ordinal)
            || cmdClean.StartsWith("tail ", StringComparison.Ordinal))
        {
            var hasRedirect = cmdClean
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Any(t => t.StartsWith('>') || t == "<" || t.StartsWith(">>"));
            if (hasRedirect)
            {
                var baseCmd = cmdClean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "cat";
                return new Classification.Unsupported(baseCmd);
            }
        }

        // RegexSet-equivalent: take the LAST (most specific) matching rule.
        var matchIdx = -1;
        for (var i = 0; i < DiscoverRules.All.Count; i++)
        {
            if (DiscoverRules.All[i].CompiledPattern.IsMatch(cmdClean))
            {
                matchIdx = i;
            }
        }

        if (matchIdx >= 0)
        {
            var rule = DiscoverRules.All[matchIdx];
            var match = rule.CompiledPattern.Match(cmdClean);
            double savings;
            RtkStatus status;

            if (match.Success && match.Groups.Count > 1 && match.Groups[1].Success)
            {
                var subcmd = match.Groups[1].Value;
                status = rule.SubcmdStatus.FirstOrDefault(s => s.Subcmd == subcmd) is { Subcmd: not null } statusOverride
                    ? statusOverride.Status
                    : RtkStatus.Existing;
                savings = rule.SubcmdSavings.FirstOrDefault(s => s.Subcmd == subcmd) is { Subcmd: not null } savingsOverride
                    ? savingsOverride.Pct
                    : rule.SavingsPct;
            }
            else
            {
                savings = rule.SavingsPct;
                status = RtkStatus.Existing;
            }

            return new Classification.Supported(rule.RtkCmd, rule.Category, savings, status);
        }

        var extractedBase = ExtractBaseCommand(cmdClean);
        return extractedBase.Length == 0
            ? Classification.Ignored.Instance
            : new Classification.Unsupported(extractedBase);
    }

    /// <summary>
    /// Extracts the base command: the first word, or the first two words when the second token
    /// looks like a subcommand (no leading <c>-</c>, no <c>/</c>, no <c>.</c>). Faithful port of Rust
    /// <c>extract_base_command</c> (<c>registry.rs</c>:200-227).
    /// </summary>
    /// <param name="cmd">The (already env/path/opt-normalized) command.</param>
    /// <returns>The extracted base command, or an empty string for empty input.</returns>
    internal static string ExtractBaseCommand(string cmd)
    {
        // Mirrors Rust's `cmd.splitn(3, char::is_whitespace)`: splits on each whitespace CHAR (not
        // whitespace runs), so — like the Rust original — this does not collapse consecutive
        // whitespace. No StringSplitOptions.RemoveEmptyEntries here for that reason.
        var parts = cmd.Split((char[]?)null, 3, StringSplitOptions.None);
        if (parts.Length == 0)
        {
            return "";
        }

        if (parts.Length == 1)
        {
            return parts[0];
        }

        var second = parts[1];
        if (!second.StartsWith('-') && !second.Contains('/') && !second.Contains('.'))
        {
            var firstWs = IndexOfWhitespace(cmd, 0);
            if (firstWs < 0)
            {
                return cmd;
            }

            var restStart = firstWs;
            while (restStart < cmd.Length && char.IsWhiteSpace(cmd[restStart]))
            {
                restStart++;
            }

            var secondWs = IndexOfWhitespace(cmd, restStart);
            var end = secondWs < 0 ? cmd.Length : secondWs;
            return cmd[..end];
        }

        return parts[0];
    }

    private static int IndexOfWhitespace(string s, int start)
    {
        for (var i = start; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Quote-aware heredoc detection, reusing the already-ported generic tokenizer. Delegates to
    /// <see cref="RewriteEngine.HasHeredoc"/> (the exact same primitive Rust's <c>has_heredoc</c>
    /// (<c>registry.rs</c>:229-234) uses under the hood — a single-pass lexer scan for a
    /// <c>Redirect</c> token starting with <c>&lt;&lt;</c>), rather than re-implementing tokenization
    /// a third time in this codebase.
    /// </summary>
    /// <param name="cmd">The command to check.</param>
    /// <returns><see langword="true"/> if <paramref name="cmd"/> contains a real (non-quoted) heredoc redirect.</returns>
    public static bool HasHeredoc(string cmd) => RewriteEngine.HasHeredoc(cmd);

    /// <summary>
    /// Splits a shell command into its logical parts on <c>&amp;&amp;</c>/<c>||</c>/<c>;</c>, stopping
    /// at the first <c>|</c> (pipe) — never splitting across a heredoc or an arithmetic-expansion
    /// opener <c>$((</c>. Faithful port of Rust <c>split_command_chain</c> (<c>registry.rs</c>:236-248).
    /// </summary>
    /// <param name="cmd">The raw shell command to split.</param>
    /// <returns>The command's logical parts, in order (empty for all-whitespace input).</returns>
    public static List<string> SplitCommandChain(string cmd)
    {
        var trimmed = cmd.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        if (HasHeredoc(trimmed) || trimmed.Contains("$(("))
        {
            return [trimmed];
        }

        return ShellLexer.SplitOnOperators(trimmed, stopAtPipe: true);
    }

    /// <summary>
    /// Strips git global options before the subcommand: <c>git -C /tmp status</c> → <c>git status</c>.
    /// Returns the input unchanged if it doesn't start with <c>"git "</c>. Faithful port of Rust
    /// <c>strip_git_global_opts</c> (<c>registry.rs</c>:250-261, issue #163).
    /// </summary>
    /// <param name="cmd">The command to strip.</param>
    /// <returns>The command with any leading git global options removed.</returns>
    internal static string StripGitGlobalOpts(string cmd)
    {
        if (!cmd.StartsWith("git ", StringComparison.Ordinal))
        {
            return cmd;
        }

        var afterGit = cmd[4..];
        var stripped = GitGlobalOpt.Replace(afterGit, "");
        return $"git {stripped.Trim()}";
    }

    /// <summary>
    /// Strips golangci-lint global options before the <c>run</c> subcommand:
    /// <c>golangci-lint --color never run ./...</c> → <c>golangci-lint run ./...</c>. Returns the
    /// input unchanged if this is not a supported compact <c>run</c> invocation. Faithful port of
    /// Rust <c>strip_golangci_global_opts</c> (<c>registry.rs</c>:263-271).
    /// </summary>
    /// <param name="cmd">The command to strip.</param>
    /// <returns>The command with any leading golangci-lint global options removed.</returns>
    internal static string StripGolangciGlobalOpts(string cmd) =>
        ParseGolangciRunParts(cmd) is { } parts ? $"golangci-lint {parts.RunSegment}" : cmd;

    /// <summary>Parsed golangci-lint <c>run</c> invocation, split into optional global flags and the run segment.</summary>
    private readonly record struct GolangciRunParts(string GlobalSegment, string RunSegment);

    /// <summary>
    /// Parses supported golangci-lint invocations with optional global flags before <c>run</c>.
    /// Faithful port of Rust <c>parse_golangci_run_parts</c> (<c>registry.rs</c>:273-315).
    /// </summary>
    private static GolangciRunParts? ParseGolangciRunParts(string cmd)
    {
        var tokens = SplitTokenSpans(cmd);
        if (tokens.Count == 0)
        {
            return null;
        }

        var first = tokens[0].Value;
        if (first != "golangci-lint" && first != "golangci")
        {
            return null;
        }

        var i = 1;
        while (i < tokens.Count)
        {
            var token = tokens[i].Value;

            if (token == "--")
            {
                return null;
            }

            if (!token.StartsWith('-'))
            {
                if (token == "run")
                {
                    var globalSegment = i > 1 ? cmd[tokens[1].Start..tokens[i].Start].Trim() : "";
                    var runSegment = cmd[tokens[i].Start..].Trim();
                    return new GolangciRunParts(globalSegment, runSegment);
                }

                return null;
            }

            if (SplitGolangciFlagName(token) is { } flag && GolangciFlagTakesSeparateValue(token, flag))
            {
                i++;
            }

            i++;
        }

        return null;
    }

    /// <summary>Extracts the flag name from a golangci arg (<c>--color=never</c> → <c>--color</c>). Port of Rust <c>split_golangci_flag_name</c> (<c>registry.rs</c>:317-327).</summary>
    private static string? SplitGolangciFlagName(string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            var eq = arg.IndexOf('=');
            return eq >= 0 ? arg[..eq] : arg;
        }

        return arg.StartsWith('-') ? arg : null;
    }

    /// <summary>Whether a golangci global flag consumes a following separate token as its value. Port of Rust <c>golangci_flag_takes_separate_value</c> (<c>registry.rs</c>:329-339).</summary>
    private static bool GolangciFlagTakesSeparateValue(string arg, string flag)
    {
        if (!GolangciGlobalOptWithValue.Contains(flag))
        {
            return false;
        }

        return !(arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('='));
    }

    /// <summary>Splits a command into whitespace-delimited token spans with byte(char)-offset bookkeeping. Port of Rust <c>split_token_spans</c> (<c>registry.rs</c>:341-360).</summary>
    private static List<(string Value, int Start, int End)> SplitTokenSpans(string cmd)
    {
        var tokens = new List<(string, int, int)>();
        int? start = null;

        for (var idx = 0; idx < cmd.Length; idx++)
        {
            if (char.IsWhiteSpace(cmd[idx]))
            {
                if (start is { } tokenStart)
                {
                    tokens.Add((cmd[tokenStart..idx], tokenStart, idx));
                    start = null;
                }
            }
            else if (start is null)
            {
                start = idx;
            }
        }

        if (start is { } lastStart)
        {
            tokens.Add((cmd[lastStart..], lastStart, cmd.Length));
        }

        return tokens;
    }

    /// <summary>
    /// Normalizes absolute binary paths: <c>/usr/bin/grep -rn foo</c> → <c>grep -rn foo</c>. Only
    /// strips if the first word contains a <c>/</c> (Unix path). Faithful port of Rust
    /// <c>strip_absolute_path</c> (<c>registry.rs</c>:362-383, issue #485).
    /// </summary>
    /// <param name="cmd">The command to strip.</param>
    /// <returns>The command with its leading absolute binary path normalized to a basename.</returns>
    internal static string StripAbsolutePath(string cmd)
    {
        var firstSpace = cmd.IndexOf(' ');
        var firstWord = firstSpace >= 0 ? cmd[..firstSpace] : cmd;
        if (!firstWord.Contains('/'))
        {
            return cmd;
        }

        var slash = firstWord.LastIndexOf('/');
        var basename = slash >= 0 ? firstWord[(slash + 1)..] : firstWord;
        if (basename.Length == 0)
        {
            return cmd;
        }

        return firstSpace >= 0 ? $"{basename}{cmd[firstSpace..]}" : basename;
    }

    /// <summary>
    /// Whether an env-prefix portion (as returned by <see cref="StripDisabledPrefix"/>) contains an
    /// <c>RTK_DISABLED=</c> assignment. Faithful port of Rust <c>prefix_contains_rtk_disabled</c>
    /// (<c>registry.rs</c>:385-387).
    /// </summary>
    /// <param name="prefixPart">The env-prefix portion of a command.</param>
    /// <returns><see langword="true"/> if it contains <c>"RTK_DISABLED="</c>.</returns>
    public static bool PrefixContainsRtkDisabled(string prefixPart) =>
        prefixPart.Contains("RTK_DISABLED=", StringComparison.Ordinal);

    /// <summary>
    /// Whether a command has an <c>RTK_DISABLED=</c> assignment in its env prefix. Faithful port of
    /// Rust <c>cmd_has_rtk_disabled_prefix</c> (<c>registry.rs</c>:389-393).
    /// </summary>
    /// <param name="cmd">The command to check.</param>
    /// <returns><see langword="true"/> if <paramref name="cmd"/>'s env prefix contains <c>RTK_DISABLED=</c>.</returns>
    public static bool CmdHasRtkDisabledPrefix(string cmd)
    {
        var (prefixPart, _) = StripDisabledPrefix(cmd);
        return PrefixContainsRtkDisabled(prefixPart);
    }

    /// <summary>
    /// Strips env-variable-assignment/<c>sudo</c>/<c>env</c> prefixes, returning
    /// <c>(envPrefix, actualCommand)</c>. Faithful port of Rust <c>strip_disabled_prefix</c>
    /// (<c>registry.rs</c>:395-405).
    /// </summary>
    /// <param name="cmd">The command to split.</param>
    /// <returns>The env-prefix portion (with its trailing whitespace) and the trimmed remainder.</returns>
    public static (string EnvPrefix, string ActualCommand) StripDisabledPrefix(string cmd)
    {
        var trimmed = cmd.Trim();
        var stripped = EnvPrefix.Replace(trimmed, "");
        var prefixLen = trimmed.Length - stripped.Length;
        var prefixPart = trimmed[..prefixLen];
        var rest = trimmed[prefixLen..].Trim();
        return (prefixPart, rest);
    }
}
