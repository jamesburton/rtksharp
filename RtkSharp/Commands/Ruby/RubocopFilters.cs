using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Core;

namespace RtkSharp.Commands.Ruby;

/// <summary>
/// Buffered filters for <c>rtk rubocop</c>: JSON parsing (the default, structured path) and a text
/// fallback (autocorrect mode, custom <c>--format</c>, or JSON parse failure). Faithful port of
/// <c>filter_rubocop_json</c>/<c>filter_rubocop_text</c> and their helpers (<c>src/cmds/ruby/rubocop_cmd.rs</c>).
/// </summary>
internal static partial class RubocopFilters
{
    private const int MaxFiles = 10;
    private const int MaxOffensesPerFile = 5;

    /// <summary>
    /// Ranks a RuboCop offense severity for ordering: lower rank sorts first (more severe).
    /// Faithful port of Rust <c>severity_rank</c> (<c>rubocop_cmd.rs</c>:92-99).
    /// </summary>
    /// <param name="severity">The RuboCop severity string (e.g. <c>"error"</c>, <c>"warning"</c>, <c>"convention"</c>).</param>
    /// <returns>0 for fatal/error, 1 for warning, 2 for convention/refactor/info, 3 otherwise.</returns>
    internal static int SeverityRank(string severity) => severity switch
    {
        "fatal" or "error" => 0,
        "warning" => 1,
        "convention" or "refactor" or "info" => 2,
        _ => 3,
    };

    /// <summary>
    /// Filters RuboCop's <c>--format json</c> output into a compact per-file offense summary,
    /// sorted by worst severity then alphabetically. Faithful port of Rust <c>filter_rubocop_json</c>
    /// (<c>rubocop_cmd.rs</c>:101-218).
    /// </summary>
    /// <param name="output">The raw <c>rubocop --format json</c> stdout.</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterRubocopJson(string output)
    {
        if (output.Trim().Length == 0)
        {
            return "RuboCop: No output";
        }

        RubocopOutput rubocop;
        try
        {
            rubocop = JsonSerializer.Deserialize(output, RubocopJsonContext.Default.RubocopOutput)
                ?? throw new JsonException("null result");
        }
        catch (JsonException e)
        {
            Console.Error.Write($"[rtk] rubocop: JSON parse failed ({e.Message})\n");
            return RubySupport.FallbackTail(output, "rubocop (JSON parse error)", 5);
        }

        var s = rubocop.Summary;

        if (s.OffenseCount == 0)
        {
            return $"ok ✓ rubocop ({s.InspectedFileCount} files)";
        }

        // When correctable_offense_count is 0, it could mean the field was absent (older RuboCop)
        // or genuinely zero. Manual count as consistent fallback.
        var correctableCount = s.CorrectableOffenseCount > 0
            ? s.CorrectableOffenseCount
            : rubocop.Files.SelectMany(f => f.Offenses).Count(o => o.Correctable);

        var result = new StringBuilder($"rubocop: {s.OffenseCount} offenses ({s.InspectedFileCount} files)\n");

        // Build list of files with offenses, sorted by worst severity then file path.
        var filesWithOffenses = rubocop.Files.Where(f => f.Offenses.Count > 0).ToList();
        filesWithOffenses.Sort((a, b) =>
        {
            var aWorst = a.Offenses.Count == 0 ? 3 : a.Offenses.Min(o => SeverityRank(o.Severity));
            var bWorst = b.Offenses.Count == 0 ? 3 : b.Offenses.Min(o => SeverityRank(o.Severity));
            var cmp = aWorst.CompareTo(bWorst);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Path, b.Path);
        });

        foreach (var file in filesWithOffenses.Take(MaxFiles))
        {
            var shortPath = CompactRubyPath(file.Path);
            result.Append($"\n{shortPath}\n");

            var sortedOffenses = file.Offenses.ToList();
            sortedOffenses.Sort((a, b) =>
            {
                var cmp = SeverityRank(a.Severity).CompareTo(SeverityRank(b.Severity));
                return cmp != 0 ? cmp : a.Location.StartLine.CompareTo(b.Location.StartLine);
            });

            foreach (var offense in sortedOffenses.Take(MaxOffensesPerFile))
            {
                var firstMsgLine = offense.Message.Split('\n').FirstOrDefault() ?? string.Empty;
                result.Append($"  :{offense.Location.StartLine} {offense.CopName} — {firstMsgLine}\n");
            }

            if (sortedOffenses.Count > MaxOffensesPerFile)
            {
                result.Append($"  … +{sortedOffenses.Count - MaxOffensesPerFile} more\n");
            }
        }

