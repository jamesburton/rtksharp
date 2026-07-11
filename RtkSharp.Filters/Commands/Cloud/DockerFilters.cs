using System.Globalization;
using RtkSharp.Core;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Filters.Commands.Cloud;

/// <summary>
/// Pure filtering/formatting logic for the <c>rtk docker</c> proxy: condenses <c>docker ps</c>/
/// <c>images</c>/<c>compose ps</c>/<c>compose build</c>/<c>compose logs</c> output. Extracted from
/// <c>RtkSharp.Commands.Cloud.DockerCommand</c> — signatures and logic for
/// <see cref="FormatContainerLine"/>/<see cref="FormatContainerLineFromParts"/>/
/// <see cref="CompactPorts"/>/<see cref="FormatComposePs"/>/<see cref="FormatComposeBuild"/>/
/// <see cref="FormatComposeLogs"/> are unchanged (moved as-is, only visibility promoted from
/// <c>internal</c> to <c>public</c> and the containing type from <c>DockerCommand</c> to
/// <c>DockerFilters</c>). <see cref="FormatPsSummary"/>/<see cref="FormatImagesSummary"/> are new
/// pure methods carved out of <c>DockerCommand</c>'s previously-fused <c>DockerPsAsync</c>/
/// <c>RunImagesAsync</c>: each of those methods mixed process-exec/Console-write with
/// summary-building, so only the summary-building half (everything downstream of the
/// already-captured <c>--format</c>-flagged stdout) moved here — named after what they actually
/// build (the <c>[docker] N containers:</c>/<c>[docker] N images (...)</c> summary text), matching
/// the brief's suggested names.
/// </summary>
/// <remarks>
/// <b><c>FormatComposeLogs</c></b> was initially deferred because its body is a one-line wrapper
/// around <c>RtkSharp.Commands.System.LogCommand.AnalyzeLogs</c>, which was hosted in the
/// <c>RtkSharp</c> assembly (not yet reachable from <c>RtkSharp.Filters</c>). That dependency has
/// since moved to <see cref="LogFilters.AnalyzeLogs"/>, unblocking this method's relocation here.
/// </remarks>
public static class DockerFilters
{
    private const int CapList = 20;
    private const int CapInventory = 50;

    /// <summary>Faithful port of <c>format_container_line</c> (<c>container.rs</c>:188-191).</summary>
    public static string? FormatContainerLine(string line, bool withPorts) =>
        FormatContainerLineFromParts(line.Split('\t'), withPorts);

    /// <summary>Faithful port of <c>format_container_line_from_parts</c> (<c>container.rs</c>:193-215).</summary>
    public static string? FormatContainerLineFromParts(IReadOnlyList<string> parts, bool withPorts)
    {
        if (parts.Count < 4)
        {
            return null;
        }

        var id = parts[0][..Math.Min(12, parts[0].Length)];
        var name = parts[1];
        var status = parts[2].Trim();
        var shortImage = parts[3].Split('/').Last();
        var portSuffix = "";
        if (withPorts)
        {
            var ports = CompactPorts(parts.Count > 4 ? parts[4] : "");
            if (ports != "-")
            {
                portSuffix = $" [{ports}]";
            }
        }

        return $"  {id} {name} ({shortImage}) {status}{portSuffix}\n";
    }

    /// <summary>
    /// Pure summary-building half of <c>docker_ps</c> (<c>container.rs</c>:57-109), carved out of
    /// <c>DockerCommand.DockerPsAsync</c>: formats the already-captured <c>--format</c>-flagged
    /// stdout into the <c>[docker] N containers:</c> summary, applying <see cref="CapList"/>
    /// truncation with a tee-recovery hint when exceeded. The impure wrapper
    /// (<c>DockerCommand.DockerPsAsync</c>) still owns spawning both <c>docker ps</c> invocations,
    /// printing, and metrics tracking. Returns <c>"[docker] 0 containers"</c> (no trailing newline)
    /// when <paramref name="stdout"/> is empty/whitespace, matching the wrapper's own pre-extraction
    /// zero-containers branch exactly (including that branch's lack of a trailing newline in the
    /// TRACKED text, as opposed to the always-present trailing newline in every other branch — the
    /// wrapper reproduces the original printed-text behavior via <c>EndsWith('\n')</c>).
    /// </summary>
    /// <param name="stdout">The captured stdout of <c>docker ps --format ...</c>.</param>
    /// <returns>The formatted summary.</returns>
    public static string FormatPsSummary(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return "[docker] 0 containers";
        }

