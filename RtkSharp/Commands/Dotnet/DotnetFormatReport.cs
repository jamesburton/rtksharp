using System.Text.Json;
using System.Text.Json.Serialization;

namespace RtkSharp.Commands.Dotnet;

/// <summary>A single formatting change within a file, as reported by <c>dotnet format --report</c>.</summary>
internal sealed class ChangeDetail
{
    /// <summary>Gets the 1-based line number of the change.</summary>
    public required uint LineNumber { get; init; }

    /// <summary>Gets the 1-based character (column) number of the change.</summary>
    public required uint CharNumber { get; init; }

    /// <summary>Gets the formatter diagnostic id (e.g. <c>IDE0055</c>), or empty if not reported.</summary>
    public required string DiagnosticId { get; init; }

    /// <summary>Gets the human-readable description of the formatting fix applied.</summary>
    public required string FormatDescription { get; init; }
}

/// <summary>A source file that needed formatting, with its individual changes.</summary>
internal sealed class FileWithChanges
{
    /// <summary>Gets the file path as reported in the format report (relative or absolute).</summary>
    public required string Path { get; init; }

    /// <summary>Gets the changes recorded for this file.</summary>
    public required IReadOnlyList<ChangeDetail> Changes { get; init; }
}

/// <summary>Aggregated summary of a <c>dotnet format --report</c> JSON report.</summary>
internal sealed class FormatSummary
{
    /// <summary>Gets the files that needed formatting, each with its change detail.</summary>
    public required IReadOnlyList<FileWithChanges> FilesWithChanges { get; init; }

    /// <summary>Gets the number of files scanned that already matched the formatting rules.</summary>
    public required int FilesUnchanged { get; init; }

    /// <summary>Gets the total number of files the report covers.</summary>
    public required int TotalFiles { get; init; }
}

/// <summary>
/// Parses <c>dotnet format --report</c> JSON reports into compact <see cref="FormatSummary"/> objects.
/// Ported from Rust <c>src/cmds/dotnet/dotnet_format_report.rs</c>.
/// </summary>
internal static partial class DotnetFormatReport
{
    /// <summary>
    /// Parses the format report at <paramref name="path"/>. Ports Rust's <c>parse_format_report</c>.
    /// </summary>
    /// <param name="path">The path to the <c>--report</c> JSON file.</param>
    /// <returns>The parsed summary.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the report cannot be read or does not contain valid report JSON. Callers rely on
    /// this to trigger the raw-output fallback, mirroring Rust's <c>Result</c>-returning signature.
    /// </exception>
    public static FormatSummary ParseFormatReport(string path)
    {
        List<FormatReportEntryDto>? entries;
        try
        {
            using var stream = File.OpenRead(path);
            entries = JsonSerializer.Deserialize(stream, FormatReportJsonContext.Default.ListFormatReportEntryDto);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Failed to read dotnet format report at {path}", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse dotnet format report JSON at {path}", ex);
        }

        entries ??= new List<FormatReportEntryDto>();
        var totalFiles = entries.Count;

        var filesWithChanges = entries
            .Where(entry => entry.FileChanges.Count > 0)
            .Select(entry => new FileWithChanges
            {
                Path = entry.FilePath,
                Changes = entry.FileChanges.Select(change => new ChangeDetail
                {
                    LineNumber = change.LineNumber,
                    CharNumber = change.CharNumber,
                    DiagnosticId = change.DiagnosticId,
                    FormatDescription = change.FormatDescription,
                }).ToList(),
            })
            .ToList();

        var filesUnchanged = Math.Max(0, totalFiles - filesWithChanges.Count);

        return new FormatSummary
        {
            FilesWithChanges = filesWithChanges,
            FilesUnchanged = filesUnchanged,
            TotalFiles = totalFiles,
        };
    }

    /// <summary>
    /// JSON shape of a single entry in a <c>dotnet format --report</c> array. The report also carries
    /// a <c>FileName</c> field, which is intentionally unmapped here (unused by the summary, and
    /// <see cref="JsonSerializer"/> ignores unmapped properties by default, matching serde's behavior).
    /// </summary>
    private sealed class FormatReportEntryDto
    {
        [JsonPropertyName("FilePath")]
        public string FilePath { get; init; } = string.Empty;

        [JsonPropertyName("FileChanges")]
        public List<FileChangeDto> FileChanges { get; init; } = new();
    }

    private sealed class FileChangeDto
    {
        [JsonPropertyName("LineNumber")]
        public uint LineNumber { get; init; }

        [JsonPropertyName("CharNumber")]
        public uint CharNumber { get; init; }

        [JsonPropertyName("DiagnosticId")]
        public string DiagnosticId { get; init; } = string.Empty;

        [JsonPropertyName("FormatDescription")]
        public string FormatDescription { get; init; } = string.Empty;
    }

    /// <summary>
    /// Source-generated JSON metadata for <see cref="FormatReportEntryDto"/>, required because the
    /// project publishes AOT (<c>PublishAot=true</c>) and reflection-based <see cref="JsonSerializer"/>
    /// overloads are unavailable/unsafe under trimming.
    /// </summary>
    [JsonSourceGenerationOptions]
    [JsonSerializable(typeof(List<FormatReportEntryDto>))]
    private sealed partial class FormatReportJsonContext : JsonSerializerContext
    {
    }
}
