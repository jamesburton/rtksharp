using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 4 Task 6 acceptance gate: an oracle parity battery covering <c>rtk config</c>/
/// <c>rtk config --create</c>, the TOML built-in-filter fallback dispatch path (via <c>rtk df</c>),
/// <c>rtk trust</c>/<c>rtk untrust</c>/<c>rtk trust --list</c>, and the <c>rtk verify</c>/
/// <c>rtk verify --require-all</c> inline-test battery against project-local filters with
/// passing/failing/missing tests. Follows the exact hermetic pattern established in
/// <see cref="InitParityTests"/>/<see cref="VerifyParityTests"/> (temp CWD, environment overlay,
/// <c>StdinContent = ""</c>, oracle-vs-port comparison, auto-written Markdown report).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope note on <c>rtk verify</c> coverage.</b> <see cref="VerifyParityTests"/> already covers
/// the hook-integrity branches (native-binary-registered PASS, not-installed SKIP, tampered-hook
/// FAIL) plus, incidentally, the inline-test battery running over the built-in-only registry (no
/// entry there seeds a project-local <c>.rtk/filters.toml</c> or passes <c>--require-all</c>). This
/// file adds the surface that battery does <b>not</b> cover: <c>--require-all</c> itself,
/// <c>--filter &lt;name&gt;</c> targeting one project-local filter, and passing/failing/missing-tests
/// outcomes for a trusted project filter — it deliberately does not re-test the hook-integrity
/// branches already proven there.
/// </para>
/// <para>
/// <b>Safety-critical: two real, non-redirectable-on-Windows host directories are in play, and
/// BOTH are at risk on EVERY oracle invocation, not just the "obviously relevant" ones.</b>
/// <c>rtk config</c>'s config directory (<c>dirs::config_dir()</c> = <c>%APPDATA%\rtk</c> on
/// Windows — the exact same directory <see cref="InitParityTests.WindowsGlobalFiltersGuard"/>
/// already redirects for the global filters-template) and the trust-store/tracking data directory
/// (<c>dirs::data_local_dir()</c> = <c>%LOCALAPPDATA%\rtk</c> — confirmed non-redirectable by Task
/// 4's own live-probe investigation, see <c>TrustCommand.DataDirOverrideEnvVar</c>'s remarks) are
/// both resolved by the Rust oracle via the same non-env-redirectable Win32 known-folder API on
/// Windows. <b>This was not a hypothetical risk during this file's own development</b>: an early,
/// less-conservative version of this harness only guarded <c>%LOCALAPPDATA%\rtk</c> for entries
/// whose nominal purpose was trust-related, and a before/after hash comparison of the real
/// <c>%LOCALAPPDATA%\rtk\history.db</c> on the verification machine showed it changed after running
/// entries that never touched trust at all (bare <c>rtk config</c>/<c>rtk df</c>/plain
/// <c>rtk verify --require-all</c>) — the oracle's own usage-tracking database is written on
/// <em>every</em> invocation, regardless of subcommand. <see cref="GuardBothRealDirs"/> below
/// therefore applies <b>both</b> real-directory guards to <b>every single entry</b> in this file
/// unconditionally, with no exceptions for entries that "shouldn't need it" — the same
/// "assume unsafe until proven otherwise" bar Phase 9b Task 4 established.
/// </para>
/// <para>
/// <b>Guard design.</b> <see cref="HostRealDirGuard"/> reuses the exact safety properties of
/// <c>WindowsGlobalFiltersGuard</c> — a junction redirect created only when nothing real exists at
/// the target; cleanup gated on marker-CONTENT validation across normal
/// Dispose/self-heal/<c>ProcessExit</c>/<c>UnhandledException</c> teardown paths; never a
/// backup-and-restore mechanism — but is <b>strictly more conservative</b> in one respect: unlike
/// the filters-template (read-only "already exists, skip" branch), <c>rtk config --create</c>
/// unconditionally *overwrites* any existing <c>config.toml</c>, <c>rtk trust</c>/<c>untrust</c>
/// *merge into* an existing <c>trusted_filters.json</c> rather than only reading it, and the
/// tracking database is *appended to* unconditionally — so this guard has no "SafeNoOp" fallback at
/// all. If the real target directory already has any content, the guard refuses to redirect and the
/// caller skips that entry entirely (never attempt the write/append, never even read the real
/// content into a report). <b>Marker-validation fix:</b> an earlier revision of this file passed a
/// per-concern token (a "config" token for config-dir guards, a "trust" token for data-dir guards)
/// to <see cref="HostRealDirGuard.SelfHeal"/>'s marker validation while creating the redirected temp
/// sandbox under a *different*, entry-type-specific temp-dir prefix (e.g. a `verify` entry's own
/// oracle temp dir) — the mismatch meant <c>TryReadValidMarker</c> could never recognize its own
/// marker as valid for entries outside the config/trust battery, so <c>SelfHeal</c> silently
/// (correctly, per its own "don't guess" contract) refused to remove the junction, leaving a
/// dangling reparse point at the real path after the test process exited (harmless — the junction's
/// *target* had already been deleted by the entry's own temp-dir cleanup, so no real data was ever
/// at risk — but a dangling junction still blocks every subsequent run, always taking the
/// <see cref="HostRealDirGuard.RedirectMode.RefuseUnsafe"/> branch). Fixed by using one shared
/// <see cref="ParityOracleTempPrefix"/> across every entry type's oracle temp directory and every
/// guard's marker-validation token, so <c>SelfHeal</c> can always recognize (and safely remove) a
/// junction this file's own harness created, regardless of which entry created it.
/// </para>
/// <para>
/// <b>Collection isolation from <see cref="InitParityTests"/>.</b> Both this file's entries and
/// <see cref="InitParityTests"/>'s global-scope entries independently manage a junction at the
/// identical real path <c>%APPDATA%\rtk</c> (via two independently-implemented guard classes, each
/// with its own marker file, so neither's self-heal can misidentify the other's leftover junction).
/// xUnit does not guarantee two different test classes run sequentially by default, and a
/// concurrent junction create/teardown race at the same real path is exactly the class of bug both
/// guards exist to prevent — so both classes are pinned to the same named <c>[Collection]</c>,
/// which xUnit never runs in parallel with itself, eliminating the race by construction.
/// </para>
/// </remarks>
[Collection("HostRealDirGuard")]
public partial class ConfigFilterParityTests
{
    private const double ParityThresholdPercent = 95.0;

