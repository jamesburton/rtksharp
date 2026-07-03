using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RtkSharp.Commands.Dotnet;

/// <summary>A single failed test parsed from a TRX file (or console output).</summary>
internal sealed class FailedTest
{
    /// <summary>Gets or sets the fully-qualified test name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets the failure detail lines (message + clipped stack trace).</summary>
    public List<string> Details { get; set; } = new();
}

/// <summary>Aggregated test-run summary parsed from one or more TRX files (or console output).</summary>
internal sealed class TestSummary
{
    /// <summary>Gets or sets the number of passing tests.</summary>
    public int Passed { get; set; }

    /// <summary>Gets or sets the number of failing tests.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the number of skipped tests.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets the total number of tests.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the number of test projects contributing to this summary.</summary>
    public int ProjectCount { get; set; }

    /// <summary>Gets or sets the failed-test detail list.</summary>
    public List<FailedTest> FailedTests { get; set; } = new();

    /// <summary>Gets or sets the human-readable duration text (e.g. <c>"4.1 s"</c>), or null if unknown.</summary>
    public string? DurationText { get; set; }
}

/// <summary>
/// Parses <c>.trx</c> test-result files (the Visual Studio Test Results XML format) into compact
/// <see cref="TestSummary"/> objects. Ported from Rust <c>src/cmds/dotnet/dotnet_trx.rs</c>.
/// </summary>
/// <remarks>
/// The Rust implementation streams the document with <c>quick_xml</c> and matches element/attribute
/// names by their local part (namespace-prefix stripped). This port uses <see cref="XDocument"/> and
/// matches on <see cref="XName.LocalName"/> for the same namespace-agnostic behavior. TRX files emitted
/// by the VSTest logger carry a default namespace
/// (<c>http://microsoft.com/schemas/VisualStudio/TeamTest/2010</c>); synthetic/edited fixtures may omit
/// it. Both parse identically.
/// </remarks>
internal static class DotnetTrx
{
    /// <summary>
    /// Parses TRX content into a <see cref="TestSummary"/>. Returns null when the content is not a
    /// valid TRX document (no <c>&lt;TestRun&gt;</c> element or malformed XML). Ports Rust's
    /// <c>parse_trx_content</c>.
    /// </summary>
    /// <param name="content">The raw TRX XML text.</param>
    /// <returns>The parsed summary, or null if the content is not a valid TRX file.</returns>
    public static TestSummary? ParseTrxContent(string content)
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

        var root = doc.Root;
        if (root is null)
        {
            return null;
        }

        var sawTestRun = root.LocalName() == "TestRun"
            || root.DescendantsAndSelf().Any(e => e.LocalName() == "TestRun");
        if (!sawTestRun)
        {
            return null;
        }

        var summary = new TestSummary();

        foreach (var element in root.DescendantsAndSelf())
        {
            switch (element.LocalName())
            {
                case "Times":
                    var start = Attr(element, "start");
                    var finish = Attr(element, "finish");
                    if (start is not null && finish is not null)
                    {
                        summary.DurationText = ParseTrxDuration(start, finish);
                    }

                    break;

                case "Counters":
                    summary.Total = ParseIntAttr(element, "total");
                    summary.Passed = ParseIntAttr(element, "passed");
                    summary.Failed = ParseIntAttr(element, "failed");
                    break;

                case "UnitTestResult":
                    var outcome = Attr(element, "outcome") ?? "Unknown";
                    if (outcome == "Failed")
                    {
                        summary.FailedTests.Add(ParseFailedTest(element));
                    }

                    break;
            }
        }

        // Calculate skipped from counters if available.
        if (summary.Total > 0)
        {
            summary.Skipped = Math.Max(0, summary.Total - (summary.Passed + summary.Failed));
        }

        // Set project count to at least 1 if there were any tests.
        if (summary.Total > 0)
        {
            summary.ProjectCount = 1;
        }

        return summary;
    }

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

        return ParseTrxContent(content);
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

            var parsed = ParseTrxContent(content);
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
            merged.DurationText = FormatDurationBetween(minStart.Value, maxFinish.Value);
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

    private static FailedTest ParseFailedTest(XElement result)
    {
        var name = Attr(result, "testName") ?? "unknown";
        var details = new List<string>();

        var errorInfo = result.Descendants().FirstOrDefault(e => e.LocalName() == "ErrorInfo");
        if (errorInfo is not null)
        {
            var message = errorInfo.Elements().FirstOrDefault(e => e.LocalName() == "Message")?.Value.Trim() ?? string.Empty;
            if (message.Length > 0)
            {
                details.Add(message);
            }

            var stack = errorInfo.Elements().FirstOrDefault(e => e.LocalName() == "StackTrace")?.Value.Trim() ?? string.Empty;
            if (stack.Length > 0)
            {
                var stackLines = stack
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Take(3)
                    .ToArray();
                if (stackLines.Length > 0)
                {
                    details.Add(string.Join("\n", stackLines));
                }
            }
        }

        return new FailedTest { Name = name, Details = details };
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

        var start = Attr(times, "start");
        var finish = Attr(times, "finish");
        if (start is null || finish is null)
        {
            return null;
        }

        if (TryParseRfc3339(start, out var startDt) && TryParseRfc3339(finish, out var finishDt))
        {
            return (startDt, finishDt);
        }

        return null;
    }

    private static string? ParseTrxDuration(string start, string finish)
    {
        if (!TryParseRfc3339(start, out var startDt) || !TryParseRfc3339(finish, out var finishDt))
        {
            return null;
        }

        return FormatDurationBetween(startDt, finishDt);
    }

    private static string? FormatDurationBetween(DateTimeOffset startDt, DateTimeOffset finishDt)
    {
        var millis = (long)(finishDt - startDt).TotalMilliseconds;
        if (millis <= 0)
        {
            return null;
        }

        if (millis >= 1000)
        {
            var seconds = millis / 1000.0;
            return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0} s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{millis} ms");
    }

    private static bool TryParseRfc3339(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out result);

    private static string? Attr(XElement element, string localName)
    {
        foreach (var attr in element.Attributes())
        {
            if (attr.Name.LocalName == localName)
            {
                return attr.Value;
            }
        }

        return null;
    }

    private static int ParseIntAttr(XElement element, string localName)
    {
        var value = Attr(element, localName);
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static string LocalName(this XElement element) => element.Name.LocalName;
}
