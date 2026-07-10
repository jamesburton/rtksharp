using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtkSharp.Filters.Toml;

namespace RtkSharp.Hooks;

// ---------------------------------------------------------------------------
// Persisted store model
// ---------------------------------------------------------------------------

/// <summary>
/// A single trust record: the SHA-256 hash a project-local filters file had when it was reviewed and
/// trusted, plus the RFC-3339 timestamp of that review. Faithful port of Rust <c>TrustEntry</c>
/// (<c>src/hooks/trust.rs:31-35</c>).
/// </summary>
public sealed class TrustEntry
{
    /// <summary>The lowercase-hex SHA-256 digest of the file's content at the time it was trusted.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The RFC-3339 timestamp (UTC) the trust decision was recorded.</summary>
    [JsonPropertyName("trusted_at")]
    public string TrustedAt { get; set; } = string.Empty;
}

/// <summary>
/// The on-disk trust store: a version tag plus every trusted file's record, keyed by its
/// canonicalized absolute path. Faithful port of Rust <c>TrustStore</c> (<c>trust.rs:25-29</c>).
/// </summary>
internal sealed class TrustStore
{
    /// <summary>Store format version (currently always written as <c>1</c>).</summary>
    [JsonPropertyName("version")]
    public uint Version { get; set; }

