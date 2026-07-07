using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RtkSharp.Discover;

/// <summary>
/// RTK support status for a command. Faithful port of Rust <c>discover::report::RtkStatus</c>
/// (<c>report.rs</c>:12-19).
/// </summary>
public enum RtkStatus
{
    /// <summary>Dedicated handler with filtering (e.g. <c>git status</c> → <c>git.rs:run_status()</c>).</summary>
    Existing,

    /// <summary>Works via external-subcommand passthrough, no filtering (e.g. <c>cargo fmt</c> → Other).</summary>
    Passthrough,

    /// <summary>RTK doesn't handle this command at all.</summary>
    NotSupported,
}

/// <summary>
/// Extension helpers for <see cref="RtkStatus"/>.
/// </summary>
public static class RtkStatusExtensions
{
    /// <summary>
    /// The lowercase display string for a status. Faithful port of Rust <c>RtkStatus::as_str</c>
    /// (<c>report.rs</c>:22-28).
    /// </summary>
    /// <param name="status">The status to render.</param>
    /// <returns><c>"existing"</c>, <c>"passthrough"</c>, or <c>"not-supported"</c>.</returns>
    public static string AsStr(this RtkStatus status) => status switch
    {
        RtkStatus.Existing => "existing",
        RtkStatus.Passthrough => "passthrough",
        RtkStatus.NotSupported => "not-supported",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>
/// A supported command that RTK already handles. Faithful port of Rust
/// <c>discover::report::SupportedEntry</c> (<c>report.rs</c>:31-41).
/// </summary>
/// <param name="Command">The display-truncated raw command (first two words), as seen in the session history.</param>
/// <param name="Count">How many times this rtk-equivalent command was seen (unfiltered) across all scanned sessions.</param>
/// <param name="RtkEquivalent">The rtk command that would have handled this, e.g. <c>"rtk git"</c>.</param>
/// <param name="Category">The rule category this command matched, e.g. <c>"Git"</c>.</param>
/// <param name="EstimatedSavingsTokens">The total estimated tokens that would have been saved across every occurrence.</param>
/// <param name="EstimatedSavingsPct">The weighted-average effective savings percentage across every occurrence.</param>
/// <param name="RtkStatus">Whether rtk's handler for this command is a dedicated filter, a bare passthrough, or unsupported.</param>
public sealed record SupportedEntry(
    string Command,
    int Count,
    string RtkEquivalent,
    string Category,
    int EstimatedSavingsTokens,
    double EstimatedSavingsPct,
    RtkStatus RtkStatus);

/// <summary>
/// An unsupported command not yet handled by RTK. Faithful port of Rust
/// <c>discover::report::UnsupportedEntry</c> (<c>report.rs</c>:44-49).
/// </summary>
/// <param name="BaseCommand">The extracted base command (first word, or first two words for subcommand-shaped invocations).</param>
/// <param name="Count">How many times this base command was seen.</param>
/// <param name="Example">One representative full command line for this base command.</param>
public sealed record UnsupportedEntry(string BaseCommand, int Count, string Example);

/// <summary>
/// Which third-party agent hooks/plugins are installed on this machine, so <c>rtk discover</c> can
/// note that those agents' sessions aren't scanned (Claude Code only). Faithful port of Rust
/// <c>discover::report::AgentIntegrationStatus</c> (<c>report.rs</c>:51-93).
/// </summary>
/// <param name="CursorHookInstalled">Whether Cursor's rtk rewrite hook is installed under the user's home directory.</param>
/// <param name="HermesPluginInstalled">Whether the Hermes <c>rtk-rewrite</c> plugin manifest is installed under the user's home directory.</param>
/// <param name="CopilotHookInstalled">Whether GitHub Copilot's rtk rewrite hook is installed in the current project (project-scoped, unlike the other two).</param>
public sealed record AgentIntegrationStatus(
    bool CursorHookInstalled,
    bool HermesPluginInstalled,
    bool CopilotHookInstalled)
{
    // Mirrors src/hooks/constants.rs's CURSOR_DIR/HOOKS_SUBDIR/REWRITE_HOOK_FILE/HERMES_DIR/
    // HERMES_PLUGINS_SUBDIR/HERMES_PLUGIN_NAME/HERMES_PLUGIN_MANIFEST_FILE/GITHUB_DIR/COPILOT_HOOK_FILE.
    // No shared constants class links RtkSharp.Discover to RtkSharp.Hooks for these agent-detection
    // paths (RtkSharp/Hooks/HookCheck.cs only tracks Claude Code's own rtk-rewrite.sh under
    // ~/.claude/hooks/, not the Cursor/Hermes/Copilot integrations this type detects), so the four
    // path segments are transcribed verbatim here rather than introducing a cross-module dependency
    // for four string literals.
    private const string CursorDir = ".cursor";
    private const string HooksSubdir = "hooks";
    private const string RewriteHookFile = "rtk-rewrite.sh";
    private const string HermesDir = ".hermes";
    private const string HermesPluginsSubdir = "plugins";
    private const string HermesPluginName = "rtk-rewrite";
    private const string HermesPluginManifestFile = "plugin.yaml";
    private const string GithubDir = ".github";
    private const string CopilotHookFile = "rtk-rewrite.json";

    /// <summary>
    /// An all-<see langword="false"/> instance, used when the home directory cannot be resolved (mirrors
    /// Rust's <c>Self::default()</c> fallback, <c>report.rs</c>:62).
    /// </summary>
    public static readonly AgentIntegrationStatus Default = new(false, false, false);

    /// <summary>
    /// Detects installed agent integrations from the real user home directory and current working
    /// directory. Faithful port of <c>AgentIntegrationStatus::detect</c> (<c>report.rs</c>:59-68).
    /// </summary>
    /// <returns>The detected integration status.</returns>
    public static AgentIntegrationStatus Detect()
    {
        string? home;
        try
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = null;
            }
        }
        catch (Exception)
        {
            home = null;
        }

        var status = home is not null ? DetectFromHome(home) : Default;

        bool copilotInstalled;
        try
        {
            copilotInstalled = CopilotHookInstalledIn(Directory.GetCurrentDirectory());
        }
        catch (Exception)
        {
            copilotInstalled = false;
        }

        return status with { CopilotHookInstalled = copilotInstalled };
    }

