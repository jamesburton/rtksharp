using System.Diagnostics;
using System.Text;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Oracle parity battery for <c>rtk telemetry status</c>/<c>enable</c>/<c>disable</c>/<c>forget</c>
/// (<c>src/core/telemetry_cmd.rs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Safety-critical: two real, non-redirectable-on-Windows host directories are in play on every
/// entry, and this was confirmed the hard way during this file's own development.</b> Rust's
/// <c>dirs::config_dir()</c> (<c>%APPDATA%\rtk</c> — where <c>config.toml</c>'s <c>[telemetry]</c>
/// section lives) and <c>dirs::data_local_dir()</c> (<c>%LOCALAPPDATA%\rtk</c> — where the device-salt
/// file, ping marker, and tracking database live) have NO environment-variable override on the Rust
/// side; the .NET-only <c>RTK_CONFIG_DIR_OVERRIDE</c>/<c>RTK_DATA_DIR_OVERRIDE</c> escape hatches the
/// port itself honors do nothing to the oracle. <b>A manual sanity check while developing this port
/// ran `rtk telemetry disable` against the real oracle with only those two (oracle-inert) overrides
/// set, and it silently wrote an opt-out consent record to the real
/// `%APPDATA%\rtk\config.toml`</b> — confirmed and cleaned up before this file was written (the
/// directory did not exist before that command; it was deleted afterward, restoring the prior state
/// exactly). This is the same class of risk Phase 4's <c>ConfigFilterParityTests</c>/Phase 9b's
/// <c>InitParityTests</c> guard against, and this file follows their exact convention:
/// <see cref="GuardBothRealDirs"/> applies an NTFS-junction redirect to BOTH real directories for
/// EVERY entry, refusing (and marking the entry <c>Skipped</c>, which counts as a vacuous pass, not a
/// failure) whenever either real directory already has content — which it will on any developer
/// machine that has ever used the real tracking database, so this battery is expected to run mostly
/// via CI/a clean environment rather than a working dev box. See <c>ConfigFilterParityTests</c>'s own
/// remarks for the full rationale behind this "assume unsafe until proven otherwise" design.
/// </para>
/// <para>
/// <b>Never invoke <c>telemetry enable</c>/<c>disable</c>/<c>forget</c> against the real,
/// un-guarded oracle for any reason — including ad hoc manual verification.</b> Every entry in this
/// file (including <c>status</c>) goes through <see cref="GuardBothRealDirs"/> first; there is no
/// "read-only, so it's fine" exception, matching <c>ConfigFilterParityTests</c>'s empirical finding
/// that the oracle touches its tracking directory on every invocation regardless of subcommand.
/// </para>
/// <para>
/// <b><c>enable</c>'s accept path is not exercisable here.</b> Every entry passes
/// <c>stdin: ""</c> (forced non-interactive, matching this project's established convention), which
/// deterministically drives both binaries down <c>enable</c>'s non-interactive bail path — there is no
/// way for an automated harness to present a real TTY to the child process. The accept path is instead
/// covered by <c>TelemetryCommandTests.cs</c>'s unit tests, injecting a fake stdin/interactive flag.
/// </para>
/// <para>
/// <b>Collection isolation from <see cref="ConfigFilterParityTests"/>/<see cref="InitParityTests"/> —
/// found the hard way, not by inspection alone.</b> This file's <see cref="HostRealDirGuard"/> manages
/// a junction at the identical real paths (<c>%APPDATA%\rtk</c>, <c>%LOCALAPPDATA%\rtk</c>) those two
/// files already guard, via three independently-implemented guard classes. Running the full parity
/// suite once without this attribute crashed the entire test host: xUnit ran this file concurrently
/// with <c>ConfigFilterParityTests</c>, and both classes' <c>HostRealDirs</c> static constructors (or
/// their per-entry guard construction) raced on <c>mklink /J</c> against the same real
/// <c>%APPDATA%\rtk</c> path, producing <c>"Cannot create a file when that file already exists"</c>
/// and an unhandled <c>InvalidOperationException</c> that aborted the whole run (the failed
/// <c>mklink</c> itself never actually mutated the real directory, confirmed by inspection
/// immediately afterward, but a partially-successful race could have left a dangling junction or,
/// worse, a mid-flight I/O race against a real directory). Joining the exact same named
/// <c>[Collection("HostRealDirGuard")]</c> those two files already use — which xUnit never runs in
/// parallel with itself — eliminates the race by construction, per their own established convention.
/// </para>
/// </remarks>
[Collection("HostRealDirGuard")]
public class TelemetryParityTests
{
    private const double ParityThresholdPercent = 100.0;

