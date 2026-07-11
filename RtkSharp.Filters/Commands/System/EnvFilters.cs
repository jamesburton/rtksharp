using System.Text;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure report-building logic for the <c>rtk env</c> CLI verb: masks sensitive values,
/// categorizes environment variables into fixed buckets, and renders the report block. Ported
/// from Rust <c>src/cmds/system/env_cmd.rs</c>. The process entry point (reading the real
/// environment, argument parsing, tracking, and the actual <see cref="Console"/> writes) lives
/// in <see cref="RtkSharp.Commands.System.EnvCommand"/> (<c>RtkSharp</c>).
/// </summary>
public static class EnvFilters
{
    // Rust CAP_WARNINGS (= 10) from src/core/truncate.rs (env_cmd.rs:75), reused verbatim as the
    // PATH-entries display cap.
    private const int MaxPathEntries = 10;

    // Rust CAP_LIST (= 20) from src/core/truncate.rs (env_cmd.rs:110), reused verbatim as the
    // "Other" section's display cap.
    private const int MaxOtherVars = 20;

    // env_cmd.rs:44's `value.len() > 100` threshold (UTF-8 byte length in Rust).
    private const int TruncationByteThreshold = 100;

    // env_cmd.rs:45's `value.chars().take(50)` preview length (Unicode-scalar-value count).
    private const int TruncationPreviewChars = 50;

    /// <summary>
    /// The rendered <c>rtk env</c> report, plus the two counters (<see cref="Total"/>/
    /// <see cref="Shown"/>) the caller needs for its tracking baseline.
    /// </summary>
    /// <param name="Report">The rendered report text (categorized sections, then the optional trailing total line).</param>
    /// <param name="Total">The total number of variables in the input set (regardless of filter/display).</param>
    /// <param name="Shown">The number of variables actually rendered across all sections.</param>
    public readonly record struct EnvReport(string Report, int Total, int Shown);

    /// <summary>
    /// Builds the categorized, masked, truncated <c>rtk env</c> report from a sorted
    /// <paramref name="vars"/> list. Faithful port of the report-building half of <c>env_cmd::run</c>
    /// (<c>env_cmd.rs</c>:19-134), excluding the actual <see cref="Console"/> writes and the
    /// tracking call, which the caller performs itself using <see cref="EnvReport.Total"/>/
    /// <see cref="EnvReport.Shown"/>.
    /// </summary>
    /// <param name="vars">
    /// The environment variables to render, already sorted by key (ordinal) — mirrors env_cmd.rs's
    /// own <c>vars.sort_by(...)</c> step, performed by the caller so this method stays a pure
    /// function of its already-ordered input.
    /// </param>
    /// <param name="filter">
    /// The <c>-f</c>/<c>--filter</c> value (case-insensitive substring match on the key only), or
    /// null when not supplied.
    /// </param>
    /// <param name="showAll">Whether <c>--show-all</c> was given (skips masking of sensitive values).</param>
    /// <returns>The rendered report and its total/shown counters.</returns>
    public static EnvReport FormatEnvReport(
        IReadOnlyList<(string Key, string Value)> vars, string? filter, bool showAll)
    {
        ArgumentNullException.ThrowIfNull(vars);

        var sensitivePatterns = GetSensitivePatterns();

        var pathVars = new List<(string Key, string Value)>();
        var langVars = new List<(string Key, string Value)>();
        var cloudVars = new List<(string Key, string Value)>();
        var toolVars = new List<(string Key, string Value)>();
        var otherVars = new List<(string Key, string Value)>();

        foreach (var (key, value) in vars)
        {
            // Filter: case-insensitive substring match on the key only (env_cmd.rs:31-35).
            if (filter is not null && !key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isSensitive = sensitivePatterns.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase));

            string displayValue;
            if (isSensitive && !showAll)
            {
                displayValue = MaskValue(value);
            }
            else if (Encoding.UTF8.GetByteCount(value) > TruncationByteThreshold)
            {
                var runes = value.EnumerateRunes().ToList();
                var preview = string.Concat(runes.Take(TruncationPreviewChars).Select(r => r.ToString()));
                displayValue = $"{preview}... ({runes.Count} chars)";
            }
            else
            {
                displayValue = value;
            }

            var entry = (Key: key, Value: displayValue);

            // Categorize into exactly one bucket, in strict priority order (env_cmd.rs:54-64).
            if (key.Contains("PATH", StringComparison.Ordinal))
            {
                pathVars.Add(entry);
            }
            else if (IsLangVar(key))
            {
                langVars.Add(entry);
            }
            else if (IsCloudVar(key))
            {
                cloudVars.Add(entry);
            }
            else if (IsToolVar(key))
            {
                toolVars.Add(entry);
            }
            else if (filter is not null || IsInterestingVar(key))
            {
                otherVars.Add(entry);
            }

            // Otherwise: silently dropped (still counted toward `total` via `vars.Count` below).
        }

