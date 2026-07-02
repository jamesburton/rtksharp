using System;
using System.IO;
using System.Linq;

namespace RtkSharp.Core;

/// <summary>
/// Controls when raw command output is teed (written) to disk for later recovery.
/// </summary>
public enum TeeMode
{
    /// <summary>Only tee output when the command exits with a non-zero code.</summary>
    Failures,

    /// <summary>Always tee output regardless of exit code.</summary>
    Always,

    /// <summary>Never tee output.</summary>
    Never
}

/// <summary>
/// Configuration controlling tee-to-disk behavior for raw command output.
/// </summary>
public class TeeConfig
{
    /// <summary>Whether teeing is enabled at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The condition under which output is teed.</summary>
    public TeeMode Mode { get; set; } = TeeMode.Failures;

    /// <summary>The maximum number of tee files to retain before old ones are pruned.</summary>
    public int MaxFiles { get; set; } = 20;

    /// <summary>The maximum size, in bytes, of a single tee file before content is truncated.</summary>
    public int MaxFileSize { get; set; } = 1_048_576; // 1MB

    /// <summary>The directory tee files are written to, or null to use the default location.</summary>
    public string? Directory { get; set; }
}

/// <summary>
/// Writes raw (unfiltered) command output to disk so it can be recovered after
/// filtering has truncated or compressed it, and formats recovery hints pointing at
/// the written file.
/// </summary>
public static class Tee
{
    private const int MinTeeSize = 500;

    /// <summary>
    /// Sanitizes a command slug for safe use in a filename, replacing any character
    /// that isn't alphanumeric, underscore, or hyphen with an underscore, and
    /// truncating to 40 characters.
    /// </summary>
    /// <param name="slug">The raw slug to sanitize.</param>
    /// <returns>The sanitized, length-capped slug.</returns>
    public static string SanitizeSlug(string slug)
    {
        var chars = new char[slug.Length];
        for (int i = 0; i < slug.Length; i++)
        {
            char c = slug[i];
            chars[i] = (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-') ? c : '_';
        }
        var sanitized = new string(chars);
        return sanitized.Length > 40 ? sanitized.Substring(0, 40) : sanitized;
    }

    /// <summary>
    /// Determines the directory tee files should be written to, honoring the
    /// <c>RTK_TEE_DIR</c> environment variable override, then <see cref="TeeConfig.Directory"/>,
    /// then falling back to a default location under local application data.
    /// </summary>
    /// <param name="config">The tee configuration.</param>
    /// <returns>The resolved tee directory, or null if it could not be determined.</returns>
    public static string? GetTeeDir(TeeConfig config)
    {
        var envDir = Environment.GetEnvironmentVariable("RTK_TEE_DIR");
        if (!string.IsNullOrEmpty(envDir))
        {
            return envDir;
        }

        if (!string.IsNullOrEmpty(config.Directory))
        {
            return config.Directory;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            return null;
        }

        return Path.Combine(localAppData, "rtk", "tee");
    }

    private static void CleanupOldFiles(string dir, int maxFiles)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            var files = Directory.GetFiles(dir, "*.log")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name)
                .ToList();

            if (files.Count <= maxFiles) return;