    private const string ParityOracleTempPrefix = "rtk-telemetry-parity-";

    private sealed record Entry(
        string Label,
        string[] Args,
        string? ConfigTomlSeed = null,
        string? SaltSeed = null,
        bool SeedMarker = false,
        bool SeedDb = false,
        (string Key, string Value)[]? ExtraEnv = null);

    [Fact]
    public async Task TelemetryVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the telemetry parity gate.");
            return;
        }

        var (portFileName, portPrefixArgs) = LocateRtkSharp(repoRoot);
        if (portFileName is null)
        {
            Assert.Fail(
                "RtkSharp binary not found. It is normally copied next to the test assembly via the " +
                "project reference; if absent, publish it with " +
                "`dotnet publish RtkSharp -c Release -o .artifacts/publish -p:PublishAot=false`.");
            return;
        }

        var battery = BuildBattery();
        var results = new List<Result>();

        foreach (var entry in battery)
        {
            results.Add(await RunEntryAsync(entry, oraclePath, portFileName, portPrefixArgs));
        }

        var matched = results.Count(r => r.IsMatch);
        var total = results.Count;
        var percent = total == 0 ? 100.0 : matched * 100.0 / total;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "telemetry-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"telemetry parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} stdoutMatch={r.StdoutMatches}");
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    // ===================== battery definition =====================

    private const string ConsentGivenToml =
        "[telemetry]\nenabled = true\nconsent_given = true\nconsent_date = \"2026-01-01T00:00:00.000000+00:00\"\n";

    private const string ConsentDeclinedToml =
        "[telemetry]\nenabled = false\nconsent_given = false\nconsent_date = \"2026-01-01T00:00:00.000000+00:00\"\n";

    private const string ValidSalt = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";

    private static IReadOnlyList<Entry> BuildBattery() =>
    [
        new("status: no config, no salt file (defaults)", ["status"]),
        new(
            "status: consent given, enabled, with salt file (device hash shown)",
            ["status"],
            ConfigTomlSeed: ConsentGivenToml,
            SaltSeed: ValidSalt),
        new(
            "status: RTK_TELEMETRY_DISABLED=1 env override shown",
            ["status"],
            ConfigTomlSeed: ConsentGivenToml,
            ExtraEnv: [("RTK_TELEMETRY_DISABLED", "1")]),
        new("enable: non-interactive stdin -> bail message, exit 1", ["enable"]),
        new(
            "disable: from a previously-consented config",
            ["disable"],
            ConfigTomlSeed: ConsentGivenToml),
        new("disable: from defaults (never asked)", ["disable"]),
        new(
            "forget: salt + marker + db present -> full deletion + erasure-fail message",
            ["forget"],
            ConfigTomlSeed: ConsentGivenToml,
            SaltSeed: ValidSalt,
            SeedMarker: true,
            SeedDb: true),
        new(
            "forget: nothing to delete (no salt/marker/db)",
            ["forget"],
            ConfigTomlSeed: ConsentDeclinedToml),
        new("missing subcommand -> usage error, exit 2", []),
        new("unrecognized subcommand -> usage error, exit 2", ["bogus"]),
        new("extra argument after subcommand -> usage error, exit 2", ["status", "extra"]),
    ];

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-telemetry-parity-oracle-");
        var portTemp = CreateTempDir("rtk-telemetry-parity-port-");

        try
        {
            var (configGuard, dataGuard, skip) = GuardBothRealDirs(entry.Label, oracleTemp, portTemp);
            if (skip is not null)
            {
                return skip;
            }

            try
            {
                // The oracle-side guard junctions the REAL "%APPDATA%\rtk"/"%LOCALAPPDATA%\rtk"
                // directories themselves (see HostRealDirs — each real path already ends in "rtk")
                // to these targets, so the oracle's own internal `.join("rtk")` lands here directly.
                var oracleConfigDir = OperatingSystem.IsWindows()
                    ? Path.Combine(oracleTemp, ".config-rtk", "rtk")
                    : Path.Combine(oracleTemp, "oracle-config", "rtk");
                var oracleDataDir = OperatingSystem.IsWindows()
                    ? Path.Combine(oracleTemp, ".data-rtk", "rtk")
                    : Path.Combine(oracleTemp, "oracle-data", "rtk");

                // RTK_CONFIG_DIR_OVERRIDE/RTK_DATA_DIR_OVERRIDE are the BASE directory (no "rtk"
                // suffix) — TelemetryCommand/Config resolve them via TrustCommand.ResolveDataDir()/
                // InitArtifacts.ResolveGlobalConfigDir(), which return the override verbatim, and
                // every actual file path is built by appending "rtk" on top (matching the established
                // DataDirGuard/GlobalScopeGuard convention — NOT ConfigFilterParityTests' own override
                // values, which happen to already end in a literal "rtk" path segment for unrelated,
                // cosmetic reasons and are harmless there only because nothing reads that segment
                // name back).
                var portConfigBase = Path.Combine(portTemp, "port-config");
                var portDataBase = Path.Combine(portTemp, "port-data");
                var portConfigDir = Path.Combine(portConfigBase, "rtk");
                var portDataDir = Path.Combine(portDataBase, "rtk");

                SeedEntry(oracleConfigDir, oracleDataDir, entry);
                SeedEntry(portConfigDir, portDataDir, entry);

                var oracleEnv = new Dictionary<string, string?>();
                var portEnv = new Dictionary<string, string?>
                {
                    ["RTK_CONFIG_DIR_OVERRIDE"] = portConfigBase,
                    ["RTK_DATA_DIR_OVERRIDE"] = portDataBase,
                };
                if (!OperatingSystem.IsWindows())
                {
                    // Non-Windows CI has no junction guard (see GuardBothRealDirs); redirect the
                    // oracle's own HOME so dirs::config_dir()/data_local_dir() resolve under it.
                    oracleEnv["HOME"] = oracleTemp;
                }

                foreach (var (key, value) in entry.ExtraEnv ?? [])
                {
                    oracleEnv[key] = value;
                    portEnv[key] = value;
                }

                var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                    oraclePath, ["telemetry", .. entry.Args], oracleTemp, oracleEnv, stdin: "");

                var portArgs = portPrefixArgs.Concat(["telemetry"]).Concat(entry.Args).ToArray();
                var (portStdout, portExit) = await ParityRunner.RunAsync(
                    portFileName, portArgs, portTemp, portEnv, stdin: "");

                var normalizedOracle = Normalize(oracleStdout)
                    .Replace(oracleDataDir, "<DATA_DIR>")
                    .Replace(oracleConfigDir, "<CONFIG_DIR>");
                var normalizedPort = Normalize(portStdout)
                    .Replace(portDataDir, "<DATA_DIR>")
                    .Replace(portConfigDir, "<CONFIG_DIR>");

                return new Result(entry.Label, normalizedOracle, normalizedPort, oracleExit, portExit);
            }
            finally
            {
                configGuard?.Dispose();
                dataGuard?.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    private static void SeedEntry(string configDir, string dataDir, Entry entry)
    {
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(dataDir);

        if (entry.ConfigTomlSeed is not null)
        {
            File.WriteAllText(Path.Combine(configDir, "config.toml"), entry.ConfigTomlSeed);
        }

        if (entry.SaltSeed is not null)
        {
            File.WriteAllText(Path.Combine(dataDir, ".device_salt"), entry.SaltSeed);
        }

        if (entry.SeedMarker)
        {
            File.WriteAllText(Path.Combine(dataDir, ".telemetry_last_ping"), "marker");
        }

        if (entry.SeedDb)
        {
            File.WriteAllText(Path.Combine(dataDir, "history.db"), "fake-db-content");
        }
    }

    // ===================== unified dual-real-directory guard (Windows only) =====================

    /// <summary>
    /// Creates junction-redirect guards for both <c>%APPDATA%\rtk</c> and <c>%LOCALAPPDATA%\rtk</c> on
    /// Windows, unconditionally for every entry (see this file's class remarks). On non-Windows this
    /// is a no-op returning <c>(null, null, null)</c> — <see cref="RunEntryAsync"/> instead redirects
    /// the oracle's own <c>HOME</c> env var, which Rust's <c>dirs</c> crate honors on Unix.
    /// </summary>
    private static (HostRealDirGuard? Config, HostRealDirGuard? Data, Result? Skip) GuardBothRealDirs(
        string label, string oracleTemp, string portTemp)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, null, null);
        }

        var configGuard = new HostRealDirGuard(
            HostRealDirs.ConfigRealDir, HostRealDirs.ConfigMarkerPath,
            Path.Combine(oracleTemp, ".config-rtk", "rtk"), ParityOracleTempPrefix);
        if (configGuard.Mode == HostRealDirGuard.RedirectMode.RefuseUnsafe)
        {
            configGuard.Dispose();
            return (null, null, Result.SkippedForHostSafety(
                label, "%APPDATA%\\rtk already has real content on this host; refusing to risk " +
                "touching the real config.toml's [telemetry] section."));
        }

        var dataGuard = new HostRealDirGuard(
            HostRealDirs.DataRealDir, HostRealDirs.DataMarkerPath,
            Path.Combine(oracleTemp, ".data-rtk", "rtk"), ParityOracleTempPrefix);
        if (dataGuard.Mode == HostRealDirGuard.RedirectMode.RefuseUnsafe)
        {
            dataGuard.Dispose();
            configGuard.Dispose();
            return (null, null, Result.SkippedForHostSafety(
                label, "%LOCALAPPDATA%\\rtk already has real content on this host (device salt/ping " +
                "marker/tracking database); refusing to risk touching it."));
        }

        return (configGuard, dataGuard, null);
    }

    private static class HostRealDirs
    {
        public static readonly string ConfigRealDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rtk");

        public static readonly string DataRealDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtk");

        public static readonly string ConfigMarkerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".rtk-telemetry-parity-redirect-marker");

        public static readonly string DataMarkerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ".rtk-telemetry-parity-redirect-marker");

        static HostRealDirs()
        {
            HostRealDirGuard.SelfHeal(ConfigRealDir, ConfigMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
            HostRealDirGuard.SelfHeal(DataRealDir, DataMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                HostRealDirGuard.SelfHeal(ConfigRealDir, ConfigMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
                HostRealDirGuard.SelfHeal(DataRealDir, DataMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, _) =>
            {
                HostRealDirGuard.SelfHeal(ConfigRealDir, ConfigMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
                HostRealDirGuard.SelfHeal(DataRealDir, DataMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
            };
        }
    }

    /// <summary>
    /// Redirects a real, non-env-redirectable-on-Windows per-machine directory into a throwaway
    /// per-entry temp sandbox via an NTFS directory junction, so the Rust oracle's reads/writes during
    /// a parity-test entry can never reach real host state. Adapted from
    /// <see cref="ConfigFilterParityTests"/>'s own <c>HostRealDirGuard</c> (duplicated rather than
    /// shared, matching this project's established per-file convention for this safety-critical
    /// machinery — see <see cref="InitParityTests.WindowsGlobalFiltersGuard"/> for the third
    /// independent instance of the same pattern). Any pre-existing real content at the target always
    /// resolves to <see cref="RedirectMode.RefuseUnsafe"/> — this guard never deletes/replaces real
    /// content to make room for a redirect.
    /// </summary>
    private sealed class HostRealDirGuard : IDisposable
    {
        public enum RedirectMode
        {
            Redirected,
            RefuseUnsafe,
        }

        private readonly string _realDir;
        private readonly string _markerPath;
        private readonly string _tempPrefixToken;

        public RedirectMode Mode { get; }

        public HostRealDirGuard(string realDir, string markerPath, string redirectTargetDir, string tempPrefixToken)
        {
            _realDir = realDir;
            _markerPath = markerPath;
            _tempPrefixToken = tempPrefixToken;

            if (Directory.Exists(realDir) || File.Exists(realDir))
            {
                Mode = RedirectMode.RefuseUnsafe;
                return;
            }

            Directory.CreateDirectory(redirectTargetDir);
            File.WriteAllText(markerPath, redirectTargetDir);
            try
            {
                CreateJunction(realDir, redirectTargetDir);
            }
            catch
            {
                TryDeleteMarker(markerPath);
                throw;
            }

            Mode = RedirectMode.Redirected;
        }

        public void Dispose()
        {
            if (Mode != RedirectMode.Redirected)
            {
                return;
            }

            SelfHeal(_realDir, _markerPath, _tempPrefixToken, throwOnFailure: true);
        }

        private static bool IsReparsePoint(string path) =>
            Directory.Exists(path) && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;

        private static void CreateJunction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start mklink process for junction creation.");
            proc.WaitForExit();

            if (proc.ExitCode != 0 || !IsReparsePoint(link))
            {
                var stderr = proc.StandardError.ReadToEnd();
                throw new InvalidOperationException(
                    $"Failed to create the HostRealDirGuard junction '{link}' -> '{target}' " +
                    $"(exit {proc.ExitCode}): {stderr}");
            }
        }

        public static void SelfHeal(string realDir, string markerPath, string tempPrefixToken, bool throwOnFailure)
        {
            if (!IsReparsePoint(realDir))
            {
                TryDeleteMarker(markerPath);
                return;
            }

            if (!TryReadValidMarker(markerPath, tempPrefixToken))
            {
                return;
            }

            var psi = new ProcessStartInfo("cmd.exe", $"/c rmdir \"{realDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start rmdir process for junction cleanup.");
            proc.WaitForExit();

            if (proc.ExitCode != 0 || Directory.Exists(realDir))
            {
                var stderr = proc.StandardError.ReadToEnd();
                var message =
                    $"CRITICAL: failed to remove the HostRealDirGuard junction at '{realDir}' " +
                    $"(exit {proc.ExitCode}): {stderr}. This path is an NTFS junction the test harness " +
                    "created and never contains real host data, but it must be removed manually " +
                    $"(run `rmdir \"{realDir}\"` from a shell) before the next parity run on this machine.";
                if (throwOnFailure)
                {
                    throw new InvalidOperationException(message);
                }

                Console.Error.WriteLine(message);
                return;
            }

            TryDeleteMarker(markerPath);
        }

        private static void TryDeleteMarker(string markerPath)
        {
            try
            {
                if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static bool TryReadValidMarker(string markerPath, string tempPrefixToken)
        {
            if (!File.Exists(markerPath))
            {
                return false;
            }

            try
            {
                var content = File.ReadAllText(markerPath).Trim();
                if (string.IsNullOrEmpty(content) || !Path.IsPathRooted(content))
                {
                    return false;
                }

                var tempRoot = Path.GetFullPath(Path.GetTempPath());
                var full = Path.GetFullPath(content);
                if (!full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return segments.Any(s => s.StartsWith(tempPrefixToken, StringComparison.Ordinal));
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    // ===================== shared helpers =====================

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static (string? FileName, string[] PrefixArgs) LocateRtkSharp(string repoRoot)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, ExeName("RtkSharp"));
        if (File.Exists(beside))
        {
            return (beside, []);
        }

        var candidates = new[]
        {
            Path.Combine(repoRoot, ".artifacts", "publish", ExeName("RtkSharp")),
            Path.Combine(repoRoot, "RtkSharp", "bin", "Release", "net10.0", ExeName("RtkSharp")),
            Path.Combine(repoRoot, "RtkSharp", "bin", "Debug", "net10.0", ExeName("RtkSharp")),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return (c, []);
            }
        }

        var dll = Path.Combine(AppContext.BaseDirectory, "RtkSharp.dll");
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "RtkSharp.runtimeconfig.json");
        if (File.Exists(dll) && File.Exists(runtimeConfig))
        {
            return ("dotnet", [dll]);
        }

        return (null, []);
    }

    private static string ExeName(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate repo root (no Cargo.toml found in any parent directory).");
    }

    private static async Task WriteReportAsync(
        string reportPath,
        IReadOnlyList<Result> results,
        double percent,
        int matched,
        int total,
        string oraclePath,
        string portFileName,
        string[] portPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# `rtk telemetry` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/TelemetryParityTests.cs`. " +
                      "Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (status x3, enable non-interactive, " +
                      "disable x2, forget x2, usage-error x3)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: on Windows, BOTH real per-machine directories " +
                      "(`%APPDATA%\\rtk`, `%LOCALAPPDATA%\\rtk`) are junction-redirected into a per-entry " +
                      "temp sandbox for the ORACLE side before it runs (refusing — and marking the entry " +
                      "SKIPPED, a vacuous pass — if either already has real content), while the PORT side " +
                      "uses its own `RTK_CONFIG_DIR_OVERRIDE`/`RTK_DATA_DIR_OVERRIDE` test escape hatches. " +
                      "On non-Windows the oracle's `HOME` env var is redirected instead. Stdout (CRLF/LF " +
                      "normalized, trailing whitespace trimmed, temp dir paths substituted with " +
                      "placeholders) and exit code are compared; stderr is not.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|--------|--------|");
        sb.AppendLine($"| **Overall parity (headline)** | **{percent:F1}%** |");
        sb.AppendLine($"| Threshold | {ParityThresholdPercent:F0}% |");
        sb.AppendLine($"| Entries matched (incl. host-safety skips) | {matched} / {total} |");
        sb.AppendLine($"| Host-safety skips | {results.Count(r => r.Skipped)} |");
        sb.AppendLine();
        sb.AppendLine("## Per-entry results");
        sb.AppendLine();
        sb.AppendLine("| Entry | Rust exit | .NET exit | Stdout match | Verdict |");
        sb.AppendLine("|-------|-----------|-----------|--------------|---------|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| `{r.Label}` | {r.RustExit} | {r.PortExit} | {(r.Skipped ? "n/a" : r.StdoutMatches ? "yes" : "no")} | {r.Verdict} |");
        }

        sb.AppendLine();
        var mismatches = results.Where(r => !r.IsMatch).ToList();
        sb.AppendLine("## Mismatch details");
        sb.AppendLine();
        if (mismatches.Count == 0)
        {
            sb.AppendLine("None — every non-skipped battery entry matched byte-exact stdout and an identical exit code.");
        }
        else
        {
            foreach (var r in mismatches)
            {
                sb.AppendLine($"### `{r.Label}`");
                sb.AppendLine();
                sb.AppendLine($"- Verdict: **{r.Verdict}**");
                sb.AppendLine($"- Rust exit: `{r.RustExit}`, .NET exit: `{r.PortExit}`");
                sb.AppendLine($"- Rust stdout: {Md(r.RustStdout)}");
                sb.AppendLine($"- .NET stdout: {Md(r.PortStdout)}");
                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(reportPath, sb.ToString());
    }

    private static string Md(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "*(empty)*";
        }

        return "`" + s.Replace("\n", " ").Replace("|", "\\|") + "`";
    }

    /// <summary>The parity outcome for a single battery entry.</summary>
    private sealed record Result(
        string Label, string RustStdout, string PortStdout, int RustExit, int PortExit,
        bool Skipped = false, string? SkipReason = null)
    {
        public static Result SkippedForHostSafety(string label, string reason) =>
            new(label, "", "", 0, 0, Skipped: true, SkipReason: reason);

        public bool StdoutMatches => Skipped || RustStdout == PortStdout;

        public bool ExitsMatch => Skipped || RustExit == PortExit;

        public bool IsMatch => Skipped || (StdoutMatches && ExitsMatch);

        public string Verdict => Skipped
            ? $"SKIPPED (host safety: {SkipReason})"
            : IsMatch
                ? "MATCH"
                : !StdoutMatches
                    ? "MISMATCH (stdout)"
                    : "MISMATCH (exit)";
    }
}
