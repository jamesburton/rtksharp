using System;
using System.IO;
using System.Security.Cryptography;

namespace RtkSharp.Hooks;

/// <summary>
/// The kind of result returned by <see cref="Integrity.VerifyHookAt"/>. Mirrors the discriminant of
/// Rust's <c>IntegrityStatus</c> enum (integrity.rs:27); the payload for <see cref="Tampered"/> is
/// carried separately on <see cref="IntegrityStatus"/> since C# enums cannot hold associated data.
/// </summary>
public enum IntegrityStatusKind
{
    /// <summary>Hash matches — hook is unmodified since last install/update.</summary>
    Verified,

    /// <summary>Hash mismatch — hook has been modified outside of <c>rtk init</c>.</summary>
    Tampered,

    /// <summary>Hook exists but no stored hash (installed before integrity checks).</summary>
    NoBaseline,

    /// <summary>Neither hook nor hash file exist (RTK not installed).</summary>
    NotInstalled,

    /// <summary>Hash file exists but hook was deleted.</summary>
    OrphanedHash,
}

/// <summary>
/// Result of hook integrity verification. Faithful port of Rust <c>IntegrityStatus</c>
/// (integrity.rs:26-38): a <see cref="Kind"/> discriminant plus, for <see cref="IntegrityStatusKind.Tampered"/>
/// only, the <see cref="Expected"/> and <see cref="Actual"/> hash payload. Value-equality (via the
/// <see langword="record"/> struct) mirrors Rust's derived <c>PartialEq</c>, which the ported unit
/// tests rely on (e.g. <c>assert_eq!(status, IntegrityStatus::Verified)</c>).
/// </summary>
/// <param name="Kind">Which variant this result represents.</param>
/// <param name="Expected">The stored (expected) hash, only set when <paramref name="Kind"/> is <see cref="IntegrityStatusKind.Tampered"/>.</param>
/// <param name="Actual">The freshly computed (actual) hash, only set when <paramref name="Kind"/> is <see cref="IntegrityStatusKind.Tampered"/>.</param>
public readonly record struct IntegrityStatus(IntegrityStatusKind Kind, string? Expected = null, string? Actual = null)
{
    /// <summary>Hash matches — hook is unmodified since last install/update.</summary>
    public static readonly IntegrityStatus Verified = new(IntegrityStatusKind.Verified);

    /// <summary>Hook exists but no stored hash (installed before integrity checks).</summary>
    public static readonly IntegrityStatus NoBaseline = new(IntegrityStatusKind.NoBaseline);

    /// <summary>Neither hook nor hash file exist (RTK not installed).</summary>
    public static readonly IntegrityStatus NotInstalled = new(IntegrityStatusKind.NotInstalled);

    /// <summary>Hash file exists but hook was deleted.</summary>
    public static readonly IntegrityStatus OrphanedHash = new(IntegrityStatusKind.OrphanedHash);

    /// <summary>Builds a <see cref="IntegrityStatusKind.Tampered"/> result carrying the mismatched hashes.</summary>
    /// <param name="expected">The stored (expected) hash.</param>
    /// <param name="actual">The freshly computed (actual) hash.</param>
    /// <returns>The <see cref="IntegrityStatusKind.Tampered"/> result.</returns>
    public static IntegrityStatus Tampered(string expected, string actual) =>
        new(IntegrityStatusKind.Tampered, expected, actual);
}

/// <summary>
/// Detects if someone tampered with the installed hook file. Faithful port of Rust
/// <c>src/hooks/integrity.rs</c>: SHA-256 hash computation and storage at install time, the
/// <c>rtk verify</c> manual check, and the (unwired, ported-for-parity) runtime verification guard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-loud contract.</b> <see cref="RunVerify"/> is a user command, not a runtime hook — the
/// RTK-wide "never block the user" fallback pattern does not apply here. A
/// <see cref="IntegrityStatusKind.Tampered"/> result is surfaced with a non-zero exit code (returned,
/// not thrown, so <see cref="VerifyCommand"/> can propagate it as the process exit code without an
/// exception round-trip — Rust's equivalent calls <c>std::process::exit(1)</c> directly from
/// <c>run_verify</c>, but returning an exit code here keeps this method callable from tests).
/// </para>
/// </remarks>
public static class Integrity
{
    /// <summary>Filename for the stored hash (dotfile alongside hook). Rust <c>HASH_FILENAME</c> (integrity.rs:23).</summary>
    private const string HashFilename = ".rtk-hook.sha256";