            int toRemove = files.Count - maxFiles;
            for (int i = 0; i < toRemove; i++)
            {
                try
                {
                    files[i].Delete();
                }
                catch
                {
                    // Ignore file delete failure
                }
            }
        }
        catch
        {
            // Ignore cleanup failure
        }
    }

    /// <summary>
    /// Determines whether output should be teed, based on the configured mode, exit code,
    /// and minimum size threshold.
    /// </summary>
    /// <param name="config">The tee configuration.</param>
    /// <param name="rawLen">The length, in characters, of the raw output.</param>
    /// <param name="exitCode">The exit code of the command that produced the output.</param>
    /// <param name="teeDir">The candidate tee directory to return if teeing should occur.</param>
    /// <returns><paramref name="teeDir"/> if output should be teed; otherwise null.</returns>
    public static string? ShouldTee(TeeConfig config, int rawLen, int exitCode, string? teeDir)
    {
        if (!config.Enabled)
        {
            return null;
        }

        switch (config.Mode)
        {
            case TeeMode.Never:
                return null;
            case TeeMode.Failures:
                if (exitCode == 0)
                {
                    return null;
                }
                break;
            case TeeMode.Always:
                break;
        }

        if (rawLen < MinTeeSize)
        {
            return null;
        }

        return teeDir;
    }

    /// <summary>
    /// Writes raw output to a tee file in <paramref name="teeDir"/>, truncating content
    /// larger than <paramref name="maxFileSize"/> and pruning old files beyond <paramref name="maxFiles"/>.
    /// </summary>
    /// <param name="raw">The raw content to write.</param>
    /// <param name="commandSlug">A short slug identifying the command, used in the filename.</param>
    /// <param name="teeDir">The directory to write the file into.</param>
    /// <param name="maxFileSize">The maximum file size, in bytes, before content is truncated.</param>
    /// <param name="maxFiles">The maximum number of tee files to retain.</param>
    /// <returns>The full path of the written file, or null if writing failed.</returns>
    public static string? WriteTeeFile(
        string raw,
        string commandSlug,
        string teeDir,
        int maxFileSize,
        int maxFiles
    )
    {
        try
        {
            Directory.CreateDirectory(teeDir);

            string slug = SanitizeSlug(commandSlug);
            long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string filename = $"{epoch}_{slug}.log";
            string filepath = Path.Combine(teeDir, filename);

            string content;
            if (raw.Length > maxFileSize)
            {
                int boundary = maxFileSize;
                if (boundary < raw.Length && char.IsLowSurrogate(raw[boundary]))
                {
                    boundary--;
                }
                content = raw.Substring(0, boundary) + $"\n\n--- truncated at {maxFileSize} bytes ---";
            }
            else
            {
                content = raw;
            }

            File.WriteAllText(filepath, content);
            CleanupOldFiles(teeDir, maxFiles);
            return filepath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tees raw output to disk if <see cref="ShouldTee"/> permits it, honoring the
    /// <c>RTK_TEE=0</c> environment variable kill switch.
    /// </summary>
    /// <param name="raw">The raw content to potentially tee.</param>
    /// <param name="commandSlug">A short slug identifying the command, used in the filename.</param>
    /// <param name="exitCode">The exit code of the command that produced the output.</param>
    /// <param name="config">The tee configuration, or null to use defaults.</param>
    /// <returns>The full path of the written file, or null if nothing was written.</returns>
    public static string? TeeRaw(string raw, string commandSlug, int exitCode, TeeConfig? config = null)
    {
        if (Environment.GetEnvironmentVariable("RTK_TEE") == "0")
        {
            return null;
        }

        config ??= new TeeConfig();
        var teeDir = GetTeeDir(config);
        if (string.IsNullOrEmpty(teeDir))
        {
            return null;
        }

        var targetDir = ShouldTee(config, raw.Length, exitCode, teeDir);
        if (string.IsNullOrEmpty(targetDir))
        {
            return null;
        }

        return WriteTeeFile(
            raw,
            commandSlug,
            targetDir,
            config.MaxFileSize,
            config.MaxFiles
        );
    }

    /// <summary>
    /// Formats a file path for display, replacing the user's home directory prefix with
    /// <c>~</c> and normalizing directory separators to forward slashes.
    /// </summary>
    /// <param name="path">The path to format.</param>
    /// <returns>The display-friendly path.</returns>
    public static string DisplayPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
        {
            var relative = path.Substring(home.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return "~/" + relative.Replace('\\', '/');
        }
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Formats a recovery hint pointing at a teed output file.
    /// </summary>
    /// <param name="path">The path of the teed file.</param>
    /// <returns>The formatted hint string.</returns>
    public static string FormatHint(string path)
    {
        return $"[full output: {DisplayPath(path)}]";
    }

    /// <summary>
    /// Tees raw output to disk and, if written, returns a formatted recovery hint pointing at it.
    /// </summary>
    /// <param name="raw">The raw content to potentially tee.</param>
    /// <param name="commandSlug">A short slug identifying the command, used in the filename.</param>
    /// <param name="exitCode">The exit code of the command that produced the output.</param>
    /// <param name="config">The tee configuration, or null to use defaults.</param>
    /// <returns>A formatted recovery hint, or null if nothing was written.</returns>
    public static string? TeeAndHint(string raw, string commandSlug, int exitCode, TeeConfig? config = null)
    {
        var path = TeeRaw(raw, commandSlug, exitCode, config);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return FormatHint(path);
    }

    private static string? ForceTeePath(string content, string commandSlug, TeeConfig? config = null)
    {
        if (Environment.GetEnvironmentVariable("RTK_TEE") == "0")
        {
            return null;
        }

        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        config ??= new TeeConfig();
        if (!config.Enabled)
        {
            return null;
        }

        var teeDir = GetTeeDir(config);
        if (string.IsNullOrEmpty(teeDir))
        {
            return null;
        }

        return WriteTeeFile(
            content,
            commandSlug,
            teeDir,
            config.MaxFileSize,
            config.MaxFiles
        );
    }

    /// <summary>
    /// Unconditionally tees content to disk (ignoring <see cref="ShouldTee"/> thresholds, but still
    /// honoring <c>RTK_TEE=0</c> and <see cref="TeeConfig.Enabled"/>) and returns a formatted recovery hint.
    /// </summary>
    /// <param name="raw">The content to tee.</param>
    /// <param name="commandSlug">A short slug identifying the command, used in the filename.</param>
    /// <param name="config">The tee configuration, or null to use defaults.</param>
    /// <returns>A formatted recovery hint, or null if nothing was written.</returns>
    public static string? ForceTeeHint(string raw, string commandSlug, TeeConfig? config = null)
    {
        var path = ForceTeePath(raw, commandSlug, config);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return FormatHint(path);
    }

    /// <summary>
    /// Unconditionally tees content to disk and returns a recovery hint referencing the
    /// remaining lines starting at <paramref name="lineOffset"/> (e.g. for truncated output).
    /// </summary>
    /// <param name="content">The content to tee.</param>
    /// <param name="commandSlug">A short slug identifying the command, used in the filename.</param>
    /// <param name="lineOffset">The 1-based line number where the omitted content resumes.</param>
    /// <param name="config">The tee configuration, or null to use defaults.</param>
    /// <returns>A formatted tail-recovery hint, or null if nothing was written.</returns>
    public static string? ForceTeeTailHint(string content, string commandSlug, int lineOffset, TeeConfig? config = null)
    {
        var path = ForceTeePath(content, commandSlug, config);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return $"[see remaining: tail -n +{lineOffset} {DisplayPath(path)}]";
    }
}
