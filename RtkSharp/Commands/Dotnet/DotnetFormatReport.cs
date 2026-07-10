using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Filters.Commands.Dotnet;

namespace RtkSharp.Commands.Dotnet;

/// <summary>
/// Reads and deserializes <c>dotnet format --report</c> JSON reports from disk, then delegates
/// summarization to <see cref="DotnetFilters.Summarize"/>. Ported from Rust
/// <c>src/cmds/dotnet/dotnet_format_report.rs</c>. The summarization logic itself moved to
/// <see cref="DotnetFilters.Summarize"/> as part of the filters-library extraction (Task 6); this
/// class retains only the file-I/O and JSON-deserialization half of the original fused
/// <c>ParseFormatReport</c>.
/// </summary>
internal static partial class DotnetFormatReport
{
    /// <summary>
    /// Reads and parses the format report at <paramref name="path"/>, then summarizes it via
    /// <see cref="DotnetFilters.Summarize"/>. Ports Rust's <c>parse_format_report</c>.
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

        return DotnetFilters.Summarize(entries ?? new List<FormatReportEntryDto>());
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
