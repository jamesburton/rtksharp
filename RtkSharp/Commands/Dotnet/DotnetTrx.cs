using System.Xml;
using System.Xml.Linq;
using RtkSharp.Filters.Commands.Dotnet;

namespace RtkSharp.Commands.Dotnet;

/// <summary>
/// Locates and parses <c>.trx</c> test-result files (the Visual Studio Test Results XML format) from
/// disk. Ported from Rust <c>src/cmds/dotnet/dotnet_trx.rs</c>. The pure content-parsing core
/// (<c>parse_trx_content</c>) moved to <see cref="DotnetFilters.ParseTrxContent"/> as part of the
/// filters-library extraction (Task 6); this class retains only the file-I/O-driven members.
/// </summary>
/// <remarks>
/// The Rust implementation streams the document with <c>quick_xml</c> and matches element/attribute
/// names by their local part (namespace-prefix stripped). This port uses <see cref="System.Xml.Linq.XDocument"/>
/// and matches on local names for the same namespace-agnostic behavior. TRX files emitted by the
/// VSTest logger carry a default namespace
/// (<c>http://microsoft.com/schemas/VisualStudio/TeamTest/2010</c>); synthetic/edited fixtures may omit
/// it. Both parse identically.
/// </remarks>
internal static class DotnetTrx
{
    /// <summary>Parses a TRX file at <paramref name="path"/>. Ports Rust's <c>parse_trx_file</c>.</summary>
    /// <param name="path">The TRX file path.</param>
    /// <returns>The parsed summary, or null if the file is missing or invalid.</returns>
    public static TestSummary? ParseTrxFile(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return DotnetFilters.ParseTrxContent(content);
    }

    /// <summary>
    /// Parses a TRX file only if it was modified at or after <paramref name="since"/>. Ports Rust's
    /// <c>parse_trx_file_since</c>.
    /// </summary>
    /// <param name="path">The TRX file path.</param>
    /// <param name="since">The lower bound on the file's last-write time (UTC).</param>
    /// <returns>The parsed summary, or null if missing, older than <paramref name="since"/>, or invalid.</returns>
    public static TestSummary? ParseTrxFileSince(string path, DateTime since)
    {
        DateTime modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (modified < since)
        {
            return null;
        }

        return ParseTrxFile(path);
    }

    /// <summary>Parses and merges every TRX file in a directory. Ports Rust's <c>parse_trx_files_in_dir</c>.</summary>
    /// <param name="dir">The directory to scan.</param>
    /// <returns>The merged summary, or null if the directory holds no parseable TRX files.</returns>
    public static TestSummary? ParseTrxFilesInDir(string dir) => ParseTrxFilesInDirSince(dir, null);

    /// <summary>
    /// Parses and merges every TRX file in a directory, optionally ignoring files older than
    /// <paramref name="since"/>. Ports Rust's <c>parse_trx_files_in_dir_since</c>: counts are summed,
    /// failed-test details concatenated, and the reported duration is the wall-clock span from the
    /// earliest start to the latest finish across all files.
    /// </summary>
    /// <param name="dir">The directory to scan.</param>
    /// <param name="since">The optional lower bound on each file's last-write time (UTC).</param>
    /// <returns>The merged summary, or null if no parseable TRX files qualify.</returns>
    public static TestSummary? ParseTrxFilesInDirSince(string dir, DateTime? since)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var summaries = new List<TestSummary>();
        DateTimeOffset? minStart = null;
        DateTimeOffset? maxFinish = null;

        string[] entries;
        try
        {
            entries = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var path in entries)
        {
            if (!path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (since is not null)
            {
                DateTime modified;
                try
                {
                    modified = File.GetLastWriteTimeUtc(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }

                if (modified < since.Value)
                {
                    continue;
                }
            }

            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            var bounds = ParseTrxTimeBounds(content);
            if (bounds is not null)
            {
                var (start, finish) = bounds.Value;
                minStart = minStart is null ? start : (start < minStart.Value ? start : minStart.Value);
                maxFinish = maxFinish is null ? finish : (finish > maxFinish.Value ? finish : maxFinish.Value);
            }

            var parsed = DotnetFilters.ParseTrxContent(content);
            if (parsed is not null)
            {
                summaries.Add(parsed);
            }
        }

        if (summaries.Count == 0)
        {
            return null;
        }

        var merged = new TestSummary();
        foreach (var summary in summaries)
        {
            merged.Passed += summary.Passed;
            merged.Failed += summary.Failed;
            merged.Skipped += summary.Skipped;
            merged.Total += summary.Total;
            merged.FailedTests.AddRange(summary.FailedTests);
            merged.ProjectCount += Math.Max(summary.ProjectCount, 1);
            merged.DurationText ??= summary.DurationText;
        }

        if (minStart is not null && maxFinish is not null)
        {
            merged.DurationText = DotnetFilters.FormatDurationBetween(minStart.Value, maxFinish.Value);
        }

        return merged;
    }

    /// <summary>
    /// Finds the most recently modified <c>.trx</c> file in <c>./TestResults</c>, if any. Ports Rust's
    /// <c>find_recent_trx_in_testresults</c>.
    /// </summary>
    /// <returns>The newest TRX path, or null if none exists.</returns>
    public static string? FindRecentTrxInTestResults() => FindRecentTrxInDir("./TestResults");

    /// <summary>Finds the most recently modified <c>.trx</c> file in a directory. Ports Rust's <c>find_recent_trx_in_dir</c>.</summary>
    /// <param name="dir">The directory to scan.</param>
    /// <returns>The newest TRX path, or null if the directory is missing or holds no TRX files.</returns>
    public static string? FindRecentTrxInDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (var path in entries)
        {
            if (!path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTime modified;
            try
            {
                modified = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            if (newest is null || modified > newestTime)
            {
                newest = path;
                newestTime = modified;
            }
        }

        return newest;
    }

    private static (DateTimeOffset Start, DateTimeOffset Finish)? ParseTrxTimeBounds(string content)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (XmlException)
        {
            return null;
        }

        var times = doc.Descendants().FirstOrDefault(e => e.LocalName() == "Times");
        if (times is null)
        {
            return null;
        }

        var start = DotnetFilters.Attr(times, "start");
        var finish = DotnetFilters.Attr(times, "finish");
        if (start is null || finish is null)
        {
            return null;
        }

        if (DotnetFilters.TryParseRfc3339(start, out var startDt) && DotnetFilters.TryParseRfc3339(finish, out var finishDt))
        {
            return (startDt, finishDt);
        }

        return null;
    }
}
