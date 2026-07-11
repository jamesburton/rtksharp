using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RtkSharp.Filters.Commands.System;

/// <summary>
/// Pure per-ecosystem dependency-manifest summarizers behind the <c>rtk deps</c> CLI verb.
/// Ported from Rust <c>src/cmds/system/deps.rs</c>. The directory scanning, file reading, and
/// tracking logic lives in <see cref="RtkSharp.Commands.System.DepsCommand"/> (<c>RtkSharp</c>).
/// </summary>
public static class DepsFilters
{
    // src/core/truncate.rs: CAP_WARNINGS = 10.
    private const int MaxDeps = 10;

    // src/core/truncate.rs: reduced(CAP_WARNINGS, 5) = 5 (dev deps are secondary to prod).
    private const int MaxDevDeps = 5;

    private static readonly Regex CargoDepRegex = new(
        """^([a-zA-Z0-9_-]+)\s*=\s*(?:"([^"]+)"|.*version\s*=\s*"([^"]+)")""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CargoSectionRegex = new(
        @"^\[([^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RequirementsDepRegex = new(
        @"^([a-zA-Z0-9_-]+)([=<>!~]+.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Summarizes a <c>Cargo.toml</c>'s <c>[dependencies]</c>/<c>[dev-dependencies]</c> sections.
    /// Faithful port of <c>summarize_cargo_str</c> (<c>deps.rs</c>:81-132).
    /// </summary>
    /// <param name="content">The raw <c>Cargo.toml</c> content.</param>
    /// <returns>The rendered summary section.</returns>
    public static string SummarizeCargo(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var currentSection = string.Empty;
        var deps = new List<string>();
        var devDeps = new List<string>();
        var outBuilder = new StringBuilder();

        foreach (var line in ReadFilters.SplitLines(content))
        {
            var sectionMatch = CargoSectionRegex.Match(line);
            if (sectionMatch.Success)
            {
                currentSection = sectionMatch.Groups[1].Value;
                continue;
            }

            var depMatch = CargoDepRegex.Match(line);
            if (!depMatch.Success)
            {
                continue;
            }

            var name = depMatch.Groups[1].Value;
            var version = depMatch.Groups[2].Success
                ? depMatch.Groups[2].Value
                : depMatch.Groups[3].Success ? depMatch.Groups[3].Value : "*";
            var dep = $"{name} ({version})";

            switch (currentSection)
            {
                case "dependencies":
                    deps.Add(dep);
                    break;
                case "dev-dependencies":
                    devDeps.Add(dep);
                    break;
            }
        }

        if (deps.Count > 0)
        {
            outBuilder.Append($"  Dependencies ({deps.Count}):\n");
            foreach (var d in deps.Take(MaxDeps))
            {
                outBuilder.Append($"    {d}\n");
            }

            if (deps.Count > MaxDeps)
            {
                outBuilder.Append($"    ... +{deps.Count - MaxDeps} more\n");
            }
        }

        if (devDeps.Count > 0)
        {
            outBuilder.Append($"  Dev ({devDeps.Count}):\n");
            foreach (var d in devDeps.Take(MaxDevDeps))
            {
                outBuilder.Append($"    {d}\n");
            }

            if (devDeps.Count > MaxDevDeps)
            {
                outBuilder.Append($"    ... +{devDeps.Count - MaxDevDeps} more\n");
            }
        }

        return outBuilder.ToString();
    }

    /// <summary>
    /// Summarizes a <c>package.json</c>'s name/version, <c>dependencies</c>, and
    /// <c>devDependencies</c>. Faithful port of <c>summarize_package_json_str</c> (<c>deps.rs</c>:134-168).
    /// </summary>
    /// <param name="content">The raw <c>package.json</c> content.</param>
    /// <returns>The rendered summary section.</returns>
    /// <exception cref="JsonException"><paramref name="content"/> is not valid JSON.</exception>
    public static string SummarizePackageJson(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var doc = JsonDocument.Parse(content);
        var json = doc.RootElement;
        var outBuilder = new StringBuilder();

        if (json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("name", out var nameEl)
            && nameEl.ValueKind == JsonValueKind.String)
        {
            var version = json.TryGetProperty("version", out var versionEl) && versionEl.ValueKind == JsonValueKind.String
                ? versionEl.GetString()
                : "?";
            outBuilder.Append($"  {nameEl.GetString()} @ {version}\n");
        }

        if (json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("dependencies", out var depsEl)
            && depsEl.ValueKind == JsonValueKind.Object)
        {
            var deps = depsEl.EnumerateObject().ToList();
            outBuilder.Append($"  Dependencies ({deps.Count}):\n");
            for (var i = 0; i < deps.Count; i++)
            {
                if (i >= MaxDeps)
                {
                    outBuilder.Append($"    ... +{deps.Count - MaxDeps} more\n");
                    break;
                }

                var version = deps[i].Value.ValueKind == JsonValueKind.String ? deps[i].Value.GetString() : "*";
                outBuilder.Append($"    {deps[i].Name} ({version})\n");
            }
        }

        if (json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("devDependencies", out var devDepsEl)
            && devDepsEl.ValueKind == JsonValueKind.Object)
        {
            var devDeps = devDepsEl.EnumerateObject().ToList();
            outBuilder.Append($"  Dev Dependencies ({devDeps.Count}):\n");
            for (var i = 0; i < devDeps.Count; i++)
            {
                if (i >= MaxDevDeps)
                {
                    outBuilder.Append($"    ... +{devDeps.Count - MaxDevDeps} more\n");
                    break;
                }

                outBuilder.Append($"    {devDeps[i].Name}\n");
            }
        }

        return outBuilder.ToString();
    }

    /// <summary>
    /// Summarizes a <c>requirements.txt</c>'s package list (blank lines and <c>#</c> comments
    /// skipped). Faithful port of <c>summarize_requirements_str</c> (<c>deps.rs</c>:170-196).
    /// </summary>
    /// <param name="content">The raw <c>requirements.txt</c> content.</param>
    /// <returns>The rendered summary section.</returns>
    public static string SummarizeRequirements(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var deps = new List<string>();
        var outBuilder = new StringBuilder();

        foreach (var rawLine in ReadFilters.SplitLines(content))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var match = RequirementsDepRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var version = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            deps.Add($"{name}{version}");
        }

        outBuilder.Append($"  Packages ({deps.Count}):\n");
        foreach (var d in deps.Take(MaxDeps))
        {
            outBuilder.Append($"    {d}\n");
        }

        if (deps.Count > MaxDeps)
        {
            outBuilder.Append($"    ... +{deps.Count - MaxDeps} more\n");
        }

        return outBuilder.ToString();
    }

    /// <summary>
    /// Summarizes a <c>pyproject.toml</c>'s <c>dependencies = [...]</c> array (a simple bracket
    /// scanner, not a TOML parser). Faithful port of <c>summarize_pyproject_str</c> (<c>deps.rs</c>:198-232).
    /// </summary>
    /// <param name="content">The raw <c>pyproject.toml</c> content.</param>
    /// <returns>The rendered summary section.</returns>
    public static string SummarizePyproject(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var inDeps = false;
        var deps = new List<string>();
        var outBuilder = new StringBuilder();

        foreach (var line in ReadFilters.SplitLines(content))
        {
            if (line.Contains("dependencies", StringComparison.Ordinal) && line.Contains('[', StringComparison.Ordinal))
            {
                inDeps = true;
                continue;
            }

            if (!inDeps)
            {
                continue;
            }

            if (line.Trim() == "]")
            {
                break;
            }

            var trimmed = line.Trim().Trim('"', '\'', ',');
            if (trimmed.Length != 0)
            {
                deps.Add(trimmed);
            }
        }

        if (deps.Count > 0)
        {
            outBuilder.Append($"  Dependencies ({deps.Count}):\n");
            foreach (var d in deps.Take(MaxDeps))
            {
                outBuilder.Append($"    {d}\n");
            }

            if (deps.Count > MaxDeps)
            {
                outBuilder.Append($"    ... +{deps.Count - MaxDeps} more\n");
            }
        }

        return outBuilder.ToString();
    }

    /// <summary>
    /// Summarizes a <c>go.mod</c>'s module name, Go version, and <c>require</c> entries (both the
    /// block form and single-line form). Faithful port of <c>summarize_gomod_str</c> (<c>deps.rs</c>:234-275).
    /// </summary>
    /// <param name="content">The raw <c>go.mod</c> content.</param>
    /// <returns>The rendered summary section.</returns>
    public static string SummarizeGoMod(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var moduleName = string.Empty;
        var goVersion = string.Empty;
        var deps = new List<string>();
        var inRequire = false;
        var outBuilder = new StringBuilder();

        foreach (var rawLine in ReadFilters.SplitLines(content))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("module ", StringComparison.Ordinal))
            {
                moduleName = line["module ".Length..];
            }
            else if (line.StartsWith("go ", StringComparison.Ordinal))
            {
                goVersion = line["go ".Length..];
            }
            else if (line == "require (")
            {
                inRequire = true;
            }
            else if (line == ")")
            {
                inRequire = false;
            }
            else if (inRequire && !line.StartsWith("//", StringComparison.Ordinal))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    deps.Add($"{parts[0]} {parts[1]}");
                }
            }
            else if (line.StartsWith("require ", StringComparison.Ordinal) && !line.Contains('(', StringComparison.Ordinal))
            {
                deps.Add(line["require ".Length..]);
            }
        }

        if (moduleName.Length != 0)
        {
            outBuilder.Append($"  {moduleName} (go {goVersion})\n");
        }

        if (deps.Count > 0)
        {
            outBuilder.Append($"  Dependencies ({deps.Count}):\n");
            foreach (var d in deps.Take(MaxDeps))
            {
                outBuilder.Append($"    {d}\n");
            }

            if (deps.Count > MaxDeps)
            {
                outBuilder.Append($"    ... +{deps.Count - MaxDeps} more\n");
            }
        }

        return outBuilder.ToString();
    }
}