        var sb = new StringBuilder();

        if (pathVars.Count > 0)
        {
            sb.Append("PATH Variables:\n");
            foreach (var (k, v) in pathVars)
            {
                if (k == "PATH")
                {
                    // env_cmd.rs:71-81: split the (possibly already-truncated) display value on a
                    // literal ':' — verbatim, not Path.PathSeparator. See EnvCommand's remarks.
                    var segments = v.Split(':');
                    sb.Append("  PATH (").Append(segments.Length).Append(" entries):\n");
                    foreach (var segment in segments.Take(MaxPathEntries))
                    {
                        sb.Append("    ").Append(segment).Append('\n');
                    }

                    if (segments.Length > MaxPathEntries)
                    {
                        sb.Append("    ... +").Append(segments.Length - MaxPathEntries).Append(" more\n");
                    }
                }
                else
                {
                    sb.Append("  ").Append(k).Append('=').Append(v).Append('\n');
                }
            }
        }

        if (langVars.Count > 0)
        {
            sb.Append("\nLanguage/Runtime:\n");
            foreach (var (k, v) in langVars)
            {
                sb.Append("  ").Append(k).Append('=').Append(v).Append('\n');
            }
        }

        if (cloudVars.Count > 0)
        {
            sb.Append("\nCloud/Services:\n");
            foreach (var (k, v) in cloudVars)
            {
                sb.Append("  ").Append(k).Append('=').Append(v).Append('\n');
            }
        }

        if (toolVars.Count > 0)
        {
            sb.Append("\nTools:\n");
            foreach (var (k, v) in toolVars)
            {
                sb.Append("  ").Append(k).Append('=').Append(v).Append('\n');
            }
        }

        if (otherVars.Count > 0)
        {
            sb.Append("\nOther:\n");
            foreach (var (k, v) in otherVars.Take(MaxOtherVars))
            {
                sb.Append("  ").Append(k).Append('=').Append(v).Append('\n');
            }

            if (otherVars.Count > MaxOtherVars)
            {
                sb.Append("  ... +").Append(otherVars.Count - MaxOtherVars).Append(" more\n");
            }
        }

        var total = vars.Count;
        var shown = pathVars.Count + langVars.Count + cloudVars.Count + toolVars.Count + Math.Min(otherVars.Count, MaxOtherVars);

        if (filter is null)
        {
            sb.Append("\nTotal: ").Append(total).Append(" vars (showing ").Append(shown).Append(" relevant)\n");
        }

