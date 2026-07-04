using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RtkSharp.Hooks;
using Tomlyn;
using Tomlyn.Serialization;

namespace RtkSharp.Core;

/// <summary>
/// User-configurable settings for auto-rewrite, filtering, tracking, and telemetry. Faithful port
/// of Rust <c>Config</c> (<c>src/core/config.rs:8-24</c>): a single <c>config.toml</c> file at
/// <c>{config_dir}/rtk/config.toml</c> (no project-local tier — see <see cref="Load"/>'s remarks).
/// </summary>
/// <remarks>
/// <para>
/// <b>Omitted field.</b> Rust's <c>Config</c> also has a <c>tee: crate::core::tee::TeeConfig</c>
/// field (config.rs:17). It is intentionally <b>not</b> ported here: <c>tee.rs</c>'s runtime
/// recovery-store logic has not been ported to RtkSharp at all yet (a separate, later phase), and
/// Tomlyn's deserializer does not require every source key to have a matching POCO property (a
/// TOML table with no matching property is simply ignored), so no placeholder <c>TeeConfig</c>
/// stub is needed for round-tripping. The practical consequence: <c>rtk config</c>'s output omits
/// the oracle's <c>[tee]</c> section entirely — a documented, deliberate scope gap, not a bug.
/// </para>
/// <para>
/// <b>Pretty-print fidelity.</b> Tomlyn 2.10.1's built-in <see cref="TomlSerializer"/> was verified
/// (by probing the real package, not assumed) to diverge from Rust's <c>toml::to_string_pretty</c>
/// in two ways: it writes string arrays inline on one line with no wrapping (Rust wraps a non-empty
/// array one element per line, 4-space indented, with a trailing comma on every element including
/// the last, and the closing bracket on its own line), and it does not insert a blank line between
/// top-level tables (Rust always does, including after the final table). Both were confirmed against
/// a real build of the Rust oracle (<c>target/release/rtk.exe config</c>) rather than assumed from
/// TOML conventions. Since <see cref="Config"/>'s shape is fixed and small (six known sections),
/// <see cref="ToPrettyToml"/> is a small hand-rolled formatter matching the oracle's exact byte
/// shape, rather than relying on Tomlyn's generic serializer for this output. Tomlyn is still used
/// for <see cref="Load"/>'s deserialization — hand-rolling a TOML *parser* for arbitrary user-edited
/// config files would be the multi-day side quest this dependency exists to avoid; only the
/// pretty-print *writer* for this one fixed schema needed a bespoke implementation.
/// </para>
/// </remarks>
public sealed class Config
{
    /// <summary>Command tracking / token-savings metrics settings.</summary>
    [TomlPropertyName("tracking")]
    public TrackingConfig Tracking { get; set; } = new();

    /// <summary>Terminal display preferences.</summary>
    [TomlPropertyName("display")]
    public DisplayConfig Display { get; set; } = new();

    /// <summary>File/directory filtering defaults (currently parsed and round-tripped only — not
    /// yet wired into any consumer, matching Rust's own "planned, not yet implemented" status for
    /// these fields).</summary>
    [TomlPropertyName("filters")]
    public FilterConfig Filters { get; set; } = new();

    /// <summary>Anonymous usage telemetry consent/opt-in settings.</summary>
    [TomlPropertyName("telemetry")]
    public TelemetryConfig Telemetry { get; set; } = new();

    /// <summary>Auto-rewrite hook behavior: commands to exclude and transparent wrapper prefixes.</summary>
    [TomlPropertyName("hooks")]
    public HooksConfig Hooks { get; set; } = new();

    /// <summary>Output size limits for various filters.</summary>
    [TomlPropertyName("limits")]
    public LimitsConfig Limits { get; set; } = new();

    private const string ConfigSubDir = "rtk";
    private const string ConfigFileName = "config.toml";

