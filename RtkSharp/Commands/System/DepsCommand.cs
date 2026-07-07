using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;

namespace RtkSharp.Commands.System;

/// <summary>
/// Implements the <c>rtk deps</c> CLI verb: summarizes project dependencies from any of five
/// recognized manifest/lock files found directly in the target directory (Cargo.toml,
/// package.json, requirements.txt, pyproject.toml, go.mod). Faithful port of Rust
/// <c>src/cmds/system/deps.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>RTK_META_COMMANDS classification.</b> Rust's <c>RTK_META_COMMANDS</c> (<c>main.rs</c>:1170-1191)
/// includes <c>"deps"</c> — a clap-layer parse failure (an unrecognized flag or a second positional)
/// exits directly via clap's own error, never falling back to a raw PATH-exec of a real <c>deps</c>
/// binary. Following the established <see cref="Analytics.SessionCommand"/>/<see cref="Hooks.HookAuditCommand"/>
/// convention for ported meta-commands, <see cref="ParseArgs"/> throws the internal
/// <see cref="DepsArgsException"/> (never <see cref="Cli.CommandArgumentParseException"/>, which routes
/// to the PASSTHROUGH fallback path — wrong for a meta-command) and <see cref="RunAsync"/> catches it,
/// prints a short <c>error: ...</c> message, and returns 2 (clap's usage-error exit code).
/// </para>
/// <para>
/// <b>No test module in the Rust source.</b> <c>src/cmds/system/deps.rs</c> has no
/// <c>#[cfg(test)] mod tests</c> block at all, so there are no oracle unit tests to port verbatim;
/// <c>DepsCommandTests.cs</c> adds new coverage exercising each of the five
/// <c>Summarize*</c> helpers directly, matching the shapes the Rust regexes/parsers are known to
/// produce.
/// </para>
/// <para>
/// <b>Output uses <c>print!</c>, not <c>println!</c>.</b> <c>deps.rs</c>:76 writes the accumulated
/// <c>rtk</c> string with no appended trailing newline beyond whatever each <c>Summarize*</c> helper's
/// own per-line <c>\n</c> already produced. <see cref="RunCore"/> mirrors this: it never appends an
/// extra <c>"\n"</c> itself.
/// </para>
/// <para>
/// <b>Manifest reads never fail the command.</b> Rust's <c>fs::read_to_string(&amp;path).unwrap_or_default()</c>
/// (<c>deps.rs</c>:35, 43, 51, 59, 67) silently treats an unreadable file (permissions, a race,
/// non-UTF-8 content) as empty content for the <i>tracking</i> baseline only — the corresponding
/// <c>Summarize*</c> call right afterward uses its own unguarded <c>fs::read_to_string(path)?</c>, so a
/// genuinely unreadable file still propagates as an error from that call (and, per the existing
/// <c>?</c>-propagation convention, aborts the whole command). This port matches: the raw/tracking
/// read uses a swallow-errors helper, while each <c>Summarize*</c> call reads the file directly and
/// lets <see cref="IOException"/> propagate to <see cref="RunAsync"/>'s outer catch (exit 1).
/// </para>
/// </remarks>
public static class DepsCommand
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
    /// Registry entry point. Runs <c>rtk deps</c> with the given arguments (the remainder after the
    /// <c>deps</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>deps</c>.</param>
    /// <returns>0 on success, 2 on a clap-equivalent argument parse failure, 1 on any other failure.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DepsArgs parsed;
        try
        {
            parsed = ParseArgs(args);
        }
        catch (DepsArgsException ex)
        {
            Console.Error.Write(ex.Message + "\n");
            return Task.FromResult(2);
        }

        try
        {
            return Task.FromResult(RunCore(parsed.Path, RuntimeOptions.Verbosity, Console.Out, Console.Error));
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return Task.FromResult(1);
        }
    }

    /// <summary>
    /// The testable core: scans <paramref name="path"/> (or its parent directory, when it is a file)
    /// for recognized manifest files and prints a combined summary. Faithful port of <c>deps::run</c>
    /// (<c>deps.rs</c>:15-79).
    /// </summary>
    /// <param name="path">The project path (a directory to scan, or a file whose parent is scanned).</param>
    /// <param name="verbose">The global verbosity level (mirrors Rust's <c>cli.verbose: u8</c>).</param>
    /// <param name="stdout">The destination for the summary.</param>
    /// <param name="stderr">The destination for the verbose diagnostic line.</param>
    /// <returns>0 (this command has no failure path once argument parsing succeeds and manifest files
    /// are readable).</returns>
    internal static int RunCore(string path, int verbose, TextWriter stdout, TextWriter stderr)
    {
        // Rust: `if path.is_file() { path.parent().unwrap_or(Path::new(".")) } else { path }`.
        // Path::parent() of a bare filename (e.g. "Cargo.toml") returns Some("") — an empty
        // path, not None — so the ".unwrap_or(...)" fallback only fires for paths with no
        // parent component at all (e.g. a root path). Path.GetDirectoryName mirrors this:
        // it returns "" for a bare filename and null only when there is truly no parent.
        var dir = File.Exists(path) ? (Path.GetDirectoryName(path) ?? ".") : path;

        if (verbose > 0)
        {
            stderr.Write($"Scanning dependencies in: {dir}\n");
        }

        var found = false;
        var rtk = new StringBuilder();
        var raw = new StringBuilder();

        var cargoPath = Path.Combine(dir, "Cargo.toml");
        if (File.Exists(cargoPath))
        {
            found = true;
            raw.Append(ReadOrEmpty(cargoPath));
            rtk.Append("Rust (Cargo.toml):\n");
            rtk.Append(SummarizeCargo(File.ReadAllText(cargoPath)));
        }

        var packagePath = Path.Combine(dir, "package.json");
        if (File.Exists(packagePath))
        {
            found = true;
            raw.Append(ReadOrEmpty(packagePath));
            rtk.Append("Node.js (package.json):\n");
            rtk.Append(SummarizePackageJson(File.ReadAllText(packagePath)));
        }

        var requirementsPath = Path.Combine(dir, "requirements.txt");
        if (File.Exists(requirementsPath))
        {
            found = true;
            raw.Append(ReadOrEmpty(requirementsPath));
            rtk.Append("Python (requirements.txt):\n");
            rtk.Append(SummarizeRequirements(File.ReadAllText(requirementsPath)));
        }

        var pyprojectPath = Path.Combine(dir, "pyproject.toml");
        if (File.Exists(pyprojectPath))
        {
            found = true;
            raw.Append(ReadOrEmpty(pyprojectPath));
            rtk.Append("Python (pyproject.toml):\n");
            rtk.Append(SummarizePyproject(File.ReadAllText(pyprojectPath)));
        }

        var gomodPath = Path.Combine(dir, "go.mod");
        if (File.Exists(gomodPath))
        {
            found = true;
            raw.Append(ReadOrEmpty(gomodPath));
            rtk.Append("Go (go.mod):\n");
            rtk.Append(SummarizeGoMod(File.ReadAllText(gomodPath)));
        }

        if (!found)
        {
            rtk.Append($"No dependency files found in {dir}");
        }

        stdout.Write(rtk.ToString());

        var timer = TimedExecution.Start();
        timer.Track("cat */deps", "rtk deps", raw.ToString(), rtk.ToString());

        return 0;
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Summarizes a <c>Cargo.toml</c>'s <c>[dependencies]</c>/<c>[dev-dependencies]</c> sections.
    /// Faithful port of <c>summarize_cargo_str</c> (<c>deps.rs</c>:81-132).
    /// </summary>
    /// <param name="content">The raw <c>Cargo.toml</c> content.</param>
    /// <returns>The rendered summary section.</returns>
    internal static string SummarizeCargo(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var currentSection = string.Empty;
        var deps = new List<string>();
        var devDeps = new List<string>();
        var outBuilder = new StringBuilder();

        foreach (var line in SplitLines(content))
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
    internal static string SummarizePackageJson(string content)
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
    internal static string SummarizeRequirements(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var deps = new List<string>();
        var outBuilder = new StringBuilder();

        foreach (var rawLine in SplitLines(content))
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
    internal static string SummarizePyproject(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var inDeps = false;
        var deps = new List<string>();
        var outBuilder = new StringBuilder();

        foreach (var line in SplitLines(content))
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
    internal static string SummarizeGoMod(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var moduleName = string.Empty;
        var goVersion = string.Empty;
        var deps = new List<string>();
        var inRequire = false;
        var outBuilder = new StringBuilder();

        foreach (var rawLine in SplitLines(content))
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

    /// <summary>
    /// Splits <paramref name="content"/> the way Rust's <c>str::lines()</c> does: split on <c>\n</c>,
    /// each line's trailing <c>\r</c> stripped, and no trailing empty entry for content ending in a
    /// newline.
    /// </summary>
    /// <param name="content">The raw file content.</param>
    /// <returns>The content's lines.</returns>
    internal static IEnumerable<string> SplitLines(string content) => ReadCommand.SplitLines(content);

    /// <summary>Parsed <c>rtk deps</c> arguments.</summary>
    /// <param name="Path">The project path to scan (default <c>"."</c>).</param>
    internal readonly record struct DepsArgs(string Path);

    /// <summary>
    /// Parses <c>rtk deps</c>'s arguments: an optional <c>path</c> positional (default <c>"."</c>).
    /// Faithful port of the clap <c>Commands::Deps</c> variant (<c>main.rs</c>:241-245).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>deps</c>.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="DepsArgsException">The arguments are malformed or unrecognized.</exception>
    internal static DepsArgs ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? path = null;

        foreach (var a in args)
        {
            if (path is not null)
            {
                throw new DepsArgsException($"error: unexpected argument '{a}' found");
            }

            path = a;
        }

        return new DepsArgs(path ?? ".");
    }
}

/// <summary>
/// A usage error from <see cref="DepsCommand.ParseArgs"/>, carrying the message to print verbatim to
/// stderr before exiting 2 (clap's usage-error exit code).
/// </summary>
internal sealed class DepsArgsException(string message) : Exception(message);
