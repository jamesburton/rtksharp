using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 9b Task 4 acceptance gate: an oracle parity battery for the <c>rtk init</c> Claude Code
/// installer (project scope and global scope), comparing stdout, exit code, and the produced file
/// tree byte-for-byte, and asserting &gt;=95% parity (expected 100% on the hermetic entries).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hermeticity.</b> Every entry runs both binaries in a fresh throwaway temp directory as the
/// working directory, with <c>CLAUDE_CONFIG_DIR={temp}/.claude</c> in the environment overlay (this
/// redirects the Claude tree for both binaries, honored by the oracle even on Windows — see
/// <c>RewriteParityTests</c>'s Windows home-dir caveat, which this mirrors) and
/// <c>StdinContent = ""</c> to force non-interactive stdin (EOF), which makes the oracle's
/// <c>is_terminal()</c> check false — telemetry/consent prompts are skipped and <c>Ask</c> patch mode
/// deterministically defaults to No.
/// </para>
/// <para>
/// <b>Global-scope filters template — the Windows asymmetry.</b> Global-scope entries additionally
/// set <c>RTK_CONFIG_DIR_OVERRIDE={temp}/.config-rtk</c> for the RtkSharp side (the Task 1 test-only
/// escape hatch <c>InitArtifacts.ResolveGlobalConfigDir</c> checks before falling back to the real OS
/// config dir) and, on Linux/macOS, <c>XDG_CONFIG_HOME={temp}/.config-rtk</c> for the oracle side too
/// (the oracle's <c>dirs::config_dir()</c> honors that env var on those platforms, landing at the
/// *same* relative path as the port so the tree manifests line up). <b>On Windows there is no oracle
/// equivalent</b>: the Rust binary has no config-dir override, and <c>dirs::config_dir()</c> resolves
/// via <c>SHGetKnownFolderPath(FOLDERID_RoamingAppData)</c>, which ignores environment variables
/// entirely — confirmed in Task 1/2's parity work. So on Windows, a global-scope entry that reaches
/// <c>generate_global_filters_template</c> would otherwise make the *oracle* write to the real
/// <c>%APPDATA%\rtk\filters.toml</c> on the host machine (outside the temp sandbox), while the *port*
/// writes to the redirected <c>{temp}/.config-rtk/rtk/filters.toml</c>.
/// </para>
/// <para>
/// <b>Resolution: the real path is never written to at all (Option A), not written-then-restored.</b>
/// A Task 4 safety review found the original approach (snapshot the real file, run the oracle
/// unprotected, restore-in-<c>Dispose</c>) unacceptable: the restore only ran on a normal
/// <c>using</c>-block exit, so a crashed test host or a killed CI job could leave a developer's real
/// file mutated with zero restoration, and a freshly-created <c>%APPDATA%\rtk</c> directory was never
/// cleaned up even on the happy path. <see cref="WindowsGlobalFiltersGuard"/> replaces that with an
/// NTFS directory junction: when <c>%APPDATA%\rtk</c> does not already exist (the expected case on a
/// clean dev machine or CI runner — see the constructor's host-state checks for the two narrow
/// fallbacks below), the guard creates the *target* directory inside the entry's own oracle temp
/// sandbox and links <c>%APPDATA%\rtk</c> to it via <c>mklink /J</c>. The genuine Win32 known-folder
/// text the oracle prints is unaffected (junctions are link-transparent to <c>dirs::config_dir()</c>),
/// but every byte the oracle actually writes physically lands inside the temp sandbox — the real host
/// profile's backing store is never touched, so there is nothing to restore and nothing that can be
/// corrupted, even by a hard process kill. Tearing down the junction (<c>rmdir</c>, no <c>/s</c>) only
/// ever removes the reparse point itself, never the (sandboxed) target's content, so cleanup can never
/// destroy real data either. A marker file plus a static self-heal check (run once, before the first
/// Windows global-scope entry) detects and removes any dangling junction left behind by a prior crash,
/// and <c>AppDomain.ProcessExit</c>/<c>UnhandledException</c> handlers add a second cleanup layer for
/// an unhandled-exception crash (an OS-level <c>kill -9</c> is the one scenario no managed handler can
/// observe — but because a junction never itself holds real content, the worst case is a harmless
/// dangling link that self-heals on the next run, never data loss). Restore/cleanup failures throw
/// (loud) rather than being swallowed.
/// </para>
/// <para>
/// <b>Two narrow, defensive fallbacks — still zero-touch.</b> If <c>%APPDATA%\rtk\filters.toml</c>
/// already exists on the host (pre-dating this test run for an unrelated reason), the guard does
/// nothing at all: the oracle's own "already exists, skip" branch means it never attempts a write, so
/// there is no risk and nothing to redirect; the harness instead strips the filters-template success
/// line from both sides' stdout and excludes that one relative path from the tree-diff for that entry
/// (a real, pre-existing file it correctly refuses to overwrite is not something a parity battery
/// should be comparing against a fresh sandbox anyway). If <c>%APPDATA%\rtk</c> exists as a real
/// directory *without* <c>filters.toml</c> (so the oracle would perform a genuine, un-redirectable
/// write into real host state), the guard refuses to run that entry's oracle invocation at all — the
/// whole entry is marked "SKIPPED (host-safety)" in the report rather than ever risking the write. Both
/// fallbacks leave the host in the exact state it was already in; neither has been observed on the
/// verification machine used for this fix (a fresh <c>%APPDATA%\rtk</c> takes the common, fully
/// redirected path). See <c>docs/parity/compatibility-ledger.md</c> for the ledgered entry.
/// </para>
/// <para>
/// <b>Stdout only, last step, exit code.</b> Some entries are multi-step (e.g. install then
/// uninstall, or a repeated run for idempotency) — only the *last* step's stdout and exit code are
/// compared, since earlier steps exist purely to establish on-disk state. Stderr is never compared
/// (matches <c>HookParityTests</c>/<c>RewriteParityTests</c> precedent — diagnostic message text can
/// carry engine-specific formatting differences that are not part of the installer's contract). For
/// write entries, the produced file tree (a recursive relative-path -&gt; SHA-256 manifest of the temp
/// directory after all steps) is compared too, so both installers must write identical files with
/// identical bytes, not just print the same thing.
/// </para>
/// <para>
/// <b>Collection isolation from <see cref="ConfigFilterParityTests"/>.</b> Phase 4 Task 6's
/// <c>ConfigFilterParityTests</c> independently manages a junction at this exact same real path
/// (<c>%APPDATA%\rtk</c>) for its own <c>rtk config</c>/TOML-fallback entries, via its own
/// <c>HostRealDirGuard</c> class (a separate marker file, so neither guard's self-heal can
/// misidentify the other's leftover junction). xUnit does not guarantee two different test classes
/// run sequentially by default, and a concurrent junction create/teardown race at the same real path
/// is exactly the class of bug both guards exist to prevent — so both classes are pinned to the same
/// named <c>[Collection]</c>, which xUnit never runs in parallel with itself, eliminating the race by
/// construction.
/// </para>
/// </remarks>
[Collection("HostRealDirGuard")]
public class InitParityTests
{
    private const double ParityThresholdPercent = 95.0;

