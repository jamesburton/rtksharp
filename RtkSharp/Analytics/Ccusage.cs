using System.Text.Json.Serialization;
using RtkSharp.Execution;

namespace RtkSharp.Analytics;

/// <summary>
/// Metrics from ccusage for a single period (day/week/month). Faithful port of Rust
/// <c>CcusageMetrics</c> (<c>src/analytics/ccusage.rs:16-30</c>).
/// </summary>
/// <param name="InputTokens">Input tokens for the period (<c>inputTokens</c> in ccusage's JSON).</param>
/// <param name="OutputTokens">Output tokens for the period (<c>outputTokens</c>).</param>
/// <param name="CacheCreationTokens">Cache-write tokens for the period (<c>cacheCreationTokens</c>, defaults to 0 when absent).</param>
/// <param name="CacheReadTokens">Cache-read tokens for the period (<c>cacheReadTokens</c>, defaults to 0 when absent).</param>
/// <param name="TotalTokens">Total tokens for the period (<c>totalTokens</c>).</param>
/// <param name="TotalCost">Total USD cost for the period (<c>totalCost</c>).</param>
public sealed record CcusageMetrics(
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long TotalTokens,
    double TotalCost);

/// <summary>
/// Period data with key (date/month/week) and metrics. Faithful port of Rust <c>CcusagePeriod</c>
/// (<c>src/analytics/ccusage.rs:33-37</c>).
/// </summary>
/// <param name="Key">
/// The period key: <c>"2026-01-30"</c> (daily), <c>"2026-01"</c> (monthly), or <c>"2026-01-20"</c>
/// (weekly, ISO Monday).
/// </param>
/// <param name="Metrics">The ccusage metrics for this period.</param>
public sealed record CcusagePeriod(string Key, CcusageMetrics Metrics);

/// <summary>
/// Time granularity for ccusage reports. Faithful port of Rust <c>Granularity</c>
/// (<c>src/analytics/ccusage.rs:40-45</c>).
/// </summary>
public enum Granularity
{
    /// <summary>Daily buckets.</summary>
    Daily,

    /// <summary>Weekly buckets (ISO week, Monday-keyed).</summary>
    Weekly,

    /// <summary>Monthly buckets.</summary>
    Monthly,
}

/// <summary>
/// Parses Claude Code spending data for economics reporting. Faithful port of Rust
/// <c>src/analytics/ccusage.rs</c>: shells out to the <c>ccusage</c> npm CLI tool (falling back to
/// <c>npx --yes ccusage</c> when it isn't installed globally), parses its <c>--json</c> output, and
/// degrades gracefully (returns <see langword="null"/>, never throws) whenever the tool itself is
/// unavailable or fails to run — matching Rust's <c>Ok(None)</c> return path. A malformed JSON
/// payload from a tool that DID run successfully is treated as an unexpected failure and propagates
/// as an exception, matching Rust's <c>.context("Failed to parse ccusage JSON output")?</c>.
/// </summary>
public static class Ccusage
{
    private static readonly IProcessExecutor Executor = new ProcessExecutor();

