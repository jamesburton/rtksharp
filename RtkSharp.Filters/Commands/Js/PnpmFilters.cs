using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Core;
using RtkSharp.Parser;

namespace RtkSharp.Filters.Commands.Js;

/// <summary>
/// Pure output-filtering logic for the <c>rtk pnpm</c> CLI proxy: <c>list</c>/<c>outdated</c>/
/// <c>install</c>. Extracted from <c>RtkSharp.Commands.Js.PnpmCommand</c> (Task 7 of the
/// filters-library extraction) — everything here is a pure function of already-captured text/data,
/// with no process execution or file I/O.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="FormatPnpmList"/>/<see cref="FormatPnpmOutdated"/> are the pure halves of
/// <c>PnpmCommand</c>'s <c>LogAndFormatList</c>/<c>LogAndFormatOutdated</c>.</b> Both original methods
/// mixed a verbosity-gated <c>Console.Error.Write</c> diagnostic (or
/// <c>OutputParserSupport.EmitDegradationWarning</c> call) with a trailing pure formatting
/// expression. The diagnostic half stays in <c>PnpmCommand</c>'s own <c>LogAndFormatList</c>/
/// <c>LogAndFormatOutdated</c>, which now call these two methods for the formatting and then perform
/// their own <c>Console.Error.Write</c>.
/// </para>
/// <para>
/// <b><c>pnpm list</c> bypasses the shared <see cref="DependencyState.Format"/> formatter.</b>
/// <see cref="FormatDependencyListing"/> is a bespoke prod/dev-grouped renderer
/// (<c>format_dependency_listing</c>, <c>pnpm_cmd.rs</c>:292-347) used only by <c>list</c>; <c>outdated</c>
/// (<see cref="FormatPnpmOutdated"/>) uses the shared <see cref="ITokenFormatter"/>-based formatter
/// instead. This Rust-source inconsistency is preserved deliberately, not "fixed" toward uniformity.
/// </para>
/// </remarks>
public static partial class PnpmFilters
{
    /// <summary>
    /// Maximum dependencies shown per <c>[prod]</c>/<c>[dev]</c> section in
    /// <see cref="FormatDependencyListing"/> before a truncation marker + tee hint. Bound to Rust's
    /// <c>CAP_LIST</c> (<c>src/core/truncate.rs</c>:9, = 20), matching <c>MAX_LISTING</c>
    /// (<c>pnpm_cmd.rs</c>:17).
    /// </summary>
    public const int MaxListing = 20;

    // -----------------------------------------------------------------------
    // filter_pnpm_install (pnpm_cmd.rs:501-537)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>pnpm install</c> output: strips progress-bar lines (containing <c>Progress</c>,
    /// <c>│</c>, or <c>%</c>) and any blank line immediately following one, while keeping
    /// error/<c>ERR</c>/<c>ERROR</c> lines and summary lines (containing <c>packages in</c> or
    /// <c>dependencies</c>, or starting with <c>+</c>/<c>-</c>). Faithful port of
    /// <c>filter_pnpm_install</c> (<c>pnpm_cmd.rs</c>:501-537).
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>pnpm install</c>.</param>
    /// <returns>The filtered output, or the literal <c>"ok"</c> if nothing survived.</returns>
    public static string FilterPnpmInstall(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new List<string>();
        var sawProgress = false;

        foreach (var line in SourceFilterLineSplitter.SplitLines(output))
        {
            if (line.Contains("Progress", StringComparison.Ordinal) || line.Contains('│') || line.Contains('%'))
            {
                sawProgress = true;
                continue;
            }

            if (sawProgress && line.Trim().Length == 0)
            {
                continue;
            }

            if (line.Contains("ERR", StringComparison.Ordinal)
                || line.Contains("error", StringComparison.Ordinal)
                || line.Contains("ERROR", StringComparison.Ordinal))
            {
                result.Add(line);
                continue;
            }

            if (line.Contains("packages in", StringComparison.Ordinal)
                || line.Contains("dependencies", StringComparison.Ordinal)
                || line.StartsWith('+')
                || line.StartsWith('-'))
            {
                result.Add(line.Trim());
            }
        }

        return result.Count == 0 ? "ok" : string.Join('\n', result);
    }