    /// <summary>
    /// The testable core of <see cref="Detect"/>'s home-scoped checks (Cursor/Hermes), taking the home
    /// directory explicitly. Faithful port of <c>AgentIntegrationStatus::detect_from_home</c>
    /// (<c>report.rs</c>:70-85).
    /// </summary>
    /// <param name="home">The user's home directory.</param>
    /// <returns>Cursor/Hermes detection results, with <see cref="CopilotHookInstalled"/> always <see langword="false"/>.</returns>
    internal static AgentIntegrationStatus DetectFromHome(string home)
    {
        var cursorHookInstalled = File.Exists(Path.Combine(home, CursorDir, HooksSubdir, RewriteHookFile));
        var hermesPluginInstalled = File.Exists(
            Path.Combine(home, HermesDir, HermesPluginsSubdir, HermesPluginName, HermesPluginManifestFile));
        return new AgentIntegrationStatus(cursorHookInstalled, hermesPluginInstalled, false);
    }

    /// <summary>
    /// The testable core of <see cref="Detect"/>'s project-scoped Copilot check. Faithful port of
    /// <c>AgentIntegrationStatus::copilot_hook_installed_in</c> (<c>report.rs</c>:87-92).
    /// </summary>
    /// <param name="dir">The directory to check (normally the current working directory).</param>
    /// <returns><see langword="true"/> if <c>.github/hooks/rtk-rewrite.json</c> exists under <paramref name="dir"/>.</returns>
    internal static bool CopilotHookInstalledIn(string dir) =>
        File.Exists(Path.Combine(dir, GithubDir, HooksSubdir, CopilotHookFile));
}