        return new EnvReport(sb.ToString(), total, shown);
    }

    /// <summary>
    /// The 11 lowercase substring patterns that mark a key's value as sensitive. Faithful port of
    /// <c>get_sensitive_patterns</c> (<c>env_cmd.rs</c>:139-153).
    /// </summary>
    /// <returns>The set of sensitive-key substrings.</returns>
    public static HashSet<string> GetSensitivePatterns() =>
    [
        "key", "secret", "password", "token", "credential", "auth", "private",
        "api_key", "apikey", "access_key", "jwt",
    ];

    /// <summary>
    /// Masks a value: values of 4 or fewer Unicode scalar values become the literal <c>"****"</c>;
    /// longer values keep their first 2 and last 2 scalar values with <c>"****"</c> in between.
    /// Faithful port of <c>mask_value</c> (<c>env_cmd.rs</c>:155-164).
    /// </summary>
    /// <param name="value">The raw value to mask.</param>
    /// <returns>The masked value.</returns>
    public static string MaskValue(string value)
    {
        var runes = value.EnumerateRunes().ToList();
        if (runes.Count <= 4)
        {
            return "****";
        }

        var prefix = string.Concat(runes.Take(2).Select(r => r.ToString()));
        var suffix = string.Concat(runes.Skip(runes.Count - 2).Select(r => r.ToString()));
        return $"{prefix}****{suffix}";
    }

    /// <summary>
    /// True if the uppercased <paramref name="key"/> contains any language/runtime keyword. Faithful
    /// port of <c>is_lang_var</c> (<c>env_cmd.rs</c>:166-172).
    /// </summary>
    /// <param name="key">The environment variable's key.</param>
    /// <returns>True if <paramref name="key"/> looks like a language/runtime variable.</returns>
    public static bool IsLangVar(string key)
    {
        string[] patterns =
        [
            "RUST", "CARGO", "PYTHON", "PIP", "NODE", "NPM", "YARN", "DENO", "BUN", "JAVA", "MAVEN",
            "GRADLE", "GO", "GOPATH", "GOROOT", "RUBY", "GEM", "PERL", "PHP", "DOTNET", "NUGET",
        ];
        var upper = key.ToUpperInvariant();
        return patterns.Any(p => upper.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// True if the uppercased <paramref name="key"/> contains any cloud/infra keyword. Faithful port
    /// of <c>is_cloud_var</c> (<c>env_cmd.rs</c>:174-190).
    /// </summary>
    /// <param name="key">The environment variable's key.</param>
    /// <returns>True if <paramref name="key"/> looks like a cloud/services variable.</returns>
    public static bool IsCloudVar(string key)
    {
        string[] patterns =
        [
            "AWS", "AZURE", "GCP", "GOOGLE_CLOUD", "DOCKER", "KUBERNETES", "K8S", "HELM",
            "TERRAFORM", "VAULT", "CONSUL", "NOMAD",
        ];
        var upper = key.ToUpperInvariant();
        return patterns.Any(p => upper.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// True if the uppercased <paramref name="key"/> contains any dev-tool keyword. Faithful port of
    /// <c>is_tool_var</c> (<c>env_cmd.rs</c>:192-208).
    /// </summary>
    /// <param name="key">The environment variable's key.</param>
    /// <returns>True if <paramref name="key"/> looks like a tool variable.</returns>
    public static bool IsToolVar(string key)
    {
        string[] patterns =
        [
            "EDITOR", "VISUAL", "SHELL", "TERM", "GIT", "SSH", "GPG", "BREW", "HOMEBREW", "XDG",
            "CLAUDE", "ANTHROPIC",
        ];
        var upper = key.ToUpperInvariant();
        return patterns.Any(p => upper.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// True if the uppercased <paramref name="key"/> STARTS WITH any general-interest keyword — unlike
    /// the three helpers above, this uses <c>StartsWith</c>, not <c>Contains</c> (deliberate asymmetry
    /// preserved verbatim from Rust). Faithful port of <c>is_interesting_var</c>
    /// (<c>env_cmd.rs</c>:210-213).
    /// </summary>
    /// <param name="key">The environment variable's key.</param>
    /// <returns>True if <paramref name="key"/> looks like a generally interesting variable.</returns>
    public static bool IsInterestingVar(string key)
    {
        string[] patterns = ["HOME", "USER", "LANG", "LC_", "TZ", "PWD", "OLDPWD"];
        var upper = key.ToUpperInvariant();
        return patterns.Any(p => upper.StartsWith(p, StringComparison.Ordinal));
    }
}
