using System;
using System.IO;
using System.Linq;

namespace RtkSharp.Core;

public enum TeeMode
{
    Failures,
    Always,
    Never
}

public class TeeConfig
{
    public bool Enabled { get; set; } = true;
    public TeeMode Mode { get; set; } = TeeMode.Failures;
    public int MaxFiles { get; set; } = 20;
    public int MaxFileSize { get; set; } = 1_048_576; // 1MB
    public string? Directory { get; set; }
}

public static class Tee
{
    private const int MinTeeSize = 500;

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

    public static string FormatHint(string path)
    {
        return $"[full output: {DisplayPath(path)}]";
    }

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

    public static string? ForceTeeHint(string raw, string commandSlug, TeeConfig? config = null)
    {
        var path = ForceTeePath(raw, commandSlug, config);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return FormatHint(path);
    }

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
