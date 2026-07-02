using System;
using System.Collections.Generic;
using System.Linq;

namespace RtkSharp.Rewrite;

/// <summary>
/// Verdict from checking a command against Bash permission rules.
/// </summary>
public enum PermissionVerdict
{
    /// <summary>An explicit allow rule matched — safe to auto-allow.</summary>
    Allow,

    /// <summary>A deny rule matched — pass through to the host's native deny handling.</summary>
    Deny,

    /// <summary>
    /// An ask rule matched, or no rule matched at all. Matches Claude Code's
    /// least-privilege default: anything not explicitly allowed requires confirmation.
    /// </summary>
    Ask
}

/// <summary>
/// Ports the built-in (no-user-config) subset of Claude Code's Bash permission
/// verdict logic from rtk's <c>src/hooks/permissions.rs</c> (<c>check_command</c> /
/// <c>check_command_with_rules</c>).
/// </summary>
/// <remarks>
/// <para>
/// This port deliberately omits project- and global-level <c>.claude/settings*.json</c>
/// rule loading (deny/ask/allow lists) — that is out of scope per the phase-3 built-in-only
/// mandate. <see cref="CheckCommand"/> always evaluates with empty rule lists, i.e. the
/// state Claude Code is in when no user permission rules exist anywhere.
/// </para>
/// <para>
/// Under that state, the Rust algorithm's own precedence rules mean: deny can never match
/// (no deny rules), allow can never match (no allow rules), and every command falls through
/// to <c>PermissionVerdict::Default</c> — except commands containing an "unattestable
/// construct" (command/process substitution, or a redirect to a file target), which the
/// algorithm always resolves to <c>Ask</c> regardless of rules, since such commands can't be
/// safely decomposed and attested to.
/// </para>
/// <para>
/// The Rust source models <c>Default</c> as a fourth enum variant distinct from <c>Ask</c>,
/// but every caller (see <c>rewrite_cmd.rs</c>'s exit-code table) maps both to the same "ask"
/// outcome, so this port folds <c>Default</c> into <see cref="PermissionVerdict.Ask"/> and
/// exposes only the three caller-visible verdicts.
/// </para>
/// </remarks>
public static class Permissions
{
    /// <summary>
    /// Checks <paramref name="cmd"/> against Bash permission rules and returns a verdict.
    /// </summary>
    /// <param name="cmd">The raw shell command string to check.</param>
    /// <returns>The permission verdict for the command.</returns>
    public static PermissionVerdict CheckCommand(string cmd)
    {
        return CheckCommandWithRules(cmd, denyRules: [], askRules: [], allowRules: []);
    }

    /// <summary>
    /// Core verdict algorithm, parameterized by rule lists so behavior can be exercised and
    /// tested independently of (currently unported) config-file loading.
    /// </summary>
    /// <param name="cmd">The raw shell command string to check.</param>
    /// <param name="denyRules">Bash deny patterns, checked against every compound-command segment.</param>
    /// <param name="askRules">Bash ask patterns, checked against every compound-command segment.</param>
    /// <param name="allowRules">Bash allow patterns; every non-empty segment must match one.</param>
    /// <returns>The permission verdict for the command.</returns>
    internal static PermissionVerdict CheckCommandWithRules(
        string cmd,
        IReadOnlyList<string> denyRules,
        IReadOnlyList<string> askRules,
        IReadOnlyList<string> allowRules)
    {
        var segments = ShellLexer.SplitForPermissions(cmd);

        // Deny takes highest priority and pre-empts every other construct.
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            foreach (var pattern in denyRules)
            {
                if (CommandMatchesPattern(segment, pattern))
                {
                    return PermissionVerdict.Deny;
                }
            }
        }

        // Can't decompose substitution / file-target redirects — never auto-allow.
        if (ShellLexer.ContainsUnattestableConstruct(cmd))
        {
            return PermissionVerdict.Ask;
        }

        var anyAsk = false;

        // Every non-empty segment must independently match an allow rule for the compound
        // command to receive Allow — a single matching segment must not escalate the whole
        // chain (mirrors upstream issue #1213).
        var allSegmentsAllowed = true;
        var sawSegment = false;

        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            sawSegment = true;

            // Ask — if any segment matches an ask rule, the final verdict is Ask.
            if (!anyAsk)
            {
                foreach (var pattern in askRules)
                {
                    if (CommandMatchesPattern(segment, pattern))
                    {
                        anyAsk = true;
                        break;
                    }
                }
            }

