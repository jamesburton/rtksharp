using System.Text;
using System.Text.RegularExpressions;

namespace RtkSharp.Rewrite;

/// <summary>
/// Rewrites raw shell commands into their token-optimized <c>rtk</c> equivalents.
/// A faithful port of <c>rewrite_command</c> and its helper call graph in the Rust
/// source <c>src/discover/registry.rs</c> (lines 482-876), preserving guard order,
/// compound/pipe handling, transparent-prefix recursion, and structured-output safety.
/// </summary>
public static class RewriteEngine
{
    /// <summary>Built-in transparent wrappers that use the strip/recurse/re-prepend contract.</summary>
    private static readonly string[] BuiltinTransparentPrefixes =
        ["uv run", "noglob", "command", "builtin", "exec", "nocorrect"];

    /// <summary>golangci-lint global flags that consume a following separate value.</summary>
    private static readonly string[] GolangciGlobalOptWithValue =
        ["-c", "--color", "--config", "--cpu-profile-path", "--mem-profile-path", "--trace-path"];

    /// <summary>Maximum transparent-prefix / env-prefix recursion depth (Rust <c>MAX_PREFIX_DEPTH</c>).</summary>
    private const int MaxPrefixDepth = 10;

    /// <summary>Command prefixes that are always passed through unchanged (Rust <c>IGNORED_PREFIXES</c>).</summary>
    private static readonly string[] IgnoredPrefixes =
    [
        "cd ", "cd\t", "echo ", "printf ", "export ", "source ", "mkdir ", "rm ", "mv ", "cp ",
        "chmod ", "chown ", "touch ", "which ", "type ", "test ", "true", "false", "sleep ",
        "wait", "kill ", "set ", "unset ", "sort ", "uniq ", "tr ", "cut ", "awk ", "sed ",
        "python3 -c", "python -c", "node -e", "ruby -e", "rtk ", "pwd", "bash ", "sh ",
        "then\n", "then ", "else\n", "else ", "do\n", "do ", "for ", "while ", "if ", "case ",
    ];

    /// <summary>Exact commands that are always passed through unchanged (Rust <c>IGNORED_EXACT</c>).</summary>
    private static readonly string[] IgnoredExact =
        ["cd", "echo", "true", "false", "wait", "pwd", "bash", "sh", "fi", "done"];

    // Regexes — compiled once, never inside a method (mirrors Rust lazy_static).

    private const string DoubleQuoted = "\"(?:[^\"\\\\]|\\\\.)*\"";
    private const string SingleQuoted = "'(?:[^'\\\\]|\\\\.)*'";
    private const string Unquoted = @"[^\s]*";

    /// <summary>Matches a leading <c>sudo</c>/<c>env</c>/<c>VAR=val</c> environment prefix (Rust <c>ENV_PREFIX</c>).</summary>
    private static readonly Regex EnvPrefix = new(
        $@"^(?:sudo\s+|env\s+|[A-Z_][A-Z0-9_]*=(?:{DoubleQuoted}|{SingleQuoted}|{Unquoted})\s+)+",
        RegexOptions.Compiled);

    /// <summary>Matches git global options before the subcommand (Rust <c>GIT_GLOBAL_OPT</c>).</summary>
    private static readonly Regex GitGlobalOpt = new(
        @"^(?:(?:-C\s+\S+|-c\s+\S+|--git-dir(?:=\S+|\s+\S+)|--work-tree(?:=\S+|\s+\S+)|--no-pager|--no-optional-locks|--bare|--literal-pathspecs)\s+)+",
        RegexOptions.Compiled);