    /// <summary>
    /// Loads <c>config.toml</c> from the resolved config path if it exists; otherwise returns an
    /// all-defaults <see cref="Config"/>. Faithful port of Rust <c>Config::load</c>
    /// (config.rs:154-164), including its per-section strictness: an entirely <b>absent</b>
    /// <c>[tracking]</c>/<c>[display]</c>/<c>[filters]</c>/<c>[limits]</c> table still defaults
    /// cleanly, but a <b>present-but-incomplete</b> one (missing one or more of that section's
    /// required keys) is a hard failure — matching Rust's serde-derive behavior, where those four
    /// structs have no per-field <c>#[serde(default)]</c> (config.rs:56-146) and so error on a
    /// partial table, unlike <c>HooksConfig</c> (both fields) and
    /// <c>TelemetryConfig.consent_given</c>/<c>consent_date</c>, which genuinely do have per-field
    /// <c>#[serde(default)]</c> and so tolerate partial tables by design —
    /// <c>TelemetryConfig.enabled</c> does <b>not</b> have <c>#[serde(default)]</c> in Rust
    /// (config.rs:115) and so is required whenever a <c>[telemetry]</c> table is present, exactly like
    /// the four all-required structs below. See the
    /// <see cref="Tomlyn.Serialization.TomlRequiredAttribute"/> attributes on
    /// <see cref="TrackingConfig"/>, <see cref="DisplayConfig"/>,
    /// <see cref="FilterConfig"/>, and <see cref="LimitsConfig"/>'s properties, plus
    /// <see cref="TelemetryConfig.Enabled"/>, which reproduce this per-key strictness (verified
    /// empirically: Tomlyn 2.10.1's <c>TomlRequiredAttribute</c> throws a <c>TomlException</c> when a
    /// present table is missing a required key, for both the reflection and AOT-safe
    /// source-generated binding paths, while still allowing the table itself to be wholly absent —
    /// see this method's tests in <c>RtkSharp.Tests/Core/ConfigTests.cs</c>).
    /// </summary>
    /// <remarks>
    /// <b>No project-local tier.</b> Unlike <c>.rtk/filters.toml</c> (a later Phase 4 task), there is
    /// no per-project <c>.rtk/config.toml</c> consulted here or anywhere in the Rust source — only
    /// the single global file. This is easy to get backwards by analogy with the filters tier, which
    /// is why <c>RtkSharp.Tests/Core/ConfigTests.cs</c> has a dedicated test asserting a project-local
    /// <c>.rtk/config.toml</c> is never read.
    /// </remarks>
    /// <returns>The loaded or default <see cref="Config"/>.</returns>
    /// <exception cref="Exception">
    /// Propagated verbatim (fail-loud) if the file exists but cannot be read or parsed. This method
    /// is called directly by the user-invoked <c>rtk config</c> command, which follows the
    /// fail-loud contract established by <c>init</c>/<c>verify</c> in Phase 9b — callers on a runtime
    /// hot path (e.g. the Phase 4 rewrite-engine wiring) are responsible for their own
    /// fallback-on-failure handling; this method itself never swallows errors.
    /// </exception>
    public static Config Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
        {
            return new Config();
        }

