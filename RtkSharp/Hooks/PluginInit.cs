using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RtkSharp.Hooks;

/// <summary>
/// Shared helper for the two "plugin file" agent installers ported in this file: Pi
/// (<see cref="PiInit"/>) and OpenCode (<see cref="OpenCodeInit"/>). Both agents are configured by
/// writing a single self-contained TypeScript plugin/extension file into an agent-specific
/// directory — no hook script, no <c>settings.json</c> patch, no <c>AGENTS.md</c>/<c>CLAUDE.md</c>
/// injection. Port of the shared plumbing behind Rust's <c>resolve_pi_dir</c> (init.rs:2791) and
/// <c>resolve_opencode_dir</c> (init.rs:2784), both of which delegate to the same
/// <c>resolve_home_subdir</c> (init.rs:2714).
/// </summary>
internal static class PluginInitShared
{
    /// <summary>
    /// Resolves <c>{home}/{subdir}</c>. Port of Rust <c>resolve_home_subdir</c> (init.rs:2714).
    /// </summary>
    /// <param name="subdir">The subdirectory name(s) to append, e.g. <c>".config"</c>.</param>
    /// <returns>The resolved path.</returns>
    /// <exception cref="InitAbortException">The home directory could not be determined.</exception>
    public static string ResolveHomeSubdir(params string[] subdir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            throw new InitAbortException(
                OperatingSystem.IsWindows()
                    ? "Cannot determine home directory. Is %USERPROFILE% set?"
                    : "Cannot determine home directory. Is $HOME set?");
        }

