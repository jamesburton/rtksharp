using System.Collections;
using System.Text;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk env</c> CLI verb: prints the process's environment variables, sorted by
/// key, masking sensitive values and grouping the rest into fixed categories. Faithful port of Rust
/// <c>src/cmds/system/env_cmd.rs</c> (305 lines including its inline test module).
/// </summary>
/// <remarks>
/// <para>
/// <b>PATH splits on a literal <c>':'</c>, even on Windows (deliberate, not a bug).</b> Rust's
/// <c>v.split(':')</c> (<c>env_cmd.rs</c>:73) is hardcoded to the Unix path-list separator
/// regardless of platform. This port preserves that literal byte-exact behavior rather than
/// "fixing" it to <see cref="System.IO.Path.PathSeparator"/> — on a real Windows <c>PATH</c> string
/// (which uses <c>;</c>), this produces exactly one giant "segment" (no <c>:</c> to split on),
/// which is the byte-exact match to what the Rust oracle itself produces when run on Windows.
/// </para>
/// <para>
/// <b>The PATH split operates on the already-truncated display value, not the raw value.</b> Rust
/// builds one <c>display_value</c> per variable (masked, &gt;100-byte-truncated, or unchanged) and
/// stores it in the bucket entry <i>before</i> the later PATH-specific print logic ever runs
/// (<c>env_cmd.rs</c>:42-54 then :70-85) — so if <c>PATH</c>'s raw value exceeds the 100-byte
/// truncation threshold (extremely common on real systems), the split-on-<c>':'</c> step operates on
/// the already-truncated 50-char preview string, not the full original value. This is preserved
/// exactly: <see cref="Run"/> categorizes using the same computed <c>displayValue</c> the truncation
/// step produced, never the original raw value.
/// </para>
/// <para>
/// <b>The truncation length check is byte-based; the preview/count are char (Unicode-scalar) based.</b>
/// Rust's <c>value.len() &gt; 100</c> (<c>env_cmd.rs</c>:44) is <see cref="string.Length"/>-like but
/// on a Rust <c>&amp;str</c> that means UTF-8 <i>byte</i> length, whereas the truncated preview
/// (<c>value.chars().take(50)</c>) and the reported count (<c>value.chars().count()</c>) are both
/// Unicode-scalar-value (char) based. For ASCII values (the overwhelming majority of real
/// environment variables) byte length and char count are identical, but this port preserves the
/// distinction faithfully: the &gt;100 check uses the UTF-8 byte count, while the preview/count use
/// <see cref="System.Text.Rune"/> enumeration (.NET's closest equivalent to Rust's <c>char</c>,
/// since both represent a single Unicode scalar value rather than a UTF-16 code unit — this avoids
/// splitting a surrogate pair the way naive <see cref="string.Length"/>/indexing could).
/// </para>
/// <para>
/// <b><see cref="MaskValue"/> is also Rune-based for the same reason.</b> Rust's <c>mask_value</c>
/// collects <c>value.chars()</c> into a <c>Vec&lt;char&gt;</c> and indexes by count, not by byte —
/// this port mirrors that with <see cref="System.Text.Rune"/> enumeration rather than raw
/// <see cref="string.Length"/>/substring indexing, so a value containing a supplementary-plane
/// character (surrogate pair) is masked identically to the Rust oracle.
/// </para>
/// <para>
/// <b>Tracking always runs, independent of <c>--filter</c>.</b> The <c>raw</c> baseline string fed to
/// <see cref="TimedExecution.Track"/> is built from the FULL unfiltered, uncategorized, UNMASKED
/// variable list (<c>env_cmd.rs</c>:130-133, iterating <c>vars</c> — not any bucket) regardless of
/// whether <c>--filter</c> was supplied; only the on-screen <c>"\nTotal: ..."</c> summary line is
/// suppressed under <c>--filter</c> (<c>env_cmd.rs</c>:126-128). These are two independent gates in
/// the source and are kept independent here.
/// </para>
/// <para>
/// <b>Injectable environment source for testability.</b> Unlike Rust, which reads
/// <c>std::env::vars()</c> directly inside <c>run</c>, this port's core logic
/// (<see cref="Run(IReadOnlyList{string},IReadOnlyDictionary{string,string},int)"/>) accepts the
/// variable set as a parameter so tests can exercise deterministic scenarios without mutating the
/// real test process's actual environment. <see cref="RunAsync"/> is the production entry point,
/// defaulting to the real <see cref="Environment.GetEnvironmentVariables()"/> snapshot.
/// </para>
/// <para>
/// <b>Not a meta-command (deliberate Rust-source asymmetry, same as <c>err</c>/<c>test</c>).</b> Rust's
/// <c>RTK_META_COMMANDS</c> list does not include <c>"env"</c> either (a malformed invocation falls
/// through to raw passthrough rather than a hard Clap parse error) — but <c>env</c> IS present in
/// Rust's <c>is_operational_command</c> whitelist. As of this port there is no
/// operational-vs-meta-command gate built yet for any command, so this is a no-op for now; <c>env</c>
/// is registered in <see cref="RtkSharp.Cli.CommandRegistry"/> as a normal dispatch entry, which
/// already gives it the registry-hit-always-wins guarantee established as the sufficient substitute
/// in Phase 6.
/// </para>
/// <para>
/// <b>Always exits 0.</b> Reading the environment cannot fail, and there is no other error path in
/// <c>env_cmd.rs</c> itself.
/// </para>
/// </remarks>
public static class EnvCommand
{
    // Rust CAP_WARNINGS (= 10) from src/core/truncate.rs (env_cmd.rs:75), reused verbatim as the
    // PATH-entries display cap. No shared constants class exists in RtkSharp yet; each command
    // defines its own local constant (established convention, e.g. PipeCommand/TestCommand/LsCommand).
    private const int MaxPathEntries = 10;