        var content = File.ReadAllText(path);
        return TomlSerializer.Deserialize<Config>(content, ConfigTomlContext.Default.Config) ?? new Config();
    }

    /// <summary>
    /// Loads config like <see cref="Load"/> but never throws: on any failure (missing, unreadable,
    /// or corrupt <c>config.toml</c>) it returns an all-defaults <see cref="Config"/> instead of
    /// propagating the exception. Intended for the <b>runtime hot paths</b> (the rewrite-engine
    /// exclude/transparent-prefix wiring and the grep per-file cap) that must degrade to today's
    /// default behavior rather than crash the command pipeline over a bad config file — per this
    /// phase's fallback-pattern Global Constraint. User-invoked one-shot commands (<c>rtk config</c>)
    /// keep calling <see cref="Load"/> directly so they still fail loud on a corrupt file.
    /// </summary>
    /// <returns>The loaded <see cref="Config"/>, or an all-defaults one if loading failed.</returns>
    public static Config LoadOrDefault()
    {
        try
        {
            return Load();
        }
        catch (Exception)
        {
            return new Config();
        }
    }

    /// <summary>
    /// Writes this <see cref="Config"/> to the resolved config path as pretty-printed TOML (see
    /// <see cref="ToPrettyToml"/>), creating parent directories as needed. Faithful port of Rust
    /// <c>Config::save</c> (config.rs:166-176).
    /// </summary>
    public void Save()
    {
        var path = GetConfigPath();
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(path, ToPrettyToml(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Builds an all-defaults <see cref="Config"/>, saves it (see <see cref="Save"/>), and returns
    /// the path written to. Faithful port of Rust <c>Config::create_default</c> (config.rs:178-182).
    /// </summary>
    /// <returns>The path the default config was written to.</returns>
    public static string CreateDefault()
    {
        var config = new Config();
        config.Save();
        return GetConfigPath();
    }

    /// <summary>
    /// Implements <c>rtk config</c>'s stdout body: the resolved path, then either the existing
    /// file's loaded content or a "not created" notice followed by the compiled-in defaults — both
    /// re-serialized as pretty TOML. Faithful port of Rust <c>show_config</c> (config.rs:190-206).
    /// </summary>
    public static void ShowConfig()
    {
        var path = GetConfigPath();
        Console.Out.Write($"Config: {path}\n");
        Console.Out.Write("\n");

        if (File.Exists(path))
        {
            var config = Load();
            Console.Out.Write(config.ToPrettyToml());
        }
        else
        {
            Console.Out.Write("(default config, file not created)\n");
            Console.Out.Write("\n");
            var config = new Config();
            Console.Out.Write(config.ToPrettyToml());
        }
    }

    /// <summary>
    /// Resolves <c>{config_dir}/rtk/config.toml</c>, reusing <see cref="InitArtifacts.ResolveGlobalConfigDir"/>
    /// (the same directory Phase 9b's global filters template already resolves — honoring the
    /// test/parity-only <c>RTK_CONFIG_DIR_OVERRIDE</c> escape hatch) rather than re-deriving the
    /// platform config directory independently.
    /// </summary>
    /// <returns>The resolved config file path.</returns>
    internal static string GetConfigPath() =>
        Path.Combine(InitArtifacts.ResolveGlobalConfigDir(), ConfigSubDir, ConfigFileName);

    /// <summary>
    /// Serializes this <see cref="Config"/> as pretty TOML matching Rust's <c>toml::to_string_pretty</c>
    /// byte shape exactly (verified against a real build of the Rust oracle — see this type's
    /// remarks): one <c>[section]</c> per sub-config in declaration order, a blank line after every
    /// section (including the last), non-empty string arrays wrapped one element per line with a
    /// trailing comma (empty arrays inline as <c>[]</c>), and <c>Option</c>-like fields omitted
    /// entirely when unset.
    /// </summary>
    /// <returns>The pretty-printed TOML text.</returns>
    public string ToPrettyToml()
    {
        var sb = new StringBuilder();

        AppendSection(sb, "tracking", w =>
        {
            w.Bool("enabled", Tracking.Enabled);
            w.UInt("history_days", Tracking.HistoryDays);
            w.OptionalString("database_path", Tracking.DatabasePath);
        });

        AppendSection(sb, "display", w =>
        {
            w.Bool("colors", Display.Colors);
            w.Bool("emoji", Display.Emoji);
            w.Int("max_width", Display.MaxWidth);
        });

        AppendSection(sb, "filters", w =>
        {
            w.StringArray("ignore_dirs", Filters.IgnoreDirs);
            w.StringArray("ignore_files", Filters.IgnoreFiles);
        });

        AppendSection(sb, "telemetry", w =>
        {
            w.Bool("enabled", Telemetry.Enabled);
            w.OptionalBool("consent_given", Telemetry.ConsentGiven);
            w.OptionalString("consent_date", Telemetry.ConsentDate);
        });

        AppendSection(sb, "hooks", w =>
        {
            w.StringArray("exclude_commands", Hooks.ExcludeCommands);
            w.StringArray("transparent_prefixes", Hooks.TransparentPrefixes);
        });

        AppendSection(sb, "limits", w =>
        {
            w.Int("grep_max_results", Limits.GrepMaxResults);
            w.Int("grep_max_per_file", Limits.GrepMaxPerFile);
            w.Int("status_max_files", Limits.StatusMaxFiles);
            w.Int("status_max_untracked", Limits.StatusMaxUntracked);
            w.Int("passthrough_max_chars", Limits.PassthroughMaxChars);
        });

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string name, Action<TomlSectionWriter> body)
    {
        sb.Append('[').Append(name).Append(']').Append('\n');
        body(new TomlSectionWriter(sb));
        sb.Append('\n');
    }

    /// <summary>
    /// Minimal key=value line writer for <see cref="ToPrettyToml"/>, scoped to the small set of TOML
    /// value shapes <see cref="Config"/> actually needs (bool, integer, string, string array,
    /// optional variants of each) — not a general-purpose TOML writer.
    /// </summary>
    private readonly struct TomlSectionWriter(StringBuilder sb)
    {
        public void Bool(string key, bool value) =>
            sb.Append(key).Append(" = ").Append(value ? "true" : "false").Append('\n');

        public void OptionalBool(string key, bool? value)
        {
            if (value is { } v)
            {
                Bool(key, v);
            }
        }

        public void Int(string key, int value) =>
            sb.Append(key).Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');

        public void UInt(string key, uint value) =>
            sb.Append(key).Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');

        public void OptionalString(string key, string? value)
        {
            if (value is not null)
            {
                sb.Append(key).Append(" = ").Append(QuoteTomlString(value)).Append('\n');
            }
        }

        public void StringArray(string key, IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                sb.Append(key).Append(" = []\n");
                return;
            }

            sb.Append(key).Append(" = [\n");
            foreach (var value in values)
            {
                sb.Append("    ").Append(QuoteTomlString(value)).Append(",\n");
            }

            sb.Append("]\n");
        }

        private static string QuoteTomlString(string value)
        {
            var escaped = value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);
            return $"\"{escaped}\"";
        }
    }
}

