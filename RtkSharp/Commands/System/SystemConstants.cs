namespace RtkSharp.Commands.System;

/// <summary>
/// Shared constants for the system command filters (ls, tree, find, grep, etc.).
/// </summary>
public static class SystemConstants
{
    /// <summary>
    /// Directory (and file) names treated as noise and excluded by default from directory
    /// listings and tree/find traversals — build artifacts, caches, VCS internals, and
    /// editor/OS metadata directories that rarely matter to an agent inspecting a project.
    /// </summary>
    /// <remarks>
    /// Transcribed verbatim from <c>src/cmds/system/constants.rs</c>'s <c>NOISE_DIRS</c>.
    /// Note: <c>env</c> (the legacy Python virtualenv directory name) is intentionally
    /// included, but <c>.env</c> (dotenv) is intentionally NOT — agents must see it.
    /// </remarks>
    public static readonly IReadOnlyList<string> NoiseDirs =
    [
        "node_modules",
        ".git",
        "target",
        "__pycache__",
        ".next",
        "dist",
        "build",
        ".cache",
        ".turbo",
        ".vercel",
        ".pytest_cache",
        ".mypy_cache",
        ".tox",
        ".venv",
        "venv",
        "env",
        "coverage",
        ".nyc_output",
        ".DS_Store",
        "Thumbs.db",
        ".idea",
        ".vscode",
        ".vs",
        "*.egg-info",
        ".eggs"
    ];
}