            // Allow — every non-empty segment must match an allow rule independently.
            if (allSegmentsAllowed)
            {
                var matched = allowRules.Any(pattern => CommandMatchesPattern(segment, pattern));
                if (!matched)
                {
                    allSegmentsAllowed = false;
                }
            }
        }

        // Precedence: Deny > Ask > Allow > Default (folded into Ask here).
        if (anyAsk)
        {
            return PermissionVerdict.Ask;
        }

        if (sawSegment && allSegmentsAllowed && allowRules.Count > 0)
        {
            return PermissionVerdict.Allow;
        }

        return PermissionVerdict.Ask;
    }

    /// <summary>
    /// Checks whether <paramref name="cmd"/> matches a Claude Code Bash permission pattern.
    /// </summary>
    /// <remarks>
    /// Pattern forms:
    /// <list type="bullet">
    /// <item><description><c>*</c> — matches everything.</description></item>
    /// <item><description><c>prefix:*</c> or <c>prefix *</c> (trailing <c>*</c>, no other wildcards)
    /// — prefix match with word boundary.</description></item>
    /// <item><description><c>* suffix</c>, <c>pre * suf</c> — glob matching where <c>*</c> matches any
    /// sequence of characters.</description></item>
    /// <item><description><c>pattern</c> — exact match, or prefix match where <paramref name="cmd"/>
    /// starts with <c>"{pattern} "</c>.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="cmd">The command (or command segment) to test.</param>
    /// <param name="pattern">The permission pattern to test against.</param>
    /// <returns>True if <paramref name="cmd"/> matches <paramref name="pattern"/>.</returns>
    internal static bool CommandMatchesPattern(string cmd, string pattern)
    {
        // 1. Global wildcard.
        if (pattern == "*")
        {
            return true;
        }

        // 2. Trailing-only wildcard: fast path with word-boundary preservation.
        //    Handles: "git push*", "git push *", "sudo:*".
        if (pattern.EndsWith('*'))
        {
            var stripped = pattern[..^1];
            var prefix = stripped.TrimEnd(':').TrimEnd();

            // After stripping, if prefix is empty or just wildcards, match everything.
            if (prefix.Length == 0 || prefix == "*")
            {
                return true;
            }

            // No other wildcards in prefix -> use word-boundary fast path.
            if (!prefix.Contains('*'))
            {
                return cmd == prefix || cmd.StartsWith(prefix + " ", StringComparison.Ordinal);
            }

            // Prefix still contains '*' -> fall through to glob matching.
        }

        // 3. Complex wildcards (leading, middle, multiple): glob matching.
        if (pattern.Contains('*'))
        {
            return GlobMatches(cmd, pattern);
        }

        // 4. No wildcard: exact match or prefix with word boundary.
        return cmd == pattern || cmd.StartsWith(pattern + " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Glob-style matching where <c>*</c> matches any character sequence (including empty).
    /// Colon syntax is normalized: <c>sudo:*</c> is treated as <c>sudo *</c> for word separation.
    /// </summary>
    /// <param name="cmd">The command (or command segment) to test.</param>
    /// <param name="pattern">The glob pattern to test against.</param>
    /// <returns>True if <paramref name="cmd"/> matches <paramref name="pattern"/>.</returns>
    private static bool GlobMatches(string cmd, string pattern)
    {
        var normalized = pattern.Replace(":*", " *").Replace("*:", "* ");
        var parts = normalized.Split('*');

        // All-stars pattern (e.g. "***") matches everything.
        if (parts.All(p => p.Length == 0))
        {
            return true;
        }

        var searchFrom = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            if (i == 0)
            {
                // First segment: must be prefix (pattern doesn't start with *).
                if (!cmd.StartsWith(part, StringComparison.Ordinal))
                {
                    return false;
                }

                searchFrom = part.Length;
            }
            else if (i == parts.Length - 1)
            {
                // Last segment: must be suffix (pattern doesn't end with *).
                if (!cmd[searchFrom..].EndsWith(part, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else
            {
                // Middle segment: find next occurrence. Also accept end-of-string when the
                // segment ends with whitespace — this handles commands that terminate at the
                // middle token without trailing args, e.g. "git -C * diff:*" matching bare
                // "git -C /path diff" (upstream #1105).
                var remaining = cmd[searchFrom..];
                var pos = remaining.IndexOf(part, StringComparison.Ordinal);
                if (pos >= 0)
                {
                    searchFrom += pos + part.Length;
                }
                else
                {
                    var trimmed = part.TrimEnd();
                    if (trimmed.Length > 0 && remaining.EndsWith(trimmed, StringComparison.Ordinal))
                    {
                        searchFrom += remaining.Length;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