        var parts = new string[subdir.Length + 1];
        parts[0] = home;
        Array.Copy(subdir, 0, parts, 1, subdir.Length);
        return Path.Combine(parts);
    }

    /// <summary>
    /// Loads an embedded resource's exact bytes (no line-ending normalization), decoded as UTF-8.
    /// Mirrors <c>CodexInit.LoadEmbeddedResource</c>.
    /// </summary>
    /// <param name="logicalName">The embedded resource's logical name (see <c>RtkSharp.csproj</c>).</param>
    /// <returns>The exact file content.</returns>
    public static string LoadEmbeddedResource(string logicalName)
    {
        var assembly = typeof(PluginInitShared).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InitAbortException($"Embedded resource {logicalName} is missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Pi coding agent plugin installer for <c>rtk init --agent pi</c> / <c>rtk init -g --agent pi</c>.
/// Faithful port of the Pi subset of Rust <c>src/hooks/init.rs</c>: <c>resolve_pi_dir</c>
/// (init.rs:2791), <c>pi_plugin_path</c>/<c>pi_plugin_path_for_scope</c> (init.rs:2801-2819),
/// <c>ensure_pi_plugin_installed</c> (init.rs:2819), <c>ensure_pi_extensions_dir</c> (init.rs:2825),
/// <c>uninstall_pi</c> (init.rs:2841), <c>run_pi_mode</c> (init.rs:2882), and <c>print_pi_result</c>
/// (init.rs:2913).
/// </summary>
/// <remarks>
/// <para>
/// <b>Plugin-file install, no hook, no <c>AGENTS.md</c> injection.</b> Unlike Claude Code or Codex,
/// Pi mode does exactly one idempotent file write: the extension file itself
/// (<see cref="PiPluginContent"/>) via <see cref="InitArtifacts.WriteIfChanged"/>. Pi discovers and
/// loads the extension automatically from its <c>extensions/</c> directory; there is no companion
/// markdown file and no reference line to patch anywhere.
/// </para>
/// <para>
/// <b>Both scopes are supported</b> (unlike OpenCode, which is global-only): global scope resolves
/// <c>$PI_CODING_AGENT_DIR/extensions/rtk.ts</c> (falling back to <c>~/.pi/agent/extensions/rtk.ts</c>
/// when <c>PI_CODING_AGENT_DIR</c> is unset or empty); project/local scope resolves
/// <c>./.pi/extensions/rtk.ts</c> relative to the current working directory.
/// </para>
/// </remarks>
public static class PiInit
{
    /// <summary>Environment variable honored by <see cref="ResolvePiDir"/> (Rust <c>PI_CODING_AGENT_DIR_ENV</c>, init.rs:35).</summary>
    private const string PiCodingAgentDirEnvVar = "PI_CODING_AGENT_DIR";

    /// <summary>Fallback subdirectory of the home directory for global scope (Rust <c>PI_DIR</c> = <c>".pi/agent"</c>).</summary>
    private static readonly string[] PiDirSegments = [".pi", "agent"];

    /// <summary>Local-scope root directory (Rust <c>PI_LOCAL_DIR</c> = <c>".pi"</c>).</summary>
    private const string PiLocalDir = ".pi";

    /// <summary>Subdirectory of the Pi config dir Pi auto-loads extensions from (Rust <c>PI_EXTENSIONS_SUBDIR</c>).</summary>
    private const string PiExtensionsSubdir = "extensions";

    /// <summary>The extension file name (Rust <c>PI_PLUGIN_FILE</c> = <c>"rtk.ts"</c>).</summary>
    private const string PiPluginFile = "rtk.ts";

    /// <summary>
    /// The Pi extension file contents. Byte-for-byte copy of Rust <c>PI_PLUGIN</c>
    /// (<c>include_str!("../../hooks/pi/rtk.ts")</c>, init.rs:28), loaded from an embedded resource
    /// the same way <see cref="CodexInit.RtkSlimCodex"/> loads its template.
    /// </summary>
    public static readonly string PiPluginContent = PluginInitShared.LoadEmbeddedResource("RtkSharp.Hooks.pi-rtk.ts");

    /// <summary>
    /// Resolves the Pi config directory: <c>$PI_CODING_AGENT_DIR</c> if set and non-empty, else
    /// <c>{home}/.pi/agent</c>. Port of Rust <c>resolve_pi_dir</c> (init.rs:2791).
    /// </summary>
    /// <returns>The resolved Pi config directory path.</returns>
    internal static string ResolvePiDir()
    {
        var envDir = Environment.GetEnvironmentVariable(PiCodingAgentDirEnvVar);
        if (!string.IsNullOrEmpty(envDir))
        {
            return envDir;
        }

        return PluginInitShared.ResolveHomeSubdir(PiDirSegments);
    }

    /// <summary>
    /// Returns the path to the installed Pi extension file under <paramref name="piDir"/>. Port of
    /// Rust <c>pi_plugin_path</c> (init.rs:2801).
    /// </summary>
    /// <param name="piDir">The Pi config directory.</param>
    /// <returns><c>{piDir}/extensions/rtk.ts</c>.</returns>
    internal static string PiPluginPath(string piDir) => Path.Combine(piDir, PiExtensionsSubdir, PiPluginFile);

    /// <summary>
    /// Returns the Pi extension install path for the given scope. Port of Rust
    /// <c>pi_plugin_path_for_scope</c> (init.rs:2808).
    /// </summary>
    /// <param name="global">
    /// <see langword="true"/> for <c>$PI_CODING_AGENT_DIR/extensions/rtk.ts</c> (or the
    /// <c>~/.pi/agent</c> fallback); <see langword="false"/> for <c>./.pi/extensions/rtk.ts</c>
    /// relative to the current working directory.
    /// </param>
    /// <returns>The resolved plugin path.</returns>
    internal static string PiPluginPathForScope(bool global) =>
        global
            ? PiPluginPath(ResolvePiDir())
            : Path.Combine(PiLocalDir, PiExtensionsSubdir, PiPluginFile);

    /// <summary>
    /// Writes the Pi extension file if missing or outdated. Port of Rust
    /// <c>ensure_pi_plugin_installed</c> (init.rs:2819).
    /// </summary>
    /// <param name="path">The extension file path.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if the file was created/updated (or would be, in dry-run).</returns>
    internal static bool EnsurePiPluginInstalled(string path, InitContext ctx) =>
        InitArtifacts.WriteIfChanged(path, PiPluginContent, "Pi extension", ctx);

    /// <summary>
    /// Creates the Pi extensions directory, or in dry-run mode, prints a message only if the
    /// directory does not yet exist (avoids reporting a no-op change). Port of Rust
    /// <c>ensure_pi_extensions_dir</c> (init.rs:2825).
    /// </summary>
    /// <param name="parent">The extensions directory to create.</param>
    /// <param name="name">A human-readable label used in printed messages.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    internal static void EnsurePiExtensionsDir(string parent, string name, InitContext ctx)
    {
        if (ctx.DryRun)
        {
            if (!Directory.Exists(parent))
            {
                Console.Out.Write($"[dry-run] would create {name}: {parent}\n");
            }
        }
        else
        {
            Directory.CreateDirectory(parent);
        }
    }

    /// <summary>
    /// Runs <c>rtk init --agent pi</c> (project scope) or <c>rtk init -g --agent pi</c> (global
    /// scope): installs the Pi extension file only (hook-only; no <c>AGENTS.md</c> injection). Port
    /// of Rust <c>run_pi_mode</c> (init.rs:2882).
    /// </summary>
    /// <param name="global">Whether this is the global-scope invocation.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void Run(bool global, InitContext ctx)
    {
        string pluginPath;
        if (global)
        {
            var piDir = ResolvePiDir();
            pluginPath = PiPluginPath(piDir);
            var parent = Path.GetDirectoryName(pluginPath);
            if (!string.IsNullOrEmpty(parent))
            {
                EnsurePiExtensionsDir(parent, "Pi extensions directory", ctx);
            }
        }
        else
        {
            pluginPath = PiPluginPathForScope(false);
            var parent = Path.GetDirectoryName(pluginPath);
            if (!string.IsNullOrEmpty(parent))
            {
                EnsurePiExtensionsDir(parent, "local Pi extensions directory", ctx);
            }
        }

        var installed = EnsurePiPluginInstalled(pluginPath, ctx);

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
        else
        {
            PrintResult(pluginPath, installed);
        }
    }

    /// <summary>
    /// Prints the success report after a non-dry-run <see cref="Run"/>. Port of Rust
    /// <c>print_pi_result</c> (init.rs:2913).
    /// </summary>
    /// <param name="pluginPath">The installed extension path.</param>
    /// <param name="installed">Whether the file was newly written (vs. already up to date).</param>
    private static void PrintResult(string pluginPath, bool installed)
    {
        var status = installed ? "installed" : "already up to date";
        Console.Out.Write($"RTK Pi extension {status}:\n");
        Console.Out.Write($"  Extension: {pluginPath}\n");
        Console.Out.Write("\n");
        Console.Out.Write("Pi will load the extension automatically on next start.\n");
        Console.Out.Write($"Verify: pi -e {pluginPath} --no-session\n");
    }

    /// <summary>
    /// Uninstalls the Pi extension for the given scope. Port of Rust <c>uninstall_pi</c>
    /// (init.rs:2841).
    /// </summary>
    /// <param name="global">Whether this is the global-scope invocation.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void Uninstall(bool global, InitContext ctx)
    {
        var pluginPath = PiPluginPathForScope(global);
        var removed = new List<string>();

        if (File.Exists(pluginPath))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove Pi extension: {pluginPath}\n");
            }
            else
            {
                File.Delete(pluginPath);
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"Removed Pi extension: {pluginPath}\n");
                }

                removed.Add($"Pi extension: {pluginPath}");
            }
        }

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
        else if (removed.Count > 0)
        {
            Console.Out.Write("RTK uninstalled (Pi):\n");
            foreach (var item in removed)
            {
                Console.Out.Write($"  - {item}\n");
            }

            Console.Out.Write("\nRestart pi to apply changes.\n");
        }
        else
        {
            Console.Out.Write("RTK Pi extension was not installed (nothing to remove)\n");
        }
    }
}