        if (filesWithOffenses.Count > MaxFiles)
        {
            result.Append($"\n… +{filesWithOffenses.Count - MaxFiles} more files\n");
            var allFiles = string.Join('\n', filesWithOffenses.Select(f => CompactRubyPath(f.Path)));
            var hint = Tee.ForceTeeTailHint(allFiles, "rubocop-files", MaxFiles + 1);
            if (hint is not null)
            {
                result.Append($"  {hint}\n");
            }
        }

        if (correctableCount > 0)
        {
            result.Append($"\n({correctableCount} correctable, run `rubocop -A`)");
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Text fallback for RuboCop output: detects Ruby/Bundler load errors first (truncated to 20
    /// lines), then autocorrect and plain-inspection summaries, then falls back to the last 5
    /// lines. Faithful port of Rust <c>filter_rubocop_text</c> (<c>rubocop_cmd.rs</c>:222-274).
    /// </summary>
    /// <param name="output">The raw <c>rubocop</c> stdout (autocorrect mode or custom format).</param>
    /// <returns>The filtered summary.</returns>
    public static string FilterRubocopText(string output)
    {
        foreach (var line in Core.SourceFilterLineSplitter.SplitLines(output))
        {
            var t = line.Trim();
            if (t.Contains("cannot load such file", StringComparison.Ordinal)
                || t.Contains("Bundler::GemNotFound", StringComparison.Ordinal)
                || t.Contains("Gem::MissingSpecError", StringComparison.Ordinal)
                || t.StartsWith("rubocop: command not found", StringComparison.Ordinal)
                || t.StartsWith("rubocop: No such file", StringComparison.Ordinal))
            {
                var allLines = Core.SourceFilterLineSplitter.SplitLines(output.Trim());
                var errorLines = allLines.Take(20).ToList();
                var truncated = string.Join('\n', errorLines);
                var totalLines = allLines.Count;
                if (totalLines > 20)
                {
                    return $"RuboCop error:\n{truncated}\n... ({totalLines - 20} more lines)";
                }

                return $"RuboCop error:\n{truncated}";
            }
        }

        var lines = Core.SourceFilterLineSplitter.SplitLines(output);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var t = lines[i].Trim();
            if (t.Contains("inspected", StringComparison.Ordinal) && t.Contains("autocorrected", StringComparison.Ordinal))
            {
                var files = ExtractLeadingNumber(t);
                var corrected = ExtractAutocorrectCount(t);
                if (files > 0 && corrected > 0)
                {
                    return $"ok ✓ rubocop -A ({files} files, {corrected} autocorrected)";
                }

                return $"RuboCop: {t}";
            }

            if (t.Contains("inspected", StringComparison.Ordinal) && (t.Contains("offense", StringComparison.Ordinal) || t.Contains("no offenses", StringComparison.Ordinal)))
            {
                if (t.Contains("no offenses", StringComparison.Ordinal))
                {
                    var files = ExtractLeadingNumber(t);
                    return files > 0 ? $"ok ✓ rubocop ({files} files)" : "ok ✓ rubocop (no offenses)";
                }

                return $"RuboCop: {t}";
            }
        }

        // Last resort: last 5 lines.
        return RubySupport.FallbackTail(output, "rubocop", 5);
    }

    /// <summary>
    /// Extracts the leading whitespace-delimited number from a string like <c>"15 files
    /// inspected"</c>. Faithful port of Rust <c>extract_leading_number</c> (<c>rubocop_cmd.rs</c>:277-282).
    /// </summary>
    /// <param name="s">The string to extract from.</param>
    /// <returns>The leading number, or 0 if none is found.</returns>
    internal static int ExtractLeadingNumber(string s)
    {
        var first = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && int.TryParse(first, out var n) ? n : 0;
    }

    /// <summary>
    /// Extracts the autocorrect count from a summary like <c>"... 3 offenses autocorrected"</c>.
    /// Faithful port of Rust <c>extract_autocorrect_count</c> (<c>rubocop_cmd.rs</c>:285-295).
    /// </summary>
    /// <param name="s">The summary string to extract from.</param>
    /// <returns>The autocorrect count, or 0 if none is found.</returns>
    internal static int ExtractAutocorrectCount(string s)
    {
        var parts = s.Split(',');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var t = parts[i].Trim();
            if (t.Contains("autocorrected", StringComparison.Ordinal))
            {
                return ExtractLeadingNumber(t);
            }
        }

        return 0;
    }