    /// <summary>The legacy shell hook filename (Rust <c>REWRITE_HOOK_FILE</c>, constants.rs:12).</summary>
    private const string RewriteHookFile = "rtk-rewrite.sh";

    /// <summary>The hooks subdirectory under the Claude config dir (Rust <c>HOOKS_SUBDIR</c>, constants.rs:15).</summary>
    private const string HooksSubdir = "hooks";

    /// <summary>The mode a fresh, writable hash file gets before being written: <c>rw-r--r--</c> (0o644).</summary>
    private const UnixFileMode WritableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>The read-only mode a hash file is set to after being written: <c>r--r--r--</c> (0o444).</summary>
    private const UnixFileMode ReadOnlyMode =
        UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// Computes the SHA-256 hash of a file, returned as lowercase hex. Port of Rust <c>compute_hash</c>
    /// (integrity.rs:41).
    /// </summary>
    /// <param name="path">The file to hash.</param>
    /// <returns>The lowercase hex SHA-256 digest.</returns>
    /// <exception cref="InitAbortException">The file could not be read.</exception>
    public static string ComputeHash(string path)
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InitAbortException($"Failed to read file: {path}: {ex.Message}");
        }

        return ComputeHashBytes(content);
    }

    /// <summary>
    /// Computes the SHA-256 hash of an in-memory byte buffer, returned as lowercase hex. Port of
    /// Rust <c>compute_hash_bytes</c> (integrity.rs:48).
    /// </summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>The lowercase hex SHA-256 digest.</returns>
    public static string ComputeHashBytes(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// Derives the hash sidecar path from the hook path (<c>{hook's parent}/.rtk-hook.sha256</c>).
    /// Port of Rust <c>hash_path</c> (integrity.rs:55) and its public alias <c>hash_path_for</c>
    /// (integrity.rs:63) — both collapse to this one method since C# has no private/public split for
    /// the same body.
    /// </summary>
    /// <param name="hookPath">The hook file path.</param>
    /// <returns>The hash sidecar path.</returns>
    public static string HashPathFor(string hookPath)
    {
        var parent = Path.GetDirectoryName(hookPath);
        return Path.Combine(string.IsNullOrEmpty(parent) ? "." : parent, HashFilename);
    }

    /// <summary>
    /// Stores the SHA-256 hash of the hook script after installation, in a format compatible with
    /// <c>sha256sum -c</c>: <c>&lt;hex_hash&gt;  &lt;filename&gt;\n</c> (two-space separator). Port of
    /// Rust <c>store_hash</c> (integrity.rs:78).
    /// </summary>
    /// <remarks>
    /// The hash file is set read-only (<c>0o444</c>) as a speed bump against casual modification —
    /// not a security boundary, since an attacker with write access can chmod it, but it forces a
    /// deliberate action rather than an accidental overwrite. Rust gates this behind
    /// <c>#[cfg(unix)]</c>; this port mirrors that by skipping all permission changes on Windows
    /// (via <see cref="OperatingSystem.IsWindows"/>) while still performing the write unconditionally.
    /// </remarks>
    /// <param name="hookPath">The installed hook file path.</param>
    /// <exception cref="InitAbortException">The hash file could not be written, or its permissions could not be set.</exception>
    public static void StoreHash(string hookPath)
    {
        var hash = ComputeHash(hookPath);
        var hashFile = HashPathFor(hookPath);
        var filename = Path.GetFileName(hookPath);
        if (string.IsNullOrEmpty(filename))
        {
            filename = RewriteHookFile;
        }

        var content = $"{hash}  {filename}\n";

        if (!OperatingSystem.IsWindows() && File.Exists(hashFile))
        {
            // If the hash file exists and is read-only, make it writable first (best-effort).
            TryMakeWritable(hashFile);
        }

        try
        {
            File.WriteAllText(hashFile, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InitAbortException($"Failed to write hash to {hashFile}: {ex.Message}");
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(hashFile, ReadOnlyMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InitAbortException($"Failed to set permissions on {hashFile}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Removes the stored hash file (called during uninstall). Port of Rust <c>remove_hash</c>
    /// (integrity.rs:110).
    /// </summary>
    /// <param name="hookPath">The hook file path whose hash sidecar should be removed.</param>
    /// <returns><see langword="true"/> if a hash file existed and was removed; otherwise <see langword="false"/>.</returns>
    /// <exception cref="InitAbortException">The hash file existed but could not be removed.</exception>
    public static bool RemoveHash(string hookPath)
    {
        var hashFile = HashPathFor(hookPath);
        if (!File.Exists(hashFile))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Make writable before removing (best-effort).
            TryMakeWritable(hashFile);
        }

        try
        {
            File.Delete(hashFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InitAbortException($"Failed to remove hash file: {hashFile}: {ex.Message}");
        }

        return true;
    }

    /// <summary>
    /// Best-effort attempt to make a file writable (Unix mode <c>rw-r--r--</c>) before a subsequent
    /// write or delete. Shared by <see cref="StoreHash"/> and <see cref="RemoveHash"/>, both of which
    /// need to clear the read-only mode <see cref="StoreHash"/> sets after writing. Mirrors Rust's
    /// discarded-result <c>let _ = ...</c> idiom: any failure here is swallowed since it is only a
    /// speed bump, not a security boundary — the subsequent write/delete will surface its own error
    /// if permissions truly block it.
    /// </summary>
    /// <param name="path">The file to make writable.</param>
    private static void TryMakeWritable(string path)
    {
        try
        {
            File.SetUnixFileMode(path, WritableMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: Rust discards this error too (`let _ = ...`).
        }
    }

    /// <summary>
    /// Verifies hook integrity for a specific hook path (testable). Port of Rust <c>verify_hook_at</c>
    /// (integrity.rs:142).
    /// </summary>
    /// <param name="hookPath">The hook file path to verify.</param>
    /// <returns>The resulting <see cref="IntegrityStatus"/>.</returns>
    /// <exception cref="InitAbortException">The stored hash file exists but is malformed, or could not be read.</exception>
    public static IntegrityStatus VerifyHookAt(string hookPath)
    {
        var hashFile = HashPathFor(hookPath);
        var hookExists = File.Exists(hookPath);
        var hashExists = File.Exists(hashFile);

        if (!hookExists && !hashExists)
        {
            return IntegrityStatus.NotInstalled;
        }

        if (!hookExists)
        {
            return IntegrityStatus.OrphanedHash;
        }

        if (!hashExists)
        {
            return IntegrityStatus.NoBaseline;
        }

        var stored = ReadStoredHash(hashFile);
        var actual = ComputeHash(hookPath);

        return stored == actual ? IntegrityStatus.Verified : IntegrityStatus.Tampered(stored, actual);
    }

    /// <summary>
    /// Reads the stored hash from the hash file. Expects the exact <c>sha256sum -c</c> format:
    /// <c>&lt;64 hex&gt;  &lt;filename&gt;\n</c> (two-space separator) — rejects malformed files
    /// rather than silently accepting them. Port of Rust <c>read_stored_hash</c> (integrity.rs:169).
    /// </summary>
    /// <param name="path">The hash sidecar file path.</param>
    /// <returns>The 64-character lowercase (or uppercase — Rust's <c>is_ascii_hexdigit</c> accepts both) hex hash.</returns>
    /// <exception cref="InitAbortException">
    /// The file could not be read, was empty, lacked the two-space separator, or the hash portion
    /// was not exactly 64 hex characters.
    /// </exception>
    private static string ReadStoredHash(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InitAbortException($"Failed to read hash file: {path}: {ex.Message}");
        }

        var lines = InitArtifacts.SplitRustLines(content);
        if (lines.Length == 0)
        {
            throw new InitAbortException($"Empty hash file: {path}");
        }

        var line = lines[0];

        // sha256sum format uses two-space separator: "<hash>  <filename>"
        var sepIdx = line.IndexOf("  ", StringComparison.Ordinal);
        if (sepIdx < 0)
        {
            throw new InitAbortException($"Invalid hash format in {path} (expected 'hash  filename')");
        }

        var hash = line[..sepIdx];
        if (hash.Length != 64 || !IsAllHexDigits(hash))
        {
            throw new InitAbortException($"Invalid SHA-256 hash in {path}");
        }

        return hash;
    }

    /// <summary>Returns <see langword="true"/> if every character is an ASCII hex digit (0-9, a-f, A-F).</summary>
    /// <param name="s">The string to check.</param>
    /// <returns>Whether <paramref name="s"/> consists entirely of ASCII hex digits.</returns>
    private static bool IsAllHexDigits(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the default hook path (<c>{ClaudeConfigDir}/hooks/rtk-rewrite.sh</c>). Port of Rust
    /// <c>resolve_hook_path</c> (integrity.rs:196).
    /// </summary>
    /// <returns>The resolved hook path.</returns>
    public static string ResolveHookPath() =>
        Path.Combine(SettingsPatcher.ResolveClaudeDir(), HooksSubdir, RewriteHookFile);

    /// <summary>
    /// Runs the integrity check and prints results (for the <c>rtk verify</c> subcommand). Port of
    /// Rust <c>run_verify</c> (integrity.rs:201).
    /// </summary>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c> counted flag).</param>
    /// <returns>
    /// The process exit code: <c>1</c> when the hook is <see cref="IntegrityStatusKind.Tampered"/>,
    /// otherwise <c>0</c>.
    /// </returns>
    public static int RunVerify(int verbose)
    {
        var hookPath = ResolveHookPath();
        var hashFile = HashPathFor(hookPath);

        if (verbose > 0)
        {
            Console.Error.Write($"Hook:  {hookPath}\n");
            Console.Error.Write($"Hash:  {hashFile}\n");
        }

        // If no legacy script exists, check for native binary command registration.
        if (!File.Exists(hookPath) && !File.Exists(hashFile))
        {
            var claudeDir = SettingsPatcher.ResolveClaudeDir();
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                string content;
                try
                {
                    content = File.ReadAllText(settingsPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    content = string.Empty;
                }

                if (content.Contains(SettingsPatcher.ClaudeHookCommand, StringComparison.Ordinal))
                {
                    Console.Out.Write("PASS  native binary hook registered in settings.json\n");
                    Console.Out.Write("      command: rtk hook claude\n");
                    Console.Out.Write("      (no script file — integrity check not applicable)\n");
                    return 0;
                }
            }

            Console.Out.Write("SKIP  RTK hook not installed\n");
            Console.Out.Write("      Run `rtk init -g` to install.\n");
            return 0;
        }

        var status = VerifyHookAt(hookPath);
        switch (status.Kind)
        {
            case IntegrityStatusKind.Verified:
                var hash = ComputeHash(hookPath);
                Console.Out.Write("PASS  hook integrity verified\n");
                Console.Out.Write($"      sha256:{hash}\n");
                Console.Out.Write($"      {hookPath}\n");
                return 0;

            case IntegrityStatusKind.Tampered:
                Console.Error.Write("FAIL  hook integrity check FAILED\n");
                Console.Error.Write("\n");
                Console.Error.Write($"  Expected: {status.Expected}\n");
                Console.Error.Write($"  Actual:   {status.Actual}\n");
                Console.Error.Write("\n");
                Console.Error.Write("  The hook file has been modified outside of `rtk init`.\n");
                Console.Error.Write("  This could indicate tampering or a manual edit.\n");
                Console.Error.Write("\n");
                Console.Error.Write("  To restore: rtk init -g --auto-patch\n");
                Console.Error.Write($"  To inspect: cat {hookPath}\n");
                return 1;

            case IntegrityStatusKind.NoBaseline:
                Console.Out.Write("WARN  no baseline hash found\n");
                Console.Out.Write("      Hook exists but was installed before integrity checks.\n");
                Console.Out.Write("      Run `rtk init -g` to establish baseline.\n");
                return 0;

            case IntegrityStatusKind.NotInstalled:
                Console.Out.Write("SKIP  RTK hook not installed\n");
                Console.Out.Write("      Run `rtk init -g` to install.\n");
                return 0;

            case IntegrityStatusKind.OrphanedHash:
                Console.Error.Write("WARN  hash file exists but hook is missing\n");
                Console.Error.Write("      Run `rtk init -g` to reinstall.\n");
                return 0;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status.Kind, "Unhandled IntegrityStatusKind");
        }
    }

    /// <summary>
    /// Runtime integrity gate, called at startup for operational commands. Port of Rust
    /// <c>runtime_check</c> (integrity.rs:279). Not currently wired into any command dispatch path in
    /// this port (that wiring is out of this task's scope) — ported here for parity and future reuse.
    /// </summary>
    /// <remarks>
    /// Behavior: <see cref="IntegrityStatusKind.Verified"/>/<see cref="IntegrityStatusKind.NotInstalled"/>/
    /// <see cref="IntegrityStatusKind.NoBaseline"/> are silent (continue); <see cref="IntegrityStatusKind.Tampered"/>
    /// prints a warning to stderr and signals a non-zero exit; <see cref="IntegrityStatusKind.OrphanedHash"/>
    /// warns to stderr but still continues. When the legacy script path does not exist (the binary-hook
    /// model has no script file), this is a cheap no-op. No env-var bypass is provided — if the hook is
    /// legitimately modified, re-run <c>rtk init -g --auto-patch</c> to re-establish the baseline.
    /// </remarks>
    /// <returns><c>1</c> when the hook is <see cref="IntegrityStatusKind.Tampered"/>; otherwise <c>0</c>.</returns>
    public static int RuntimeCheck()
    {
        var hookPath = ResolveHookPath();

        // If the legacy script doesn't exist, skip integrity check entirely. In the new binary
        // command model, there is no script file to verify.
        if (!File.Exists(hookPath))
        {
            return 0;
        }

        var status = VerifyHookAt(hookPath);
        switch (status.Kind)
        {
            case IntegrityStatusKind.Verified:
            case IntegrityStatusKind.NotInstalled:
                // All good, proceed.
                return 0;

            case IntegrityStatusKind.NoBaseline:
                // Installed before integrity checks — don't block. Silently skip to avoid noise
                // for users who haven't re-run init.
                return 0;

            case IntegrityStatusKind.Tampered:
                Console.Error.Write("rtk: hook integrity check FAILED\n");
                Console.Error.Write($"  Expected hash: {Truncate16(status.Expected!)}...\n");
                Console.Error.Write($"  Actual hash:   {Truncate16(status.Actual!)}...\n");
                Console.Error.Write("\n");
                Console.Error.Write("  The hook at ~/.claude/hooks/rtk-rewrite.sh has been modified.\n");
                Console.Error.Write("  This may indicate tampering. RTK will not execute.\n");
                Console.Error.Write("\n");
                Console.Error.Write("  To restore:  rtk init -g --auto-patch\n");
                Console.Error.Write("  To inspect:  rtk verify\n");
                return 1;

            case IntegrityStatusKind.OrphanedHash:
                Console.Error.Write("rtk: warning: hash file exists but hook is missing\n");
                Console.Error.Write("  Run `rtk init -g` to reinstall.\n");
                // Don't block — hook is gone, nothing to exploit.
                return 0;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status.Kind, "Unhandled IntegrityStatusKind");
        }
    }

    /// <summary>Truncates a hash to its first 16 characters (or fewer, if shorter), matching Rust's <c>expected.get(..16).unwrap_or(&amp;expected)</c>.</summary>
    /// <param name="hash">The hash to truncate.</param>
    /// <returns>The first 16 characters of <paramref name="hash"/>, or the whole string if shorter.</returns>
    private static string Truncate16(string hash) => hash.Length <= 16 ? hash : hash[..16];
}