    /// <summary>Relative subdirectory (within each entry's temp dir) standing in for the global config root.</summary>
    private const string ConfigRtkSubdir = ".config-rtk";

    /// <summary>Relative subdirectory (within each entry's temp dir) standing in for <c>$CODEX_HOME</c>.</summary>
    private const string CodexHomeSubdir = ".codex-home";

    /// <summary>
    /// The one relative path excluded from the Windows global-scope tree-diff, but only in the
    /// narrow <see cref="WindowsGlobalFiltersGuard.RedirectMode.SafeNoOp"/> fallback — see the class
    /// remarks' "Windows asymmetry" section. In the common, fully-redirected case this path is
    /// compared like every other file.
    /// </summary>
    private const string WindowsAsymmetricPath = ".config-rtk/rtk/filters.toml";

    /// <summary>A single step within a battery entry: one invocation of both binaries.</summary>
    private sealed record Step(string[] Args);

    /// <summary>
    /// A battery entry: a label, whether it exercises global scope (so the Claude-specific config-dir
    /// override env vars and the Windows filters-template guard are applied), whether it exercises
    /// Codex mode (so <c>CODEX_HOME</c> is redirected instead), whether the produced file tree should
    /// be compared, and the ordered steps to run sequentially against the same temp directory.
    /// </summary>
    /// <remarks>
    /// <c>Global</c> and <c>Codex</c> are independent: Codex entries always set <c>Global: false</c>
    /// here regardless of whether their <see cref="Step.Args"/> include <c>-g</c>, since Codex's
    /// global-vs-project scope is entirely determined by that CLI flag and <c>CODEX_HOME</c>'s
    /// resolution (a plain env var, honored identically on every platform) — it needs none of the
    /// Claude-specific <c>CLAUDE_CONFIG_DIR</c>/<c>RTK_CONFIG_DIR_OVERRIDE</c>/<see cref="WindowsGlobalFiltersGuard"/>
    /// machinery that <c>Global: true</c> triggers for Claude Code entries.
    /// </remarks>
    private sealed record Entry(string Label, bool Global, bool CompareTree, IReadOnlyList<Step> Steps, bool Codex = false);