/// <summary>
/// The full <c>rtk discover</c> report. Faithful port of Rust <c>discover::report::DiscoverReport</c>
/// (<c>report.rs</c>:95-121), plus its text/JSON renderers (<c>format_text</c>/<c>format_json</c>,
/// <c>report.rs</c>:124-271).
/// </summary>
public sealed record DiscoverReport
{
    /// <summary>How many session-transcript files were scanned.</summary>
    public required int SessionsScanned { get; init; }

    /// <summary>The total number of (chain-split) Bash commands examined across all scanned sessions.</summary>
    public required int TotalCommands { get; init; }

    /// <summary>How many of <see cref="TotalCommands"/> already invoked <c>rtk</c> directly.</summary>
    public required int AlreadyRtk { get; init; }

    /// <summary>The <c>--since</c> day window used for this scan.</summary>
    public required ulong SinceDays { get; init; }

    /// <summary>Commands rtk already handles, sorted by estimated savings descending.</summary>
    public required IReadOnlyList<SupportedEntry> Supported { get; init; }

    /// <summary>Commands rtk does not yet handle, sorted by occurrence count descending.</summary>
    public required IReadOnlyList<UnsupportedEntry> Unsupported { get; init; }

    /// <summary>How many session files failed to parse and were skipped.</summary>
    public required int ParseErrors { get; init; }

    /// <summary>How many commands ran with an <c>RTK_DISABLED=</c> env-prefix bypass on an otherwise-supported command.</summary>
    public required int RtkDisabledCount { get; init; }

    /// <summary>Up to 5 example commands that used the <c>RTK_DISABLED=</c> bypass, formatted as <c>"cmd (Nx)"</c>, most frequent first.</summary>
    public required IReadOnlyList<string> RtkDisabledExamples { get; init; }

    /// <summary>Which third-party agent integrations were detected on this machine.</summary>
    public required AgentIntegrationStatus AgentStatus { get; init; }

    /// <summary>
    /// The total estimated tokens saveable across every supported entry. Faithful port of
    /// <c>DiscoverReport::total_saveable_tokens</c> (<c>report.rs</c>:111-116).
    /// </summary>
    /// <returns>The summed <see cref="SupportedEntry.EstimatedSavingsTokens"/> across <see cref="Supported"/>.</returns>
    public int TotalSaveableTokens() => Supported.Sum(s => s.EstimatedSavingsTokens);

    /// <summary>
    /// The total occurrence count across every supported entry. Faithful port of
    /// <c>DiscoverReport::total_supported_count</c> (<c>report.rs</c>:118-120).
    /// </summary>
    /// <returns>The summed <see cref="SupportedEntry.Count"/> across <see cref="Supported"/>.</returns>
    public int TotalSupportedCount() => Supported.Sum(s => s.Count);
}

