namespace RtkSharp.Parser;

/// <summary>
/// Canonical dependency-listing/outdated-check state (pnpm, npm, cargo, etc.). Faithful port of
/// Rust <c>DependencyState</c> (<c>src/parser/types.rs:24-30</c>), including its
/// <c>TokenFormatter</c> implementation (<c>src/parser/formatter.rs:122-206</c>).
/// </summary>
/// <remarks>
/// <b>Not used by <c>pnpm list</c>.</b> Per Rust's own comment on <c>format_compact</c> below, a
/// plain package listing (<c>pnpm list</c> / <c>npm ls</c>) deliberately does <b>not</b> route
/// through this shared type — it has its own bespoke prod/dev-grouped formatter (Task 3, built
/// there, not here). This type is used by <c>pnpm outdated</c>, which does carry genuine
/// current/latest/wanted version comparison data.
/// </remarks>
public sealed class DependencyState : ITokenFormatter
{
    /// <summary>
    /// Maximum dependencies individually listed in <see cref="FormatCompact"/>'s plain-listing
    /// branch before an overflow marker. Bound to <c>CAP_INVENTORY</c> (= 50) from
    /// <c>src/core/truncate.rs</c>, matching Rust's <c>MAX_DEPS_LISTING</c> constant
    /// (<c>src/parser/formatter.rs:3-5</c>). Following this project's established convention (no
    /// shared constants class), the value is defined locally here rather than referencing a
    /// central cap type.
    /// </summary>
    private const int CapInventory = 50;

    /// <summary>Maximum outdated packages individually listed in <see cref="FormatCompact"/> before an overflow marker.</summary>
    private const int MaxCompactOutdatedListing = 10;

    /// <summary>Total number of packages known.</summary>
    public required int TotalPackages { get; init; }

    /// <summary>Number of packages that are outdated (have a newer version available).</summary>
    public required int OutdatedCount { get; init; }

    /// <summary>The individual dependency entries.</summary>
    public IReadOnlyList<Dependency> Dependencies { get; init; } = [];

    /// <summary>
    /// Compact rendering. A plain package listing (every dependency's <see cref="Dependency.LatestVersion"/>
    /// is <see langword="null"/>, and <see cref="OutdatedCount"/> is 0) renders the actual package
    /// list (up to <see cref="CapInventory"/> entries plus an overflow marker) rather than the
    /// generic "all up-to-date" message — reporting "up-to-date" there would be a false positive
    /// that hides the entire list. Otherwise renders an outdated-count summary followed by up to
    /// <see cref="MaxCompactOutdatedListing"/> individual upgrade lines and an overflow marker.
    /// </summary>
    /// <returns>The compact rendering.</returns>
    public string FormatCompact()
    {
        var isListing = OutdatedCount == 0
            && Dependencies.Count > 0
            && Dependencies.All(d => d.LatestVersion is null);

        if (isListing)
        {
            var total = Math.Max(TotalPackages, Dependencies.Count);
            var lines = new List<string> { $"{total} packages" };
            foreach (var dep in Dependencies.Take(CapInventory))
            {
                var dev = dep.DevDependency ? " (dev)" : "";
                lines.Add($"  {dep.Name} {dep.CurrentVersion}{dev}");
            }

            if (Dependencies.Count > CapInventory)
            {
                lines.Add($"  ... +{Dependencies.Count - CapInventory} more");
            }

            return string.Join('\n', lines);
        }

        if (OutdatedCount == 0)
        {
            return "All packages up-to-date";
        }

        var summaryLines = new List<string> { $"{OutdatedCount} outdated packages (of {TotalPackages})" };

        foreach (var dep in Dependencies.Take(MaxCompactOutdatedListing))
        {
            if (dep.LatestVersion is { } latest && dep.CurrentVersion != latest)
            {
                summaryLines.Add($"{dep.Name}: {dep.CurrentVersion} → {latest}");
            }
        }

        if (OutdatedCount > MaxCompactOutdatedListing)
        {
            summaryLines.Add($"\n... +{OutdatedCount - MaxCompactOutdatedListing} more");
        }

        return string.Join('\n', summaryLines);
    }

    /// <summary>
    /// Verbose rendering: total/outdated summary, then (if any are outdated) every outdated
    /// dependency with its current/latest/dev-marker and an optional wanted-version note when it
    /// differs from latest.
    /// </summary>
    /// <returns>The verbose rendering.</returns>
    public string FormatVerbose()
    {
        var lines = new List<string> { $"Total packages: {TotalPackages} ({OutdatedCount} outdated)" };

        if (OutdatedCount > 0)
        {
            lines.Add("\nOutdated packages:");
            foreach (var dep in Dependencies)
            {
                if (dep.LatestVersion is { } latest && dep.CurrentVersion != latest)
                {
                    var devMarker = dep.DevDependency ? " (dev)" : "";
                    lines.Add($"  {dep.Name}: {dep.CurrentVersion} → {latest}{devMarker}");
                    if (dep.WantedVersion is { } wanted && wanted != latest)
                    {
                        lines.Add($"    (wanted: {wanted})");
                    }
                }
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>Ultra-compact rendering: <c>pkg:N ^N</c> (total packages, outdated count).</summary>
    /// <returns>The ultra rendering.</returns>
    public string FormatUltra() => $"pkg:{TotalPackages} ^{OutdatedCount}";
}

/// <summary>
/// A single dependency's version-comparison state. Faithful port of Rust <c>Dependency</c>
/// (<c>src/parser/types.rs:32-39</c>).
/// </summary>
public sealed class Dependency
{
    /// <summary>The package name.</summary>
    public required string Name { get; init; }

    /// <summary>The currently installed version.</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>The latest available version, or <see langword="null"/> for a plain listing with no upgrade info.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>The version satisfying the declared semver range, if reported and different from latest.</summary>
    public string? WantedVersion { get; init; }

    /// <summary>Whether this is a development-only dependency.</summary>
    public required bool DevDependency { get; init; }
}