    /// <summary>Trust records keyed by the trusted file's canonicalized absolute path.</summary>
    [JsonPropertyName("trusted")]
    public Dictionary<string, TrustEntry> Trusted { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Source-generated JSON metadata for <see cref="TrustStore"/>, required because
/// <c>RtkSharp.csproj</c> publishes with <c>PublishAot=true</c> — reflection-based
/// <see cref="JsonSerializer"/> overloads are unavailable/unsafe under trimming.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TrustStore))]
internal sealed partial class TrustStoreJsonContext : JsonSerializerContext;

// ---------------------------------------------------------------------------
// Trust-check result (this module's own, richer shape)
// ---------------------------------------------------------------------------

/// <summary>
/// Which of the four states <see cref="TrustCommand.CheckTrustWithContent"/> resolved to. Faithful
/// port of Rust <c>TrustStatus</c>'s discriminant (<c>trust.rs:37-43</c>); the
/// <c>ContentChanged{expected,actual}</c> payload lives on <see cref="TrustCheckResult"/> instead,
/// since C# enums cannot carry associated data.
/// </summary>
public enum TrustStatusKind
{
    /// <summary>The file's content hash matches its stored trust record.</summary>
    Trusted,

    /// <summary>No trust record exists for this file.</summary>
    Untrusted,

    /// <summary>A trust record exists, but the file's content hash no longer matches it.</summary>
    ContentChanged,

    /// <summary><c>RTK_TRUST_PROJECT_FILTERS=1</c> was honored (CI environment detected).</summary>
    EnvOverride,
}

/// <summary>
/// Result of <see cref="TrustCommand.CheckTrustWithContent"/>: the resolved status, the mismatched
/// hash pair for <see cref="TrustStatusKind.ContentChanged"/>, and the verified file content for
/// <see cref="TrustStatusKind.Trusted"/>/<see cref="TrustStatusKind.EnvOverride"/>. Faithful port of
/// Rust's <c>(TrustStatus, Option&lt;String&gt;)</c> return shape (<c>trust.rs:103</c>) — this is the
/// module's own, full-fidelity result type; <see cref="TrustCommand.ToFilterTrustResult"/> adapts it
/// down to Task 3's simpler <see cref="FilterTrustResult"/> shape for <see cref="TomlFilterRegistry"/>.
/// </summary>
/// <param name="Kind">Which state this result represents.</param>
/// <param name="ExpectedHash">The previously stored hash, set only when <paramref name="Kind"/> is <see cref="TrustStatusKind.ContentChanged"/>.</param>
/// <param name="ActualHash">The freshly computed hash, set only when <paramref name="Kind"/> is <see cref="TrustStatusKind.ContentChanged"/>.</param>
/// <param name="Content">The verified file content, set only when <paramref name="Kind"/> is <see cref="TrustStatusKind.Trusted"/> or <see cref="TrustStatusKind.EnvOverride"/>.</param>
public readonly record struct TrustCheckResult(TrustStatusKind Kind, string? ExpectedHash = null, string? ActualHash = null, string? Content = null)
{
    /// <summary>The shared <see cref="TrustStatusKind.Untrusted"/> result (carries no payload).</summary>
    public static readonly TrustCheckResult UntrustedResult = new(TrustStatusKind.Untrusted);

    /// <summary>Builds a <see cref="TrustStatusKind.Trusted"/> result carrying the verified content.</summary>
    /// <param name="content">The file's verified content.</param>
    /// <returns>The <see cref="TrustStatusKind.Trusted"/> result.</returns>
    public static TrustCheckResult TrustedResult(string content) => new(TrustStatusKind.Trusted, Content: content);

    /// <summary>Builds a <see cref="TrustStatusKind.ContentChanged"/> result carrying the mismatched hashes.</summary>
    /// <param name="expected">The stored (expected) hash.</param>
    /// <param name="actual">The freshly computed (actual) hash.</param>
    /// <returns>The <see cref="TrustStatusKind.ContentChanged"/> result.</returns>
    public static TrustCheckResult ContentChangedResult(string expected, string actual) => new(TrustStatusKind.ContentChanged, expected, actual);

    /// <summary>Builds an <see cref="TrustStatusKind.EnvOverride"/> result carrying the file content.</summary>
    /// <param name="content">The file's content (unverified against any hash — the env override bypasses hashing entirely).</param>
    /// <returns>The <see cref="TrustStatusKind.EnvOverride"/> result.</returns>
    public static TrustCheckResult EnvOverrideResult(string content) => new(TrustStatusKind.EnvOverride, Content: content);
}

// ---------------------------------------------------------------------------
// Command
// ---------------------------------------------------------------------------

/// <summary>
/// Controls which project-local TOML filters are allowed to run. Faithful port of Rust
/// <c>src/hooks/trust.rs</c>: the <see cref="TrustStore"/> model, <c>check_trust_with_content</c>,
/// and the <c>rtk trust</c>/<c>rtk untrust</c> CLI verbs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trust-before-load model.</b> <c>.rtk/filters.toml</c> is loaded from CWD with the highest
/// precedence. An attacker can commit this file to a public repo to control what an LLM sees —
/// hiding malicious code, suppressing security scanner output, or rewriting command output entirely
/// via <c>replace</c> and <c>match_output</c> primitives. Untrusted filters are <b>skipped</b> (not
/// "loaded with a warning"); <c>rtk trust</c> stores the file's SHA-256 hash only after the user has
/// reviewed its content; any content change invalidates trust; <c>RTK_TRUST_PROJECT_FILTERS=1</c>
/// overrides the gate, but only inside a detected CI environment.
/// </para>
/// <para>
/// <b>Fail-secure, not fail-loud, inside <see cref="CheckTrustWithContent"/>.</b> Rust's own
/// <c>check_trust_with_content</c> signature is fallible (<c>Result&lt;(TrustStatus, ...)&gt;</c>) —
/// a canonicalization failure, for instance, propagates as an <c>Err</c> via <c>?</c>
/// (<c>trust.rs:125</c>). But every real production call site immediately collapses that <c>Err</c>
/// to <see cref="TrustStatusKind.Untrusted"/> via <c>.unwrap_or((TrustStatus::Untrusted, None))</c>
/// (<c>src/core/toml_filter.rs:195-196</c> and <c>:564-565</c> — the only two call sites in the
/// entire codebase). Since this port's <see cref="TrustCheckResult"/> is a plain value (not a
/// fallible <c>Result</c>-alike) and <see cref="TomlFilterRegistry.TrustChecker"/> has no channel for
/// propagating an exception as a distinct fourth state anyway, <see cref="CheckTrustWithContent"/>
/// bakes that same collapse in directly: every internal failure (canonicalization, an unreadable
/// filter, a corrupt store) resolves to <see cref="TrustStatusKind.Untrusted"/> here, matching the
/// only <em>observable</em> behavior Rust ever actually exhibits at either call site — never
/// <see cref="TrustStatusKind.Trusted"/>, per the "all errors are soft, fail-secure" contract stated
/// in <c>check_trust_with_content</c>'s own doc comment (<c>trust.rs:94-95</c>).
/// </para>
/// <para>
/// <b>Fail-loud for the CLI verbs.</b> <c>rtk trust</c>/<c>rtk untrust</c>/<c>rtk trust --list</c> are
/// user-invoked, one-shot commands, like <c>rtk config</c>/<c>init</c>/<c>verify</c> — the RTK-wide
/// "never block the user" fallback pattern does not apply to them. <see cref="RunTrust"/> and
/// <see cref="RunUntrust"/> catch any exception once, at the top, and report it as <c>rtk: {message}</c>
/// on stderr with exit code 1, mirroring Rust's <c>anyhow::Error</c> propagation out of <c>main</c>.
/// </para>
/// </remarks>
public static class TrustCommand
{
    private const string ProjectFilterRelativePath = ".rtk/filters.toml";
    private const string DataSubDir = "rtk";
    private const string TrustedFiltersFileName = "trusted_filters.json";
    private const string EnvOverrideVar = "RTK_TRUST_PROJECT_FILTERS";

    /// <summary>
    /// Test/parity-only override for the platform local-data directory (the .NET equivalent of
    /// Rust's <c>dirs::data_local_dir()</c>), consulted by <see cref="ResolveDataDir"/> before
    /// falling back to the real OS local-data directory. This is <b>never a user-facing flag</b> —
    /// it exists solely so parity/unit tests can redirect the trust store path deterministically on
    /// every OS, including Windows, where <c>dirs::data_local_dir()</c> resolves via the same Win32
    /// known-folder API (<c>SHGetKnownFolderPath</c>) as <c>dirs::config_dir()</c> — and was
    /// empirically confirmed (by overriding <c>%LOCALAPPDATA%</c> in-process and observing
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> still return the real
    /// path) to ignore an overridden <c>%LOCALAPPDATA%</c> process environment variable, exactly like
    /// Phase 9b Task 4's <c>RTK_CONFIG_DIR_OVERRIDE</c> finding for <c>%APPDATA%</c>. Do not read this
    /// from user-facing documentation or CLI help text.
    /// </summary>
    /// <remarks>
    /// <b>Only redirects this port, not the Rust oracle.</b> This override only ever affects
    /// RtkSharp's own path resolution. A parity test that also shells out to the real <c>rtk</c>
    /// binary and expects <em>it</em> to honor this variable will be disappointed on Windows — the
    /// oracle's own <c>dirs::data_local_dir()</c> call still resolves the real
    /// <c>%LOCALAPPDATA%\rtk\trusted_filters.json</c> regardless of any env var set for the port's
    /// process, since setting an env var for this (.NET) process does not affect a separately-invoked
    /// oracle process's own environment inspection of the real Win32 known-folder API. A parity
    /// harness comparing both sides must isolate the oracle's trust store some other way (e.g. a
    /// throwaway <c>HOME</c>/profile redirection at the process-launch level, or simply never
    /// exercising `rtk trust`/`rtk untrust` against the real oracle binary on a shared machine).
    /// </remarks>
    internal const string DataDirOverrideEnvVar = "RTK_DATA_DIR_OVERRIDE";

    // -----------------------------------------------------------------------
    // Store path resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the platform local-data directory, honoring <see cref="DataDirOverrideEnvVar"/> for
    /// tests/parity harnesses. Faithful port of Rust's <c>dirs::data_local_dir()</c> resolution rules
    /// (Windows: <c>%LOCALAPPDATA%</c>; macOS: <c>~/Library/Application Support</c>; Linux/other:
    /// <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c>).
    /// </summary>
    /// <returns>The resolved local-data directory.</returns>
    internal static string ResolveDataDir()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirOverrideEnvVar);
        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden;
        }

