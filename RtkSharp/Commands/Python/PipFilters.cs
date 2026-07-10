using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RtkSharp.Commands.Python;

/// <summary>
/// Buffered filters for <c>pip</c>/<c>uv pip</c> <c>list --format=json</c> and
/// <c>list --outdated --format=json</c> output. Faithful port of <c>filter_pip_list</c>/
/// <c>filter_pip_outdated</c> (<c>src/cmds/python/pip_cmd.rs</c>).
/// </summary>
internal static class PipFilters
{
    // Rust CAP_INVENTORY / CAP_LIST from src/core/truncate.rs (duplicated here per this codebase's
    // established convention of a private-per-file constant mirroring the Rust constant, rather than
    // a shared caps class — see RtkSharp.Filters.Commands.Rust.CargoFilters for the precedent).
    private const int CapInventory = 50;
    private const int CapList = 20;

    /// <summary>
    /// Groups installed packages by first letter (case-insensitive) for easier scanning. This is an
    /// inventory query — dependency audits need every package visible — so the per-group
    /// <see cref="CapInventory"/> cap is a safety bound for pathological environments, not a
    /// normal-case truncation. Faithful port of Rust <c>filter_pip_list</c> (<c>pip_cmd.rs</c>:142-188).
    /// </summary>
    /// <param name="output">The raw <c>pip list --format=json</c> stdout.</param>
    /// <returns>The filtered, letter-grouped summary.</returns>
    public static string FilterPipList(string output)
    {
        List<PipPackage>? packages;
        try
        {
            packages = JsonSerializer.Deserialize(output, PipJsonContext.Default.ListPipPackage);
        }
        catch (JsonException e)
        {
            return $"pip list (JSON parse failed: {e.Message})";
        }

        packages ??= [];

        if (packages.Count == 0)
        {
            return "pip list: No packages installed";
        }

        var result = new StringBuilder();
        result.Append($"pip list: {packages.Count} packages\n");

        var byLetter = new Dictionary<char, List<PipPackage>>();
        foreach (var pkg in packages)
        {
            var firstChar = pkg.Name.Length > 0 ? char.ToLowerInvariant(pkg.Name[0]) : '?';
            if (!byLetter.TryGetValue(firstChar, out var list))
            {
                list = [];
                byLetter[firstChar] = list;
            }

            list.Add(pkg);
        }

        var letters = byLetter.Keys.ToList();
        letters.Sort();

        const int maxPerLetter = CapInventory;
        foreach (var letter in letters)
        {
            var pkgs = byLetter[letter];
            result.Append($"\n[{char.ToUpperInvariant(letter)}]\n");

            foreach (var pkg in pkgs.Take(maxPerLetter))
            {
                result.Append($"  {pkg.Name} ({pkg.Version})\n");
            }

            if (pkgs.Count > maxPerLetter)
            {
                result.Append($"  ... +{pkgs.Count - maxPerLetter} more\n");
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Lists outdated packages with their current and latest versions, capped at
    /// <see cref="CapList"/>. Faithful port of Rust <c>filter_pip_outdated</c> (<c>pip_cmd.rs</c>:191-228).
    /// </summary>
    /// <param name="output">The raw <c>pip list --outdated --format=json</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterPipOutdated(string output)
    {
        List<PipPackage>? packages;
        try
        {
            packages = JsonSerializer.Deserialize(output, PipJsonContext.Default.ListPipPackage);
        }
        catch (JsonException e)
        {
            return $"pip outdated (JSON parse failed: {e.Message})";
        }

        packages ??= [];

        if (packages.Count == 0)
        {
            return "pip outdated: All packages up to date";
        }

        var result = new StringBuilder();
        result.Append($"pip outdated: {packages.Count} packages\n");

        const int maxPipPackages = CapList;
        var i = 0;
        foreach (var pkg in packages.Take(maxPipPackages))
        {
            var latest = pkg.LatestVersion ?? "unknown";
            result.Append($"{i + 1}. {pkg.Name} ({pkg.Version} → {latest})\n");
            i++;
        }

        if (packages.Count > maxPipPackages)
        {
            result.Append($"\n... +{packages.Count - maxPipPackages} more packages\n");
        }

        result.Append("\n[hint] Run `pip install --upgrade <package>` to update\n");

        return result.ToString().Trim();
    }
}

/// <summary>pip/uv <c>list --format=json</c> per-package shape. Port of Rust <c>Package</c> (<c>pip_cmd.rs</c>:10-16).</summary>
internal sealed class PipPackage
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; set; }
}

/// <summary>Source-generated JSON context for <see cref="PipCommand"/>'s DTOs, avoiding reflection-based (de)serialization under <c>PublishAot</c>.</summary>
[JsonSerializable(typeof(List<PipPackage>))]
internal sealed partial class PipJsonContext : JsonSerializerContext;