/// <summary>
/// Renders a <see cref="DiscoverReport"/> as human-readable text or JSON. Faithful port of Rust
/// <c>discover::report::format_text</c>/<c>format_json</c> (<c>report.rs</c>:124-247).
/// </summary>
public static class DiscoverReportFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly DiscoverReportJsonContext JsonContext = new(JsonOptions);

    /// <summary>
    /// Renders the report as human-readable text. Faithful port of Rust <c>format_text</c>
    /// (<c>report.rs</c>:124-228).
    /// </summary>
    /// <param name="report">The report to render.</param>
    /// <param name="limit">The maximum number of rows to print per section.</param>
    /// <param name="verbose">Whether to append the parse-error count footer.</param>
    /// <returns>The rendered text report.</returns>
    public static string FormatText(DiscoverReport report, int limit, bool verbose)
    {
        var sb = new StringBuilder(2048);

        sb.Append("RTK Discover -- Savings Opportunities\n");
        sb.Append(new string('=', 52)).Append('\n');
        sb.Append(
            $"Scanned: {report.SessionsScanned} sessions (last {report.SinceDays} days), {report.TotalCommands} Bash commands\n");
        var alreadyRtkPct = report.TotalCommands > 0
            ? report.AlreadyRtk * 100.0 / report.TotalCommands
            : 0.0;
        sb.Append($"Already using RTK: {report.AlreadyRtk} commands ({alreadyRtkPct.ToString("F1", CultureInfo.InvariantCulture)}%)\n");

        if (report.Supported.Count == 0 && report.Unsupported.Count == 0)
        {
            sb.Append("\nNo missed savings found. RTK usage looks good!\n");
            AppendAgentNotes(sb, report.AgentStatus);
            return sb.ToString();
        }

        if (report.Supported.Count > 0)
        {
            sb.Append("\nMISSED SAVINGS -- Commands RTK already handles\n");
            sb.Append(new string('-', 72)).Append('\n');
            sb.Append(FormatRow("Command", 24)).Append(' ').Append(FormatRowRight("Count", 5))
                .Append("    ").Append(FormatRow("RTK Equivalent", 18)).Append(' ')
                .Append(FormatRow("Status", 13)).Append(' ').Append(FormatRowRight("Est. Savings", 12)).Append('\n');

            foreach (var entry in report.Supported.Take(limit))
            {
                sb.Append(FormatRow(TruncateStr(entry.Command, 23), 24)).Append(' ')
                    .Append(FormatRowRight(entry.Count.ToString(CultureInfo.InvariantCulture), 5)).Append("    ")
                    .Append(FormatRow(entry.RtkEquivalent, 18)).Append(' ')
                    .Append(FormatRow(entry.RtkStatus.AsStr(), 13)).Append(' ')
                    .Append('~').Append(FormatTokens(entry.EstimatedSavingsTokens)).Append('\n');
            }

            sb.Append(new string('-', 72)).Append('\n');
            sb.Append($"Total: {report.TotalSupportedCount()} commands -> ~{FormatTokens(report.TotalSaveableTokens())} saveable\n");
        }

        if (report.Unsupported.Count > 0)
        {
            sb.Append("\nTOP UNHANDLED COMMANDS -- open an issue?\n");
            sb.Append(new string('-', 52)).Append('\n');
            sb.Append(FormatRow("Command", 24)).Append(' ').Append(FormatRowRight("Count", 5))
                .Append("    ").Append("Example").Append('\n');

            foreach (var entry in report.Unsupported.Take(limit))
            {
                sb.Append(FormatRow(TruncateStr(entry.BaseCommand, 23), 24)).Append(' ')
                    .Append(FormatRowRight(entry.Count.ToString(CultureInfo.InvariantCulture), 5)).Append("    ")
                    .Append(TruncateStr(entry.Example, 40)).Append('\n');
            }

            sb.Append(new string('-', 52)).Append('\n');
            sb.Append("-> github.com/rtk-ai/rtk/issues\n");
        }

        if (report.RtkDisabledCount > 0)
        {
            sb.Append($"\nRTK_DISABLED BYPASS -- {report.RtkDisabledCount} commands ran without filtering\n");
            sb.Append(new string('-', 72)).Append('\n');
            sb.Append("These commands used RTK_DISABLED=1 unnecessarily:\n");
            if (report.RtkDisabledExamples.Count > 0)
            {
                sb.Append("  ").Append(string.Join(", ", report.RtkDisabledExamples)).Append('\n');
            }

            sb.Append("-> Remove RTK_DISABLED=1 to recover token savings\n");
        }

        sb.Append("\n~estimated from tool_result output sizes\n");

        AppendAgentNotes(sb, report.AgentStatus);

        if (verbose && report.ParseErrors > 0)
        {
            sb.Append($"Parse errors skipped: {report.ParseErrors}\n");
        }

        return sb.ToString();
    }

    private static void AppendAgentNotes(StringBuilder sb, AgentIntegrationStatus status)
    {
        if (status.CursorHookInstalled)
        {
            sb.Append("\nNote: Cursor sessions are tracked via `rtk gain` (discover scans Claude Code only)\n");
        }

        if (status.HermesPluginInstalled)
        {
            sb.Append("\nNote: Hermes plugin is installed; Hermes sessions are tracked via `rtk gain` (discover scans Claude Code only)\n");
        }

        if (status.CopilotHookInstalled)
        {
            sb.Append("\nNote: GitHub Copilot sessions are tracked via `rtk gain` (discover scans Claude Code only)\n");
        }
    }

    /// <summary>
    /// Renders the report as pretty-printed JSON. Faithful port of Rust <c>format_json</c>
    /// (<c>report.rs</c>:245-247).
    /// </summary>
    /// <param name="report">The report to render.</param>
    /// <returns>The pretty-printed JSON document.</returns>
    public static string FormatJson(DiscoverReport report)
    {
        try
        {
            return JsonSerializer.Serialize(ToDto(report), JsonContext.DiscoverReportDto)
                .Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return "{}";
        }
    }

    private static DiscoverReportDto ToDto(DiscoverReport report) => new()
    {
        SessionsScanned = report.SessionsScanned,
        TotalCommands = report.TotalCommands,
        AlreadyRtk = report.AlreadyRtk,
        SinceDays = report.SinceDays,
        Supported = report.Supported.Select(s => new SupportedEntryDto
        {
            Command = s.Command,
            Count = s.Count,
            RtkEquivalent = s.RtkEquivalent,
            Category = s.Category,
            EstimatedSavingsTokens = s.EstimatedSavingsTokens,
            EstimatedSavingsPct = s.EstimatedSavingsPct,
            RtkStatus = s.RtkStatus.ToString(),
        }).ToList(),
        Unsupported = report.Unsupported.Select(u => new UnsupportedEntryDto
        {
            BaseCommand = u.BaseCommand,
            Count = u.Count,
            Example = u.Example,
        }).ToList(),
        ParseErrors = report.ParseErrors,
        RtkDisabledCount = report.RtkDisabledCount,
        RtkDisabledExamples = report.RtkDisabledExamples,
        AgentStatus = new AgentIntegrationStatusDto
        {
            CursorHookInstalled = report.AgentStatus.CursorHookInstalled,
            HermesPluginInstalled = report.AgentStatus.HermesPluginInstalled,
            CopilotHookInstalled = report.AgentStatus.CopilotHookInstalled,
        },
    };

    /// <summary>
    /// Formats a token count with a magnitude suffix. Faithful port of Rust <c>format_tokens</c>
    /// (<c>report.rs</c>:249-257).
    /// </summary>
    /// <param name="tokens">The token count to format.</param>
    /// <returns>e.g. <c>"1.2M tokens"</c>, <c>"3.4K tokens"</c>, or <c>"42 tokens"</c>.</returns>
    internal static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{(tokens / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture)}M tokens";
        }

        if (tokens >= 1_000)
        {
            return $"{(tokens / 1_000.0).ToString("F1", CultureInfo.InvariantCulture)}K tokens";
        }

        return $"{tokens} tokens";
    }

    /// <summary>
    /// Truncates a string to at most <paramref name="max"/> UTF-8 bytes, Unicode-scalar-safe.
    /// Faithful port of Rust <c>truncate_str</c> (<c>report.rs</c>:259-271): the length check is
    /// UTF-8-byte-based (matching Rust's <c>str::len()</c>), while the truncation walk is
    /// Unicode-scalar (Rune) based (matching Rust's <c>char_indices()</c>).
    /// </summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="max">The maximum UTF-8 byte length before truncation kicks in.</param>
    /// <returns><paramref name="s"/> unchanged if its UTF-8 byte length is at most <paramref name="max"/>; otherwise a byte-budget-limited prefix followed by <c>".."</c>.</returns>
    internal static string TruncateStr(string s, int max)
    {
        if (Encoding.UTF8.GetByteCount(s) <= max)
        {
            return s;
        }

        var budget = max >= 2 ? max - 2 : 0;
        var sb = new StringBuilder();
        var byteOffset = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            if (byteOffset >= budget)
            {
                break;
            }

            sb.Append(rune.ToString());
            byteOffset += rune.Utf8SequenceLength;
        }

        return sb.Append("..").ToString();
    }

    private static string FormatRow(string s, int width) => s.PadRight(width);

    private static string FormatRowRight(string s, int width) => s.PadLeft(width);
}