        if (OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return xdg;
        }

        var linuxHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(linuxHome, ".local", "share");
    }

    /// <summary>
    /// Resolves the trust store path: <c>{data_dir}/rtk/trusted_filters.json</c>. Port of Rust
    /// <c>store_path</c> (<c>trust.rs:49-52</c>).
    /// </summary>
    /// <returns>The resolved trust store file path.</returns>
    internal static string StorePath() => Path.Combine(ResolveDataDir(), DataSubDir, TrustedFiltersFileName);

    // -----------------------------------------------------------------------
    // Store I/O
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the trust store, or an empty default if it does not exist. Port of Rust <c>read_store</c>
    /// (<c>trust.rs:54-63</c>).
    /// </summary>
    /// <returns>The loaded (or default) <see cref="TrustStore"/>.</returns>
    /// <exception cref="IOException">The store file exists but could not be read.</exception>
    /// <exception cref="JsonException">The store file exists but is not valid JSON for this shape.</exception>
    private static TrustStore ReadStoreOrThrow()
    {
        var path = StorePath();
        if (!File.Exists(path))
        {
            return new TrustStore();
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Failed to read trust store: {path}", ex);
        }

        return JsonSerializer.Deserialize(content, TrustStoreJsonContext.Default.TrustStore) ?? new TrustStore();
    }

    /// <summary>
    /// Reads the trust store, silently defaulting to empty on any failure. Port of Rust's
    /// <c>read_store().unwrap_or_default()</c> idiom, used by <c>trust_filter_with_hash</c>,
    /// <c>untrust_filter</c>, and <c>list_trusted</c> (all of which discard read errors, unlike
    /// <see cref="CheckTrustWithContent"/>'s explicit warning path).
    /// </summary>
    /// <returns>The loaded store, or an empty default if reading/parsing failed.</returns>
    private static TrustStore TryReadStoreOrDefault()
    {
        try
        {
            return ReadStoreOrThrow();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new TrustStore();
        }
    }

    /// <summary>
    /// Writes the trust store, creating its parent directory as needed. Port of Rust <c>write_store</c>
    /// (<c>trust.rs:65-74</c>).
    /// </summary>
    /// <param name="store">The store to persist.</param>
    private static void WriteStore(TrustStore store)
    {
        var path = StorePath();
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var content = JsonSerializer.Serialize(store, TrustStoreJsonContext.Default.TrustStore);
        File.WriteAllText(path, content);
    }

    // -----------------------------------------------------------------------
    // Canonical path helper
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves <paramref name="filterPath"/> to an absolute, symlink-resolved canonical path for use
    /// as a trust-store key, or <see langword="null"/> if it cannot be resolved. Port of Rust
    /// <c>canonical_key</c> (<c>trust.rs:80-86</c>), which wraps <c>std::fs::canonicalize</c> —
    /// requires the path to exist (canonicalize is not defined for a nonexistent path) and resolves
    /// any symlink chain to its final target. This port approximates the latter via
    /// <see cref="FileInfo.ResolveLinkTarget"/> (<c>returnFinalTarget: true</c>): exact for a genuine
    /// reparse-point symlink, but does not attempt to replicate every corner of NTFS junction/hard-link
    /// resolution <c>std::fs::canonicalize</c> may special-case on Windows — a documented,
    /// intentionally narrow divergence, not a silent one.
    /// </summary>
    /// <param name="filterPath">The path to canonicalize.</param>
    /// <returns>The canonical absolute path, or <see langword="null"/> on any failure (fail-closed).</returns>
    internal static string? CanonicalKey(string filterPath)
    {
        try
        {
            if (!File.Exists(filterPath))
            {
                // std::fs::canonicalize errors for a nonexistent path -- fail closed, matching Rust.
                return null;
            }

            var info = new FileInfo(filterPath);
            var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
            return finalTarget is not null ? finalTarget.FullName : Path.GetFullPath(filterPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Public trust-check API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Checks whether <paramref name="filterPath"/> is a trusted project-local filters file, reading
    /// its bytes exactly once and returning both the trust status and (when permitted) its verified
    /// content. Faithful port of Rust <c>check_trust_with_content</c> (<c>trust.rs:103-164</c>) — see
    /// this type's remarks for why every internal failure resolves to
    /// <see cref="TrustStatusKind.Untrusted"/> here rather than propagating as an exception.
    /// </summary>
    /// <param name="filterPath">The path to the candidate project-local filters file.</param>
    /// <returns>The resolved <see cref="TrustCheckResult"/>.</returns>
    public static TrustCheckResult CheckTrustWithContent(string filterPath)
    {
        if (Environment.GetEnvironmentVariable(EnvOverrideVar) == "1")
        {
            if (IsCiEnvironment())
            {
                string envContent;
                try
                {
                    envContent = File.ReadAllText(filterPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Rust's `?` propagates this specific read failure as an Err (trust.rs:111-113),
                    // but both real call sites collapse any Err to Untrusted (see this type's
                    // remarks) -- so that is what this port observably does too.
                    return TrustCheckResult.UntrustedResult;
                }

                return TrustCheckResult.EnvOverrideResult(envContent);
            }

            Console.Error.Write("[rtk] WARNING: RTK_TRUST_PROJECT_FILTERS=1 ignored (CI environment not detected)\n");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filterPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TrustCheckResult.UntrustedResult;
        }

        var key = CanonicalKey(filterPath);
        if (key is null)
        {
            return TrustCheckResult.UntrustedResult;
        }

        TrustStore store;
        try
        {
            store = ReadStoreOrThrow();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Console.Error.Write($"[rtk] WARNING: trust store unreadable ({ex.Message}), treating all filters as untrusted\n");
            store = new TrustStore();
        }

        if (!store.Trusted.TryGetValue(key, out var entry))
        {
            return TrustCheckResult.UntrustedResult;
        }

        var actualHash = Integrity.ComputeHashBytes(bytes);

        if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
        {
            return TrustCheckResult.ContentChangedResult(entry.Sha256, actualHash);
        }

        try
        {
            var content = DecodeStrictUtf8(bytes);
            return TrustCheckResult.TrustedResult(content);
        }
        catch (DecoderFallbackException)
        {
            Console.Error.Write($"[rtk] WARNING: trusted filter {filterPath} is not valid UTF-8 — treating as untrusted\n");
            return TrustCheckResult.UntrustedResult;
        }
    }

    /// <summary>
    /// Adapts this module's full-fidelity <see cref="TrustCheckResult"/> down to Task 3's simpler
    /// <see cref="FilterTrustResult"/> shape, dropping the <see cref="TrustStatusKind.ContentChanged"/>
    /// hash payload <see cref="TomlFilterRegistry"/> does not need. This is the ready-made
    /// <see cref="TomlFilterRegistry.TrustChecker"/> implementation for whichever call site wires
    /// <see cref="TomlFilterRegistry.Load"/> up to the real trust check.
    /// </summary>
    /// <param name="filterPath">The path to the candidate project-local filters file.</param>
    /// <returns>The equivalent <see cref="FilterTrustResult"/>.</returns>
    public static FilterTrustResult ToFilterTrustResult(string filterPath) =>
        ToFilterTrustResult(CheckTrustWithContent(filterPath));

    /// <summary>
    /// Adapts an already-computed <see cref="TrustCheckResult"/> down to Task 3's
    /// <see cref="FilterTrustResult"/> shape. Exposed separately from
    /// <see cref="ToFilterTrustResult(string)"/> so tests can verify the mapping for all four states
    /// without needing four real files on disk.
    /// </summary>
    /// <param name="result">The result to adapt.</param>
    /// <returns>The equivalent <see cref="FilterTrustResult"/>.</returns>
    public static FilterTrustResult ToFilterTrustResult(TrustCheckResult result) => result.Kind switch
    {
        TrustStatusKind.Trusted => new FilterTrustResult(FilterTrustStatus.Trusted, result.Content),
        TrustStatusKind.Untrusted => new FilterTrustResult(FilterTrustStatus.Untrusted, null),
        TrustStatusKind.ContentChanged => new FilterTrustResult(FilterTrustStatus.ContentChanged, null),
        TrustStatusKind.EnvOverride => new FilterTrustResult(FilterTrustStatus.EnvOverride, result.Content),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Kind, "Unhandled TrustStatusKind"),
    };

    /// <summary>
    /// The <see cref="TomlFilterRegistry.TrustChecker"/> delegate backed by <see cref="CheckTrustWithContent"/>
    /// and <see cref="ToFilterTrustResult(TrustCheckResult)"/> — pass this directly to
    /// <see cref="TomlFilterRegistry.Load"/> once the real dispatch layer wires it in.
    /// </summary>
    /// <returns>The <see cref="TomlFilterRegistry.TrustChecker"/> delegate.</returns>
    public static TrustChecker AsTrustChecker() => ToFilterTrustResult;

    /// <summary>Whether any of the five CI-indicator env vars Rust checks is present (any value, including empty).</summary>
    /// <returns><see langword="true"/> if a CI environment was detected.</returns>
    private static bool IsCiEnvironment() =>
        Environment.GetEnvironmentVariable("CI") is not null ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null ||
        Environment.GetEnvironmentVariable("GITLAB_CI") is not null ||
        Environment.GetEnvironmentVariable("JENKINS_URL") is not null ||
        Environment.GetEnvironmentVariable("BUILDKITE") is not null;

    /// <summary>Decodes <paramref name="bytes"/> as strict UTF-8, throwing on any invalid sequence (mirrors Rust's <c>String::from_utf8</c>).</summary>
    /// <param name="bytes">The bytes to decode.</param>
    /// <returns>The decoded string.</returns>
    /// <exception cref="DecoderFallbackException">The bytes are not valid UTF-8.</exception>
    private static string DecodeStrictUtf8(byte[] bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);

    // -----------------------------------------------------------------------
    // Store mutation (used by the CLI verbs)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stores a pre-computed SHA-256 hash as trusted (avoids a second, TOCTOU-prone file read). Port
    /// of Rust <c>trust_filter_with_hash</c> (<c>trust.rs:167-180</c>).
    /// </summary>
    /// <param name="filterPath">The path to the file being trusted.</param>
    /// <param name="hash">The pre-computed SHA-256 hash of that file's exact reviewed bytes.</param>
    /// <exception cref="IOException"><paramref name="filterPath"/> could not be canonicalized.</exception>
    internal static void TrustFilterWithHash(string filterPath, string hash)
    {
        var key = CanonicalKey(filterPath) ?? throw new IOException($"Cannot resolve path: {filterPath}");

        var store = TryReadStoreOrDefault();
        store.Version = 1;
        store.Trusted[key] = new TrustEntry { Sha256 = hash, TrustedAt = RfcTimestampUtcNow() };
        WriteStore(store);
    }

    /// <summary>
    /// Removes the trust entry for <paramref name="filterPath"/>. Port of Rust <c>untrust_filter</c>
    /// (<c>trust.rs:183-191</c>).
    /// </summary>
    /// <param name="filterPath">The path to the file being untrusted.</param>
    /// <returns><see langword="true"/> if an entry existed and was removed.</returns>
    /// <exception cref="IOException"><paramref name="filterPath"/> could not be canonicalized.</exception>
    internal static bool UntrustFilter(string filterPath)
    {
        var key = CanonicalKey(filterPath) ?? throw new IOException($"Cannot resolve path: {filterPath}");

        var store = TryReadStoreOrDefault();
        var removed = store.Trusted.Remove(key);
        if (removed)
        {
            WriteStore(store);
        }

        return removed;
    }

    /// <summary>
    /// Lists every currently-trusted file. Port of Rust <c>list_trusted</c> (<c>trust.rs:194-197</c>).
    /// </summary>
    /// <returns>The trusted-file records, keyed by canonicalized path.</returns>
    internal static Dictionary<string, TrustEntry> ListTrusted() => TryReadStoreOrDefault().Trusted;

    /// <summary>
    /// Builds an RFC-3339 UTC timestamp string for "now", approximating Rust's
    /// <c>chrono::Utc::now().to_rfc3339()</c>. Shared with <see cref="RtkSharp.Core.TelemetryCommand"/>,
    /// which needs the identical format for its own <c>consent_date</c> field.
    /// </summary>
    /// <returns>The formatted timestamp.</returns>
    internal static string RfcTimestampUtcNow() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------------
    // CLI verbs
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs <c>rtk trust</c> (optionally <c>--list</c>) with the given arguments (the remainder after
    /// the <c>trust</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>trust</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int RunTrust(string[] args)
    {
        try
        {
            return RunTrustCore(args);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    private static int RunTrustCore(string[] args)
    {
        var list = false;
        foreach (var arg in args)
        {
            if (arg == "--list")
            {
                list = true;
            }
            else
            {
                throw new InvalidOperationException($"unrecognized trust argument: {arg}");
            }
        }

        return list ? RunListCore() : RunTrustReviewCore();
    }

    /// <summary>Port of the <c>list: true</c> branch of Rust <c>run_trust</c> (<c>trust.rs:205-219</c>).</summary>
    private static int RunListCore()
    {
        var trusted = ListTrusted();
        if (trusted.Count == 0)
        {
            Console.Out.Write("No trusted project filters.\n");
            return 0;
        }

        Console.Out.Write("Trusted project filters:\n");
        Console.Out.Write(new string('═', 60) + "\n");
        foreach (var (path, entry) in trusted)
        {
            var date = entry.TrustedAt.Length >= 10 ? entry.TrustedAt[..10] : entry.TrustedAt;
            Console.Out.Write($"  {path} (trusted {date})\n");
            Console.Out.Write($"    sha256:{entry.Sha256}\n");
        }

        return 0;
    }

    /// <summary>Port of the default (non-<c>--list</c>) branch of Rust <c>run_trust</c> (<c>trust.rs:221-256</c>).</summary>
    private static int RunTrustReviewCore()
    {
        if (!File.Exists(ProjectFilterRelativePath))
        {
            throw new InvalidOperationException("No .rtk/filters.toml found in current directory");
        }

        // Read ONCE to prevent TOCTOU: display and hash both come from this same buffer.
        var contentBytes = File.ReadAllBytes(ProjectFilterRelativePath);
        var content = DecodeLossyUtf8(contentBytes);

        Console.Out.Write("=== .rtk/filters.toml ===\n");
        Console.Out.Write(content + "\n");
        Console.Out.Write("=========================\n");
        Console.Out.Write("\n");

        PrintRiskSummary(content);

        var hash = Integrity.ComputeHashBytes(contentBytes);

        TrustFilterWithHash(ProjectFilterRelativePath, hash);

        Console.Out.Write("\n");
        var shortHash = hash.Length >= 16 ? hash[..16] : hash;
        Console.Out.Write($"Trusted .rtk/filters.toml (sha256:{shortHash})\n");
        Console.Out.Write("Project-local filters will now be applied.\n");

        return 0;
    }

    /// <summary>
    /// Runs <c>rtk untrust</c> with the given arguments (the remainder after the <c>untrust</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>untrust</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int RunUntrust(string[] args)
    {
        try
        {
            if (args.Length > 0)
            {
                throw new InvalidOperationException($"unrecognized untrust argument: {args[0]}");
            }

            bool removed;
            try
            {
                removed = UntrustFilter(ProjectFilterRelativePath);
            }
            catch (IOException)
            {
                // Matches Rust's `untrust_filter(...).unwrap_or(false)` -- if the file no longer
                // exists (so it can't be canonicalized), fall back gracefully rather than fail loud.
                removed = false;
            }

            if (removed)
            {
                Console.Out.Write("Trust revoked for .rtk/filters.toml\n");
                Console.Out.Write("Project-local filters will no longer be applied.\n");
            }
            else
            {
                Console.Out.Write("No trust entry found for current directory.\n");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------------
    // Risk analysis
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prints the risk summary for a project-local filters file's content. Port of Rust
    /// <c>print_risk_summary</c> (<c>trust.rs:277-298</c>).
    /// </summary>
    /// <param name="content">The filters file's content.</param>
    internal static void PrintRiskSummary(string content)
    {
        var filterCount = CountOccurrences(content, "[filters.");
        var hasReplace = content.Contains("replace", StringComparison.Ordinal);
        var hasMatchOutput = content.Contains("match_output", StringComparison.Ordinal);
        var hasDotPattern = content.Contains("pattern = \".\"", StringComparison.Ordinal) ||
            content.Contains("pattern = '.'", StringComparison.Ordinal);

        Console.Out.Write("Risk summary:\n");
        Console.Out.Write($"  Filters: {filterCount.ToString(CultureInfo.InvariantCulture)}\n");

        if (hasReplace)
        {
            Console.Out.Write("  [!] Contains 'replace' rules (can rewrite output)\n");
        }

        if (hasMatchOutput)
        {
            Console.Out.Write("  [!] Contains 'match_output' rules (can replace entire output)\n");
        }

        if (hasDotPattern)
        {
            Console.Out.Write("  [!] Contains catch-all pattern '.' (matches everything)\n");
        }

        if (!hasReplace && !hasMatchOutput && !hasDotPattern)
        {
            Console.Out.Write("  No high-risk patterns detected.\n");
        }
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/> (mirrors Rust's <c>str::matches(..).count()</c>).</summary>
    /// <param name="haystack">The string to search.</param>
    /// <param name="needle">The substring to count.</param>
    /// <returns>The number of occurrences.</returns>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Decodes <paramref name="bytes"/> as UTF-8, substituting the replacement character for any invalid sequence (mirrors Rust's <c>String::from_utf8_lossy</c>).</summary>
    /// <param name="bytes">The bytes to decode.</param>
    /// <returns>The decoded string.</returns>
    private static string DecodeLossyUtf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