        var lines = SourceFilterLineSplitter.SplitLines(stdout)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => FormatContainerLine(l, withPorts: true))
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

        var rtk = $"[docker] {lines.Count} containers:\n";
        rtk += string.Concat(lines.Take(CapList));
        if (lines.Count > CapList)
        {
            rtk += $"  … +{lines.Count - CapList} more\n";
            var full = string.Concat(lines);
            if (Tee.ForceTeeHint(full, "docker-ps") is { } hint)
            {
                rtk += hint + "\n";
            }
        }

        return rtk;
    }

    /// <summary>
    /// Pure summary-building half of <c>docker_images</c> (<c>container.rs</c>:217-305), carved out
    /// of <c>DockerCommand.RunImagesAsync</c>: formats the already-captured <c>--format</c>-flagged
    /// stdout into the <c>[docker] N images (totalSize)</c> summary, applying
    /// <see cref="CapInventory"/> truncation with a tee-recovery hint when exceeded. The impure
    /// wrapper (<c>DockerCommand.RunImagesAsync</c>) still owns spawning both <c>docker images</c>
    /// invocations, printing, and metrics tracking. Returns <c>"[docker] 0 images"</c> (no trailing
    /// newline) when <paramref name="stdout"/> has no lines, matching the wrapper's own
    /// pre-extraction zero-images branch exactly (same trailing-newline asymmetry as
    /// <see cref="FormatPsSummary"/> — see its remarks).
    /// </summary>
    /// <param name="stdout">The captured stdout of <c>docker images --format ...</c>.</param>
    /// <returns>The formatted summary.</returns>
    public static string FormatImagesSummary(string stdout)
    {
        var lines = SourceFilterLineSplitter.SplitLines(stdout).ToList();

        if (lines.Count == 0)
        {
            return "[docker] 0 images";
        }

        var totalSizeMb = 0.0;
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length <= 1)
            {
                continue;
            }

            var sizeStr = parts[1];
            if (sizeStr.Contains("GB", StringComparison.Ordinal))
            {
                if (double.TryParse(sizeStr.Replace("GB", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gb))
                {
                    totalSizeMb += gb * 1024.0;
                }
            }
            else if (sizeStr.Contains("MB", StringComparison.Ordinal))
            {
                if (double.TryParse(sizeStr.Replace("MB", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb))
                {
                    totalSizeMb += mb;
                }
            }
        }

        var totalDisplay = totalSizeMb > 1024.0
            ? $"{(totalSizeMb / 1024.0).ToString("F1", CultureInfo.InvariantCulture)}GB"
            : $"{totalSizeMb.ToString("F0", CultureInfo.InvariantCulture)}MB";

        var rtk = $"[docker] {lines.Count} images ({totalDisplay})\n";

        var imageLines = lines.Select(line =>
        {
            var parts = line.Split('\t');
            var image = parts.Length > 0 ? parts[0] : "";
            var size = parts.Length > 1 ? parts[1] : "";
            return $"  {image} [{size}]\n";
        }).ToList();

        var fullRtk = rtk + string.Concat(imageLines);

        rtk += string.Concat(imageLines.Take(CapInventory));
        if (imageLines.Count > CapInventory)
        {
            rtk += $"  … +{imageLines.Count - CapInventory} more\n";
            if (Tee.ForceTeeTailHint(fullRtk, "docker-images", CapInventory + 2) is { } hint)
            {
                rtk += hint + "\n";
            }
        }

        return rtk;
    }

    /// <summary>Faithful port of <c>format_compose_ps</c> (<c>container.rs</c>:509-562).</summary>
    public static string FormatComposePs(string raw)
    {
        var lines = SourceFilterLineSplitter.SplitLines(raw).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0)
        {
            return "[compose] 0 services";
        }

        var result = $"[compose] {lines.Count} services:\n";

        var allFormatted = lines.Select(line =>
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
            {
                return null;
            }

            var name = parts[0];
            var image = parts[1];
            var status = parts[2];
            var ports = parts[3];
            var shortImage = image.Contains('/') ? image.Split('/').Last() : image;
            var portStr = "";
            if (!string.IsNullOrWhiteSpace(ports))
            {
                var compact = CompactPorts(ports.Trim());
                if (compact != "-")
                {
                    portStr = $" [{compact}]";
                }
            }

            return $"  {name} ({shortImage}) {status}{portStr}";
        }).Where(l => l is not null).Select(l => l!).ToList();

        result += string.Join('\n', allFormatted.Take(CapList));
        if (allFormatted.Count > 0)
        {
            result += "\n";
        }

        if (allFormatted.Count > CapList)
        {
            result += $"  … +{allFormatted.Count - CapList} more\n";
            var allText = string.Join('\n', allFormatted);
            if (Tee.ForceTeeTailHint(allText, "compose-ps", CapList + 1) is { } hint)
            {
                result += $"  {hint}\n";
            }
        }

        return result.TrimEnd();
    }

    /// <summary>Faithful port of <c>format_compose_build</c> (<c>container.rs</c>:577-631).</summary>
    public static string FormatComposeBuild(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "[compose] Build: no output";
        }

        var lines = SourceFilterLineSplitter.SplitLines(raw).ToList();
        var result = "";

        foreach (var line in lines)
        {
            if (line.Contains("Building", StringComparison.Ordinal) && line.Contains("FINISHED", StringComparison.Ordinal))
            {
                result += $"[compose] {line.Trim()}\n";
                break;
            }
        }

        if (result.Length == 0)
        {
            var buildingLine = lines.FirstOrDefault(l => l.Contains("Building", StringComparison.Ordinal));
            result += buildingLine is not null ? $"[compose] {buildingLine.Trim()}\n" : "[compose] Build:\n";
        }

        var services = new List<string>();
        foreach (var line in lines)
        {
            var start = line.IndexOf('[');
            if (start < 0)
            {
                continue;
            }

            var end = line.IndexOf(']', start + 1);
            if (end < 0)
            {
                continue;
            }

            var bracket = line[(start + 1)..end];
            // Rust's split_whitespace() splits on any whitespace (space, tab, ...), not just ' '.
            var svc = bracket.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (svc.Length > 0 && svc != "+" && !services.Contains(svc))
            {
                services.Add(svc);
            }
        }

        if (services.Count > 0)
        {
            result += $"  Services: {string.Join(", ", services)}\n";
        }

        var stepCount = lines.Count(l => l.TrimStart().StartsWith("=> ", StringComparison.Ordinal));
        if (stepCount > 0)
        {
            result += $"  Steps: {stepCount}";
        }

        return result.TrimEnd();
    }

    /// <summary>Faithful port of <c>format_compose_logs</c> (<c>container.rs</c>:564-574).</summary>
    public static string FormatComposeLogs(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? "[compose] No logs" : $"[compose] Logs:\n{LogFilters.AnalyzeLogs(raw)}";

    /// <summary>Faithful port of <c>compact_ports</c> (<c>container.rs</c>:633-653).</summary>
    public static string CompactPorts(string ports)
    {
        if (ports.Length == 0)
        {
            return "-";
        }

        var portNums = ports.Split(',')
            .Select(p => p.Split("->")[0].Split(':').Last())
            .ToList();

        if (portNums.Count <= 3)
        {
            return string.Join(", ", portNums);
        }

        return $"{string.Join(", ", portNums.Take(2))}, … +{portNums.Count - 2}";
    }
}