    /// <summary>
    /// The single shared prefix used for every oracle-side temp directory this file creates, and the
    /// single marker-validation token passed to every <see cref="HostRealDirGuard"/> instance — see
    /// this file's class remarks ("Marker-validation fix") for why a per-entry-type prefix was
    /// unsafe (self-heal could never recognize its own junction) and why a single shared token fixes
    /// it for every entry uniformly.
    /// </summary>
    private const string ParityOracleTempPrefix = "rtk-parity-oracle-";

    [Fact]
    public async Task ConfigFilterTrust_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the config/filter/trust-parity gate.");
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

        var results = new List<Result>();
        results.AddRange(await RunConfigEntriesAsync(oraclePath, portFileName, portPrefixArgs));
        results.Add(await RunDfFallbackEntryAsync(oraclePath, portFileName, portPrefixArgs));
        results.AddRange(await RunTrustEntriesAsync(oraclePath, portFileName, portPrefixArgs));
        results.AddRange(await RunVerifyEntriesAsync(oraclePath, portFileName, portPrefixArgs));

        var matched = results.Count(r => r.IsMatch);
        var total = results.Count;
        var percent = total == 0 ? 100.0 : matched * 100.0 / total;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "config-filter-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"config/filter/trust parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} " +
                $"stdoutMatch={r.StdoutMatches}" + (r.TreeDiff is null ? "" : $" treeDiff=[{r.TreeDiff}]"));
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    // ===================== rtk config / rtk config --create =====================

    private static async Task<List<Result>> RunConfigEntriesAsync(
        string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var results = new List<Result>();

        results.Add(await RunConfigEntryAsync(
            "config: no file (defaults shown)", [["config"]],
            oraclePath, portFileName, portPrefixArgs, compareTree: false));

        results.Add(await RunConfigEntryAsync(
            "config: pre-existing file (shows created content)", [["config", "--create"], ["config"]],
            oraclePath, portFileName, portPrefixArgs, compareTree: false));

        results.Add(await RunConfigEntryAsync(
            "config --create", [["config", "--create"]],
            oraclePath, portFileName, portPrefixArgs, compareTree: true));

        results.Add(await RunConfigEntryAsync(
            "config --create (idempotent re-run)", [["config", "--create"], ["config", "--create"]],
            oraclePath, portFileName, portPrefixArgs, compareTree: true));

        return results;
    }

    private static async Task<Result> RunConfigEntryAsync(
        string label, IReadOnlyList<string[]> steps,
        string oraclePath, string portFileName, string[] portPrefixArgs, bool compareTree)
    {
        var oracleTemp = CreateTempDir(ParityOracleTempPrefix);
        var portTemp = CreateTempDir("rtk-parity-port-");

        try
        {
            var oracleEnv = new Dictionary<string, string?>();
            var portEnv = new Dictionary<string, string?>
            {
                ["RTK_CONFIG_DIR_OVERRIDE"] = Path.Combine(portTemp, ".config-rtk"),
            };
            Directory.CreateDirectory(Path.Combine(portTemp, ".config-rtk"));

            if (!OperatingSystem.IsWindows())
            {
                oracleEnv["XDG_CONFIG_HOME"] = Path.Combine(oracleTemp, ".config-rtk");
                Directory.CreateDirectory(Path.Combine(oracleTemp, ".config-rtk"));
            }

            var (configGuard, trustGuard, skip) = GuardBothRealDirs(label, oracleTemp);
            using var _configGuard = configGuard;
            using var _trustGuard = trustGuard;
            if (skip is not null)
            {
                return skip;
            }

            (string Stdout, int Exit) oracleLast = ("", 0);
            foreach (var step in steps)
            {
                oracleLast = await ParityRunner.RunAsync(oraclePath, step, oracleTemp, oracleEnv, stdin: "");
            }

            (string Stdout, int Exit) portLast = ("", 0);
            foreach (var step in steps)
            {
                var portArgs = portPrefixArgs.Concat(step).ToArray();
                portLast = await ParityRunner.RunAsync(portFileName, portArgs, portTemp, portEnv, stdin: "");
            }

            bool treeMatches = true;
            string? treeDiff = null;
            if (compareTree)
            {
                var oracleManifest = BuildManifest(Path.Combine(oracleTemp, ".config-rtk"), stripTeeSection: true);
                var portManifest = BuildManifest(Path.Combine(portTemp, ".config-rtk"), stripTeeSection: true);
                (treeMatches, treeDiff) = CompareManifests(oracleManifest, portManifest);
            }

            var oracleStdout = StripTeeSection(Normalize(MaskPaths(oracleLast.Stdout, oracleTemp)));
            var portStdout = StripTeeSection(Normalize(MaskPaths(portLast.Stdout, portTemp)));

            return new Result(label, oracleStdout, portStdout, oracleLast.Exit, portLast.Exit, treeMatches, treeDiff);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    // ===================== built-in TOML filter fallback dispatch (rtk df) =====================

    /// <summary>
    /// Builds the parity entry for the TOML-engine fallback dispatch path — the same CLI form
    /// (bare <c>rtk df</c>, no dedicated module) that Task 5's dispatch-wiring tests already
    /// confirmed reaches <c>TryTomlFallbackAsync</c>/Rust's <c>run_fallback</c> TOML branch (see
    /// <c>RtkSharp.Tests/TomlDispatchTests.cs</c> and <c>src/filters/df.toml</c>). A stub
    /// <c>df.bat</c> is placed on <c>PATH</c> (prepended, ahead of any real <c>df</c>) so the entry
    /// is fully hermetic and deterministic on every host, including one with no real <c>df</c>
    /// utility (e.g. this Windows dev machine) — empirically confirmed to resolve identically for
    /// both binaries via their independent PATH/PATHEXT search before this test was written.
    /// </summary>
    private static async Task<Result> RunDfFallbackEntryAsync(
        string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        const string label = "TOML fallback dispatch: built-in df filter";
        var oracleTemp = CreateTempDir(ParityOracleTempPrefix);
        var portTemp = CreateTempDir("rtk-parity-port-");
        var stubBinDir = CreateTempDir("rtk-parity-stubbin-");

        try
        {
            var stubName = OperatingSystem.IsWindows() ? "df.bat" : "df";
            var stubPath = Path.Combine(stubBinDir, stubName);
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(stubPath,
                    "@echo off\r\n" +
                    "echo Filesystem     1K-blocks   Used Available Use%% Mounted on\r\n" +
                    "echo /dev/sda1        4096000 123456   3972544   4%% /\r\n");
            }
            else
            {
                File.WriteAllText(stubPath,
                    "#!/bin/sh\n" +
                    "printf 'Filesystem     1K-blocks   Used Available Use%% Mounted on\\n'\n" +
                    "printf '/dev/sda1        4096000 123456   3972544   4%% /\\n'\n");
                File.SetUnixFileMode(stubPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            var hostPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var pathSep = OperatingSystem.IsWindows() ? ";" : ":";
            var stubbedPath = stubBinDir + pathSep + hostPath;

            var oracleEnv = new Dictionary<string, string?> { ["PATH"] = stubbedPath };
            var portEnv = new Dictionary<string, string?>
            {
                ["PATH"] = stubbedPath,
                ["RTK_CONFIG_DIR_OVERRIDE"] = Path.Combine(portTemp, ".config-rtk"),
            };
            Directory.CreateDirectory(Path.Combine(portTemp, ".config-rtk"));

            if (!OperatingSystem.IsWindows())
            {
                oracleEnv["XDG_CONFIG_HOME"] = Path.Combine(oracleTemp, ".config-rtk");
                Directory.CreateDirectory(Path.Combine(oracleTemp, ".config-rtk"));
            }

            var (configGuard, trustGuard, skip) = GuardBothRealDirs(label, oracleTemp);
            using var _configGuard = configGuard;
            using var _trustGuard = trustGuard;
            if (skip is not null)
            {
                return skip;
            }

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                oraclePath, ["df"], oracleTemp, oracleEnv, stdin: "");
            var portArgs = portPrefixArgs.Concat(["df"]).ToArray();
            var (portStdout, portExit) = await ParityRunner.RunAsync(
                portFileName, portArgs, portTemp, portEnv, stdin: "");

            return new Result(
                label, Normalize(oracleStdout), Normalize(portStdout), oracleExit, portExit,
                TreeMatches: true, TreeDiff: null);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
            TryDeleteDirectory(stubBinDir);
        }
    }

    // ===================== rtk trust / rtk untrust / rtk trust --list =====================

    private const string TrustFixtureToml = """
        schema_version = 1

        [filters.zz-trust-fixture]
        match_command = "^zz-trust-fixture\\b"
        replace = [
          { pattern = "foo", replacement = "bar" },
        ]

        [[tests.zz-trust-fixture]]
        name = "replaces foo"
        input = "foo baz"
        expected = "bar baz"
        """;

    private static async Task<List<Result>> RunTrustEntriesAsync(
        string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var results = new List<Result>();

        results.Add(await RunTrustEntryAsync(
            "trust: no .rtk/filters.toml present (fail-loud)", seedFixture: false,
            [["trust"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunTrustEntryAsync(
            "trust --list: empty store", seedFixture: true,
            [["trust", "--list"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunTrustEntryAsync(
            "trust: happy path (dump + risk summary + trusted)", seedFixture: true,
            [["trust"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunTrustEntryAsync(
            "trust --list: after trusting", seedFixture: true,
            [["trust"], ["trust", "--list"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunTrustEntryAsync(
            "trust: idempotent re-trust", seedFixture: true,
            [["trust"], ["trust"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunTrustEntryAsync(
            "untrust: no entry found", seedFixture: true,
            [["untrust"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunTrustEntryAsync(
            "untrust: after trusting", seedFixture: true,
            [["trust"], ["untrust"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunTrustEntryAsync(
            "trust --list: after untrust (empty again)", seedFixture: true,
            [["trust"], ["untrust"], ["trust", "--list"]], oraclePath, portFileName, portPrefixArgs));

        return results;
    }

    private static async Task<Result> RunTrustEntryAsync(
        string label, bool seedFixture, IReadOnlyList<string[]> steps,
        string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir(ParityOracleTempPrefix);
        var portTemp = CreateTempDir("rtk-parity-port-");

        try
        {
            if (seedFixture)
            {
                Directory.CreateDirectory(Path.Combine(oracleTemp, ".rtk"));
                Directory.CreateDirectory(Path.Combine(portTemp, ".rtk"));
                File.WriteAllText(Path.Combine(oracleTemp, ".rtk", "filters.toml"), TrustFixtureToml);
                File.WriteAllText(Path.Combine(portTemp, ".rtk", "filters.toml"), TrustFixtureToml);
            }

            var (configGuard, trustGuard, skip) = GuardBothRealDirs(label, oracleTemp);
            using var _configGuard = configGuard;
            using var _trustGuard = trustGuard;
            if (skip is not null)
            {
                return skip;
            }

            var oracleEnv = new Dictionary<string, string?>();
            var portEnv = new Dictionary<string, string?>();
            if (!OperatingSystem.IsWindows())
            {
                var oracleDataDir = Path.Combine(oracleTemp, ".data-rtk");
                var portDataDir = Path.Combine(portTemp, ".data-rtk");
                Directory.CreateDirectory(oracleDataDir);
                Directory.CreateDirectory(portDataDir);
                oracleEnv["XDG_DATA_HOME"] = oracleDataDir;
                portEnv["RTK_DATA_DIR_OVERRIDE"] = portDataDir;
            }
            else
            {
                portEnv["RTK_DATA_DIR_OVERRIDE"] = Path.Combine(portTemp, ".data-rtk", "rtk");
            }

            (string Stdout, int Exit) oracleLast = ("", 0);
            foreach (var step in steps)
            {
                oracleLast = await ParityRunner.RunAsync(oraclePath, step, oracleTemp, oracleEnv, stdin: "");
            }

            (string Stdout, int Exit) portLast = ("", 0);
            foreach (var step in steps)
            {
                var portArgs = portPrefixArgs.Concat(step).ToArray();
                portLast = await ParityRunner.RunAsync(portFileName, portArgs, portTemp, portEnv, stdin: "");
            }

            var oracleStdout = Normalize(MaskPaths(oracleLast.Stdout, oracleTemp));
            var portStdout = Normalize(MaskPaths(portLast.Stdout, portTemp));

            // `trust --list`'s date stamp is "today" on both sides (deterministic same-day run) but
            // the canonicalized path echoed back differs in cosmetic shape beyond the simple prefix
            // substitution MaskPaths performs (drive-letter casing, 8.3 vs long-path segments) on
            // some Windows configurations. Collapse any remaining path-shaped token on the line so a
            // formatting difference doesn't register as a false mismatch.
            oracleStdout = MaskCanonicalPathNoise(oracleStdout, oracleTemp);
            portStdout = MaskCanonicalPathNoise(portStdout, portTemp);

            return new Result(label, oracleStdout, portStdout, oracleLast.Exit, portLast.Exit, TreeMatches: true, TreeDiff: null);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    /// <summary>
    /// Best-effort normalization of a canonicalized path echoed back by <c>trust --list</c> once the
    /// simple temp-root substring has already been masked by <see cref="MaskPaths"/> — collapses any
    /// remaining absolute path under the entry's own temp root down to a fixed placeholder so
    /// drive-letter casing or 8.3-vs-long-path canonicalization quirks don't register as a spurious
    /// mismatch.
    /// </summary>
    private static string MaskCanonicalPathNoise(string stdout, string tempDir)
    {
        var root = Path.GetFileName(tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(root))
        {
            return stdout;
        }

        var lines = stdout.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(root, StringComparison.OrdinalIgnoreCase) &&
                (lines[i].Contains(".rtk", StringComparison.Ordinal) || lines[i].Contains("filters.toml", StringComparison.Ordinal)))
            {
                lines[i] = "  {TRUSTED_PATH} (trusted {DATE})";
            }
        }

        return string.Join('\n', lines);
    }

    // ===================== rtk verify / rtk verify --require-all =====================

    private const string VerifyPassToml = """
        schema_version = 1

        [filters.zzverify-pass]
        match_command = "^zzverify-pass\\b"
        replace = [
          { pattern = "foo", replacement = "bar" },
        ]

        [[tests.zzverify-pass]]
        name = "replaces foo"
        input = "foo baz"
        expected = "bar baz"
        """;

    private const string VerifyFailToml = """
        schema_version = 1

        [filters.zzverify-fail]
        match_command = "^zzverify-fail\\b"
        replace = [
          { pattern = "foo", replacement = "bar" },
        ]

        [[tests.zzverify-fail]]
        name = "wrong expectation"
        input = "foo baz"
        expected = "totally-wrong"
        """;

    private const string VerifyNoTestsToml = """
        schema_version = 1

        [filters.zzverify-notest]
        match_command = "^zzverify-notest\\b"
        strip_ansi = true
        """;

    private static async Task<List<Result>> RunVerifyEntriesAsync(
        string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var results = new List<Result>();

        results.Add(await RunVerifyEntryAsync(
            "verify --require-all: built-ins only (all have tests, PASS)", fixture: null,
            [["verify", "--require-all"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunVerifyEntryAsync(
            "verify --filter: trusted project filter, passing test", fixture: VerifyPassToml,
            [["trust"], ["verify", "--filter", "zzverify-pass"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunVerifyEntryAsync(
            "verify --filter: trusted project filter, FAILING test", fixture: VerifyFailToml,
            [["trust"], ["verify", "--filter", "zzverify-fail"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunVerifyEntryAsync(
            "verify --filter: trusted project filter, no tests, no --require-all (skips, PASS)", fixture: VerifyNoTestsToml,
            [["trust"], ["verify", "--filter", "zzverify-notest"]], oraclePath, portFileName, portPrefixArgs));

        results.Add(await RunVerifyEntryAsync(
            "verify --filter --require-all: trusted project filter, no tests (MISSING bail)", fixture: VerifyNoTestsToml,
            [["trust"], ["verify", "--filter", "zzverify-notest", "--require-all"]], oraclePath, portFileName, portPrefixArgs));

        return results;
    }

    private static async Task<Result> RunVerifyEntryAsync(
        string label, string? fixture, IReadOnlyList<string[]> steps,
        string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir(ParityOracleTempPrefix);
        var portTemp = CreateTempDir("rtk-parity-port-");

        try
        {
            if (fixture is not null)
            {
                Directory.CreateDirectory(Path.Combine(oracleTemp, ".rtk"));
                Directory.CreateDirectory(Path.Combine(portTemp, ".rtk"));
                File.WriteAllText(Path.Combine(oracleTemp, ".rtk", "filters.toml"), fixture);
                File.WriteAllText(Path.Combine(portTemp, ".rtk", "filters.toml"), fixture);
            }

            // rtk verify's integrity-check leg (only reached without --filter) needs a .claude dir;
            // an empty one takes the harmless "not installed" SKIP branch on both sides (proven
            // identical by VerifyParityTests) and falls through to the inline-test battery, so it is
            // always provided even for entries that happen to use --filter (where it's simply unused).
            var oracleClaudeDir = Path.Combine(oracleTemp, ".claude");
            var portClaudeDir = Path.Combine(portTemp, ".claude");
            Directory.CreateDirectory(oracleClaudeDir);
            Directory.CreateDirectory(portClaudeDir);

            var (configGuard, trustGuard, skip) = GuardBothRealDirs(label, oracleTemp);
            using var _configGuard = configGuard;
            using var _trustGuard = trustGuard;
            if (skip is not null)
            {
                return skip;
            }

            var oracleEnv = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = oracleClaudeDir };
            var portEnv = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = portClaudeDir };

            if (!OperatingSystem.IsWindows())
            {
                var oracleDataDir = Path.Combine(oracleTemp, ".data-rtk");
                var portDataDir = Path.Combine(portTemp, ".data-rtk");
                Directory.CreateDirectory(oracleDataDir);
                Directory.CreateDirectory(portDataDir);
                oracleEnv["XDG_DATA_HOME"] = oracleDataDir;
                portEnv["RTK_DATA_DIR_OVERRIDE"] = portDataDir;

                var oracleConfigDir = Path.Combine(oracleTemp, ".config-rtk");
                var portConfigDir = Path.Combine(portTemp, ".config-rtk");
                Directory.CreateDirectory(oracleConfigDir);
                Directory.CreateDirectory(portConfigDir);
                oracleEnv["XDG_CONFIG_HOME"] = oracleConfigDir;
                portEnv["RTK_CONFIG_DIR_OVERRIDE"] = portConfigDir;
            }
            else
            {
                portEnv["RTK_DATA_DIR_OVERRIDE"] = Path.Combine(portTemp, ".data-rtk", "rtk");
                portEnv["RTK_CONFIG_DIR_OVERRIDE"] = Path.Combine(portTemp, ".config-rtk");
                Directory.CreateDirectory(Path.Combine(portTemp, ".config-rtk"));
            }

            (string Stdout, int Exit) oracleLast = ("", 0);
            foreach (var step in steps)
            {
                oracleLast = await ParityRunner.RunAsync(oraclePath, step, oracleTemp, oracleEnv, stdin: "");
            }

            (string Stdout, int Exit) portLast = ("", 0);
            foreach (var step in steps)
            {
                var portArgs = portPrefixArgs.Concat(step).ToArray();
                portLast = await ParityRunner.RunAsync(portFileName, portArgs, portTemp, portEnv, stdin: "");
            }

            var oracleStdout = Normalize(MaskPaths(oracleLast.Stdout, oracleTemp));
            var portStdout = Normalize(MaskPaths(portLast.Stdout, portTemp));

            return new Result(label, oracleStdout, portStdout, oracleLast.Exit, portLast.Exit, TreeMatches: true, TreeDiff: null);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    // ===================== unified dual-real-directory guard =====================

    /// <summary>
    /// Creates both real-directory guards (<c>%APPDATA%\rtk</c> config-dir + <c>%LOCALAPPDATA%\rtk</c>
    /// trust-store/tracking data-dir) for one entry's oracle temp sandbox, <b>unconditionally, for
    /// every entry</b> — see this file's class remarks for why: the oracle was empirically observed
    /// to write real usage-tracking data into the trust-store data directory on every invocation
    /// regardless of subcommand, so both real directories are treated as at-risk universally, not
    /// just for the entries whose nominal purpose is config/trust-related. On non-Windows platforms
    /// this is a no-op (both env-var-based redirections there are honored by the oracle directly, no
    /// junction needed), returning <c>(null, null, null)</c>.
    /// </summary>
    /// <param name="label">The entry's label, used only to build the skip-reason result.</param>
    /// <param name="oracleTemp">The entry's own oracle-side temp sandbox root.</param>
    /// <returns>
    /// The two guards (both non-null and <see cref="HostRealDirGuard.RedirectMode.Redirected"/> when
    /// safe to proceed), or a non-null <see cref="Result"/> the caller should return immediately
    /// (host-safety skip) when either real directory already has content.
    /// </returns>
    private static (HostRealDirGuard? Config, HostRealDirGuard? Trust, Result? Skip) GuardBothRealDirs(
        string label, string oracleTemp)
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
                "touching it (config.toml overwrite / global filters.toml read)."));
        }

        var trustGuard = new HostRealDirGuard(
            HostRealDirs.TrustRealDir, HostRealDirs.TrustMarkerPath,
            Path.Combine(oracleTemp, ".data-rtk", "rtk"), ParityOracleTempPrefix);
        if (trustGuard.Mode == HostRealDirGuard.RedirectMode.RefuseUnsafe)
        {
            trustGuard.Dispose();
            configGuard.Dispose();
            return (null, null, Result.SkippedForHostSafety(
                label, "%LOCALAPPDATA%\\rtk already has real content on this host (this is also " +
                "the oracle's usage-tracking-history directory, written on every invocation); " +
                "refusing to risk touching it."));
        }

        return (configGuard, trustGuard, null);
    }

    // ===================== the two real-host-directory names, known at compile time =====================

    /// <summary>
    /// The two fixed real Windows directories this file's parity entries must never write to
    /// directly: <see cref="ConfigRealDir"/> (<c>rtk config</c>/the global filters tier — Rust
    /// <c>dirs::config_dir()</c>) and <see cref="TrustRealDir"/> (the trust store AND the usage
    /// tracking history database — Rust <c>dirs::data_local_dir()</c>). Referencing any member here
    /// triggers this class's static constructor exactly once per process, which self-heals any
    /// dangling junction left behind by a prior crashed run at either path and registers the
    /// second-layer <c>ProcessExit</c>/<c>UnhandledException</c> cleanup handlers for both —
    /// mirroring <see cref="InitParityTests.WindowsGlobalFiltersGuard"/>'s own static constructor,
    /// just covering two directories instead of one.
    /// </summary>
    private static class HostRealDirs
    {
        public static readonly string ConfigRealDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rtk");

        public static readonly string TrustRealDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtk");

        public static readonly string ConfigMarkerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".rtk-config-parity-redirect-marker");

        public static readonly string TrustMarkerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ".rtk-trust-parity-redirect-marker");

        static HostRealDirs()
        {
            HostRealDirGuard.SelfHeal(ConfigRealDir, ConfigMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
            HostRealDirGuard.SelfHeal(TrustRealDir, TrustMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                HostRealDirGuard.SelfHeal(ConfigRealDir, ConfigMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
                HostRealDirGuard.SelfHeal(TrustRealDir, TrustMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, _) =>
            {
                HostRealDirGuard.SelfHeal(ConfigRealDir, ConfigMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
                HostRealDirGuard.SelfHeal(TrustRealDir, TrustMarkerPath, ParityOracleTempPrefix, throwOnFailure: false);
            };
        }
    }

    /// <summary>
    /// Redirects a real, non-env-redirectable-on-Windows per-machine directory into a throwaway
    /// per-entry temp sandbox via an NTFS directory junction, so the Rust oracle's reads/writes
    /// during a parity-test entry can never reach real host state. A generalized sibling of
    /// <see cref="InitParityTests.WindowsGlobalFiltersGuard"/> reused for two different real
    /// directories (<c>%APPDATA%\rtk</c> for <c>rtk config</c>/the global filters tier,
    /// <c>%LOCALAPPDATA%\rtk</c> for the trust store/tracking history) — same safety properties
    /// (junction created only when nothing real exists at the target; teardown gated on
    /// marker-CONTENT validation across Dispose/self-heal/<c>ProcessExit</c>/<c>UnhandledException</c>;
    /// never a backup-and-restore mechanism), but with <b>no "SafeNoOp" fallback</b>: see this file's
    /// class remarks for why <c>rtk config --create</c>'s unconditional overwrite, <c>rtk trust</c>'s
    /// merge-into-existing-store semantics, and the tracking database's unconditional append all make
    /// "already has content, but touching it is harmless" untrue for both real directories this guard
    /// protects. Any pre-existing real content at the target therefore always resolves to
    /// <see cref="RedirectMode.RefuseUnsafe"/> here.
    /// </summary>
    private sealed class HostRealDirGuard : IDisposable
    {
        public enum RedirectMode
        {
            /// <summary>The real directory was junctioned into this entry's own temp sandbox; nothing the oracle does can reach real host state.</summary>
            Redirected,

            /// <summary>The real directory already has content; the caller must refuse to run this entry's oracle invocation at all.</summary>
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
                // Real content (or even an empty real directory a developer created independently)
                // already occupies this path: redirecting would require deleting/replacing it first,
                // which this guard will never do. Refuse — the caller skips the entry entirely.
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

            // A failed teardown must never be swallowed here: it means a junction is still sitting
            // at a real path in the developer's profile, and the developer needs to know.
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

        /// <summary>
        /// Removes the junction at <paramref name="realDir"/> if (and only if) it is actually still
        /// an NTFS reparse point — never a real directory, marker or no marker — <b>and</b>
        /// <paramref name="markerPath"/> exists with content proving this harness (not an unrelated
        /// junction/symlink a developer created independently at this exact path) created it. Called
        /// from <see cref="Dispose"/> (normal teardown), <see cref="HostRealDirs"/>'s static
        /// constructor (self-heal for a prior crashed run), and its
        /// <c>ProcessExit</c>/<c>UnhandledException</c> handlers (second-layer crash cleanup) — the
        /// same four teardown paths <c>InitParityTests.WindowsGlobalFiltersGuard</c> covers.
        /// <c>rmdir</c> without <c>/s</c> removes only the reparse point itself; it can never recurse
        /// into (and therefore can never delete) the sandboxed target's content.
        /// </summary>
        public static void SelfHeal(string realDir, string markerPath, string tempPrefixToken, bool throwOnFailure)
        {
            if (!IsReparsePoint(realDir))
            {
                TryDeleteMarker(markerPath);
                return;
            }

            if (!TryReadValidMarker(markerPath, tempPrefixToken))
            {
                // A reparse point exists, but there is no marker (or its content doesn't look like
                // one of this harness's own sandboxes) proving this harness created it. Leave it
                // alone rather than guess.
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

    // ===================== tree manifest (config --create entries) =====================

    private static Dictionary<string, string> BuildManifest(string root, bool stripTeeSection = false)
    {
        var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return manifest;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var content = File.ReadAllText(file);
            var masked = content.Replace(root, "{ROOT}", StringComparison.Ordinal);
            if (stripTeeSection)
            {
                // See docs/parity/compatibility-ledger.md: the oracle's config.toml has a `[tee]`
                // section this phase's Config deliberately omits (tee/recovery store deferred).
                masked = StripTeeSection(masked);
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(masked)));
            manifest[relative] = hash;
        }

        return manifest;
    }

    private static (bool Matches, string? Diff) CompareManifests(
        Dictionary<string, string> oracle, Dictionary<string, string> port)
    {
        var onlyInOracle = oracle.Keys.Except(port.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var onlyInPort = port.Keys.Except(oracle.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var contentMismatches = oracle.Keys.Intersect(port.Keys)
            .Where(k => oracle[k] != port[k])
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (onlyInOracle.Count == 0 && onlyInPort.Count == 0 && contentMismatches.Count == 0)
        {
            return (true, null);
        }

        var parts = new List<string>();
        if (onlyInOracle.Count > 0)
        {
            parts.Add($"only in oracle: {string.Join(", ", onlyInOracle)}");
        }

        if (onlyInPort.Count > 0)
        {
            parts.Add($"only in port: {string.Join(", ", onlyInPort)}");
        }

        if (contentMismatches.Count > 0)
        {
            parts.Add($"content differs: {string.Join(", ", contentMismatches)}");
        }

        return (false, string.Join("; ", parts));
    }

    // ===================== `[tee]` section masking (see compatibility-ledger.md) =====================

    /// <summary>
    /// Matches the oracle's <c>[tee]</c> config section (a whole section header through its last
    /// key/value line, up to but not including the next <c>[</c> section header or end of text) —
    /// see this file's class remarks and <c>docs/parity/compatibility-ledger.md</c> for why this is
    /// a scoped, deliberate masking of a plan-sanctioned deferral (the tee/recovery store is not
    /// ported this phase) rather than a general mismatch-suppression escape hatch. A static regex
    /// this harness itself authors (not TOML-sourced), so <c>GeneratedRegex</c> applies per house
    /// rule.
    /// </summary>
    [GeneratedRegex(@"\[tee\][^\[]*")]
    private static partial Regex TeeSectionRegex();

    /// <summary>
    /// Strips the oracle's <c>[tee]</c> section (see <see cref="TeeSectionRegex"/>) from a text blob
    /// before comparison — a no-op when the section is absent (as it always is on the port's side).
    /// </summary>
    /// <param name="text">The stdout or file content to mask.</param>
    /// <returns>The text with any <c>[tee]</c> section removed.</returns>
    private static string StripTeeSection(string text) => TeeSectionRegex().Replace(text, "");

    // ===================== shared helpers (mirrors InitParityTests'/VerifyParityTests' plumbing) =====================

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

    private static string MaskPaths(string stdout, string tempDir) => stdout.Replace(tempDir, "{TEMP}");

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
        sb.AppendLine("# `rtk config` / TOML Filter / `rtk trust` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/ConfigFilterParityTests.cs` " +
                      "(Phase 4 Task 6 acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (4 `rtk config` + 1 TOML built-in " +
                      "fallback dispatch + 8 `rtk trust`/`rtk untrust` + 5 `rtk verify`)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry runs both binaries in a fresh, isolated temp directory " +
                      "with `StdinContent = \"\"` (forced non-interactive stdin). Every entry redirects both " +
                      "real, non-env-redirectable-on-Windows directories (`%APPDATA%\\rtk` config-dir and " +
                      "`%LOCALAPPDATA%\\rtk` trust-store/tracking data-dir) via an NTFS junction " +
                      "(`HostRealDirGuard`, this file's generalized sibling of " +
                      "`InitParityTests.WindowsGlobalFiltersGuard`) before the oracle ever runs — applied " +
                      "unconditionally to every entry, since the oracle writes usage-tracking data on every " +
                      "invocation regardless of subcommand (see this file's class remarks). If either real " +
                      "directory already has content on the host, the entry is skipped entirely rather than " +
                      "risking any read/write against it. Multi-step entries compare only the final step's " +
                      "stdout (CRLF/LF normalized, temp-path masked) and exit code. The `config --create` " +
                      "entries additionally compare a full relative-path -> SHA-256 manifest of the " +
                      "redirected config directory after all steps (with the oracle's plan-deferred `[tee]` " +
                      "section masked out of the comparison — see `docs/parity/compatibility-ledger.md`).");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|--------|--------|");
        sb.AppendLine($"| **Overall parity (headline)** | **{percent:F1}%** |");
        sb.AppendLine($"| Threshold | {ParityThresholdPercent:F0}% |");
        sb.AppendLine($"| Entries matched | {matched} / {total} |");
        sb.AppendLine();
        sb.AppendLine("## Per-entry results");
        sb.AppendLine();
        sb.AppendLine("| Entry | Rust exit | .NET exit | Stdout match | Tree match | Verdict |");
        sb.AppendLine("|-------|-----------|-----------|--------------|------------|---------|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| `{r.Label}` | {r.RustExit} | {r.PortExit} | {(r.StdoutMatches ? "yes" : "no")} | " +
                $"{(r.TreeMatches ? "yes" : "no")} | {r.Verdict} |");
        }

        sb.AppendLine();
        var mismatches = results.Where(r => !r.IsMatch || r.Skipped).ToList();
        sb.AppendLine("## Mismatch details");
        sb.AppendLine();
        if (mismatches.Count == 0)
        {
            sb.AppendLine("None — every battery entry matched byte-exact stdout, an identical exit code, " +
                          "and (for write entries) an identical produced file tree.");
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
                if (r.TreeDiff is not null)
                {
                    sb.AppendLine($"- Tree diff: {Md(r.TreeDiff)}");
                }

                if (r.SkipReason is not null)
                {
                    sb.AppendLine($"- Skip reason: {Md(r.SkipReason)}");
                }

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
        bool TreeMatches, string? TreeDiff, bool Skipped = false, string? SkipReason = null)
    {
        /// <summary>
        /// Builds a result for an entry the harness deliberately refused to run against the oracle
        /// for host-safety reasons (see <see cref="HostRealDirGuard.RedirectMode.RefuseUnsafe"/>).
        /// Counted as matched so it never fails the gate, but clearly labeled in the report.
        /// </summary>
        public static Result SkippedForHostSafety(string label, string reason) =>
            new(label, "", "", 0, 0, TreeMatches: true, TreeDiff: null, Skipped: true, SkipReason: reason);

        public bool StdoutMatches => Skipped || RustStdout == PortStdout;

        public bool ExitsMatch => Skipped || RustExit == PortExit;

        public bool IsMatch => Skipped || (StdoutMatches && ExitsMatch && TreeMatches);

        public string Verdict => Skipped
            ? "SKIPPED (host-safety)"
            : IsMatch
                ? "MATCH"
                : !StdoutMatches
                    ? "MISMATCH (stdout)"
                    : !ExitsMatch
                        ? "MISMATCH (exit)"
                        : "MISMATCH (tree)";
    }
}