/// <summary>
/// OpenCode plugin installer for <c>rtk init -g --opencode</c>. Faithful port of the OpenCode subset
/// of Rust <c>src/hooks/init.rs</c>: <c>resolve_opencode_dir</c> (init.rs:2784),
/// <c>opencode_plugin_path</c> (init.rs:2927), <c>prepare_opencode_plugin_path</c> (init.rs:2932),
/// <c>ensure_opencode_plugin_installed</c> (init.rs:2940), <c>remove_opencode_plugin</c>
/// (init.rs:2957), and <c>run_opencode_only_mode</c> (init.rs:3587).
/// </summary>
/// <remarks>
/// <para>
/// <b>Global-only, no per-scope choice.</b> Rust rejects <c>--opencode</c> without <c>--global</c>
/// at the <c>run()</c> dispatch level (init.rs:285: <c>"OpenCode plugin is global-only. Use: rtk
/// init -g --opencode"</c> — already ported verbatim in <c>InitCommand.RunCore</c>, which this class
/// does not duplicate). <see cref="ResolveOpencodeDir"/> therefore always resolves
/// <c>{home}/.config/opencode</c> — unlike <see cref="PiInit.ResolvePiDir"/>, there is <b>no</b>
/// environment-variable override for the OpenCode config directory in the Rust oracle, so (matching
/// Rust's own test suite, which exercises <c>ensure_opencode_plugin_installed</c>/
/// <c>opencode_plugin_path</c> directly against a fabricated directory rather than calling
/// <c>run_opencode_only_mode</c> end-to-end) this port's own tests do the same rather than mutating
/// the real <c>%USERPROFILE%/.config/opencode</c> on the machine running the tests.
/// </para>
/// <para>
/// <b>Reusable co-install primitive.</b> Rust threads a separate <c>install_opencode: bool</c> flag
/// through <c>run_default_mode</c>/<c>run_claude_md_mode</c>/<c>run_hook_only_mode</c> (init.rs:1144,
/// 1435, 1526 and friends) so that <c>rtk init -g --opencode</c> (Claude Code + OpenCode combined)
/// calls the exact same <see cref="PrepareOpencodePluginPath"/> + <see cref="EnsureOpencodePluginInstalled"/>
/// pair used here by <see cref="Run"/>. Those two primitives are exposed as <c>internal</c> precisely
/// so a later central-wiring pass can call them from the Claude Code co-install path without
/// duplicating this file-write logic.
/// </para>
/// </remarks>
public static class OpenCodeInit
{
    /// <summary>Subdirectory of the home directory holding user config (Rust <c>CONFIG_DIR</c> = <c>".config"</c>).</summary>
    private const string ConfigDir = ".config";