// -----------------------------------------------------------------------
// JSON DTOs (mirrors serde's field-name-as-written-in-source-order serialization; no
// #[serde(rename_all)] attribute appears on any of report.rs's types, so JSON keys equal the
// snake_case Rust field names verbatim, and RtkStatus - which likewise has no rename attribute -
// serializes as its bare PascalCase variant name, e.g. "Existing").
// -----------------------------------------------------------------------

internal sealed class DiscoverReportDto
{
    [JsonPropertyName("sessions_scanned")]
    public required int SessionsScanned { get; init; }

    [JsonPropertyName("total_commands")]
    public required int TotalCommands { get; init; }

    [JsonPropertyName("already_rtk")]
    public required int AlreadyRtk { get; init; }

    [JsonPropertyName("since_days")]
    public required ulong SinceDays { get; init; }

    [JsonPropertyName("supported")]
    public required List<SupportedEntryDto> Supported { get; init; }

    [JsonPropertyName("unsupported")]
    public required List<UnsupportedEntryDto> Unsupported { get; init; }

    [JsonPropertyName("parse_errors")]
    public required int ParseErrors { get; init; }

    [JsonPropertyName("rtk_disabled_count")]
    public required int RtkDisabledCount { get; init; }

    [JsonPropertyName("rtk_disabled_examples")]
    public required IReadOnlyList<string> RtkDisabledExamples { get; init; }