/// <summary>
/// Auto-rewrite hook behavior. Faithful port of Rust <c>HooksConfig</c> (config.rs:26-54).
/// </summary>
public sealed class HooksConfig
{
    /// <summary>
    /// Commands to exclude from auto-rewrite (e.g. <c>["curl", "playwright"]</c>). Survives
    /// <c>rtk init -g</c> re-runs since <c>config.toml</c> is user-owned.
    /// </summary>
    [TomlPropertyName("exclude_commands")]
    public List<string> ExcludeCommands { get; set; } = [];

    /// <summary>
    /// Wrapper prefixes that should be transparently stripped before routing to a filter, then
    /// re-prepended on the rewrite (e.g. <c>docker exec mycontainer</c>, <c>direnv exec .</c>,
    /// <c>poetry run</c>, <c>bundle exec</c>). Matching is literal, not pattern-based.
    /// </summary>
    [TomlPropertyName("transparent_prefixes")]
    public List<string> TransparentPrefixes { get; set; } = [];
}

/// <summary>
/// Command tracking / token-savings metrics settings. Faithful port of Rust <c>TrackingConfig</c>
/// (config.rs:56-72).
/// </summary>
public sealed class TrackingConfig
{
    /// <summary>Whether command tracking is enabled at all. Defaults to <see langword="true"/>.</summary>
    [TomlPropertyName("enabled"), TomlRequired]
    public bool Enabled { get; set; } = true;

    /// <summary>How many days of tracking history to retain. Defaults to 90.</summary>
    [TomlPropertyName("history_days"), TomlRequired]
    public uint HistoryDays { get; set; } = 90;

    /// <summary>Override path for the tracking SQLite database, or <see langword="null"/> for the default location.</summary>
    [TomlPropertyName("database_path")]
    public string? DatabasePath { get; set; }
}

/// <summary>
/// Terminal display preferences. Faithful port of Rust <c>DisplayConfig</c> (config.rs:74-89).
/// </summary>
public sealed class DisplayConfig
{
    /// <summary>Whether to use ANSI colors in output. Defaults to <see langword="true"/>.</summary>
    [TomlPropertyName("colors"), TomlRequired]
    public bool Colors { get; set; } = true;

