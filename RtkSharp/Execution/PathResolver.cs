using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RtkSharp.Execution;

public static class PathResolver
{
    public static string Resolve(string name)
    {
        return Resolve(
            name,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            Environment.CurrentDirectory
        );
    }

    public static string Resolve(string name, string? pathEnv, string? pathExtEnv, string currentDirectory)
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : name;
        }

        if (File.Exists(name))
        {
            return Path.GetFullPath(name);
        }

        var rootedCandidate = Path.Combine(currentDirectory, name);
        if (File.Exists(rootedCandidate))
        {
            return Path.GetFullPath(rootedCandidate);
        }

        var paths = (pathEnv ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var extensions = GetWindowsExtensions(pathExtEnv);
            var hasExtension = Path.HasExtension(name);

            foreach (var path in paths)
            {
                foreach (var candidate in CandidateNames(name, hasExtension, extensions))
                {
                    var fullPath = Path.Combine(path, candidate);
                    if (File.Exists(fullPath))
                    {
                        return Path.GetFullPath(fullPath);
                    }
                }
            }
        }
        else
        {
            foreach (var path in paths)
            {
                var fullPath = Path.Combine(path, name);
                if (File.Exists(fullPath))
                {
                    return Path.GetFullPath(fullPath);
                }
            }
        }

        return name;
    }

    private static string[] GetWindowsExtensions(string? pathExtEnv)
    {
        var value = string.IsNullOrWhiteSpace(pathExtEnv)
            ? ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC"
            : pathExtEnv;

        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<string> CandidateNames(string name, bool hasExtension, IEnumerable<string> extensions)
    {
        if (hasExtension)
        {
            yield return name;
            yield break;
        }

        foreach (var extension in extensions)
        {
            yield return name + extension;
        }
    }
}