    [JsonPropertyName("agent_status")]
    public required AgentIntegrationStatusDto AgentStatus { get; init; }
}

internal sealed class SupportedEntryDto
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("rtk_equivalent")]
    public required string RtkEquivalent { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("estimated_savings_tokens")]
    public required int EstimatedSavingsTokens { get; init; }

    [JsonPropertyName("estimated_savings_pct")]
    [JsonConverter(typeof(SerdeDoubleConverter))]
    public required double EstimatedSavingsPct { get; init; }

    [JsonPropertyName("rtk_status")]
    public required string RtkStatus { get; init; }
}

internal sealed class UnsupportedEntryDto
{
    [JsonPropertyName("base_command")]
    public required string BaseCommand { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("example")]
    public required string Example { get; init; }
}

internal sealed class AgentIntegrationStatusDto
{
    [JsonPropertyName("cursor_hook_installed")]
    public required bool CursorHookInstalled { get; init; }

    [JsonPropertyName("hermes_plugin_installed")]
    public required bool HermesPluginInstalled { get; init; }

    [JsonPropertyName("copilot_hook_installed")]
    public required bool CopilotHookInstalled { get; init; }
}

/// <summary>
/// Forces every <see cref="double"/> to serialize the way Rust's <c>serde_json</c> does: the
/// shortest round-trippable decimal representation, always including a decimal point (so an exact
/// whole number like <c>70.0</c> serializes as <c>"70.0"</c>, never bare <c>"70"</c>). Mirrors the
/// identical converter in <c>RtkSharp.Commands.Analytics.GainCommand</c> — duplicated rather than
/// shared because that file is off-limits for this port (a parallel worktree's territory) and this
/// is a small, self-contained, dependency-free utility.
/// </summary>
internal sealed class SerdeDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteRawValue(FormatSerdeFloat(value));

    private static string FormatSerdeFloat(double value)
    {
        var s = value.ToString(CultureInfo.InvariantCulture);
        return s.IndexOfAny(['.', 'e', 'E']) < 0 ? s + ".0" : s;
    }
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <c>rtk discover --format json</c>'s
/// output shape, avoiding reflection-based serialization under <c>PublishAot=true</c> (mirrors the
/// approach <c>GainCommand</c> and <c>Config</c> already use).
/// </summary>
[JsonSerializable(typeof(DiscoverReportDto))]
[JsonSerializable(typeof(SupportedEntryDto))]
[JsonSerializable(typeof(UnsupportedEntryDto))]
[JsonSerializable(typeof(AgentIntegrationStatusDto))]
internal sealed partial class DiscoverReportJsonContext : JsonSerializerContext;