    /// <summary>
    /// Fetches usage data from ccusage for the configured lookback window (mirrors Rust's
    /// <c>--since 20250101</c> literal). Faithful port of Rust <c>fetch</c>
    /// (<c>src/analytics/ccusage.rs:123-164</c>).
    /// </summary>
    /// <param name="granularity">The requested time granularity.</param>
    /// <param name="cancellationToken">A token to cancel the underlying process execution.</param>
    /// <returns>
    /// <see langword="null"/> if ccusage is unavailable or fails to execute (graceful degradation,
    /// with a <c>[warn]</c>/<c>[info]</c> line written to stderr); otherwise the parsed periods.
    /// </returns>
    /// <exception cref="InvalidOperationException">The tool ran successfully but its JSON output could not be parsed.</exception>
    public static async Task<IReadOnlyList<CcusagePeriod>?> FetchAsync(
        Granularity granularity, CancellationToken cancellationToken = default)
    {
        var command = await ResolveCommandAsync(cancellationToken).ConfigureAwait(false);
        if (command is null)
        {
            Console.Error.Write("[warn] ccusage not found. Install: npm i -g ccusage (or use npx ccusage)\n");
            return null;
        }

        var subcommand = granularity switch
        {
            Granularity.Daily => "daily",
            Granularity.Weekly => "weekly",
            Granularity.Monthly => "monthly",
            _ => throw new ArgumentOutOfRangeException(nameof(granularity), granularity, null),
        };

        var (fileName, baseArgs) = command.Value;
        var arguments = baseArgs.Concat([subcommand, "--json", "--since", "20250101"]).ToList();

        ExecutionResult result;
        try
        {
            result = await Executor.ExecuteAsync(new ExecutionRequest(fileName, arguments), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"[warn] ccusage execution failed: {ex.Message}\n");
            return null;
        }

        if (!result.WasStarted)
        {
            Console.Error.Write($"[warn] ccusage execution failed: {result.Failure}\n");
            return null;
        }

        if (result.ExitCode != 0)
        {
            Console.Error.Write($"[warn] ccusage exited with {result.ExitCode}: {result.Stderr.Trim()}\n");
            return null;
        }

        try
        {
            return ParseJson(result.Stdout, granularity);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse ccusage JSON output: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Resolves the ccusage invocation: the <c>ccusage</c> binary directly if it's on <c>PATH</c>,
    /// otherwise a working <c>npx --yes ccusage</c> invocation, or <see langword="null"/> if neither
    /// is available. Faithful port of Rust <c>build_command</c>
    /// (<c>src/analytics/ccusage.rs:93-116</c>).
    /// </summary>
    private static async Task<(string FileName, IReadOnlyList<string> BaseArgs)?> ResolveCommandAsync(
        CancellationToken cancellationToken)
    {
        if (ToolExists("ccusage"))
        {
            return ("ccusage", []);
        }

        Console.Error.Write("[info] ccusage not installed globally, fetching via npx...\n");

        ExecutionResult npxCheck;
        try
        {
            npxCheck = await Executor.ExecuteAsync(
                new ExecutionRequest("npx", ["--yes", "ccusage", "--help"]), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        return npxCheck.WasStarted && npxCheck.ExitCode == 0
            ? ("npx", (IReadOnlyList<string>)["--yes", "ccusage"])
            : null;
    }

    /// <summary>Checks whether <paramref name="name"/> resolves to an existing file via <c>PATH</c>.</summary>
    /// <param name="name">The executable name to look up.</param>
    /// <returns>True if the executable was found.</returns>
    private static bool ToolExists(string name)
    {
        var resolved = PathResolver.Resolve(name);
        return Path.IsPathRooted(resolved) && File.Exists(resolved);
    }

    /// <summary>
    /// Parses ccusage's <c>--json</c> output for the given granularity. Faithful port of Rust
    /// <c>parse_json</c> (<c>src/analytics/ccusage.rs:168-207</c>).
    /// </summary>
    /// <param name="json">The raw JSON text produced by ccusage.</param>
    /// <param name="granularity">The granularity that determines which top-level array/field to read.</param>
    /// <returns>The parsed periods.</returns>
    internal static IReadOnlyList<CcusagePeriod> ParseJson(string json, Granularity granularity)
    {
        switch (granularity)
        {
            case Granularity.Daily:
            {
                var resp = System.Text.Json.JsonSerializer.Deserialize(json, CcusageJsonContext.Default.DailyResponse)
                    ?? throw new InvalidOperationException("Invalid JSON structure for daily data");
                return resp.Daily.Select(e => new CcusagePeriod(e.Date, ToMetrics(e))).ToList();
            }

            case Granularity.Weekly:
            {
                var resp = System.Text.Json.JsonSerializer.Deserialize(json, CcusageJsonContext.Default.WeeklyResponse)
                    ?? throw new InvalidOperationException("Invalid JSON structure for weekly data");
                return resp.Weekly.Select(e => new CcusagePeriod(e.Week, ToMetrics(e))).ToList();
            }

            case Granularity.Monthly:
            {
                var resp = System.Text.Json.JsonSerializer.Deserialize(json, CcusageJsonContext.Default.MonthlyResponse)
                    ?? throw new InvalidOperationException("Invalid JSON structure for monthly data");
                return resp.Monthly.Select(e => new CcusagePeriod(e.Month, ToMetrics(e))).ToList();
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(granularity), granularity, null);
        }
    }

    private static CcusageMetrics ToMetrics(CcusageEntryBase entry) => new(
        entry.InputTokens,
        entry.OutputTokens,
        entry.CacheCreationTokens,
        entry.CacheReadTokens,
        entry.TotalTokens,
        entry.TotalCost);

    /// <summary>Shared fields across daily/weekly/monthly ccusage JSON entries (serde's <c>#[serde(flatten)]</c>).</summary>
    internal class CcusageEntryBase
    {
        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }

        [JsonPropertyName("cacheCreationTokens")]
        public long CacheCreationTokens { get; set; }

        [JsonPropertyName("cacheReadTokens")]
        public long CacheReadTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public long TotalTokens { get; set; }

        [JsonPropertyName("totalCost")]
        public double TotalCost { get; set; }
    }

    internal sealed class DailyEntry : CcusageEntryBase
    {
        [JsonPropertyName("date")]
        public required string Date { get; set; }
    }

    internal sealed class WeeklyEntry : CcusageEntryBase
    {
        [JsonPropertyName("week")]
        public required string Week { get; set; }
    }

    internal sealed class MonthlyEntry : CcusageEntryBase
    {
        [JsonPropertyName("month")]
        public required string Month { get; set; }
    }

    internal sealed class DailyResponse
    {
        [JsonPropertyName("daily")]
        public required List<DailyEntry> Daily { get; set; }
    }

    internal sealed class WeeklyResponse
    {
        [JsonPropertyName("weekly")]
        public required List<WeeklyEntry> Weekly { get; set; }
    }

    internal sealed class MonthlyResponse
    {
        [JsonPropertyName("monthly")]
        public required List<MonthlyEntry> Monthly { get; set; }
    }
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for ccusage's JSON response shapes,
/// required because <c>RtkSharp.csproj</c> sets <c>PublishAot=true</c>.
/// </summary>
[JsonSerializable(typeof(Ccusage.DailyResponse))]
[JsonSerializable(typeof(Ccusage.WeeklyResponse))]
[JsonSerializable(typeof(Ccusage.MonthlyResponse))]
internal sealed partial class CcusageJsonContext : JsonSerializerContext;
