using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RtkSharp.Execution;

/// <summary>
/// Resolves an executable name or relative path to a full file path by searching the
/// current directory and the <c>PATH</c> environment variable, applying <c>PATHEXT</c>
/// extension matching on Windows.
/// </summary>
public static class PathResolver
{
    /// <summary>
    /// Resolves an executable name to a full path, using the current process's
    /// <c>PATH</c>, <c>PATHEXT</c>, and current directory.
    /// </summary>
    /// <param name="name">The executable name or path to resolve.</param>
    /// <returns>The resolved full path, or <paramref name="name"/> unchanged if it could not be resolved.</returns>
    public static string Resolve(string name)
    {
        return Resolve(
            name,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            Environment.CurrentDirectory
        );
    }

    /// <summary>
    /// Resolves an executable name to a full path, searching the given directory and
    /// <c>PATH</c> entries, with explicit <c>PATHEXT</c> extension matching on Windows.
    /// </summary>
    /// <param name="name">The executable name or path to resolve.</param>
    /// <param name="pathEnv">The <c>PATH</c>-style, platform path-separator-delimited search list.</param>
    /// <param name="pathExtEnv">The <c>PATHEXT</c>-style extension list used on Windows, or null to use the default set.</param>
    /// <param name="currentDirectory">The directory to check before searching <paramref name="pathEnv"/>.</param>
    /// <returns>The resolved full path, or <paramref name="name"/> unchanged if it could not be resolved.</returns>
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

    /// <summary>
    /// Resolves an <see cref="ExecutionRequest"/>'s executable name to a full path, honoring
    /// any <c>PATH</c>/<c>PATHEXT</c> overrides in <see cref="ExecutionRequest.Environment"/>
    /// (falling back to the current process's own environment) and the request's
    /// <see cref="ExecutionRequest.WorkingDirectory"/> (falling back to the current directory).
    /// Shared by every executor that spawns a child process from an <see cref="ExecutionRequest"/>.
    /// </summary>
    /// <param name="request">The execution request whose <see cref="ExecutionRequest.FileName"/> to resolve.</param>
    /// <returns>The resolved full path, or the request's file name unchanged if it could not be resolved.</returns>
    public static string ResolveForRequest(ExecutionRequest request)
    {
        string? requestPath = null;
        string? requestPathExt = null;
        request.Environment?.TryGetValue("PATH", out requestPath);
        request.Environment?.TryGetValue("PATHEXT", out requestPathExt);

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Environment.CurrentDirectory
            : request.WorkingDirectory;

        return Resolve(
            request.FileName,
            requestPath ?? Environment.GetEnvironmentVariable("PATH"),
            requestPathExt ?? Environment.GetEnvironmentVariable("PATHEXT"),
            workingDirectory
        );
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
