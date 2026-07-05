using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RtkSharp.Core.Tracking;
using RtkSharp.Hooks;

namespace RtkSharp.Core;

/// <summary>
/// Implements the <c>rtk telemetry</c> CLI verb group (<c>status</c>/<c>enable</c>/<c>disable</c>/
/// <c>forget</c>): manages the RGPD/GDPR anonymous-usage-telemetry consent state. Faithful port of
/// Rust <c>src/core/telemetry_cmd.rs</c> (<c>run</c> and its four subcommand handlers) plus the
/// salt/device-hash primitives from <c>src/core/telemetry.rs</c> that those handlers call.
/// </summary>
/// <remarks>
/// <para>
/// <b>The actual network calls are permanently inert, matching the shipped oracle exactly — not a
/// simplification, a faithful reproduction.</b> Rust's <c>send_ping</c>/<c>send_erasure_request</c>
/// are gated on <c>option_env!("RTK_TELEMETRY_URL")</c>, a compile-time environment variable never set
/// on the reference <c>target/release/rtk.exe</c> build this port is verified against (confirmed via a
/// binary string scan — no telemetry URL/domain literal exists anywhere in that executable). On that
/// oracle, <c>send_erasure_request</c> always immediately fails with <c>"no telemetry endpoint
/// configured"</c>, and the pre-dispatch <c>maybe_ping()</c> (called before every command,
/// <c>main.rs</c>:1470) always no-ops. This port therefore never attempts a real HTTP call: the one
/// observable network-shaped behavior (<c>forget</c>'s erasure-request failure text) is reproduced as
/// a hardcoded "no telemetry endpoint configured" failure path, byte-identical to what the real oracle
/// binary actually prints. Should the Rust oracle ever ship with a real compiled-in URL, this would
/// become a genuine divergence — see <c>docs/parity/compatibility-ledger.md</c>.
/// </para>
/// <para>
/// <b><c>forget</c>'s local-tracking-database path deliberately ignores <c>RTK_DB_PATH</c>/
/// <c>config.tracking.database_path</c> — a faithfully-preserved Rust-source inconsistency.</b> Unlike
/// <see cref="Tracking.Tracker.ResolveDbPath"/> (used everywhere else tracking data is read/written,
/// honoring <c>RTK_DB_PATH</c> as its top-priority override), Rust's <c>run_forget</c>
/// (<c>telemetry_cmd.rs</c>:132-135) computes the history-database path directly as
/// <c>dirs::data_local_dir()/rtk/history.db</c>, never consulting <c>RTK_DB_PATH</c> or the config
/// file at all. This means a user who has redirected their tracking database via <c>RTK_DB_PATH</c>
/// will find <c>rtk telemetry forget</c> deletes the wrong (default-location) file, or reports nothing
/// to delete if no file exists there — a real, surprising-but-genuine oracle behavior, reproduced here
/// via a direct <see cref="TrustCommand.ResolveDataDir"/>/<see cref="TrackingConstants"/> path build
/// rather than routing through <c>Tracker.ResolveDbPath</c>.
/// </para>
/// <para>
/// <b>Disclosed simplification: Clap usage-error text.</b> Like <c>GainCommand</c>/
/// <c>HookAuditCommand</c>, a missing or unrecognized subcommand does not reproduce Clap's full
/// multi-line usage banner byte-for-byte; this port emits a short <c>error: ...</c> message and exits
/// 2 (Clap's own usage-error exit code).
/// </para>
/// </remarks>
public static class TelemetryCommand
{
    /// <summary>
    /// Runs <c>rtk telemetry</c> with the given arguments (the remainder after the <c>telemetry</c>
    /// verb): dispatches to <c>status</c>/<c>enable</c>/<c>disable</c>/<c>forget</c>.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>telemetry</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.Write("error: 'rtk telemetry' requires a subcommand but one was not provided\n  [subcommands: status, enable, disable, forget]\n\nUsage: rtk telemetry <COMMAND>\n\nFor more information, try '--help'.\n");
            return 2;
        }

        var subcommand = args[0];
        if (args.Length > 1)
        {
            Console.Error.Write($"error: unexpected argument '{args[1]}' found\n");
            return 2;
        }

        try
        {
            return subcommand switch
            {
                "status" => RunStatus(Console.Out),
                "enable" => RunEnable(Console.In, Console.Out, Console.Error, isInteractive: !Console.IsInputRedirected),
                "disable" => RunDisable(Console.Out),
                "forget" => RunForget(Console.Out, Console.Error),
                _ => UnrecognizedSubcommand(subcommand),
            };
        }
        catch (Exception ex)
        {
            // Mirrors Rust's top-level `eprintln!("rtk: {:#}", e); std::process::exit(1);` for a
            // propagated `?` failure — the only such path here is `run_forget`'s hard salt-delete
            // failure (telemetry_cmd.rs:123-124).
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    private static int UnrecognizedSubcommand(string subcommand)
    {
        Console.Error.Write($"error: unrecognized subcommand '{subcommand}'\n\nUsage: rtk telemetry <COMMAND>\n\nFor more information, try '--help'.\n");
        return 2;
    }

    /// <summary>
    /// Runs <c>rtk telemetry status</c>. Faithful port of <c>run_status</c>
    /// (<c>telemetry_cmd.rs</c>:21-61).
    /// </summary>
    /// <param name="stdout">The destination for all output.</param>
    /// <returns>0 (this subcommand never fails).</returns>
    internal static int RunStatus(TextWriter stdout)
    {
        var config = Config.LoadOrDefault();

        var consentStr = config.Telemetry.ConsentGiven switch
        {
            true => "yes",
            false => "no",
            null => "never asked",
        };

        var enabledStr = config.Telemetry.Enabled ? "yes" : "no";
        var envOverride = Environment.GetEnvironmentVariable("RTK_TELEMETRY_DISABLED") == "1";

        stdout.Write("Telemetry status:\n");
        stdout.Write($"  consent:       {consentStr}\n");
        if (config.Telemetry.ConsentDate is { } date)
        {
            stdout.Write($"  consent date:  {date}\n");
        }

        stdout.Write($"  enabled:       {enabledStr}\n");
        if (envOverride)
        {
            stdout.Write("  env override:  RTK_TELEMETRY_DISABLED=1 (blocked)\n");
        }

        var saltPath = SaltFilePath();
        if (File.Exists(saltPath))
        {
            var hash = GenerateDeviceHash();
            stdout.Write($"  device hash:   {hash[..8]}...{hash[56..]}\n");
        }
        else
        {
            stdout.Write("  device hash:   (no salt file)\n");
        }

        stdout.Write("\n");
        stdout.Write("Data controller: RTK AI Labs, contact@rtk-ai.app\n");
        stdout.Write("Details: https://github.com/rtk-ai/rtk/blob/master/docs/TELEMETRY.md\n");

        return 0;
    }

    /// <summary>
    /// Runs <c>rtk telemetry enable</c>. Faithful port of <c>run_enable</c>
    /// (<c>telemetry_cmd.rs</c>:63-101), including its interactive-terminal requirement.
    /// </summary>
    /// <param name="stdin">The source to read the y/N response from.</param>
    /// <param name="stdout">The destination for the outcome message.</param>
    /// <param name="stderr">The destination for the consent-notice prompt (and the non-interactive error).</param>
    /// <param name="isInteractive">Whether stdin is an interactive terminal (Rust's <c>io::stdin().is_terminal()</c>).</param>
    /// <returns>0 on success; 1 if <paramref name="isInteractive"/> is <see langword="false"/> (Rust's <c>anyhow::bail!</c>, rendered by the top-level handler as exit 1).</returns>
    internal static int RunEnable(TextReader stdin, TextWriter stdout, TextWriter stderr, bool isInteractive)
    {
        if (!isInteractive)
        {
            stderr.Write("rtk: consent requires interactive terminal — cannot enable telemetry in piped mode\n");
            return 1;
        }

        stderr.Write("RTK collects anonymous usage metrics once per day to improve filters.\n");
        stderr.Write("\n");
        stderr.Write("  What:    command names (not arguments), token savings, OS, version\n");
        stderr.Write("  Who:     RTK AI Labs, contact@rtk-ai.app\n");
        stderr.Write("  Details: https://github.com/rtk-ai/rtk/blob/master/docs/TELEMETRY.md\n");
        stderr.Write("\n");
        stderr.Write("Enable anonymous telemetry? [y/N] ");

        var line = stdin.ReadLine() ?? string.Empty;
        var response = line.Trim().ToLowerInvariant();
        var accepted = response is "y" or "yes";

        SaveTelemetryConsent(accepted);

        stdout.Write(accepted
            ? "Telemetry enabled. Disable anytime: rtk telemetry disable\n"
            : "Telemetry not enabled.\n");

        return 0;
    }

    /// <summary>
    /// Runs <c>rtk telemetry disable</c>. Faithful port of <c>run_disable</c>
    /// (<c>telemetry_cmd.rs</c>:103-107).
    /// </summary>
    /// <param name="stdout">The destination for the confirmation message.</param>
    /// <returns>0 (this subcommand never fails).</returns>
    internal static int RunDisable(TextWriter stdout)
    {
        SaveTelemetryConsent(false);
        stdout.Write("Telemetry disabled.\n");
        return 0;
    }

    /// <summary>
    /// Runs <c>rtk telemetry forget</c> (GDPR Art. 17 right-to-erasure). Faithful port of
    /// <c>run_forget</c> (<c>telemetry_cmd.rs</c>:109-159): revokes consent, computes the device hash
    /// before deleting the salt file (hard-failing if the delete itself fails), best-effort deletes
    /// the ping marker, deletes the local tracking database (soft-failing to stderr), and always
    /// hits the erasure-request's "no endpoint configured" failure path (see this type's remarks).
    /// </summary>
    /// <param name="stdout">The destination for success/status messages.</param>
    /// <param name="stderr">The destination for soft-failure messages.</param>
    /// <returns>0 on success; propagates a thrown exception (surfaced as exit 1 by <see cref="Run"/>'s caller) only if the salt-file delete itself fails, matching Rust's hard <c>.with_context()?</c>.</returns>
    internal static int RunForget(TextWriter stdout, TextWriter stderr)
    {
        SaveTelemetryConsent(false);

        var saltPath = SaltFilePath();
        var markerPath = TelemetryMarkerPath();

        var deviceHash = File.Exists(saltPath) ? GenerateDeviceHash() : null;

        if (File.Exists(saltPath))
        {
            try
            {
                File.Delete(saltPath);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to delete {saltPath}: {ex.Message}", ex);
            }
        }

        if (File.Exists(markerPath))
        {
            try
            {
                File.Delete(markerPath);
            }
            catch (Exception)
            {
                // Best-effort, matching Rust's `let _ = std::fs::remove_file(&marker_path);`.
            }
        }

        var dbPath = Path.Combine(TrustCommand.ResolveDataDir(), TrackingConstants.RtkDataDir, TrackingConstants.HistoryDb);
        if (File.Exists(dbPath))
        {
            try
            {
                File.Delete(dbPath);
                stdout.Write($"Local tracking database deleted: {dbPath}\n");
            }
            catch (Exception ex)
            {
                stderr.Write($"rtk: could not delete {dbPath}: {ex.Message}\n");
            }
        }

        if (deviceHash is { } hash)
        {
            // send_erasure_request always fails with this exact message on the real oracle binary —
            // see this type's remarks on why a real network call is never attempted here.
            stderr.Write("rtk: could not reach server: no telemetry endpoint configured\n");
            stderr.Write("  To complete erasure, email contact@rtk-ai.app\n");
            stderr.Write($"  with your device hash: {hash}\n");
        }

        stdout.Write("Local telemetry data deleted. Telemetry disabled.\n");
        return 0;
    }

    /// <summary>
    /// Records the user's consent decision to <c>config.toml</c>. Faithful port of
    /// <c>save_telemetry_consent</c> (<c>init.rs</c>:449-457): setting <c>enabled</c> equal to
    /// <paramref name="accepted"/> (not independently controllable), stamping <c>consent_date</c> with
    /// the current UTC instant every time this is called (even to re-decline).
    /// </summary>
    /// <param name="accepted">Whether the user accepted telemetry.</param>
    private static void SaveTelemetryConsent(bool accepted)
    {
        var config = Config.LoadOrDefault();
        config.Telemetry.ConsentGiven = accepted;
        config.Telemetry.Enabled = accepted;
        config.Telemetry.ConsentDate = TrustCommand.RfcTimestampUtcNow();
        config.Save();
    }

    /// <summary>
    /// Resolves the device-salt file path: <c>{data_dir}/rtk/.device_salt</c>. Faithful port of
    /// <c>salt_file_path()</c> (<c>telemetry.rs</c>:210-215), reusing <see cref="TrustCommand.ResolveDataDir"/>
    /// for the platform-specific base (which also honors the .NET-only <c>RTK_DATA_DIR_OVERRIDE</c>
    /// test escape hatch — Rust's own <c>dirs::data_local_dir()</c> has no such override).
    /// </summary>
    /// <returns>The resolved salt-file path.</returns>
    internal static string SaltFilePath() => Path.Combine(TrustCommand.ResolveDataDir(), "rtk", ".device_salt");

    /// <summary>
    /// Resolves the last-ping marker file path, creating the containing directory as a side effect —
    /// faithful port of <c>telemetry_marker_path()</c> (<c>telemetry.rs</c>:441-447), which
    /// unconditionally calls <c>std::fs::create_dir_all</c> every time it is invoked, even when only
    /// checking for the marker's existence (e.g. in <c>run_forget</c>).
    /// </summary>
    /// <returns>The resolved marker-file path.</returns>
    internal static string TelemetryMarkerPath()
    {
        var dataDir = Path.Combine(TrustCommand.ResolveDataDir(), "rtk");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, ".telemetry_last_ping");
    }

    /// <summary>
    /// Computes <c>sha256(salt)</c> as a lowercase hex string. Faithful port of
    /// <c>generate_device_hash()</c> (<c>telemetry.rs</c>:157-162) — despite the "device hash" name,
    /// this hashes only the persisted salt, not any actual machine-identifying data.
    /// </summary>
    /// <returns>The 64-character lowercase-hex device hash.</returns>
    internal static string GenerateDeviceHash()
    {
        var salt = GetOrCreateSalt();
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(salt));
        return Convert.ToHexStringLower(bytes);
    }

    private static string? _cachedSalt;

    /// <summary>
    /// Clears the process-wide salt cache. Mirrors what a fresh Rust CLI process gets for free (its
    /// <c>OnceLock</c> only ever lives for one invocation) — a real CLI invocation of this port is
    /// also a fresh process, so this is exposed purely for test isolation, where many "invocations"
    /// share one process and would otherwise leak a cached salt across unrelated test cases.
    /// </summary>
    internal static void ResetSaltCacheForTests() => _cachedSalt = null;

    /// <summary>
    /// Reads the persisted salt if valid (64 lowercase-or-uppercase hex chars), else generates and
    /// persists a fresh random one, best-effort (a write failure is swallowed, matching Rust's
    /// <c>if let Ok(mut f) = File::create(...)</c>). Faithful port of <c>get_or_create_salt()</c>
    /// (<c>telemetry.rs</c>:164-194), including its process-wide cache (Rust's <c>OnceLock</c>) —
    /// harmless here since each CLI invocation is a fresh process, so the cache only dedupes repeated
    /// calls within one invocation (e.g. <c>forget</c>'s hash-before-delete plus any future caller).
    /// </summary>
    /// <returns>The 64-character lowercase-hex salt.</returns>
    internal static string GetOrCreateSalt()
    {
        if (_cachedSalt is { } cached)
        {
            return cached;
        }

        var saltPath = SaltFilePath();
        if (File.Exists(saltPath))
        {
            var trimmed = File.ReadAllText(saltPath).Trim();
            if (trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit))
            {
                _cachedSalt = trimmed;
                return trimmed;
            }
        }

        var salt = RandomSalt();
        var parent = Path.GetDirectoryName(saltPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            File.WriteAllText(saltPath, salt);
        }
        catch (Exception)
        {
            // Best-effort persistence, matching Rust's `if let Ok(mut f) = File::create(...)` swallow.
        }

        _cachedSalt = salt;
        return salt;
    }

    /// <summary>
    /// Generates a fresh 64-character lowercase-hex random salt (32 random bytes). Faithful port of
    /// <c>random_salt()</c> (<c>telemetry.rs</c>:196-208), including its SHA-256-of-timestamp+PID
    /// fallback for the (practically unreachable on .NET) case where secure random generation fails.
    /// </summary>
    /// <returns>The 64-character lowercase-hex salt.</returns>
    private static string RandomSalt()
    {
        try
        {
            var buf = new byte[32];
            RandomNumberGenerator.Fill(buf);
            return Convert.ToHexStringLower(buf);
        }
        catch (Exception)
        {
            var fallback = $"{DateTime.UtcNow:O}:{Environment.ProcessId}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fallback));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