    /// <summary>Subdirectory of the config dir holding OpenCode's own config (Rust <c>OPENCODE_SUBDIR</c>).</summary>
    private const string OpencodeSubdir = "opencode";

    /// <summary>Subdirectory of the OpenCode config dir holding plugins (Rust <c>PLUGIN_SUBDIR</c>).</summary>
    private const string PluginSubdir = "plugins";

    /// <summary>The plugin file name (Rust <c>OPENCODE_PLUGIN_FILE</c> = <c>"rtk.ts"</c>).</summary>
    private const string OpencodePluginFile = "rtk.ts";

    /// <summary>
    /// The OpenCode plugin file contents. Byte-for-byte copy of Rust <c>OPENCODE_PLUGIN</c>
    /// (<c>include_str!("../../hooks/opencode/rtk.ts")</c>, init.rs:25).
    /// </summary>
    public static readonly string OpencodePluginContent = PluginInitShared.LoadEmbeddedResource("RtkSharp.Hooks.opencode-rtk.ts");

    /// <summary>
    /// Resolves the OpenCode config directory: <c>{home}/.config/opencode</c>. Port of Rust
    /// <c>resolve_opencode_dir</c> (init.rs:2784). No environment-variable override exists in the
    /// Rust oracle (see this class's <b>Remarks</b>).
    /// </summary>
    /// <returns>The resolved OpenCode config directory path.</returns>
    internal static string ResolveOpencodeDir() => Path.Combine(PluginInitShared.ResolveHomeSubdir(ConfigDir), OpencodeSubdir);

    /// <summary>
    /// Returns the OpenCode plugin install path under <paramref name="opencodeDir"/>. Port of Rust
    /// <c>opencode_plugin_path</c> (init.rs:2927).
    /// </summary>
    /// <param name="opencodeDir">The OpenCode config directory.</param>
    /// <returns><c>{opencodeDir}/plugins/rtk.ts</c>.</returns>
    internal static string OpencodePluginPath(string opencodeDir) => Path.Combine(opencodeDir, PluginSubdir, OpencodePluginFile);

