using RtkSharp.Execution;

namespace RtkSharp.Core;

/// <summary>
/// The resolved executable and base arguments for invoking a JS tool through a package manager's
/// exec mechanism. Callers append the tool's own arguments after <see cref="BaseArguments"/> (e.g.
/// building an <see cref="ExecutionRequest"/> with <c>[.. BaseArguments, ..toolArgs]</c>), mirroring
/// how Rust's <c>package_manager_exec</c> returns a bare <c>Command</c> ready to have tool-specific
/// args appended (<c>src/core/utils.rs:284-307</c>).
/// </summary>
/// <param name="FileName">The executable name to run (e.g. <c>"vitest"</c>, <c>"pnpm"</c>, <c>"npx"</c>).</param>
/// <param name="BaseArguments">Arguments that must precede the tool's own arguments (empty when invoking the tool directly).</param>
public readonly record struct PackageManagerCommand(string FileName, IReadOnlyList<string> BaseArguments);

/// <summary>
/// Detects the active JS package manager and builds package-manager-exec commands for tools that
/// may not be directly resolvable on <c>PATH</c>. Faithful port of Rust
/// <c>detect_package_manager</c>/<c>package_manager_exec</c> (<c>src/core/utils.rs:272-307</c>).
/// </summary>
public static class PackageManagerDetection
{
    /// <summary>Lockfile that indicates pnpm is the active package manager.</summary>
    private const string PnpmLockFile = "pnpm-lock.yaml";

    /// <summary>Lockfile that indicates yarn is the active package manager.</summary>
    private const string YarnLockFile = "yarn.lock";

    /// <summary>
    /// Detects the package manager used in the current directory by lockfile presence. Faithful
    /// port of Rust <c>detect_package_manager</c> (<c>src/core/utils.rs:272-280</c>).
    /// </summary>
    /// <returns><c>"pnpm"</c> if <c>pnpm-lock.yaml</c> exists, <c>"yarn"</c> if <c>yarn.lock</c> exists, otherwise <c>"npm"</c>.</returns>
    public static string DetectPackageManager() => DetectPackageManager(Environment.CurrentDirectory);

    /// <summary>
    /// Detects the package manager used in <paramref name="directory"/> by lockfile presence.
    /// Overload of <see cref="DetectPackageManager()"/> taking an explicit directory, for
    /// deterministic testing without mutating the process's current directory.
    /// </summary>
    /// <param name="directory">The directory to check for lockfiles.</param>
    /// <returns><c>"pnpm"</c> if <c>pnpm-lock.yaml</c> exists, <c>"yarn"</c> if <c>yarn.lock</c> exists, otherwise <c>"npm"</c>.</returns>
    public static string DetectPackageManager(string directory)
    {
        if (File.Exists(Path.Combine(directory, PnpmLockFile)))
        {
            return "pnpm";
        }

        if (File.Exists(Path.Combine(directory, YarnLockFile)))
        {
            return "yarn";
        }

        return "npm";
    }

    /// <summary>
    /// Builds the command to invoke <paramref name="tool"/>, using it directly if resolvable on
    /// <c>PATH</c> (PATHEXT-aware, via <see cref="Execution.PathResolver.Resolve(string)"/>),
    /// otherwise falling back through the detected package manager's exec mechanism: <c>pnpm exec
    /// -- {tool}</c>, <c>yarn exec -- {tool}</c>, or <c>npx --no-install -- {tool}</c> (the last for
    /// both an explicitly detected npm project and any undetected/unknown case). Faithful port of
    /// Rust <c>package_manager_exec</c> (<c>src/core/utils.rs:284-307</c>).
    /// </summary>
    /// <param name="tool">The tool binary name (e.g. <c>"vitest"</c>, <c>"tsc"</c>, <c>"playwright"</c>).</param>
    /// <returns>The resolved <see cref="PackageManagerCommand"/>.</returns>
    public static PackageManagerCommand PackageManagerExec(string tool) =>
        PackageManagerExec(tool, static name => PathResolver.Resolve(name) != name, DetectPackageManager);

    /// <summary>
    /// Testable core of <see cref="PackageManagerExec(string)"/>: the tool-existence predicate and
    /// package-manager detector are injected so each of the four branches (direct resolution;
    /// pnpm/yarn/npm fallback) can be exercised deterministically, following the same
    /// dependency-injection pattern <c>TreeCommand.RunAsync</c> uses for its own tool-existence
    /// check.
    /// </summary>
    /// <param name="tool">The tool binary name.</param>
    /// <param name="toolExists">Predicate reporting whether <paramref name="tool"/> is directly resolvable.</param>
    /// <param name="detectPackageManager">Supplies the active package manager name (<c>"pnpm"</c>, <c>"yarn"</c>, or <c>"npm"</c>) when the direct resolution fails.</param>
    /// <returns>The resolved <see cref="PackageManagerCommand"/>.</returns>
    internal static PackageManagerCommand PackageManagerExec(
        string tool,
        Func<string, bool> toolExists,
        Func<string> detectPackageManager)
    {
        if (toolExists(tool))
        {
            return new PackageManagerCommand(tool, []);
        }

        return detectPackageManager() switch
        {
            "pnpm" => new PackageManagerCommand("pnpm", ["exec", "--", tool]),
            "yarn" => new PackageManagerCommand("yarn", ["exec", "--", tool]),
            _ => new PackageManagerCommand("npx", ["--no-install", "--", tool]),
        };
    }
}
