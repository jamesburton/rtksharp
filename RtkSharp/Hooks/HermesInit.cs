using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RtkSharp.Hooks;

/// <summary>
/// Hermes CLI plugin-install support for <c>rtk init --agent hermes</c>. Faithful port of the Hermes
/// subset of Rust <c>src/hooks/init.rs</c>: <c>run_hermes_mode</c>/<c>run_hermes_mode_at</c>
/// (init.rs:1765-1821), <c>uninstall_hermes</c>/<c>uninstall_hermes_at</c> (init.rs:1821-1903),
/// <c>patch_hermes_config</c>/<c>unpatch_hermes_config</c>/<c>rewrite_hermes_config</c>
/// (init.rs:1903-1981), the YAML-surgery helpers (init.rs:1981-2234), and
/// <c>resolve_hermes_home</c>/<c>resolve_hermes_home_from_env</c> (init.rs:2763-2791).
/// </summary>
/// <remarks>
/// <para>
/// <b>Unlike every other agent target, Hermes installs a plugin directory plus edits an existing
/// YAML config file via targeted line-based surgery</b> — not a structured YAML parse/serialize
/// round-trip. This deliberately preserves the user's original formatting, comments, and
/// indentation style (including PyYAML's "indentation-less sequence" style, where a list's items
/// sit at the <i>same</i> indent as their parent key rather than nested two spaces deeper) rather
/// than normalizing the whole file through a YAML library. The core primitive is
/// <see cref="RewriteHermesConfig"/>, operating on the file as a list of newline-inclusive line
/// strings (mirroring Rust's <c>split_inclusive('\n')</c> via <see cref="SplitYamlLines"/>) so every
/// edit is a targeted splice rather than a full re-serialization.
/// </para>
/// <para>
/// <b>Hermes has no project vs. global scope distinction</b> (unlike Claude Code/Codex): it always
/// targets <c>$HERMES_HOME/config.yaml</c> (or <c>~/.hermes/config.yaml</c> when unset), matching
/// Rust's <c>run_hermes_mode</c>, which takes no scope parameter at all. <see cref="RunHermes"/>'s
/// <paramref name="global">global</paramref> parameter exists purely so the agent dispatcher in
/// <c>InitCommand</c> can call every agent's entry point with a uniform signature; it is otherwise
/// unused here — a deliberate judgment call since the Rust oracle has no such parameter to mirror.
/// </para>
/// </remarks>
public static class HermesInit
{
    /// <summary>Fallback subdirectory of the user's home directory (Rust <c>HERMES_DIR</c> = <c>".hermes"</c>).</summary>
    internal const string HermesDir = ".hermes";

    /// <summary>Environment variable honored by <see cref="ResolveHermesHome"/> (Rust <c>HERMES_HOME</c>).</summary>
    internal const string HermesHomeEnvVar = "HERMES_HOME";

    private const string HermesPluginsSubdir = "plugins";

    /// <summary>The plugin name RTK installs/looks for in Hermes's <c>enabled</c> plugin list (Rust <c>HERMES_PLUGIN_NAME</c>).</summary>
    internal const string HermesPluginName = "rtk-rewrite";

    private const string HermesPluginInitFile = "__init__.py";
    private const string HermesPluginManifestFile = "plugin.yaml";
    private const string HermesConfigFile = "config.yaml";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Embedded Hermes Python plugin adapter. Byte-for-byte copy of Rust <c>HERMES_PLUGIN_INIT</c>
    /// (<c>include_str!("../../hooks/hermes/rtk-rewrite/__init__.py")</c>), loaded from an embedded
    /// resource (like <see cref="InitArtifacts.RtkSlim"/>/<see cref="CodexInit.RtkSlimCodex"/>) so it
    /// tracks the exact bytes — including whatever CRLF/LF the current checkout produced — of
    /// <c>hooks/hermes/rtk-rewrite/__init__.py</c>.
    /// </summary>
    public static readonly string HermesPluginInit = LoadEmbeddedResource("RtkSharp.Hooks.hermes-rtk-rewrite-init.py");

    /// <summary>
    /// Embedded Hermes plugin manifest. Byte-for-byte copy of Rust <c>HERMES_PLUGIN_YAML</c>
    /// (<c>include_str!("../../hooks/hermes/rtk-rewrite/plugin.yaml")</c>), loaded the same way as
    /// <see cref="HermesPluginInit"/>.
    /// </summary>
    public static readonly string HermesPluginManifest = LoadEmbeddedResource("RtkSharp.Hooks.hermes-rtk-rewrite-plugin.yaml");