    private static readonly Regex HeadN = new(@"^head\s+-(\d+)\s+(\S+)$", RegexOptions.Compiled);
    private static readonly Regex HeadLines = new(@"^head\s+--lines=(\d+)\s+(\S+)$", RegexOptions.Compiled);
    private static readonly Regex TailN = new(@"^tail\s+-(\d+)\s+(\S+)$", RegexOptions.Compiled);
    private static readonly Regex TailNSpace = new(@"^tail\s+-n\s+(\d+)\s+(\S+)$", RegexOptions.Compiled);
    private static readonly Regex TailLinesEq = new(@"^tail\s+--lines=(\d+)\s+(\S+)$", RegexOptions.Compiled);
    private static readonly Regex TailLinesSpace = new(@"^tail\s+--lines\s+(\d+)\s+(\S+)$", RegexOptions.Compiled);

    /// <summary>Matches a bash line continuation (backslash-newline) plus surrounding horizontal whitespace.</summary>
    private static readonly Regex LineContinuation = new(
        "[ \t\x0B\x0C]*\\\\\r?\n[ \t\x0B\x0C]*",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Rewrites a raw shell command into its <c>rtk</c>-wrapped equivalent, handling compound
    /// commands (<c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, background <c>&amp;</c>) by rewriting
    /// each segment independently. Faithful port of Rust <c>rewrite_command</c> (registry.rs:482).
    /// </summary>
    /// <param name="cmd">The raw shell command to rewrite.</param>
    /// <param name="excluded">User-configured <c>exclude_commands</c> patterns that suppress rewrite.</param>
    /// <param name="transparentPrefixes">User-configured wrapper prefixes stripped before routing.</param>
    /// <returns>The rewritten command, or <c>null</c> if no rewrite applies (Rust <c>Option::None</c>).</returns>
    public static string? RewriteCommand(string cmd, IReadOnlyList<string> excluded, IReadOnlyList<string> transparentPrefixes)
    {
        // Bash line continuations are equivalent to a single space; normalize before trimming.
        string normalized = CollapseLineContinuations(cmd);
        string trimmed = normalized.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (HasHeredoc(trimmed) || trimmed.Contains("$(("))
        {
            return null;
        }

        List<ExcludePattern> compiled = CompileExcludePatterns(excluded);
        List<string> normalizedPrefixes = NormalizeTransparentPrefixes(transparentPrefixes);

        // Simple (non-compound) already-RTK command — return as-is. Compound commands that
        // start with "rtk" fall through so the remaining segments get rewritten.
        bool hasCompound = trimmed.Contains("&&")
            || trimmed.Contains("||")
            || trimmed.Contains(';')
            || trimmed.Contains('|')
            || trimmed.Contains(" & ");
        if (!hasCompound && (trimmed.StartsWith("rtk ") || trimmed == "rtk"))
        {
            return trimmed;
        }

        return RewriteCompound(trimmed, compiled, normalizedPrefixes);
    }

    /// <summary>
    /// Rewrites a compound command by rewriting each operator/pipe/background-delimited segment.
    /// Faithful port of Rust <c>rewrite_compound</c> (registry.rs:520).
    /// </summary>
    private static string? RewriteCompound(string cmd, List<ExcludePattern> excluded, List<string> transparentPrefixes)
    {
        List<ParsedToken> tokens = ShellLexer.Tokenize(cmd);
        var result = new StringBuilder(cmd.Length + 32);
        bool anyChanged = false;
        int segStart = 0;

        foreach (ParsedToken tok in tokens)
        {
            if (tok.Offset < segStart)
            {
                continue;
            }

            switch (tok.Kind)
            {
                case TokenKind.Operator:
                {
                    string seg = cmd[segStart..tok.Offset].Trim();
                    string rewritten = RewriteSegment(seg, excluded, transparentPrefixes) ?? seg;
                    if (rewritten != seg)
                    {
                        anyChanged = true;
                    }
                    result.Append(rewritten);
                    if (tok.Value == ";")
                    {
                        result.Append(';');
                        int after = tok.Offset + tok.Value.Length;
                        if (after < cmd.Length)
                        {
                            result.Append(' ');
                        }
                    }
                    else
                    {
                        result.Append(' ');
                        result.Append(tok.Value);
                        result.Append(' ');
                    }
                    segStart = tok.Offset + tok.Value.Length;
                    while (segStart < cmd.Length && cmd[segStart] == ' ')
                    {
                        segStart++;
                    }
                    break;
                }

                case TokenKind.Pipe:
                {
                    string seg = cmd[segStart..tok.Offset].Trim();
                    bool isPipeIncompatible = seg.StartsWith("find ")
                        || seg == "find"
                        || seg.StartsWith("fd ")
                        || seg == "fd";
                    string rewritten = isPipeIncompatible
                        ? seg
                        : RewriteSegment(seg, excluded, transparentPrefixes) ?? seg;
                    if (rewritten != seg)
                    {
                        anyChanged = true;
                    }
                    result.Append(rewritten);

                    ParsedToken? pipeGroupEnd = tokens.FirstOrDefault(t =>
                        t.Offset > tok.Offset
                        && (t.Kind == TokenKind.Operator
                            || (t.Kind == TokenKind.Shellism && t.Value == "&")));

                    if (pipeGroupEnd is not null)
                    {
                        result.Append(' ');
                        result.Append(cmd[tok.Offset..pipeGroupEnd.Offset].Trim());
                        segStart = pipeGroupEnd.Offset;
                    }
                    else
                    {
                        result.Append(' ');
                        result.Append(cmd[tok.Offset..].TrimStart());
                        return anyChanged ? result.ToString() : null;
                    }
                    break;
                }

                case TokenKind.Shellism when tok.Value == "&":
                {
                    string seg = cmd[segStart..tok.Offset].Trim();
                    string rewritten = RewriteSegment(seg, excluded, transparentPrefixes) ?? seg;
                    if (rewritten != seg)
                    {
                        anyChanged = true;
                    }
                    result.Append(rewritten);
                    result.Append(" & ");
                    segStart = tok.Offset + tok.Value.Length;
                    while (segStart < cmd.Length && cmd[segStart] == ' ')
                    {
                        segStart++;
                    }
                    break;
                }
            }
        }

        string finalSeg = cmd[segStart..].Trim();
        string finalRewritten = RewriteSegment(finalSeg, excluded, transparentPrefixes) ?? finalSeg;
        if (finalRewritten != finalSeg)
        {
            anyChanged = true;
        }
        result.Append(finalRewritten);

        return anyChanged ? result.ToString() : null;
    }

    /// <summary>
    /// Rewrites a single (non-compound) command segment. Faithful port of Rust
    /// <c>rewrite_segment</c> / <c>rewrite_segment_inner</c> (registry.rs:717/732).
    /// </summary>
    private static string? RewriteSegment(string seg, List<ExcludePattern> excluded, List<string> transparentPrefixes)
    {
        return RewriteSegmentInner(seg, excluded, transparentPrefixes, 0);
    }

    private static string? RewriteSegmentInner(string seg, List<ExcludePattern> excluded, List<string> transparentPrefixes, int depth)
    {
        string trimmed = seg.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (depth >= MaxPrefixDepth)
        {
            return null;
        }

        (string envPrefix, string restAfterEnv) = StripDisabledPrefix(trimmed);
        if (envPrefix.Length != 0)
        {
            // RTK_DISABLED=1 in the env prefix disables rewrite entirely; warn on stderr.
            if (envPrefix.Contains("RTK_DISABLED="))
            {
                Console.Error.WriteLine(
                    "[rtk] RTK_DISABLED=1 detected — skipping filter for this command. " +
                    "Remove RTK_DISABLED=1 to restore token savings.");
                return null;
            }
            string? rewrittenEnv = RewriteSegmentInner(restAfterEnv, excluded, transparentPrefixes, depth + 1);
            return rewrittenEnv is null ? null : $"{envPrefix}{rewrittenEnv}";
        }

        foreach (string prefix in BuiltinTransparentPrefixes)
        {
            if (StripWordPrefix(trimmed, prefix) is { } rest)
            {
                if (rest.Length == 0)
                {
                    return null;
                }
                string? rewrittenInner = RewriteSegmentInner(rest, excluded, transparentPrefixes, depth + 1);
                return rewrittenInner is null ? null : $"{prefix} {rewrittenInner}";
            }
        }

        // User-configured wrapper prefixes — same strip/recurse/re-prepend contract.
        foreach (string prefix in transparentPrefixes)
        {
            if (StripWordPrefix(trimmed, prefix) is { } rest)
            {
                if (rest.Length == 0)
                {
                    return null;
                }
                string? rewrittenInner = RewriteSegmentInner(rest, excluded, transparentPrefixes, depth + 1);
                return rewrittenInner is null ? null : $"{prefix} {rewrittenInner}";
            }
        }

        // Strip trailing stderr/stdout redirects before matching; re-append after.
        (string cmdPart, string redirectSuffix) = StripTrailingRedirects(trimmed);

        // Already RTK — pass through unchanged.
        if (cmdPart.StartsWith("rtk ") || cmdPart == "rtk")
        {
            return trimmed;
        }

        if (cmdPart.StartsWith("head -") || cmdPart.StartsWith("tail "))
        {
            string? lineRange = RewriteLineRange(cmdPart);
            return lineRange is null ? null : $"{lineRange}{redirectSuffix}";
        }

        // Only `cat -n` maps to `rtk read -n`; any other cat flag has no rtk equivalent.
        if (cmdPart.StartsWith("cat "))
        {
            string args = cmdPart["cat ".Length..].TrimStart();
            if (args.StartsWith('-') && !args.StartsWith("-n ") && !args.StartsWith("-n\t"))
            {
                return null;
            }
        }

        // Classify for correct ignore/prefix handling; null => Ignored/Unsupported => no rewrite.
        string? rtkEquivalent = ClassifyRtkEquivalent(cmdPart);
        if (rtkEquivalent is null)
        {
            return null;
        }

        string strippedForExclude = EnvPrefix.Replace(cmdPart, "");
        string cmdClean = strippedForExclude.Trim();
        if (IsExcluded(cmdClean, excluded))
        {
            return null;
        }

        // rtk_cmd values are unique across all rules, so this recovers the classified rule.
        RewriteRule? rule = RewriteRules.All.FirstOrDefault(r => r.RtkCmd == rtkEquivalent);
        if (rule is null)
        {
            return null;
        }

        if (ParseGolangciRunParts(cmdPart) is { } parts)
        {
            return parts.GlobalSegment.Length == 0
                ? $"rtk golangci-lint {parts.RunSegment}"
                : $"rtk golangci-lint {parts.GlobalSegment} {parts.RunSegment}";
        }

        // #196: gh with --json/--jq/--template produces structured output rtk would corrupt.
        if (rule.RtkCmd == "rtk gh")
        {
            string argsLower = cmdPart.ToLowerInvariant();
            if (argsLower.Contains("--json") || argsLower.Contains("--jq") || argsLower.Contains("--template"))
            {
                return null;
            }
        }

        // Try each rewrite prefix in order with a word-boundary check.
        foreach (string prefix in rule.RewritePrefixes)
        {
            if (StripWordPrefix(cmdPart, prefix) is { } rest)
            {
                return rest.Length == 0
                    ? $"{rule.RtkCmd}{redirectSuffix}"
                    : $"{rule.RtkCmd} {rest}{redirectSuffix}";
            }
        }

        return null;
    }

    /// <summary>
    /// Classifies a command and returns its <c>rtk</c> equivalent, or <c>null</c> when the command
    /// is ignored or unsupported. Faithful reduction of Rust <c>classify_command</c> (registry.rs:94):
    /// only the <c>rtk_equivalent</c> is used by the rewrite path, so the unused
    /// category/savings/status fields and the Ignored-vs-Unsupported distinction (both suppress
    /// rewrite) are elided.
    /// </summary>
    private static string? ClassifyRtkEquivalent(string cmd)
    {
        string trimmed = cmd.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        foreach (string exact in IgnoredExact)
        {
            if (trimmed == exact)
            {
                return null;
            }
        }
        foreach (string prefix in IgnoredPrefixes)
        {
            if (trimmed.StartsWith(prefix))
            {
                return null;
            }
        }

        string stripped = EnvPrefix.Replace(trimmed, "");
        string cmdClean = stripped.Trim();
        if (cmdClean.Length == 0)
        {
            return null;
        }

        cmdClean = StripAbsolutePath(cmdClean);
        cmdClean = StripGitGlobalOpts(cmdClean);
        cmdClean = StripGolangciGlobalOpts(cmdClean);

        // Exclude cat/head/tail with redirect operators — these are writes, not reads (#315).
        if (cmdClean.StartsWith("cat ") || cmdClean.StartsWith("head ") || cmdClean.StartsWith("tail "))
        {
            bool hasRedirect = cmdClean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Any(t => t.StartsWith('>') || t == "<" || t.StartsWith(">>"));
            if (hasRedirect)
            {
                return null;
            }
        }

        // RegexSet-equivalent: take the LAST (most specific) matching rule.
        string? match = null;
        foreach (RewriteRule rule in RewriteRules.All)
        {
            if (rule.CompiledPattern.IsMatch(cmdClean))
            {
                match = rule.RtkCmd;
            }
        }
        return match;
    }

    /// <summary>Rewrites <c>head</c>/<c>tail</c> line-range reads. Port of Rust <c>rewrite_line_range</c> (registry.rs:628).</summary>
    private static string? RewriteLineRange(string cmd)
    {
        foreach (Regex re in new[] { HeadN, HeadLines })
        {
            Match caps = re.Match(cmd);
            if (caps.Success)
            {
                string n = caps.Groups[1].Value;
                string file = caps.Groups[2].Value;
                return $"rtk read {file} --max-lines {n}";
            }
        }

        if (cmd.StartsWith("head -"))
        {
            return null;
        }

        foreach (Regex re in new[] { TailN, TailNSpace, TailLinesEq, TailLinesSpace })
        {
            Match caps = re.Match(cmd);
            if (caps.Success)
            {
                string n = caps.Groups[1].Value;
                string file = caps.Groups[2].Value;
                return $"rtk read {file} --tail-lines {n}";
            }
        }

        return null;
    }

    /// <summary>Quote-aware heredoc detection. Port of Rust <c>has_heredoc</c> (registry.rs:230).</summary>
    private static bool HasHeredoc(string cmd)
    {
        return ShellLexer.Tokenize(cmd)
            .Any(t => t.Kind == TokenKind.Redirect && t.Value.StartsWith("<<"));
    }

    /// <summary>Replaces every bash line continuation with a single space. Port of <c>collapse_line_continuations</c>.</summary>
    private static string CollapseLineContinuations(string s)
    {
        return LineContinuation.Replace(s, " ");
    }

    /// <summary>
    /// Strips a leading env/sudo prefix, returning <c>(envPrefix, rest)</c>. The env prefix
    /// retains its trailing whitespace. Port of Rust <c>strip_disabled_prefix</c> (registry.rs:396).
    /// </summary>
    private static (string EnvPrefix, string Remainder) StripDisabledPrefix(string cmd)
    {
        string trimmed = cmd.Trim();
        string stripped = EnvPrefix.Replace(trimmed, "");
        int prefixLen = trimmed.Length - stripped.Length;
        string prefixPart = trimmed[..prefixLen];
        string rest = trimmed[prefixLen..].Trim();
        return (prefixPart, rest);
    }

    /// <summary>
    /// Splits off trailing redirect tokens, returning <c>(cmdPart, redirectSuffix)</c> where the
    /// suffix includes the leading whitespace. Port of Rust <c>strip_trailing_redirects</c> (registry.rs:407).
    /// </summary>
    private static (string CmdPart, string RedirectSuffix) StripTrailingRedirects(string cmd)
    {
        List<ParsedToken> tokens = ShellLexer.Tokenize(cmd);
        if (tokens.Count == 0)
        {
            return (cmd, "");
        }

        int redirBoundary = tokens.Count;
        int i = tokens.Count;
        while (i > 0)
        {
            i--;
            switch (tokens[i].Kind)
            {
                case TokenKind.Redirect:
                    redirBoundary = i;
                    break;
                case TokenKind.Arg:
                    if (i > 0 && tokens[i - 1].Kind == TokenKind.Redirect)
                    {
                        redirBoundary = i - 1;
                        i--;
                    }
                    else
                    {
                        goto done;
                    }
                    break;
                default:
                    goto done;
            }
        }
    done:

        if (redirBoundary >= tokens.Count)
        {
            return (cmd, "");
        }

        int cut = tokens[redirBoundary].Offset;
        string cmdPart = cmd[..cut].TrimEnd();
        string redirPart = cmd[cmdPart.Length..];
        return (cmdPart, redirPart);
    }

    /// <summary>
    /// Strips a command prefix with a word-boundary check. Returns the remainder after the prefix,
    /// or <c>null</c> if the prefix does not match. Port of Rust <c>strip_word_prefix</c> (registry.rs:865).
    /// </summary>
    private static string? StripWordPrefix(string cmd, string prefix)
    {
        if (cmd == prefix)
        {
            return "";
        }
        if (cmd.Length > prefix.Length && cmd.StartsWith(prefix) && cmd[prefix.Length] == ' ')
        {
            return cmd[(prefix.Length + 1)..].TrimStart();
        }
        return null;
    }

    /// <summary>Normalizes absolute binary paths (<c>/usr/bin/grep</c> → <c>grep</c>). Port of <c>strip_absolute_path</c> (registry.rs:364).</summary>
    private static string StripAbsolutePath(string cmd)
    {
        int firstSpace = cmd.IndexOf(' ');
        string firstWord = firstSpace >= 0 ? cmd[..firstSpace] : cmd;
        if (firstWord.Contains('/'))
        {
            int slash = firstWord.LastIndexOf('/');
            string basename = slash >= 0 ? firstWord[(slash + 1)..] : firstWord;
            if (basename.Length == 0)
            {
                return cmd;
            }
            return firstSpace >= 0 ? $"{basename}{cmd[firstSpace..]}" : basename;
        }
        return cmd;
    }

    /// <summary>Strips git global options before the subcommand. Port of Rust <c>strip_git_global_opts</c> (registry.rs:253).</summary>
    private static string StripGitGlobalOpts(string cmd)
    {
        if (!cmd.StartsWith("git "))
        {
            return cmd;
        }
        string afterGit = cmd[4..];
        string stripped = GitGlobalOpt.Replace(afterGit, "");
        return $"git {stripped.Trim()}";
    }

    /// <summary>Strips golangci-lint global options before <c>run</c>. Port of Rust <c>strip_golangci_global_opts</c> (registry.rs:266).</summary>
    private static string StripGolangciGlobalOpts(string cmd)
    {
        return ParseGolangciRunParts(cmd) is { } parts
            ? $"golangci-lint {parts.RunSegment}"
            : cmd;
    }

    /// <summary>Parsed golangci-lint <c>run</c> invocation split into optional global flags and the run segment.</summary>
    private readonly record struct GolangciRunParts(string GlobalSegment, string RunSegment);

    /// <summary>Parses supported golangci-lint <c>run</c> invocations. Port of Rust <c>parse_golangci_run_parts</c> (registry.rs:274).</summary>
    private static GolangciRunParts? ParseGolangciRunParts(string cmd)
    {
        List<(string Value, int Start, int End)> tokens = SplitTokenSpans(cmd);
        if (tokens.Count == 0)
        {
            return null;
        }
        string first = tokens[0].Value;
        if (first != "golangci-lint" && first != "golangci")
        {
            return null;
        }

        int i = 1;
        while (i < tokens.Count)
        {
            string token = tokens[i].Value;

            if (token == "--")
            {
                return null;
            }

            if (!token.StartsWith('-'))
            {
                if (token == "run")
                {
                    string globalSegment = i > 1
                        ? cmd[tokens[1].Start..tokens[i].Start].Trim()
                        : "";
                    string runSegment = cmd[tokens[i].Start..].Trim();
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

    /// <summary>Extracts the flag name from a golangci arg. Port of Rust <c>split_golangci_flag_name</c> (registry.rs:317).</summary>
    private static string? SplitGolangciFlagName(string arg)
    {
        if (arg.StartsWith("--"))
        {
            int eq = arg.IndexOf('=');
            return eq >= 0 ? arg[..eq] : arg;
        }
        if (arg.StartsWith('-'))
        {
            return arg;
        }
        return null;
    }

    /// <summary>Whether a golangci flag consumes a following separate value. Port of Rust <c>golangci_flag_takes_separate_value</c> (registry.rs:329).</summary>
    private static bool GolangciFlagTakesSeparateValue(string arg, string flag)
    {
        if (!GolangciGlobalOptWithValue.Contains(flag))
        {
            return false;
        }
        if (arg.StartsWith("--") && arg.Contains('='))
        {
            return false;
        }
        return true;
    }

    /// <summary>Splits a command into whitespace-delimited token spans. Port of Rust <c>split_token_spans</c> (registry.rs:341).</summary>
    private static List<(string Value, int Start, int End)> SplitTokenSpans(string cmd)
    {
        var tokens = new List<(string, int, int)>();
        int? start = null;

        for (int idx = 0; idx < cmd.Length; idx++)
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

    /// <summary>An exclude pattern: either a compiled regex or a literal prefix fallback.</summary>
    private sealed record ExcludePattern(Regex? Regex, string? Prefix);

    /// <summary>Compiles user <c>exclude_commands</c> patterns. Port of Rust <c>compile_exclude_patterns</c> (registry.rs:672).</summary>
    private static List<ExcludePattern> CompileExcludePatterns(IReadOnlyList<string> patterns)
    {
        var result = new List<ExcludePattern>();
        foreach (string pattern in patterns)
        {
            string trimmed = pattern.Trim();
            if (trimmed.Length == 0 || trimmed == "^")
            {
                Console.Error.WriteLine($"rtk: warning: ignoring trivial exclude_commands pattern '{pattern}'");
                continue;
            }
            string anchored = trimmed.StartsWith('^')
                ? trimmed
                : $@"^{Regex.Escape(trimmed)}($|\s)";
            try
            {
                result.Add(new ExcludePattern(new Regex(anchored), null));
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine($"rtk: warning: invalid exclude_commands pattern '{pattern}': {e.Message}");
                result.Add(new ExcludePattern(null, trimmed));
            }
        }
        return result;
    }

    /// <summary>Normalizes and sorts transparent prefixes longest-first. Port of Rust <c>normalize_transparent_prefixes</c> (registry.rs:703).</summary>
    private static List<string> NormalizeTransparentPrefixes(IReadOnlyList<string> prefixes)
    {
        var normalized = prefixes
            .Select(p => p.Trim())
            .Where(p => p.Length != 0)
            .ToList();

        // Match longer wrappers first; tie-break lexicographically.
        normalized.Sort((a, b) =>
        {
            int byLen = b.Length.CompareTo(a.Length);
            return byLen != 0 ? byLen : string.CompareOrdinal(a, b);
        });

        // Dedup consecutive equal entries (mirrors Rust Vec::dedup after sort).
        var deduped = new List<string>();
        foreach (string p in normalized)
        {
            if (deduped.Count == 0 || deduped[^1] != p)
            {
                deduped.Add(p);
            }
        }
        return deduped;
    }

    /// <summary>Whether a command matches any exclude pattern. Port of Rust <c>is_excluded</c> (registry.rs:725).</summary>
    private static bool IsExcluded(string cmd, List<ExcludePattern> excluded)
    {
        return excluded.Any(pat => pat.Regex is not null
            ? pat.Regex.IsMatch(cmd)
            : cmd.StartsWith(pat.Prefix!));
    }
}
