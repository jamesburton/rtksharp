using System.IO.Compression;

namespace RtkSharp.Filters.Tests.Commands.Jvm;

/// <summary>
/// Loads the real-command fixture files under the repo's <c>tests/fixtures/</c> directory (the same
/// files Rust's <c>include_str!</c>/<c>include_bytes!</c> macros embed in <c>gradlew_cmd.rs</c> /
/// <c>mvn_cmd.rs</c>'s own <c>#[cfg(test)]</c> suite), so the ported xUnit tests exercise byte-identical
/// input. Walks up from the test assembly's output directory to find the repo root rather than
/// embedding the fixture text inline, since several of the mvn fixtures are large and/or gzip-compressed.
/// </summary>
internal static class JvmFixtures
{
    /// <summary>Reads a text fixture from <c>tests/fixtures/&lt;relativePath&gt;</c>.</summary>
    /// <param name="relativePath">The fixture file name, relative to <c>tests/fixtures/</c>.</param>
    /// <returns>The fixture's raw text content.</returns>
    public static string LoadText(string relativePath) => File.ReadAllText(ResolvePath(relativePath));

    /// <summary>Reads and gunzips a <c>.gz</c> fixture from <c>tests/fixtures/&lt;relativePath&gt;</c>.</summary>
    /// <param name="relativePath">The gzipped fixture file name, relative to <c>tests/fixtures/</c>.</param>
    /// <returns>The decompressed text content.</returns>
    public static string LoadGzText(string relativePath)
    {
        using var fileStream = File.OpenRead(ResolvePath(relativePath));
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    private static string ResolvePath(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Could not locate fixture '{relativePath}' under any ancestor's tests/fixtures/ directory (searched from {AppContext.BaseDirectory}).");
    }
}