    private static string LoadEmbeddedResource(string logicalName)
    {
        var assembly = typeof(HermesInit).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InitAbortException($"Embedded resource {logicalName} is missing");
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Resolves the Hermes home directory: <c>$HERMES_HOME</c> if set and non-empty, else
    /// <c>{home}/.hermes</c>. Port of Rust <c>resolve_hermes_home</c> (init.rs:2763), which delegates
    /// to <see cref="ResolveHermesHomeFrom"/> (the pure, test-friendly core, ported as
    /// <c>resolve_hermes_home_from_env</c>, init.rs:2770).
    /// </summary>
    /// <returns>The resolved Hermes home directory path.</returns>
    internal static string ResolveHermesHome() =>
        ResolveHermesHomeFrom(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable(HermesHomeEnvVar));

    /// <summary>
    /// Pure core of <see cref="ResolveHermesHome"/>: prefers <paramref name="hermesHomeEnv"/> (if
    /// non-empty), else falls back to <c>{homeDir}/.hermes</c>. Port of Rust
    /// <c>resolve_hermes_home_from_env</c> (init.rs:2770).
    /// </summary>
    /// <param name="homeDir">The user's home directory, or <see langword="null"/>/empty if undeterminable.</param>
    /// <param name="hermesHomeEnv">The <c>$HERMES_HOME</c> value, or <see langword="null"/>/empty if unset.</param>
    /// <returns>The resolved Hermes home directory path.</returns>
    internal static string ResolveHermesHomeFrom(string? homeDir, string? hermesHomeEnv)
    {
        if (!string.IsNullOrEmpty(hermesHomeEnv))
        {
            return hermesHomeEnv;
        }

        if (string.IsNullOrEmpty(homeDir))
        {
            throw new InitAbortException("Cannot determine Hermes home directory. Set $HERMES_HOME or $HOME.");
        }

        return Path.Combine(homeDir, HermesDir);
    }

    /// <summary>
    /// Returns the Hermes RTK plugin's install directory under <paramref name="hermesHome"/>. Port
    /// of Rust <c>hermes_plugin_dir</c> (init.rs:1770).
    /// </summary>
    /// <param name="hermesHome">The resolved Hermes home directory.</param>
    /// <returns>The RTK plugin directory (<c>{hermesHome}/plugins/rtk-rewrite</c>).</returns>
    internal static string HermesPluginDir(string hermesHome) =>
        Path.Combine(hermesHome, HermesPluginsSubdir, HermesPluginName);

    /// <summary>
    /// Runs <c>rtk init --agent hermes</c>: resolves <c>$HERMES_HOME</c> and delegates to
    /// <see cref="RunHermesAt"/>. Port of Rust <c>run_hermes_mode</c> (init.rs:1767).
    /// </summary>
    /// <param name="global">
    /// Unused: see this class's remarks. Accepted only for a uniform agent-dispatch signature.
    /// </param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void RunHermes(bool global, InitContext ctx)
    {
        _ = global;
        var hermesHome = ResolveHermesHome();
        RunHermesAt(hermesHome, ctx);
    }

    /// <summary>
    /// Writes the Hermes RTK plugin files and patches <c>config.yaml</c> to enable the plugin, then
    /// (outside dry-run) prints the success report. Port of Rust <c>run_hermes_mode_at</c>
    /// (init.rs:1777).
    /// </summary>
    /// <param name="hermesHome">The resolved Hermes home directory.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void RunHermesAt(string hermesHome, InitContext ctx)
    {
        var pluginDir = HermesPluginDir(hermesHome);
        if (!ctx.DryRun)
        {
            Directory.CreateDirectory(pluginDir);
        }

        var initPath = Path.Combine(pluginDir, HermesPluginInitFile);
        var manifestPath = Path.Combine(pluginDir, HermesPluginManifestFile);
        InitArtifacts.WriteIfChanged(initPath, HermesPluginInit, "Hermes plugin", ctx);
        InitArtifacts.WriteIfChanged(manifestPath, HermesPluginManifest, "Hermes plugin manifest", ctx);

        var configPath = Path.Combine(hermesHome, HermesConfigFile);
        var existingConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var patchedConfig = PatchHermesConfig(existingConfig);
        InitArtifacts.WriteIfChanged(configPath, patchedConfig, "Hermes config", ctx);

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
            return;
        }

        Console.Out.Write("\nRTK configured for Hermes.\n\n");
        Console.Out.Write($"  Plugin: {pluginDir}\n");
        Console.Out.Write($"  Config: {configPath}\n");
        Console.Out.Write("  Hermes will now rewrite terminal commands through rtk.\n");
        Console.Out.Write("  Restart Hermes. Test with: git status\n\n");
    }