    // -----------------------------------------------------------------------
    // Pure halves of LogAndFormatList/LogAndFormatOutdated (pnpm_cmd.rs:364-467)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Formats a parsed <c>pnpm list</c> dependency tree for stdout. The pure half of
    /// <c>PnpmCommand.LogAndFormatList</c> — see class remarks.
    /// </summary>
    /// <param name="data">The parsed dependency state.</param>
    /// <param name="isFiltered">
    /// True when the invocation already carried an explicit <c>--prod</c>/<c>-P</c>/<c>--dev</c>/
    /// <c>-D</c> flag - the listing is never capped in that case (the user already narrowed it
    /// themselves).
    /// </param>
    /// <returns>The formatted listing.</returns>
    public static string FormatPnpmList(DependencyState data, bool isFiltered) =>
        FormatDependencyListing(data, cap: !isFiltered);

    /// <summary>
    /// Formats a parsed <c>pnpm outdated</c> result for stdout via the shared
    /// <see cref="ITokenFormatter"/>-based formatter. The pure half of
    /// <c>PnpmCommand.LogAndFormatOutdated</c> — see class remarks.
    /// </summary>
    /// <param name="data">The parsed dependency state.</param>
    /// <param name="mode">The resolved format mode (scales with verbosity).</param>
    /// <returns>The formatted outdated report.</returns>
    public static string FormatPnpmOutdated(DependencyState data, FormatMode mode)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ((ITokenFormatter)data).Format(mode);
    }

    // -----------------------------------------------------------------------
    // format_dependency_listing (pnpm_cmd.rs:292-347) - bespoke, NOT the shared TokenFormatter
    // -----------------------------------------------------------------------

    /// <summary>
    /// Formats a dependency listing with grouped <c>[prod]</c>/<c>[dev]</c> sections. Faithful port of
    /// <c>format_dependency_listing</c> (<c>pnpm_cmd.rs</c>:292-347): each section is capped and
    /// tee-hinted independently, only when <paramref name="cap"/> is true.
    /// </summary>
    /// <param name="state">The parsed dependency state.</param>
    /// <param name="cap">
    /// True for a plain <c>pnpm list</c> (both categories present, may truncate); false for
    /// <c>pnpm list --prod</c>/<c>--dev</c> (the user already narrowed the listing, so every package
    /// must be shown).
    /// </param>
    /// <returns>The formatted listing.</returns>
    public static string FormatDependencyListing(DependencyState state, bool cap)
    {
        ArgumentNullException.ThrowIfNull(state);

        var prod = state.Dependencies.Where(d => !d.DevDependency).ToList();
        var dev = state.Dependencies.Where(d => d.DevDependency).ToList();
        var total = Math.Max(state.TotalPackages, state.Dependencies.Count);

        var lines = new List<string> { $"{total} packages ({prod.Count} prod / {dev.Count} dev)" };

        AppendListingSection(lines, "[prod]", prod, cap, "pnpm-prod");
        AppendListingSection(lines, "[dev]", dev, cap, "pnpm-dev");

        return string.Join('\n', lines);
    }

    private static void AppendListingSection(List<string> lines, string header, List<Dependency> deps, bool cap, string teeSlug)
    {
        if (deps.Count == 0)
        {
            return;
        }

        lines.Add(header);

        var shown = cap ? Math.Min(deps.Count, MaxListing) : deps.Count;
        foreach (var dep in deps.Take(shown))
        {
            lines.Add($"  {dep.Name} {dep.CurrentVersion}");
        }

        if (cap && deps.Count > MaxListing)
        {
            lines.Add($"  … +{deps.Count - MaxListing} more");

            var allEntries = string.Join('\n', deps.Select(d => $"  {d.Name} {d.CurrentVersion}"));
            var hint = Tee.ForceTeeTailHint(allEntries, teeSlug, MaxListing + 1);
            if (hint is not null)
            {
                lines.Add($"  {hint}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Parsers (Task 1's OutputParser<T> abstraction over DependencyState)
    // -----------------------------------------------------------------------

    /// <summary>
    /// JSON shape of a single dependency-tree package node in <c>pnpm list --json</c> output,
    /// recursive through <c>dependencies</c>/<c>devDependencies</c>. Faithful port of
    /// <c>PackageJsonListItem</c> (<c>pnpm_cmd.rs</c>:27-34).
    /// </summary>
    private sealed class PnpmListPackage
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("dependencies")]
        public Dictionary<string, PnpmListPackage>? Dependencies { get; set; }

        [JsonPropertyName("devDependencies")]
        public Dictionary<string, PnpmListPackage>? DevDependencies { get; set; }
    }

    /// <summary>
    /// JSON shape of a top-level workspace entry in <c>pnpm list --json</c> output. Faithful port of
    /// <c>PnpmListOutput</c> (<c>pnpm_cmd.rs</c>:20-25) — unlike <see cref="PnpmListPackage"/>, this
    /// carries a required <c>name</c> field (matching Rust's non-<c>Option</c> <c>name: String</c>,
    /// which fails deserialization of the whole array when absent).
    /// </summary>
    private sealed class PnpmListEntry
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("dependencies")]
        public Dictionary<string, PnpmListPackage>? Dependencies { get; set; }

        [JsonPropertyName("devDependencies")]
        public Dictionary<string, PnpmListPackage>? DevDependencies { get; set; }
    }

    /// <summary>
    /// Parser for <c>pnpm list --json</c> output. Faithful port of <c>PnpmListParser</c>
    /// (<c>pnpm_cmd.rs</c>:52-97): tier 1 recursively walks the JSON dependency tree; tier 2
    /// (<see cref="ExtractListText"/>) regex/text-scans the human-readable tree output.
    /// </summary>
    public sealed class PnpmListParser : OutputParser<DependencyState>
    {
        /// <inheritdoc/>
        protected override DependencyState? TryFull(string input)
        {
            List<PnpmListEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize(input, PnpmJsonContext.Default.ListPnpmListEntry);
            }
            catch (JsonException)
            {
                return null;
            }

            if (entries is null)
            {
                return null;
            }

            var dependencies = new List<Dependency>();
            var totalCount = 0;

            foreach (var entry in entries)
            {
                CollectDependencies(entry.Name, entry.Version, entry.Dependencies, entry.DevDependencies, isDev: false, dependencies, ref totalCount);
            }

            return new DependencyState { TotalPackages = totalCount, OutdatedCount = 0, Dependencies = dependencies };
        }

        /// <inheritdoc/>
        protected override (DependencyState Data, IReadOnlyList<string> Warnings)? TryDegraded(string input)
        {
            var extracted = ExtractListText(input);
            return extracted is null ? null : (extracted, new[] { "JSON parse failed" });
        }

        /// <summary>
        /// Recursively collects dependencies from a pnpm package tree node. Faithful port of
        /// <c>collect_dependencies</c> (<c>pnpm_cmd.rs</c>:100-125): entries reached through
        /// <c>devDependencies</c> are always marked dev, regardless of the parent's own
        /// <paramref name="isDev"/> flag.
        /// </summary>
        private static void CollectDependencies(
            string name,
            string? version,
            Dictionary<string, PnpmListPackage>? dependencies,
            Dictionary<string, PnpmListPackage>? devDependencies,
            bool isDev,
            List<Dependency> output,
            ref int count)
        {
            if (version is not null)
            {
                output.Add(new Dependency { Name = name, CurrentVersion = version, DevDependency = isDev });
                count++;
            }

            if (dependencies is not null)
            {
                foreach (var (depName, depPkg) in dependencies)
                {
                    CollectDependencies(depName, depPkg.Version, depPkg.Dependencies, depPkg.DevDependencies, isDev, output, ref count);
                }
            }

            if (devDependencies is not null)
            {
                foreach (var (depName, depPkg) in devDependencies)
                {
                    CollectDependencies(depName, depPkg.Version, depPkg.Dependencies, depPkg.DevDependencies, true, output, ref count);
                }
            }
        }

        /// <summary>
        /// Tier 2: extracts a dependency listing from pnpm's human-readable tree output. Faithful port
        /// of <c>extract_list_text</c> (<c>pnpm_cmd.rs</c>:127-185).
        /// </summary>
        private static DependencyState? ExtractListText(string output)
        {
            var dependencies = new List<Dependency>();
            var count = 0;
            var isDev = false;

            foreach (var line in SourceFilterLineSplitter.SplitLines(output))
            {
                var trimmed = line.Trim();

                if (trimmed == "devDependencies:")
                {
                    isDev = true;
                    continue;
                }

                if (trimmed == "dependencies:")
                {
                    isDev = false;
                    continue;
                }

                if (line.Contains('│') || line.Contains('├') || line.Contains('└')
                    || line.Contains("Legend:", StringComparison.Ordinal) || trimmed.Length == 0)
                {
                    continue;
                }

                var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                var pkgStr = parts[0];
                var atPos = pkgStr.LastIndexOf('@');
                if (atPos < 0)
                {
                    continue;
                }

                var name = pkgStr[..atPos];
                var version = pkgStr[(atPos + 1)..];
                if (name.Length == 0 || version.Length == 0)
                {
                    continue;
                }

                dependencies.Add(new Dependency { Name = name, CurrentVersion = version, DevDependency = isDev });
                count++;
            }

            return count > 0
                ? new DependencyState { TotalPackages = count, OutdatedCount = 0, Dependencies = dependencies }
                : null;
        }
    }

    /// <summary>
    /// JSON shape of a single package entry in <c>pnpm outdated --format json</c> output. Faithful port
    /// of <c>PnpmOutdatedPackage</c> (<c>pnpm_cmd.rs</c>:43-50).
    /// </summary>
    private sealed class PnpmOutdatedPackage
    {
        [JsonPropertyName("current")]
        public required string Current { get; set; }

        [JsonPropertyName("latest")]
        public required string Latest { get; set; }

        [JsonPropertyName("wanted")]
        public string? Wanted { get; set; }

        [JsonPropertyName("dependencyType")]
        public string DependencyType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Parser for <c>pnpm outdated --format json</c> output. Faithful port of <c>PnpmOutdatedParser</c>
    /// (<c>pnpm_cmd.rs</c>:187-236): tier 1 parses the current/latest/wanted JSON map; tier 2
    /// (<see cref="ExtractOutdatedText"/>) regex/text-scans the human-readable table output.
    /// </summary>
    public sealed class PnpmOutdatedParser : OutputParser<DependencyState>
    {
        /// <inheritdoc/>
        protected override DependencyState? TryFull(string input)
        {
            Dictionary<string, PnpmOutdatedPackage>? packages;
            try
            {
                packages = JsonSerializer.Deserialize(input, PnpmJsonContext.Default.DictionaryStringPnpmOutdatedPackage);
            }
            catch (JsonException)
            {
                return null;
            }

            if (packages is null)
            {
                return null;
            }

            var dependencies = new List<Dependency>();
            var outdatedCount = 0;

            foreach (var (name, pkg) in packages)
            {
                if (pkg.Current != pkg.Latest)
                {
                    outdatedCount++;
                }

                dependencies.Add(new Dependency
                {
                    Name = name,
                    CurrentVersion = pkg.Current,
                    LatestVersion = pkg.Latest,
                    WantedVersion = pkg.Wanted,
                    DevDependency = pkg.DependencyType == "devDependencies",
                });
            }

            return new DependencyState { TotalPackages = dependencies.Count, OutdatedCount = outdatedCount, Dependencies = dependencies };
        }

        /// <inheritdoc/>
        protected override (DependencyState Data, IReadOnlyList<string> Warnings)? TryDegraded(string input)
        {
            var extracted = ExtractOutdatedText(input);
            return extracted is null ? null : (extracted, new[] { "JSON parse failed" });
        }

        /// <summary>
        /// Tier 2: extracts current/wanted/latest columns from pnpm's human-readable outdated table.
        /// Faithful port of <c>extract_outdated_text</c> (<c>pnpm_cmd.rs</c>:238-286).
        /// </summary>
        private static DependencyState? ExtractOutdatedText(string output)
        {
            var dependencies = new List<Dependency>();
            var outdatedCount = 0;

            foreach (var line in SourceFilterLineSplitter.SplitLines(output))
            {
                if (line.Contains('│') || line.Contains('├') || line.Contains('└') || line.Contains('─')
                    || line.StartsWith("Legend:", StringComparison.Ordinal)
                    || line.StartsWith("Package", StringComparison.Ordinal)
                    || line.Trim().Length == 0)
                {
                    continue;
                }

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                {
                    continue;
                }

                var name = parts[0];
                var current = parts[1];
                var latest = parts[3];

                if (current != latest)
                {
                    outdatedCount++;
                }

                dependencies.Add(new Dependency
                {
                    Name = name,
                    CurrentVersion = current,
                    LatestVersion = latest,
                    WantedVersion = parts[2],
                    DevDependency = false,
                });
            }

            return dependencies.Count > 0
                ? new DependencyState { TotalPackages = dependencies.Count, OutdatedCount = outdatedCount, Dependencies = dependencies }
                : null;
        }
    }

    /// <summary>
    /// Source-generated JSON metadata for pnpm's <c>list</c>/<c>outdated</c> JSON shapes, required
    /// because <c>RtkSharp.csproj</c> publishes with <c>PublishAot=true</c> — reflection-based
    /// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
    [JsonSerializable(typeof(List<PnpmListEntry>))]
    [JsonSerializable(typeof(Dictionary<string, PnpmOutdatedPackage>))]
    private sealed partial class PnpmJsonContext : JsonSerializerContext;
}