    // Rust CAP_LIST (= 20) from src/core/truncate.rs (env_cmd.rs:110), reused verbatim as the
    // "Other" section's display cap.
    private const int MaxOtherVars = 20;

    // env_cmd.rs:44's `value.len() > 100` threshold (UTF-8 byte length in Rust).
    private const int TruncationByteThreshold = 100;

    // env_cmd.rs:45's `value.chars().take(50)` preview length (Unicode-scalar-value count).
    private const int TruncationPreviewChars = 50;

    /// <summary>
    /// Registry entry point. Runs <c>rtk env</c> with the given arguments (the remainder after the
    /// <c>env</c> verb), reading the real process environment and the global verbosity flag.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>env</c>.</param>
    /// <returns>0 (always — <c>env</c> has no failure path).</returns>
    public static Task<int> RunAsync(string[] args) =>
        Task.FromResult(Run(args, GetProcessEnvironmentVariables(), RuntimeOptions.Verbosity));

    /// <summary>
    /// Runs <c>rtk env</c>'s core logic against an injectable environment-variable set, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>env</c>.</param>
    /// <param name="envVars">The environment variables to display (key/value pairs, any order).</param>
    /// <param name="verbosity">The global verbosity level (mirrors Rust's <c>cli.verbose: u8</c>).</param>
    /// <returns>0 (always — <c>env</c> has no failure path).</returns>
    internal static int Run(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> envVars, int verbosity)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(envVars);

        var (filter, showAll) = ParseArgs(args);
        var timer = TimedExecution.Start();

        if (verbosity > 0)
        {
            Console.Error.Write("Environment variables:\n");
        }

        var sensitivePatterns = GetSensitivePatterns();

        // env_cmd.rs:19-20: `let mut vars: Vec<(String, String)> = env::vars().collect(); vars.sort_by(...)`.
        var vars = envVars
            .Select(kv => (Key: kv.Key, Value: kv.Value))
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .ToList();

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
                    // literal ':' — verbatim, not Path.PathSeparator. See class remarks.
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

        Console.Out.Write(sb.ToString());

        // env_cmd.rs:130-134: the tracking baseline is built from the FULL unfiltered, uncategorized,
        // UNMASKED `vars` list - independent of the `--filter`-suppresses-the-summary-line gate above.
        var raw = new StringBuilder();
        foreach (var (k, v) in vars)
        {
            raw.Append(k).Append('=').Append(v).Append('\n');
        }

        var rtk = $"{total} vars -> {shown} shown";
        timer.Track("env", "rtk env", raw.ToString(), rtk);

        return 0;
    }

    /// <summary>
    /// Parses <c>-f</c>/<c>--filter &lt;value&gt;</c>/<c>--filter=&lt;value&gt;</c> and
    /// <c>--show-all</c> out of the raw <c>env</c> arguments.
    /// </summary>
    /// <param name="args">The raw CLI arguments following the <c>env</c> verb.</param>
    /// <returns>The resolved filter string (or null if not given) and whether <c>--show-all</c> was set.</returns>
    internal static (string? Filter, bool ShowAll) ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? filter = null;
        var showAll = false;

        var i = 0;
        while (i < args.Count)
        {
            var a = args[i];

            if (a == "-f" || a == "--filter")
            {
                if (i + 1 < args.Count)
                {
                    filter = args[i + 1];
                    i += 2;
                }
                else
                {
                    i++;
                }

                continue;
            }

            if (a.StartsWith("--filter=", StringComparison.Ordinal))
            {
                filter = a["--filter=".Length..];
                i++;
                continue;
            }

            if (a == "--show-all")
            {
                showAll = true;
                i++;
                continue;
            }

            i++;
        }

        return (filter, showAll);
    }

    /// <summary>
    /// The 11 lowercase substring patterns that mark a key's value as sensitive. Faithful port of
    /// <c>get_sensitive_patterns</c> (<c>env_cmd.rs</c>:139-153).
    /// </summary>
    /// <returns>The set of sensitive-key substrings.</returns>
    internal static HashSet<string> GetSensitivePatterns() =>
    [
        "key", "secret", "password", "token", "credential", "auth", "private",
        "api_key", "apikey", "access_key", "jwt",
    ];

    /// <summary>
    /// Masks a value: values of 4 or fewer Unicode scalar values become the literal <c>"****"</c>;
    /// longer values keep their first 2 and last 2 scalar values with <c>"****"</c> in between.
    /// Faithful port of <c>mask_value</c> (<c>env_cmd.rs</c>:155-164) — see the class remarks for why
    /// this is Rune-based rather than <see cref="string.Length"/>-based.
    /// </summary>
    /// <param name="value">The raw value to mask.</param>
    /// <returns>The masked value.</returns>
    internal static string MaskValue(string value)
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
    internal static bool IsLangVar(string key)
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
    internal static bool IsCloudVar(string key)
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
    internal static bool IsToolVar(string key)
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
    internal static bool IsInterestingVar(string key)
    {
        string[] patterns = ["HOME", "USER", "LANG", "LC_", "TZ", "PWD", "OLDPWD"];
        var upper = key.ToUpperInvariant();
        return patterns.Any(p => upper.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Snapshots the real process environment as a plain dictionary, for <see cref="RunAsync"/>'s
    /// production entry point.
    /// </summary>
    /// <returns>The current process's environment variables.</returns>
    private static IReadOnlyDictionary<string, string> GetProcessEnvironmentVariables()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key as string;
            if (key is not null)
            {
                result[key] = entry.Value as string ?? string.Empty;
            }
        }

        return result;
    }
}