    /// <summary>Whether to use emoji in output. Defaults to <see langword="true"/>.</summary>
    [TomlPropertyName("emoji"), TomlRequired]
    public bool Emoji { get; set; } = true;

    /// <summary>Maximum display width in columns. Defaults to 120.</summary>
    [TomlPropertyName("max_width"), TomlRequired]
    public int MaxWidth { get; set; } = 120;
}

/// <summary>
/// File/directory filtering defaults. Faithful port of Rust <c>FilterConfig</c> (config.rs:91-111).
/// </summary>
public sealed class FilterConfig
{
    /// <summary>Directory names to ignore when walking a project tree.</summary>
    [TomlPropertyName("ignore_dirs"), TomlRequired]
    public List<string> IgnoreDirs { get; set; } =
    [
        ".git",
        "node_modules",
        "target",
        "__pycache__",
        ".venv",
        "vendor",
    ];

    /// <summary>File glob patterns to ignore when walking a project tree.</summary>
    [TomlPropertyName("ignore_files"), TomlRequired]
    public List<string> IgnoreFiles { get; set; } =
    [
        "*.lock",
        "*.min.js",
        "*.min.css",
    ];
}

/// <summary>
/// Anonymous usage telemetry consent/opt-in settings. Faithful port of Rust <c>TelemetryConfig</c>
/// (config.rs:113-120).
/// </summary>
public sealed class TelemetryConfig
{
    /// <summary>Whether telemetry is enabled. Defaults to <see langword="false"/> (opt-in).</summary>
    [TomlPropertyName("enabled"), TomlRequired]
    public bool Enabled { get; set; }

    /// <summary>Whether the user has explicitly answered the telemetry consent prompt.</summary>
    [TomlPropertyName("consent_given")]
    public bool? ConsentGiven { get; set; }

    /// <summary>The ISO-8601 timestamp the consent decision was recorded, if any.</summary>
    [TomlPropertyName("consent_date")]
    public string? ConsentDate { get; set; }
}

/// <summary>
/// Output size limits for various filters. Faithful port of Rust <c>LimitsConfig</c>
/// (config.rs:122-146).
/// </summary>
public sealed class LimitsConfig
{
    /// <summary>Max total grep results to show. Defaults to 200.</summary>
    [TomlPropertyName("grep_max_results"), TomlRequired]
    public int GrepMaxResults { get; set; } = 200;

    /// <summary>Max matches per file in grep output. Defaults to 25.</summary>
    [TomlPropertyName("grep_max_per_file"), TomlRequired]
    public int GrepMaxPerFile { get; set; } = 25;

    /// <summary>Max staged/modified files shown in git status. Defaults to 15.</summary>
    [TomlPropertyName("status_max_files"), TomlRequired]
    public int StatusMaxFiles { get; set; } = 15;

    /// <summary>Max untracked files shown in git status. Defaults to 10.</summary>
    [TomlPropertyName("status_max_untracked"), TomlRequired]
    public int StatusMaxUntracked { get; set; } = 10;

    /// <summary>Max chars for parser passthrough fallback. Defaults to 2000.</summary>
    [TomlPropertyName("passthrough_max_chars"), TomlRequired]
    public int PassthroughMaxChars { get; set; } = 2000;
}

/// <summary>
/// Source-generation context for <see cref="Config"/> deserialization (see <see cref="Config.Load"/>),
/// so <see cref="TomlSerializer.Deserialize{T}(string, Tomlyn.TomlTypeInfo{T})"/> resolves type info
/// without reflection. Required for <c>RtkSharp.csproj</c>'s <c>PublishAot=true</c>: Tomlyn disables
/// its reflection-based model binding by default once it detects a Native AOT publish, so relying on
/// the plain <c>TomlSerializer.Deserialize&lt;Config&gt;(string)</c> overload (no explicit type info)
/// would be reflection-dependent and unsafe under trimming.
/// </summary>
[TomlSerializable(typeof(Config))]
internal sealed partial class ConfigTomlContext : TomlSerializerContext;