    /// <summary>
    /// Resolves the OpenCode config directory and returns the plugin install path. Directory
    /// creation is deferred to install time. Port of Rust <c>prepare_opencode_plugin_path</c>
    /// (init.rs:2932).
    /// </summary>
    /// <returns>The resolved plugin path.</returns>
    internal static string PrepareOpencodePluginPath() => OpencodePluginPath(ResolveOpencodeDir());

    /// <summary>
    /// Writes the OpenCode plugin file if missing or outdated, creating the parent directory first
    /// (skipped in dry-run). Port of Rust <c>ensure_opencode_plugin_installed</c> (init.rs:2940).
    /// </summary>
    /// <param name="path">The plugin file path.</param>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns><see langword="true"/> if the file was created/updated (or would be, in dry-run).</returns>
    internal static bool EnsureOpencodePluginInstalled(string path, InitContext ctx)
    {
        if (!ctx.DryRun)
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        return InitArtifacts.WriteIfChanged(path, OpencodePluginContent, "OpenCode plugin", ctx);
    }

    /// <summary>
    /// Removes the OpenCode plugin file if present. Port of Rust <c>remove_opencode_plugin</c>
    /// (init.rs:2957).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    /// <returns>The list of paths removed (or that would be removed, in dry-run) — zero or one entry.</returns>
    internal static List<string> RemoveOpencodePlugin(InitContext ctx)
    {
        var opencodeDir = ResolveOpencodeDir();
        var path = OpencodePluginPath(opencodeDir);
        var removed = new List<string>();

        if (File.Exists(path))
        {
            if (ctx.DryRun)
            {
                Console.Out.Write($"[dry-run] would remove OpenCode plugin: {path}\n");
            }
            else
            {
                File.Delete(path);
                if (ctx.Verbose > 0)
                {
                    Console.Error.Write($"Removed OpenCode plugin: {path}\n");
                }
            }

            removed.Add(path);
        }

        return removed;
    }

    /// <summary>
    /// Runs <c>rtk init -g --opencode</c> (OpenCode-only mode): installs the OpenCode plugin file
    /// only. Port of Rust <c>run_opencode_only_mode</c> (init.rs:3587).
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void Run(InitContext ctx)
    {
        var pluginPath = PrepareOpencodePluginPath();
        EnsureOpencodePluginInstalled(pluginPath, ctx);

        if (!ctx.DryRun)
        {
            Console.Out.Write("\nOpenCode plugin installed (global).\n\n");
            Console.Out.Write($"  OpenCode: {pluginPath}\n");
            Console.Out.Write("  Restart OpenCode. Test with: git status\n\n");
        }
    }

    /// <summary>
    /// Removes the OpenCode plugin and prints a summary. Port of the OpenCode-specific slice of
    /// Rust's combined global <c>uninstall()</c> (init.rs:810-814): unlike Pi, the Rust oracle has no
    /// standalone <c>--opencode --uninstall</c> CLI surface — <c>remove_opencode_plugin</c> is only
    /// ever called as one step of the larger Claude Code global uninstall. This method exists so a
    /// later central-wiring pass has a self-contained OpenCode-only uninstall primitive to call
    /// (either standalone, if that CLI surface is added, or folded into the combined uninstall
    /// report); its "nothing to remove" / "uninstalled" messages follow the same shape as
    /// <see cref="PiInit.Uninstall"/> for consistency since Rust defines no oracle text of its own
    /// for this specific combination.
    /// </summary>
    /// <param name="ctx">The verbosity/dry-run context.</param>
    public static void Uninstall(InitContext ctx)
    {
        var removed = RemoveOpencodePlugin(ctx);

        if (ctx.DryRun)
        {
            InitArtifacts.PrintDryRunFooter();
        }
        else if (removed.Count > 0)
        {
            Console.Out.Write("RTK uninstalled (OpenCode):\n");
            foreach (var path in removed)
            {
                Console.Out.Write($"  - OpenCode plugin: {path}\n");
            }

            Console.Out.Write("\nRestart OpenCode to apply changes.\n");
        }
        else
        {
            Console.Out.Write("RTK OpenCode plugin was not installed (nothing to remove)\n");
        }
    }
}