    [Fact]
    public async Task InitVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the init-parity gate.");
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
        var results = new List<InitResult>();

        foreach (var entry in battery)
        {
            results.Add(await RunEntryAsync(entry, oraclePath, portFileName, portPrefixArgs));
        }

        var matched = results.Count(r => r.IsMatch);
        var total = results.Count;
        var percent = total == 0 ? 100.0 : matched * 100.0 / total;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "init-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"init parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} " +
                $"stdoutMatch={r.StdoutMatches} treeMatch={r.TreeMatches}" +
                (r.TreeDiff is null ? "" : $" treeDiff=[{r.TreeDiff}]"));
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    /// <summary>Builds the 13-entry battery: 6 project-scope + 7 global-scope entries.</summary>
    private static IReadOnlyList<Entry> BuildBattery()
    {
        var entries = new List<Entry>
        {
            // ── project scope (fully hermetic on every platform) ───────────
            new("project: default init", Global: false, CompareTree: true,
                [new(["init"])]),
            new("project: init --claude-md", Global: false, CompareTree: true,
                [new(["init", "--claude-md"])]),
            new("project: init --hook-only (warn path)", Global: false, CompareTree: true,
                [new(["init", "--hook-only"])]),
            // Rust's `-v`/`-vv`/`-vvv`/`--verbose` is a top-level `Cli` flag "only recognized
            // before the subcommand" (main.rs:65-67) — `rtk init --dry-run -v` is a clap parse
            // error (exit 2). The correct invocation threading verbosity into InitContext is
            // `rtk -v init --dry-run`.
            new("project: init --dry-run -v", Global: false, CompareTree: true,
                [new(["-v", "init", "--dry-run"])]),
            new("project: init --uninstall (after default install)", Global: false, CompareTree: true,
                [new(["init"]), new(["init", "--uninstall"])]),
            new("project: re-run idempotency", Global: false, CompareTree: true,
                [new(["init"]), new(["init"])]),

            // ── global scope (hermetic on every platform via RTK_CONFIG_DIR_OVERRIDE) ──
            new("global: default init -g", Global: true, CompareTree: true,
                [new(["init", "-g"])]),
            new("global: init -g --auto-patch", Global: true, CompareTree: true,
                [new(["init", "-g", "--auto-patch"])]),
            new("global: init -g --no-patch", Global: true, CompareTree: true,
                [new(["init", "-g", "--no-patch"])]),
            new("global: init -g --dry-run", Global: true, CompareTree: true,
                [new(["init", "-g", "--dry-run"])]),
            new("global: init -g --uninstall (after default install)", Global: true, CompareTree: true,
                [new(["init", "-g"]), new(["init", "-g", "--uninstall"])]),
            new("global: init -g --show", Global: true, CompareTree: true,
                [new(["init", "-g", "--show"])]),
            new("global: re-run idempotency", Global: true, CompareTree: true,
                [new(["init", "-g"]), new(["init", "-g"])]),

            // ── Codex project scope (fully hermetic on every platform: no runtime hook, no
            //    settings.json patch — just RTK.md + AGENTS.md reference writes under the CWD) ──
            new("codex: default init", Global: false, CompareTree: true,
                [new(["init", "--codex"])], Codex: true),
            new("codex: init --dry-run", Global: false, CompareTree: true,
                [new(["init", "--codex", "--dry-run"])], Codex: true),
            new("codex: re-run idempotency", Global: false, CompareTree: true,
                [new(["init", "--codex"]), new(["init", "--codex"])], Codex: true),
            new("codex: uninstall without --global (error path)", Global: false, CompareTree: false,
                [new(["init", "--codex", "--uninstall"])], Codex: true),

            // ── Codex global scope (hermetic on every platform via CODEX_HOME — a plain env var
            //    read by resolve_codex_dir, unlike Claude's dirs::config_dir() Win32 API call) ──
            new("codex: global default init -g --codex", Global: false, CompareTree: true,
                [new(["init", "-g", "--codex"])], Codex: true),
            new("codex: global init -g --codex --dry-run", Global: false, CompareTree: true,
                [new(["init", "-g", "--codex", "--dry-run"])], Codex: true),
            new("codex: global uninstall (after default install)", Global: false, CompareTree: true,
                [new(["init", "-g", "--codex"]), new(["init", "-g", "--codex", "--uninstall"])], Codex: true),
            new("codex: global re-run idempotency", Global: false, CompareTree: true,
                [new(["init", "-g", "--codex"]), new(["init", "-g", "--codex"])], Codex: true),
            new("codex: --show --codex", Global: false, CompareTree: true,
                [new(["init", "--codex", "--show"])], Codex: true),
        };

        return entries;
    }

    // ===================== execution =====================

    /// <summary>Runs one battery entry against both binaries and compares the outcome.</summary>
    private static async Task<InitResult> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-init-parity-oracle-");
        var portTemp = CreateTempDir("rtk-init-parity-port-");

        try
        {
            Directory.CreateDirectory(Path.Combine(oracleTemp, ".claude"));
            Directory.CreateDirectory(Path.Combine(portTemp, ".claude"));

            var oracleEnv = new Dictionary<string, string?>
            {
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(oracleTemp, ".claude"),
            };
            var portEnv = new Dictionary<string, string?>
            {
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(portTemp, ".claude"),
            };

            if (entry.Global)
            {
                Directory.CreateDirectory(Path.Combine(oracleTemp, ConfigRtkSubdir));
                Directory.CreateDirectory(Path.Combine(portTemp, ConfigRtkSubdir));

                portEnv["RTK_CONFIG_DIR_OVERRIDE"] = Path.Combine(portTemp, ConfigRtkSubdir);
                if (!OperatingSystem.IsWindows())
                {
                    // Belt-and-braces per the plan: makes the oracle's global scope hermetic on
                    // platforms where dirs::config_dir() honors XDG_CONFIG_HOME, landing at the
                    // same relative path as the port's override for a clean tree-diff.
                    oracleEnv["XDG_CONFIG_HOME"] = Path.Combine(oracleTemp, ConfigRtkSubdir);
                }
            }

            if (entry.Codex)
            {
                // Codex's resolve_codex_dir reads $CODEX_HOME via a plain std::env::var_os call —
                // honored identically on every platform (unlike dirs::config_dir()'s Win32
                // known-folder API for the Claude filters-template path) — so a single env var
                // redirection is fully hermetic here with no WindowsGlobalFiltersGuard needed, for
                // both the --codex project-scope entries (which never consult CODEX_HOME at all,
                // so this is simply unused/harmless) and the -g --codex global-scope entries.
                var oracleCodexHome = Path.Combine(oracleTemp, CodexHomeSubdir);
                var portCodexHome = Path.Combine(portTemp, CodexHomeSubdir);
                Directory.CreateDirectory(oracleCodexHome);
                Directory.CreateDirectory(portCodexHome);
                oracleEnv["CODEX_HOME"] = oracleCodexHome;
                portEnv["CODEX_HOME"] = portCodexHome;
            }

            // On Windows global-scope entries, redirect %APPDATA%\rtk to a directory inside this
            // entry's own oracle temp sandbox via an NTFS junction *before* the oracle ever runs,
            // so its filters-template write can never reach the real host profile — see the class
            // remarks' "Windows asymmetry" section. `using` guarantees Dispose (junction teardown)
            // runs on every normal exit path, including the early-return skip below.
            using var guard = OperatingSystem.IsWindows() && entry.Global
                ? new WindowsGlobalFiltersGuard(Path.Combine(oracleTemp, ConfigRtkSubdir, "rtk"))
                : null;

            if (guard is { Mode: WindowsGlobalFiltersGuard.RedirectMode.RefuseUnsafe })
            {
                // %APPDATA%\rtk exists as a real directory without filters.toml: the oracle would
                // perform a genuine, un-redirectable write into real host state. Refuse to run this
                // entry's oracle invocation at all rather than risk it — see the class remarks.
                return InitResult.SkippedForHostSafety(
                    entry.Label,
                    "%APPDATA%\\rtk exists on this host without filters.toml; refusing to risk a " +
                    "real write there. Remove/rename that directory (or pre-create filters.toml) " +
                    "to exercise this entry, or accept the reduced coverage.");
            }

            (string Stdout, int Exit) oracleLast = ("", 0);
            foreach (var step in entry.Steps)
            {
                oracleLast = await ParityRunner.RunAsync(oraclePath, step.Args, oracleTemp, oracleEnv, stdin: "");
            }

            (string Stdout, int Exit) portLast = ("", 0);
            foreach (var step in entry.Steps)
            {
                var portArgs = portPrefixArgs.Concat(step.Args).ToArray();
                portLast = await ParityRunner.RunAsync(portFileName, portArgs, portTemp, portEnv, stdin: "");
            }

            bool guardSafeNoOp = guard is { Mode: WindowsGlobalFiltersGuard.RedirectMode.SafeNoOp };

            bool treeMatches = true;
            string? treeDiff = null;
            if (entry.CompareTree)
            {
                var oracleManifest = BuildManifest(oracleTemp);
                var portManifest = BuildManifest(portTemp);

                if (guardSafeNoOp)
                {
                    // The real filters.toml already existed before this run, so the oracle's own
                    // "already exists, skip" branch means it never wrote anything for it, while the
                    // port always writes a fresh template into its redirected sandbox. Exclude that
                    // one relative path rather than claim unachievable parity for pre-existing,
                    // host-specific state the guard correctly refused to touch.
                    oracleManifest.Remove(WindowsAsymmetricPath);
                    portManifest.Remove(WindowsAsymmetricPath);
                }

                (treeMatches, treeDiff) = CompareManifests(oracleManifest, portManifest);
            }

            var oracleStdout = MaskPaths(oracleLast.Stdout, oracleTemp, entry.Global);
            var portStdout = MaskPaths(portLast.Stdout, portTemp, entry.Global);

            if (guardSafeNoOp)
            {
                // Strip the filters-template success line the port prints (and the oracle does not,
                // per the tree-diff comment above) so this pre-existing host state doesn't register
                // as a stdout mismatch.
                oracleStdout = StripFiltersTemplateLine(oracleStdout);
                portStdout = StripFiltersTemplateLine(portStdout);
            }

            return new InitResult(
                entry.Label, Normalize(oracleStdout), Normalize(portStdout),
                oracleLast.Exit, portLast.Exit, treeMatches, treeDiff);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    /// <summary>Removes the filters-template success line printed by <c>rtk init -g</c>, used only for the guard's narrow "already exists" fallback (see class remarks).</summary>
    private static string StripFiltersTemplateLine(string stdout) =>
        string.Join('\n', stdout.Split('\n')
            .Where(line => !line.Contains("template, edit to add user-global filters", StringComparison.Ordinal)));

    /// <summary>
    /// Redirects the real, unredirectable Windows global filters-template target
    /// (<c>%APPDATA%\rtk</c>) to a directory inside the current entry's own temp sandbox via an NTFS
    /// directory junction, so the oracle's write during a global-scope entry can never reach real host
    /// state at all — see the class remarks' "Windows asymmetry" section for the full rationale and
    /// the two narrow, still zero-touch fallbacks (<see cref="RedirectMode.SafeNoOp"/> and
    /// <see cref="RedirectMode.RefuseUnsafe"/>) this guard falls back to when <c>%APPDATA%\rtk</c>
    /// already has real content.
    /// </summary>
    private sealed class WindowsGlobalFiltersGuard : IDisposable
    {
        /// <summary>The outcome of trying to make <c>%APPDATA%\rtk</c> safe for the oracle to write into.</summary>
        public enum RedirectMode
        {
            /// <summary><c>%APPDATA%\rtk</c> was junctioned into this entry's own temp sandbox; the oracle's write never reaches real host state.</summary>
            Redirected,

            /// <summary><c>%APPDATA%\rtk\filters.toml</c> already existed; the oracle's own "already exists, skip" branch means it never writes, so nothing was touched.</summary>
            SafeNoOp,

            /// <summary><c>%APPDATA%\rtk</c> exists as a real directory without <c>filters.toml</c>; running the oracle would risk a genuine write, so the caller must refuse to run it.</summary>
            RefuseUnsafe,
        }

        private static readonly string AppDataDir =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        private static readonly string RtkDir = Path.Combine(AppDataDir, "rtk");

        private static readonly string FiltersPath = Path.Combine(RtkDir, "filters.toml");

        /// <summary>
        /// Marks an in-progress redirect. Written just before the junction is created and deleted
        /// right after it is torn down, so a leftover marker after a crash unambiguously means "a
        /// junction (never real data) may still be sitting at <see cref="RtkDir"/>" — safe for the
        /// static self-heal check below to act on unconditionally.
        /// </summary>
        private static readonly string MarkerPath =
            Path.Combine(AppDataDir, ".rtk-init-parity-redirect-marker");

        /// <summary>
        /// Prefix used by <c>CreateTempDir("rtk-init-parity-oracle-")</c> for the oracle temp
        /// sandbox whose subdirectory becomes the junction target. The marker's recorded content
        /// (the redirect target path) is checked for a path segment carrying this prefix before any
        /// cleanup path is allowed to touch <see cref="RtkDir"/> — this is what actually makes the
        /// marker trustworthy evidence "this reparse point is one our test harness created", rather
        /// than merely a file whose *presence* is asserted but never read.
        /// </summary>
        private const string OracleTempPrefix = "rtk-init-parity-oracle-";

        public RedirectMode Mode { get; }

        static WindowsGlobalFiltersGuard()
        {
            // Runs once, before the first Windows global-scope entry in this test process touches
            // anything: if a previous run crashed between creating the junction and removing it,
            // self-heal by removing the dangling junction now. This only ever acts on an actual NTFS
            // reparse point at RtkDir (checked inside RemoveJunctionIfPresent) — a real directory is
            // never touched, marker or not.
            RemoveJunctionIfPresent(throwOnFailure: false);

            // Second-layer cleanup for the common "unhandled exception kills the test process"
            // failure mode (does not cover an OS-level kill -9, which no managed handler can
            // observe — but a dangling junction never itself holds real data, so that residual risk
            // is a harmless, self-healing leftover rather than data loss).
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveJunctionIfPresent(throwOnFailure: false);
            AppDomain.CurrentDomain.UnhandledException += (_, _) => RemoveJunctionIfPresent(throwOnFailure: false);
        }

        public WindowsGlobalFiltersGuard(string redirectTargetDir)
        {
            if (File.Exists(FiltersPath))
            {
                // The oracle will take its own "already exists, skip" branch — zero risk, nothing to do.
                Mode = RedirectMode.SafeNoOp;
                return;
            }

            if (Directory.Exists(RtkDir))
            {
                // A real directory without filters.toml: writing here would be a genuine,
                // un-redirectable host mutation. Refuse — the caller skips this entry entirely.
                Mode = RedirectMode.RefuseUnsafe;
                return;
            }

            // Common case: nothing real exists at RtkDir yet. Redirect it wholesale into this
            // entry's own temp sandbox so the oracle's write can only ever land there. The marker is
            // written first (so a crash *during* junction creation still leaves evidence a redirect
            // was in flight for the self-heal check to reason about) but is deleted again if junction
            // creation itself throws, so a failed `mklink` never leaves an orphaned marker sitting in
            // the real %APPDATA% root for this run.
            Directory.CreateDirectory(redirectTargetDir);
            File.WriteAllText(MarkerPath, redirectTargetDir);
            try
            {
                CreateJunction(RtkDir, redirectTargetDir);
            }
            catch
            {
                TryDeleteMarker();
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

            // A failed teardown must never be swallowed: it means a junction is still sitting at a
            // real path in the developer's profile, and the developer needs to know.
            RemoveJunctionIfPresent(throwOnFailure: true);
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
                    $"Failed to create the WindowsGlobalFiltersGuard junction '{link}' -> '{target}' " +
                    $"(exit {proc.ExitCode}): {stderr}");
            }
        }

        /// <summary>
        /// Removes the junction at <see cref="RtkDir"/> if (and only if) it is actually still an NTFS
        /// reparse point — never a real directory, marker or no marker — <b>and</b>
        /// <see cref="MarkerPath"/> exists with content that itself looks like one of this harness's
        /// own oracle temp sandboxes (see <see cref="TryReadValidMarker"/>). A reparse point at
        /// <see cref="RtkDir"/> with no such marker is left completely untouched: it might be a real
        /// junction/symlink a developer created independently at this exact path for unrelated
        /// reasons, and this class has no way to distinguish that from one of its own without the
        /// marker, so the safe default is to no-op rather than guess. <c>rmdir</c> without <c>/s</c>
        /// removes only the reparse point itself; it can never recurse into (and therefore can never
        /// delete) the sandboxed target's content.
        /// </summary>
        private static void RemoveJunctionIfPresent(bool throwOnFailure)
        {
            if (!IsReparsePoint(RtkDir))
            {
                TryDeleteMarker();
                return;
            }

            if (!TryReadValidMarker(out _))
            {
                // A reparse point exists at RtkDir, but there is no marker (or its content doesn't
                // look like one of this harness's own sandboxes) proving this harness created it.
                // Leave it alone — see the remarks above and the class-level XML doc.
                return;
            }

            var psi = new ProcessStartInfo("cmd.exe", $"/c rmdir \"{RtkDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start rmdir process for junction cleanup.");
            proc.WaitForExit();

            if (proc.ExitCode != 0 || Directory.Exists(RtkDir))
            {
                var stderr = proc.StandardError.ReadToEnd();
                var message =
                    $"CRITICAL: failed to remove the WindowsGlobalFiltersGuard junction at '{RtkDir}' " +
                    $"(exit {proc.ExitCode}): {stderr}. This path is an NTFS junction the test harness " +
                    "created and never contains real host data, but it must be removed manually " +
                    $"(run `rmdir \"{RtkDir}\"` from a shell) before the next `rtk init -g` on this machine.";
                if (throwOnFailure)
                {
                    throw new InvalidOperationException(message);
                }

                Console.Error.WriteLine(message);
                return;
            }

            TryDeleteMarker();
        }

        private static void TryDeleteMarker()
        {
            try
            {
                if (File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Reads <see cref="MarkerPath"/> and validates its recorded content — the redirect target
        /// directory written in the constructor — actually looks like one of this harness's own
        /// oracle temp sandboxes, not merely that the marker file happens to exist. Requires the
        /// recorded path to be rooted, to resolve under <see cref="Path.GetTempPath"/>, and to
        /// contain a path segment starting with <see cref="OracleTempPrefix"/> (the
        /// <c>CreateTempDir("rtk-init-parity-oracle-")</c> naming convention). Any I/O failure, empty
        /// content, or content that fails those checks is treated as "not a valid marker" — the safe
        /// default when we cannot prove the reparse point is ours.
        /// </summary>
        private static bool TryReadValidMarker(out string? recordedTarget)
        {
            recordedTarget = null;

            if (!File.Exists(MarkerPath))
            {
                return false;
            }

            try
            {
                var content = File.ReadAllText(MarkerPath).Trim();
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
                if (!segments.Any(s => s.StartsWith(OracleTempPrefix, StringComparison.Ordinal)))
                {
                    return false;
                }

                recordedTarget = full;
                return true;
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

    // ===================== tree manifest =====================

    /// <summary>
    /// Builds a relative-path -&gt; lowercase-hex-SHA-256 manifest of every file under
    /// <paramref name="root"/>. Each file's text content has <paramref name="root"/>'s own absolute
    /// path masked out to a fixed placeholder before hashing — needed because Codex's global-scope
    /// <c>AGENTS.md</c> reference embeds an <b>absolute</b> path to <c>RTK.md</c> (issue #892: Codex
    /// resolves <c>@</c> references relative to CWD, not the file's location), and the oracle/port
    /// each run in their own independently-named temp directory, so that one file would otherwise
    /// never byte-match between the two sides even when the installer behaved identically — the same
    /// masking already applied to captured stdout via <see cref="MaskPaths"/>, extended to on-disk
    /// file content for this one structural case.
    /// </summary>
    private static Dictionary<string, string> BuildManifest(string root)
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
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(masked)));
            manifest[relative] = hash;
        }

        return manifest;
    }

    /// <summary>Compares two file-tree manifests, returning a match flag and a human-readable diff summary.</summary>
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

    // ===================== helpers =====================

    /// <summary>Normalizes CRLF/LF line endings so a platform newline difference never counts as a mismatch.</summary>
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Masks the entry's own randomly-named temp directory out of a captured stdout so two
    /// independent (necessarily different) absolute paths don't register as a stdout mismatch. On
    /// Windows, global-scope entries additionally mask the real, unredirectable
    /// <c>%APPDATA%\rtk</c> directory the oracle prints (see the class remarks' "Windows asymmetry"
    /// section) down to the same placeholder the port's redirected <c>.config-rtk\rtk</c> path masks
    /// to, so the two sides converge on identical text.
    /// </summary>
    private static string MaskPaths(string stdout, string tempDir, bool global)
    {
        var masked = stdout.Replace(tempDir, "{TEMP}");

        if (global && OperatingSystem.IsWindows())
        {
            var appDataRtkDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rtk");
            masked = masked.Replace(appDataRtkDir, Path.Combine("{TEMP}", ConfigRtkSubdir, "rtk"));
        }

        return masked;
    }

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
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
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
        IReadOnlyList<InitResult> results,
        double percent,
        int matched,
        int total,
        string oraclePath,
        string portFileName,
        string[] portPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# `rtk init` Installer Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/InitParityTests.cs` " +
                      "(Phase 9b Task 4 acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (6 Claude project-scope + 7 Claude " +
                      "global-scope + 4 Codex project-scope + 5 Codex global-scope)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry runs both binaries in a fresh, isolated temp directory " +
                      "(`CLAUDE_CONFIG_DIR` redirected for both; Claude global-scope entries also redirect " +
                      "the global filters-template path via `RTK_CONFIG_DIR_OVERRIDE` for the port and " +
                      "`XDG_CONFIG_HOME` for the oracle on Linux/macOS; Codex entries redirect `CODEX_HOME` " +
                      "for both binaries, which — being a plain environment-variable read on every platform, " +
                      "unlike `dirs::config_dir()`'s Win32 known-folder API — needs no Windows-specific " +
                      "junction guard) with `StdinContent = \"\"` (forced non-interactive stdin). Multi-step " +
                      "entries (install-then-uninstall, repeated-run idempotency) compare only the final " +
                      "step's stdout (CRLF/LF normalized) and exit code. Write entries additionally compare " +
                      "a full relative-path -> SHA-256 manifest of the temp directory after all steps.");
        sb.AppendLine();
        if (OperatingSystem.IsWindows())
        {
            sb.AppendLine("> **Windows global-filters-template asymmetry.** The Rust oracle has no config-dir " +
                          "override; `dirs::config_dir()` resolves via a Win32 known-folder API that ignores " +
                          "environment variables, so on Windows the oracle's `generate_global_filters_template` " +
                          "would otherwise write to the real `%APPDATA%\\rtk\\filters.toml` (outside the temp " +
                          "sandbox) while the port writes to the redirected `.config-rtk/rtk/filters.toml` " +
                          "inside its own temp sandbox. `WindowsGlobalFiltersGuard` eliminates the real-write " +
                          "risk entirely rather than backing up and restoring the real file: it redirects " +
                          "`%APPDATA%\\rtk` into the oracle's own temp sandbox via an NTFS directory junction " +
                          "*before* the oracle ever runs, so its write physically lands inside the sandbox and " +
                          "never touches the real host profile — every file in the tree, including the " +
                          "filters template, is compared byte-exact. Two narrow fallbacks apply only if " +
                          "`%APPDATA%\\rtk` already has real content on this host (never observed on the " +
                          "verification machine): if `filters.toml` already exists, the guard is a no-op " +
                          "(the oracle's own \"already exists\" branch means it never writes) and only that " +
                          "one relative path plus its stdout line are excluded from comparison; if the " +
                          "directory exists without `filters.toml`, the entire entry is skipped rather than " +
                          "risking a real write. See `docs/parity/compatibility-ledger.md` for the ledgered " +
                          "entry.");
            sb.AppendLine();
        }

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

    /// <summary>Escapes a cell value for a Markdown table (pipes, newlines, empties).</summary>
    private static string Md(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "*(empty)*";
        }

        return "`" + s.Replace("\n", " ").Replace("|", "\\|") + "`";
    }

    /// <summary>The parity outcome for a single init battery entry.</summary>
    private sealed record InitResult(
        string Label, string RustStdout, string PortStdout, int RustExit, int PortExit,
        bool TreeMatches, string? TreeDiff, bool Skipped = false, string? SkipReason = null)
    {
        /// <summary>
        /// Builds a result for an entry the harness deliberately refused to run against the oracle
        /// for host-safety reasons (see <see cref="WindowsGlobalFiltersGuard.RedirectMode.RefuseUnsafe"/>).
        /// Counted as matched so it never fails the gate, but clearly labeled in the report.
        /// </summary>
        public static InitResult SkippedForHostSafety(string label, string reason) =>
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