    /// <summary>
    /// Compacts a Ruby file path by finding the nearest Rails-convention directory and stripping the
    /// absolute path prefix. Faithful port of Rust <c>compact_ruby_path</c> (<c>rubocop_cmd.rs</c>:299-328).
    /// </summary>
    /// <param name="path">The raw file path (possibly absolute, possibly Windows-separated).</param>
    /// <returns>The compacted, project-relative-looking path.</returns>
    internal static string CompactRubyPath(string path)
    {
        var normalized = path.Replace('\\', '/');

        string[] prefixes =
        [
            "app/models/",
            "app/controllers/",
            "app/views/",
            "app/helpers/",
            "app/services/",
            "app/jobs/",
            "app/mailers/",
            "lib/",
            "spec/",
            "test/",
            "config/",
        ];

        foreach (var prefix in prefixes)
        {
            var pos = normalized.IndexOf(prefix, StringComparison.Ordinal);
            if (pos >= 0)
            {
                return normalized[pos..];
            }
        }

        // Generic: strip up to last known directory marker.
        var appPos = normalized.LastIndexOf("/app/", StringComparison.Ordinal);
        if (appPos >= 0)
        {
            return normalized[(appPos + 1)..];
        }

        var slashPos = normalized.LastIndexOf('/');
        return slashPos >= 0 ? normalized[(slashPos + 1)..] : normalized;
    }

    // ── JSON structures matching RuboCop's --format json output ────────────────────────────────

    /// <summary>Faithful port of <c>RubocopOutput</c> (<c>rubocop_cmd.rs</c>:15-19).</summary>
    private sealed class RubocopOutput
    {
        [JsonPropertyName("files")]
        public List<RubocopFile> Files { get; set; } = [];

        [JsonPropertyName("summary")]
        public required RubocopSummary Summary { get; set; }
    }

    /// <summary>Faithful port of <c>RubocopFile</c> (<c>rubocop_cmd.rs</c>:21-25).</summary>
    private sealed class RubocopFile
    {
        [JsonPropertyName("path")]
        public required string Path { get; set; }

        [JsonPropertyName("offenses")]
        public List<RubocopOffense> Offenses { get; set; } = [];
    }

    /// <summary>Faithful port of <c>RubocopOffense</c> (<c>rubocop_cmd.rs</c>:27-34).</summary>
    private sealed class RubocopOffense
    {
        [JsonPropertyName("cop_name")]
        public required string CopName { get; set; }

        [JsonPropertyName("severity")]
        public required string Severity { get; set; }

        [JsonPropertyName("message")]
        public required string Message { get; set; }

        [JsonPropertyName("correctable")]
        public bool Correctable { get; set; }

        [JsonPropertyName("location")]
        public required RubocopLocation Location { get; set; }
    }

    /// <summary>Faithful port of <c>RubocopLocation</c> (<c>rubocop_cmd.rs</c>:36-39).</summary>
    private sealed class RubocopLocation
    {
        [JsonPropertyName("start_line")]
        public int StartLine { get; set; }
    }

    /// <summary>Faithful port of <c>RubocopSummary</c> (<c>rubocop_cmd.rs</c>:41-49).</summary>
    private sealed class RubocopSummary
    {
        [JsonPropertyName("offense_count")]
        public int OffenseCount { get; set; }

        [JsonPropertyName("inspected_file_count")]
        public int InspectedFileCount { get; set; }

        [JsonPropertyName("correctable_offense_count")]
        public int CorrectableOffenseCount { get; set; }
    }

    /// <summary>
    /// Source-generated JSON metadata for RuboCop's <c>--format json</c> schema, required because
    /// <c>RtkSharp.csproj</c> publishes with <c>PublishAot=true</c> — reflection-based
    /// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming, matching the
    /// convention established elsewhere (e.g. <c>VitestCommand.VitestJsonContext</c>).
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
    [JsonSerializable(typeof(RubocopOutput))]
    private sealed partial class RubocopJsonContext : JsonSerializerContext;
}
