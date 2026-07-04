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
/// <c>generate_global_filters_template</c> makes the *oracle* write to the real
/// <c>%APPDATA%\rtk\filters.toml</c> on the host machine (outside the temp sandbox), while the *port*
/// writes to the redirected <c>{temp}/.config-rtk/rtk/filters.toml</c>. This harness (a) wraps every
/// oracle invocation on a Windows global-scope entry in <see cref="WindowsGlobalFiltersGuard"/>, which
/// snapshots the real file before the run and restores it (or deletes it, if it did not exist) after —
/// so the battery never leaves a permanent mark on the host — and (b) excludes the
/// <c>.config-rtk/rtk/filters.toml</c> relative path from the Windows tree-diff (both binaries still
/// have to write byte-identical content everywhere else) rather than claim unachievable full
/// hermetic-tree parity for that one file on that one platform. This is the "own judgment" resolution
/// the Task 4 brief calls for; see <c>docs/parity/compatibility-ledger.md</c> for the ledgered entry.
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
/// </remarks>
public class InitParityTests
{
    private const double ParityThresholdPercent = 95.0;

    /// <summary>Relative subdirectory (within each entry's temp dir) standing in for the global config root.</summary>
    private const string ConfigRtkSubdir = ".config-rtk";

    /// <summary>
    /// The one relative path excluded from the Windows global-scope tree-diff — see the class
    /// remarks' "Windows asymmetry" section.
    /// </summary>
    private const string WindowsAsymmetricPath = ".config-rtk/rtk/filters.toml";

    /// <summary>A single step within a battery entry: one invocation of both binaries.</summary>
    private sealed record Step(string[] Args);

    /// <summary>
    /// A battery entry: a label, whether it exercises global scope (so the config-dir override env
    /// vars are applied), whether the produced file tree should be compared, and the ordered steps to
    /// run sequentially against the same temp directory.
    /// </summary>
    private sealed record Entry(string Label, bool Global, bool CompareTree, IReadOnlyList<Step> Steps);

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

            (string Stdout, int Exit) oracleLast = ("", 0);
            using (OperatingSystem.IsWindows() && entry.Global ? new WindowsGlobalFiltersGuard() : null)
            {
                foreach (var step in entry.Steps)
                {
                    oracleLast = await ParityRunner.RunAsync(oraclePath, step.Args, oracleTemp, oracleEnv, stdin: "");
                }
            }

            (string Stdout, int Exit) portLast = ("", 0);
            foreach (var step in entry.Steps)
            {
                var portArgs = portPrefixArgs.Concat(step.Args).ToArray();
                portLast = await ParityRunner.RunAsync(portFileName, portArgs, portTemp, portEnv, stdin: "");
            }

            bool treeMatches = true;
            string? treeDiff = null;
            if (entry.CompareTree)
            {
                var oracleManifest = BuildManifest(oracleTemp);
                var portManifest = BuildManifest(portTemp);

                if (OperatingSystem.IsWindows() && entry.Global)
                {
                    oracleManifest.Remove(WindowsAsymmetricPath);
                    portManifest.Remove(WindowsAsymmetricPath);
                }

                (treeMatches, treeDiff) = CompareManifests(oracleManifest, portManifest);
            }

            var oracleStdout = MaskPaths(oracleLast.Stdout, oracleTemp, entry.Global);
            var portStdout = MaskPaths(portLast.Stdout, portTemp, entry.Global);

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

    /// <summary>
    /// Snapshots the real, unredirectable Windows global filters-template target
    /// (<c>%APPDATA%\rtk\filters.toml</c>) before an oracle invocation and restores it (or deletes
    /// it, if it did not previously exist) afterward — see the class remarks' "Windows asymmetry"
    /// section for why the oracle can touch this real path at all under a global-scope entry.
    /// </summary>
    private sealed class WindowsGlobalFiltersGuard : IDisposable
    {
        private readonly string _path;
        private readonly bool _existed;
        private readonly byte[]? _backup;

        public WindowsGlobalFiltersGuard()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _path = Path.Combine(appData, "rtk", "filters.toml");
            _existed = File.Exists(_path);
            _backup = _existed ? File.ReadAllBytes(_path) : null;
        }

        public void Dispose()
        {
            try
            {
                if (_existed)
                {
                    File.WriteAllBytes(_path, _backup!);
                }
                else if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (IOException)
            {
                // Best-effort restoration; never fail the test on cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort restoration; never fail the test on cleanup.
            }
        }
    }

    // ===================== tree manifest =====================

    /// <summary>Builds a relative-path -&gt; lowercase-hex-SHA-256 manifest of every file under <paramref name="root"/>.</summary>
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
            using var stream = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
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
        sb.AppendLine($"- **Battery size:** {results.Count} entries (6 project-scope + 7 global-scope)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry runs both binaries in a fresh, isolated temp directory " +
                      "(`CLAUDE_CONFIG_DIR` redirected for both; global-scope entries also redirect the " +
                      "global filters-template path via `RTK_CONFIG_DIR_OVERRIDE` for the port and " +
                      "`XDG_CONFIG_HOME` for the oracle on Linux/macOS) with `StdinContent = \"\"` (forced " +
                      "non-interactive stdin). Multi-step entries (install-then-uninstall, repeated-run " +
                      "idempotency) compare only the final step's stdout (CRLF/LF normalized) and exit code. " +
                      "Write entries additionally compare a full relative-path -> SHA-256 manifest of the " +
                      "temp directory after all steps.");
        sb.AppendLine();
        if (OperatingSystem.IsWindows())
        {
            sb.AppendLine("> **Windows global-filters-template asymmetry.** The Rust oracle has no config-dir " +
                          "override; `dirs::config_dir()` resolves via a Win32 known-folder API that ignores " +
                          "environment variables, so on Windows the oracle's `generate_global_filters_template` " +
                          "writes to the real `%APPDATA%\\rtk\\filters.toml` (outside the temp sandbox) while " +
                          "the port writes to the redirected `.config-rtk/rtk/filters.toml` inside the temp " +
                          "sandbox. This harness backs up/restores the real file around every affected oracle " +
                          "run (see `WindowsGlobalFiltersGuard`) and excludes that one relative path from the " +
                          "Windows tree-diff — every other file in the tree is still compared byte-exact. See " +
                          "`docs/parity/compatibility-ledger.md` for the ledgered entry.");
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
        var mismatches = results.Where(r => !r.IsMatch).ToList();
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
        bool TreeMatches, string? TreeDiff)
    {
        public bool StdoutMatches => RustStdout == PortStdout;

        public bool ExitsMatch => RustExit == PortExit;

        public bool IsMatch => StdoutMatches && ExitsMatch && TreeMatches;

        public string Verdict => IsMatch
            ? "MATCH"
            : !StdoutMatches
                ? "MISMATCH (stdout)"
                : !ExitsMatch
                    ? "MISMATCH (exit)"
                    : "MISMATCH (tree)";
    }
}