    /// <summary>
    /// Runs <c>rtk init --agent hermes --uninstall</c>: resolves <c>$HERMES_HOME</c>, removes the
    /// plugin directory, and cleans <c>config.yaml</c>. Port of Rust <c>uninstall_hermes</c>
    /// (init.rs:1834).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void UninstallHermes(InitContext ctx)
    {
        var hermesHome = ResolveHermesHome();
        var removed = UninstallHermesAt(hermesHome, ctx);

        if (removed.Count == 0)
        {
            Console.Out.Write("RTK Hermes support was not installed (nothing to remove)\n");
        }
        else
        {
            var header = ctx.DryRun ? "[dry-run] would uninstall RTK for Hermes CLI:" : "RTK uninstalled for Hermes CLI:";
            Console.Out.Write($"{header}\n");
            foreach (var item in removed)
            {
                Console.Out.Write($"  - {item}\n");
            }
        }

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
    }

    /// <summary>
    /// Removes the Hermes RTK plugin directory (if present) and strips the plugin entry out of
    /// <c>config.yaml</c> (if present and changed). Port of Rust <c>uninstall_hermes_at</c>
    /// (init.rs:1854).
    /// </summary>
    /// <param name="hermesHome">The resolved Hermes home directory.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns>The list of human-readable descriptions of what was removed (or would be, in dry-run).</returns>
    internal static List<string> UninstallHermesAt(string hermesHome, InitContext ctx)
    {
        var removed = new List<string>();

        var pluginDir = HermesPluginDir(hermesHome);
        if (Directory.Exists(pluginDir))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove Hermes plugin directory: {pluginDir}\n");
            }
            else
            {
                Directory.Delete(pluginDir, recursive: true);
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"Removed Hermes plugin directory: {pluginDir}\n");
                }
            }

            removed.Add($"Hermes plugin: {pluginDir}");
        }

        var configPath = Path.Combine(hermesHome, HermesConfigFile);
        if (File.Exists(configPath))
        {
            var existingConfig = File.ReadAllText(configPath);
            var patchedConfig = UnpatchHermesConfig(existingConfig);

            if (patchedConfig != existingConfig)
            {
                if (ctx.DryRun)
                {
                    Console.Out.Write($"[dry-run] would update Hermes config: {configPath}\n");
                    if (ctx.Verbose > 0)
                    {
                        Console.Out.Write($"[dry-run] content:\n{patchedConfig}\n");
                    }
                }
                else
                {
                    InitArtifacts.AtomicWrite(configPath, patchedConfig);
                    if (ctx.Verbose > 0)
                    {
                        Console.Error.Write($"Updated Hermes config: {configPath}\n");
                    }
                }

                removed.Add("Hermes config: removed RTK plugin entry");
            }
        }

        return removed;
    }

    /// <summary>Adds the RTK plugin entry to a Hermes <c>config.yaml</c>. Port of Rust <c>patch_hermes_config</c> (init.rs:1903).</summary>
    /// <param name="existing">The existing <c>config.yaml</c> content (empty string if the file does not exist).</param>
    /// <returns>The patched content.</returns>
    internal static string PatchHermesConfig(string existing) => RewriteHermesConfig(existing, addRtk: true);

    /// <summary>Removes the RTK plugin entry from a Hermes <c>config.yaml</c>. Port of Rust <c>unpatch_hermes_config</c> (init.rs:1907).</summary>
    /// <param name="existing">The existing <c>config.yaml</c> content.</param>
    /// <returns>The patched content.</returns>
    internal static string UnpatchHermesConfig(string existing) => RewriteHermesConfig(existing, addRtk: false);

    /// <summary>
    /// Core YAML line-surgery: adds or removes the <c>rtk-rewrite</c> entry in the
    /// <c>plugins.enabled</c> list, preserving the rest of the file's formatting exactly. Port of
    /// Rust <c>rewrite_hermes_config</c> (init.rs:1911).
    /// </summary>
    /// <remarks>
    /// Operates as targeted line-list splicing rather than a structured YAML parse/round-trip
    /// (see this class's remarks). Handles four shapes of the <c>enabled</c> list: absent entirely
    /// (inserted fresh under <c>plugins</c>), a block-style sequence (<c>- item</c> per line, at
    /// either the conventional two-space-deeper indent or PyYAML's indentation-less-sequence style),
    /// and an inline flow sequence (<c>enabled: [a, b]</c>).
    /// </remarks>
    /// <param name="existing">The existing <c>config.yaml</c> content.</param>
    /// <param name="addRtk"><see langword="true"/> to add the RTK entry; <see langword="false"/> to remove it.</param>
    /// <returns>The rewritten content.</returns>
    internal static string RewriteHermesConfig(string existing, bool addRtk)
    {
        if (existing.Trim().Length == 0)
        {
            return addRtk ? HermesPluginsBlock() : string.Empty;
        }

        var lines = SplitYamlLines(existing);
        var pluginsIdx = FindYamlKeyLine(lines, "plugins", 0, null);
        if (pluginsIdx is null)
        {
            return addRtk ? AppendHermesPluginsBlock(existing) : existing;
        }

        var pluginsIndent = YamlIndent(lines[pluginsIdx.Value]);
        var pluginsEnd = YamlBlockEnd(lines, pluginsIdx.Value, pluginsIndent);
        var enabledIdx = FindYamlKeyLine(lines, "enabled", pluginsIdx.Value + 1, (pluginsEnd, pluginsIndent));
        if (enabledIdx is null)
        {
            if (addRtk)
            {
                var (enabledIndent, itemIndent) = HermesMissingEnabledIndents(lines, pluginsIdx.Value, pluginsEnd, pluginsIndent);
                var enabledBlock = $"{new string(' ', enabledIndent)}enabled:\n{new string(' ', itemIndent)}- {HermesPluginName}\n";
                EnsurePreviousYamlLineEndsWithNewline(lines, pluginsEnd);
                lines.Insert(pluginsEnd, enabledBlock);
            }

            return string.Concat(lines);
        }

        if (YamlLineWithoutEnding(lines[enabledIdx.Value]).Contains('['))
        {
            RewriteInlineHermesEnabled(lines, enabledIdx.Value, addRtk);
            return string.Concat(lines);
        }

        RewriteBlockHermesEnabled(lines, enabledIdx.Value, addRtk);
        return string.Concat(lines);
    }

    /// <summary>
    /// Splits <paramref name="input"/> into newline-inclusive chunks, mirroring Rust's
    /// <c>str::split_inclusive('\n')</c>: each chunk keeps its trailing <c>\n</c> (and any preceding
    /// <c>\r</c>, since only <c>\n</c> is a split point), except possibly the last chunk if the input
    /// does not end with a newline.
    /// </summary>
    /// <param name="input">The content to split.</param>
    /// <returns>A mutable list of line chunks.</returns>
    private static List<string> SplitYamlLines(string input)
    {
        var result = new List<string>();
        if (input.Length == 0)
        {
            return result;
        }

        var start = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\n')
            {
                result.Add(input.Substring(start, i - start + 1));
                start = i + 1;
            }
        }

        if (start < input.Length)
        {
            result.Add(input[start..]);
        }

        return result;
    }

    /// <summary>
    /// If <paramref name="insertIdx"/> is not the start of the list, ensures the line immediately
    /// before it ends with <c>\n</c> (appending one if it was the file's un-newline-terminated final
    /// line). Port of Rust <c>ensure_previous_yaml_line_ends_with_newline</c> (init.rs:1972).
    /// </summary>
    private static void EnsurePreviousYamlLineEndsWithNewline(List<string> lines, int insertIdx)
    {
        if (insertIdx == 0)
        {
            return;
        }

        var prevIdx = insertIdx - 1;
        if (prevIdx < lines.Count && !lines[prevIdx].EndsWith('\n'))
        {
            lines[prevIdx] += "\n";
        }
    }

    /// <summary>Freshly-formatted <c>plugins:</c> block used when the config file is empty or has no <c>plugins</c> key. Port of Rust <c>hermes_plugins_block</c> (init.rs:1983).</summary>
    private static string HermesPluginsBlock() => $"plugins:\n  enabled:\n    - {HermesPluginName}\n";

    /// <summary>Appends <see cref="HermesPluginsBlock"/> to the end of an existing config with no <c>plugins</c> key. Port of Rust <c>append_hermes_plugins_block</c> (init.rs:1987).</summary>
    private static string AppendHermesPluginsBlock(string existing)
    {
        var patched = existing;
        if (!patched.EndsWith('\n'))
        {
            patched += "\n";
        }

        return patched + HermesPluginsBlock();
    }

    /// <summary>
    /// Finds the first line in <c>[start, end)</c> (or the whole file if <paramref name="block"/> is
    /// <see langword="null"/>) whose trimmed content is or starts with <c>{key}:</c>, skipping
    /// blank/comment lines and (if <paramref name="block"/> is given) lines indented at or shallower
    /// than the block's parent indent. Port of Rust <c>find_yaml_key_line</c> (init.rs:1995).
    /// </summary>
    private static int? FindYamlKeyLine(List<string> lines, string key, int start, (int End, int Indent)? block)
    {
        var end = block?.End ?? lines.Count;
        int? minIndent = block?.Indent;
        var keyPrefix = $"{key}:";

        for (var i = start; i < end; i++)
        {
            var raw = YamlLineWithoutEnding(lines[i]);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (minIndent is not null && YamlIndent(lines[i]) <= minIndent.Value)
            {
                continue;
            }

            if (trimmed == keyPrefix || trimmed.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds where a YAML block starting at <paramref name="start"/> ends: the first subsequent
    /// non-blank/non-comment line indented at or shallower than <paramref name="parentIndent"/>, or
    /// the end of the file. Port of Rust <c>yaml_block_end</c> (init.rs:2020).
    /// </summary>
    private static int YamlBlockEnd(List<string> lines, int start, int parentIndent)
    {
        for (var i = start + 1; i < lines.Count; i++)
        {
            var raw = YamlLineWithoutEnding(lines[i]);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (YamlIndent(lines[i]) <= parentIndent)
            {
                return i;
            }
        }

        return lines.Count;
    }

    /// <summary>
    /// Rewrites an inline flow-sequence <c>enabled: [a, b]</c> line in place, adding or removing the
    /// <c>rtk-rewrite</c> entry (deduplicating any pre-existing duplicates down to at most one when
    /// adding). Port of Rust <c>rewrite_inline_hermes_enabled</c> (init.rs:2039).
    /// </summary>
    private static void RewriteInlineHermesEnabled(List<string> lines, int enabledIdx, bool addRtk)
    {
        var lineEnding = YamlLineEnding(lines[enabledIdx]);
        var raw = YamlLineWithoutEnding(lines[enabledIdx]);

        var bracketIdx = raw.IndexOf('[');
        if (bracketIdx < 0)
        {
            return;
        }

        var prefix = raw[..bracketIdx];
        var rest = raw[(bracketIdx + 1)..];

        var closeIdx = rest.LastIndexOf(']');
        if (closeIdx < 0)
        {
            return;
        }

        var itemsRaw = rest[..closeIdx];
        var suffix = rest[(closeIdx + 1)..];

        var items = new List<string>();
        var sawRtk = false;
        foreach (var item in itemsRaw.Split(','))
        {
            var trimmed = item.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (IsHermesPluginName(trimmed))
            {
                if (addRtk && !sawRtk)
                {
                    items.Add(trimmed);
                    sawRtk = true;
                }
            }
            else
            {
                items.Add(trimmed);
            }
        }

        if (addRtk && !sawRtk)
        {
            items.Add(HermesPluginName);
        }

        lines[enabledIdx] = items.Count == 0
            ? $"{prefix}[]{suffix}{lineEnding}"
            : $"{prefix}[{string.Join(", ", items)}]{suffix}{lineEnding}";
    }

    /// <summary>
    /// Rewrites a block-style (<c>- item</c> per line) <c>enabled</c> sequence in place, adding or
    /// removing the <c>rtk-rewrite</c> entry, collapsing to <c>enabled: []</c> if removal empties the
    /// list. Port of Rust <c>rewrite_block_hermes_enabled</c> (init.rs:2078).
    /// </summary>
    private static void RewriteBlockHermesEnabled(List<string> lines, int enabledIdx, bool addRtk)
    {
        var enabledEnd = HermesEnabledListEnd(lines, enabledIdx);
        var itemIndent = HermesEnabledListItemIndent(lines, enabledIdx, enabledEnd);

        var kept = new List<string>();
        var sawRtk = false;

        for (var i = enabledIdx + 1; i < enabledEnd; i++)
        {
            var line = lines[i];
            if (IsYamlListItemNamed(line, HermesPluginName))
            {
                if (addRtk && !sawRtk)
                {
                    kept.Add(line);
                    sawRtk = true;
                }

                continue;
            }

            kept.Add(line);
        }

        if (addRtk && !sawRtk)
        {
            var insertIdx = kept.Count;
            EnsurePreviousYamlLineEndsWithNewline(kept, insertIdx);
            kept.Add($"{new string(' ', itemIndent)}- {HermesPluginName}\n");
        }

        var enabledLine = addRtk || kept.Any(IsYamlListItemLine)
            ? lines[enabledIdx]
            : CollapseYamlListKeyToEmpty(lines[enabledIdx]);

        if (addRtk && kept.Any(line => IsYamlListItemNamed(line, HermesPluginName)) && !enabledLine.EndsWith('\n'))
        {
            enabledLine += "\n";
        }

        var patched = new List<string>(lines.Count + 1);
        patched.AddRange(lines.Take(enabledIdx));
        patched.Add(enabledLine);
        patched.AddRange(kept);
        patched.AddRange(lines.Skip(enabledEnd));

        lines.Clear();
        lines.AddRange(patched);
    }

    /// <summary>
    /// Finds where a block-style <c>enabled</c> sequence ends: the first subsequent non-blank/
    /// non-comment line that is indented shallower than the <c>enabled</c> key, or indented the same
    /// but is not itself a list item (i.e. a sibling key at the same indent, common in PyYAML's
    /// indentation-less-sequence style). Port of Rust <c>hermes_enabled_list_end</c> (init.rs:2160).
    /// </summary>
    private static int HermesEnabledListEnd(List<string> lines, int enabledIdx)
    {
        var enabledIndent = YamlIndent(lines[enabledIdx]);

        for (var i = enabledIdx + 1; i < lines.Count; i++)
        {
            var raw = YamlLineWithoutEnding(lines[i]);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = YamlIndent(lines[i]);
            if (indent < enabledIndent || (indent == enabledIndent && !IsYamlListItemLine(lines[i])))
            {
                return i;
            }
        }

        return lines.Count;
    }

    /// <summary>
    /// Returns the indent of a block-style <c>enabled</c> sequence's list items (the first one found),
    /// or <c>enabledIndent + 2</c> if the list is currently empty. Port of Rust
    /// <c>hermes_enabled_list_item_indent</c> (init.rs:2184).
    /// </summary>
    private static int HermesEnabledListItemIndent(List<string> lines, int enabledIdx, int enabledEnd)
    {
        for (var i = enabledIdx + 1; i < enabledEnd; i++)
        {
            if (IsYamlListItemLine(lines[i]))
            {
                return YamlIndent(lines[i]);
            }
        }

        return YamlIndent(lines[enabledIdx]) + 2;
    }

    /// <summary>
    /// Infers the indent to use for a freshly-inserted <c>enabled:</c> key and its list item when
    /// <c>plugins</c> has no <c>enabled</c> key at all yet, by examining <c>plugins</c>'s other
    /// children: the key indent matches the shallowest existing child, and the item indent matches
    /// that child indent too if the file already uses PyYAML's indentation-less-sequence style for
    /// some other list, else two spaces deeper. Port of Rust <c>hermes_missing_enabled_indents</c>
    /// (init.rs:2197).
    /// </summary>
    private static (int EnabledIndent, int ItemIndent) HermesMissingEnabledIndents(
        List<string> lines, int pluginsIdx, int pluginsEnd, int pluginsIndent)
    {
        int? childIndent = null;
        for (var i = pluginsIdx + 1; i < pluginsEnd; i++)
        {
            var raw = YamlLineWithoutEnding(lines[i]);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = YamlIndent(lines[i]);
            if (indent > pluginsIndent && (childIndent is null || indent < childIndent.Value))
            {
                childIndent = indent;
            }
        }

        var resolvedChildIndent = childIndent ?? (pluginsIndent + 2);

        var usesIndentationlessSequences = false;
        for (var i = pluginsIdx + 1; i < pluginsEnd; i++)
        {
            if (IsYamlListItemLine(lines[i]) && YamlIndent(lines[i]) == resolvedChildIndent)
            {
                usesIndentationlessSequences = true;
                break;
            }
        }

        var itemIndent = usesIndentationlessSequences ? resolvedChildIndent : resolvedChildIndent + 2;
        return (resolvedChildIndent, itemIndent);
    }

    /// <summary>Strips a line's trailing <c>\r</c>/<c>\n</c>. Port of Rust <c>yaml_line_without_ending</c> (init.rs:2216).</summary>
    private static string YamlLineWithoutEnding(string line) => line.TrimEnd('\r', '\n');

    /// <summary>Returns a line's line-ending (<c>"\r\n"</c>, <c>"\n"</c>, or <c>""</c>). Port of Rust <c>yaml_line_ending</c> (init.rs:2220).</summary>
    private static string YamlLineEnding(string line)
    {
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        return line.EndsWith('\n') ? "\n" : string.Empty;
    }

    /// <summary>Counts a line's leading whitespace characters. Port of Rust <c>yaml_indent</c> (init.rs:2230).</summary>
    private static int YamlIndent(string line)
    {
        var raw = YamlLineWithoutEnding(line);
        var count = 0;
        foreach (var ch in raw)
        {
            if (!char.IsWhiteSpace(ch))
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>Returns whether a line is a <c>- {expected}</c> list item (after quote-stripping and comment removal). Port of Rust <c>is_yaml_list_item_named</c> (init.rs:2238).</summary>
    private static bool IsYamlListItemNamed(string line, string expected)
    {
        var trimmed = YamlLineWithoutEnding(line).Trim();
        if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            return false;
        }

        var item = trimmed[2..];
        return NormalizedYamlScalar(item) == expected;
    }

    /// <summary>Returns whether a line is any YAML sequence item (<c>- ...</c>). Port of Rust <c>is_yaml_list_item_line</c> (init.rs:2248).</summary>
    private static bool IsYamlListItemLine(string line) => YamlLineWithoutEnding(line).Trim().StartsWith("- ", StringComparison.Ordinal);

    /// <summary>Returns whether a scalar value (after quote-stripping and comment removal) names the RTK plugin. Port of Rust <c>is_hermes_plugin_name</c> (init.rs:2252).</summary>
    private static bool IsHermesPluginName(string value) => NormalizedYamlScalar(value) == HermesPluginName;

    /// <summary>
    /// Collapses a now-empty block-style <c>{key}:</c> sequence header to <c>{key}: []</c>, preserving
    /// its indentation and any trailing inline comment. Port of Rust
    /// <c>collapse_yaml_list_key_to_empty</c> (init.rs:2256).
    /// </summary>
    private static string CollapseYamlListKeyToEmpty(string line)
    {
        var raw = YamlLineWithoutEnding(line);
        var indent = YamlIndent(line);

        var colonIdx = raw.IndexOf(':');
        if (colonIdx < 0)
        {
            return $"{new string(' ', indent)}enabled: []\n";
        }

        var key = raw[..colonIdx];
        var suffix = raw[(colonIdx + 1)..];

        var hashIdx = suffix.IndexOf('#');
        var comment = hashIdx >= 0 ? $" {suffix[hashIdx..].TrimStart()}" : string.Empty;

        return $"{key}: []{comment}\n";
    }

    /// <summary>
    /// Normalizes a YAML scalar for name comparison: strips any trailing <c>#</c> comment, trims
    /// surrounding whitespace, then trims surrounding <c>'</c>/<c>"</c> quote characters. Returns
    /// <see langword="null"/> if the result is empty. Port of Rust <c>normalized_yaml_scalar</c>
    /// (init.rs:2269).
    /// </summary>
    private static string? NormalizedYamlScalar(string value)
    {
        var hashIdx = value.IndexOf('#');
        var withoutComment = hashIdx >= 0 ? value[..hashIdx] : value;
        var trimmed = withoutComment.Trim().Trim('\'', '"');
        return trimmed.Length == 0 ? null : trimmed;
    }
}
