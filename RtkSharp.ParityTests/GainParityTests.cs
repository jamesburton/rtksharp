using System.Text;
using Microsoft.Data.Sqlite;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 5 Task 6 acceptance gate: an oracle parity battery for <c>rtk gain</c> covering the default
/// summary view, <c>--daily</c>/<c>--weekly</c>/<c>--monthly</c>/<c>--all</c>, <c>--graph</c>,
/// <c>--history</c>, <c>--quota</c> (default tier, <c>-t 5x</c>, <c>-t pro</c>, an invalid tier value),
/// <c>--format json</c>/<c>--format csv</c>, <c>--failures</c> (zero and non-zero cases), and
/// <c>--reset --yes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hermeticity via <c>RTK_DB_PATH</c> — no <c>HostRealDirGuard</c> needed, empirically verified.</b>
/// Unlike <see cref="ConfigFilterParityTests"/>/<see cref="InitParityTests"/>, this battery does not
/// need the NTFS-junction real-directory guard at all: <c>RTK_DB_PATH</c> is a full, top-priority
/// override on the oracle side (<c>tracking.rs</c>'s <c>get_db_path()</c> checks it first, verbatim,
/// before ever consulting <c>dirs::data_local_dir()</c>), so every entry here redirects the tracking
/// database itself with no fallback to the real <c>%LOCALAPPDATA%\rtk\history.db</c>. The one other
/// path Rust's <c>gain.rs</c> default view touches under <c>%LOCALAPPDATA%\rtk</c> is
/// <c>hook_check</c>'s rate-limit marker (<c>.hook_warn_last</c>, written by <c>maybe_warn()</c>) — but
/// <c>gain.rs</c> calls <c>hook_check::status()</c> directly (gain.rs:125), which is read-only (it
/// only reads <c>$CLAUDE_CONFIG_DIR</c>/settings.json and the legacy hook file, never writes anything),
/// and <c>maybe_warn()</c> (the only function that ever writes the marker) is never called from
/// <c>gain</c> at all — Rust's own <c>main.rs</c> pre-dispatch sequence explicitly excludes the
/// <c>Gain</c> command from that call, mirrored by <c>RtkSharp</c>'s <c>RtkProgram</c> dispatch. This
/// was verified empirically before writing this battery, not just reasoned about from source: with
/// <c>RTK_DB_PATH</c> set to a throwaway file and nothing else redirected, a canary
/// <c>rtk gain --format json</c> invocation against the real Rust oracle left every file under the
/// real <c>%LOCALAPPDATA%\rtk</c> (including <c>history.db</c> and <c>.hook_warn_last</c>) with an
/// identical mtime before and after — confirming the oracle never touches that directory for any
/// <c>gain</c> invocation when <c>RTK_DB_PATH</c> is set, exactly as the Phase 5 plan predicted. No
/// defensive guard is therefore applied in this file. This is not a fully exhaustive proof by static
/// reasoning alone, though: <c>main.rs</c>'s pre-dispatch sequence also calls
/// <c>core::telemetry::maybe_ping()</c> for every command including <c>Gain</c>, and that function's
/// marker/salt file paths likewise resolve via <c>dirs::data_local_dir()</c> (not redirected by
/// <c>RTK_DB_PATH</c>/<c>CLAUDE_CONFIG_DIR</c>). It is harmless in practice only because
/// <c>maybe_ping()</c> returns immediately when the compile-time <c>TELEMETRY_URL</c>
/// (<c>option_env!</c>) is absent — the default oracle build used here — and is additionally gated on
/// <c>telemetry.consent_given == Some(true)</c>; the empirical canary above is what actually closes
/// this gap for this specific oracle binary, not the source-reading argument on its own.
/// </para>
/// <para>
/// <b>Seeding strategy: raw SQL, not through either binary; a fresh database built from scratch per
/// side per entry (not a shared master file).</b> A deterministic dataset (18 <c>commands</c> rows
/// spanning April-July 2026 across 10 distinct <c>rtk_cmd</c> values, for realistic
/// daily/weekly/monthly/by-command/history coverage; a separate 5-row <c>parse_failures</c> dataset
/// for the non-zero <c>--failures</c> case) is inserted directly via
/// <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> against the exact schema
/// <c>RtkSharp/Core/Tracking/Tracker.cs</c>'s <c>InitSchema</c> creates (mirrored here verbatim, not
/// invoked through <see cref="RtkSharp.Core.Tracking.Tracker"/> itself, which has no public
/// externally-usable constructor and whose <c>internal</c> one is not exposed to this test assembly) —
/// this guarantees both binaries see byte-identical starting bytes on disk, independent of either
/// implementation's own write path. Every entry independently re-runs this seeding routine for each
/// side (oracle gets its own freshly-created file, the port gets its own freshly-created file) rather
/// than copying from one shared master file — chosen over sharing one file between both invocations
/// because <c>gain.rs</c>'s <c>--reset</c> path is the one command in this surface that mutates the
/// database, and giving every entry (not just <c>--reset</c>) its own pristine, independently-seeded
/// database per side removes any need to reason about invocation order or partial mutation for the
/// other 17 read-only entries — simplest-robust over cleverness, per the task brief's own guidance.
/// </para>
/// <para>
/// <b>Stdout only, forced non-interactive stdin.</b> Every entry passes <c>StdinContent = ""</c> (EOF
/// immediately), which matches <c>confirm_reset()</c>'s non-interactive default-No path and makes
/// <c>--reset</c> without <c>--yes</c> unnecessary to test here (it would simply print
/// <c>"Aborted.\n"</c> and do nothing — covered instead by unit tests). <c>CLAUDE_CONFIG_DIR</c> is
/// redirected to a non-existent per-entry temp path for both sides so <c>hook_check::status()</c>
/// resolves deterministically to <c>HookStatus.Ok</c> (the "no <c>.claude</c> dir" no-op path) on both
/// binaries — its output goes to stderr, which this harness (like every other parity file in this
/// project) does not capture or compare, but redirecting it anyway keeps the run fully hermetic and
/// avoids any real, if harmless, read of the developer's actual <c>~/.claude</c> directory.
/// </para>
/// </remarks>
public class GainParityTests
{
    private const double ParityThresholdPercent = 95.0;

    /// <summary>A single battery entry: a label, the CLI args after <c>gain</c>, and which seed dataset to copy for this entry.</summary>
    private sealed record Entry(string Label, string[] Args, SeedKind Seed);

    private enum SeedKind
    {
        /// <summary>18 <c>commands</c> rows, zero <c>parse_failures</c> rows.</summary>
        CommandsOnly,

        /// <summary>The same 18 <c>commands</c> rows plus 5 <c>parse_failures</c> rows.</summary>
        CommandsAndFailures,

        /// <summary>An empty (schema-only) database — used only for the zero-failures <c>--failures</c> entry.</summary>
        Empty,
    }

    [Fact]
    public async Task GainVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the gain-parity gate.");
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

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "gain-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"gain parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} stdoutMatch={r.StdoutMatches}");
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    // ===================== battery definition =====================

    private static IReadOnlyList<Entry> BuildBattery() =>
    [
        new("default summary (global scope)", [], SeedKind.CommandsOnly),
        new("--daily", ["--daily"], SeedKind.CommandsOnly),
        new("--weekly", ["--weekly"], SeedKind.CommandsOnly),
        new("--monthly", ["--monthly"], SeedKind.CommandsOnly),
        new("--all (daily+weekly+monthly tables)", ["--all"], SeedKind.CommandsOnly),
        new("--graph (default view + Daily Savings graph)", ["--graph"], SeedKind.CommandsOnly),
        new("--history (default view + Recent Commands)", ["--history"], SeedKind.CommandsOnly),
        new("--quota (default tier 20x)", ["--quota"], SeedKind.CommandsOnly),
        new("--quota -t 5x", ["--quota", "-t", "5x"], SeedKind.CommandsOnly),
        new("--quota -t pro", ["--quota", "-t", "pro"], SeedKind.CommandsOnly),
        new("--quota -t bogus (invalid tier, silent pro fallback)", ["--quota", "-t", "bogus"], SeedKind.CommandsOnly),
        new("--format json (summary only)", ["--format", "json"], SeedKind.CommandsOnly),
        new("--format json --all", ["--format", "json", "--all"], SeedKind.CommandsOnly),
        new("--format csv (no period flags -> empty stdout)", ["--format", "csv"], SeedKind.CommandsOnly),
        new("--format csv --all", ["--format", "csv", "--all"], SeedKind.CommandsOnly),
        new("--failures (zero failures)", ["--failures"], SeedKind.Empty),
        new("--failures (with failures)", ["--failures"], SeedKind.CommandsAndFailures),
        new("--reset --yes", ["--reset", "--yes"], SeedKind.CommandsOnly),
    ];

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-gain-parity-oracle-");
        var portTemp = CreateTempDir("rtk-gain-parity-port-");

        try
        {
            var oracleDbPath = Path.Combine(oracleTemp, "history.db");
            var portDbPath = Path.Combine(portTemp, "history.db");
            SeedDatabase(oracleDbPath, entry.Seed);
            SeedDatabase(portDbPath, entry.Seed);

            var oracleClaudeDir = Path.Combine(oracleTemp, "no-claude-dir");
            var portClaudeDir = Path.Combine(portTemp, "no-claude-dir");

            var oracleEnv = new Dictionary<string, string?>
            {
                ["RTK_DB_PATH"] = oracleDbPath,
                ["CLAUDE_CONFIG_DIR"] = oracleClaudeDir,
            };
            var portEnv = new Dictionary<string, string?>
            {
                ["RTK_DB_PATH"] = portDbPath,
                ["CLAUDE_CONFIG_DIR"] = portClaudeDir,
            };

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                oraclePath, ["gain", .. entry.Args], oracleTemp, oracleEnv, stdin: "");

            var portArgs = portPrefixArgs.Concat(["gain"]).Concat(entry.Args).ToArray();
            var (portStdout, portExit) = await ParityRunner.RunAsync(
                portFileName, portArgs, portTemp, portEnv, stdin: "");

            return new Result(
                entry.Label, Normalize(oracleStdout), Normalize(portStdout), oracleExit, portExit);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    // ===================== seed data =====================

    /// <summary>
    /// Creates a fresh SQLite file at <paramref name="dbPath"/> with the exact schema
    /// <c>Tracker.InitSchema</c>/Rust's <c>Tracker::new()</c> both create, then seeds it per
    /// <paramref name="seed"/> — entirely via raw SQL, never through either binary, so both sides of
    /// every entry start from byte-identical on-disk data.
    /// </summary>
    private static void SeedDatabase(string dbPath, SeedKind seed)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS commands (
                id INTEGER PRIMARY KEY,
                timestamp TEXT NOT NULL,
                original_cmd TEXT NOT NULL,
                rtk_cmd TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                saved_tokens INTEGER NOT NULL,
                savings_pct REAL NOT NULL,
                exec_time_ms INTEGER DEFAULT 0,
                project_path TEXT DEFAULT ''
            )
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_timestamp ON commands(timestamp)");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_project_path_timestamp ON commands(project_path, timestamp)");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS parse_failures (
                id INTEGER PRIMARY KEY,
                timestamp TEXT NOT NULL,
                raw_command TEXT NOT NULL,
                error_message TEXT NOT NULL,
                fallback_succeeded INTEGER NOT NULL DEFAULT 0
            )
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_pf_timestamp ON parse_failures(timestamp)");

        if (seed == SeedKind.Empty)
        {
            return;
        }

        foreach (var row in CommandSeedRows)
        {
            InsertCommand(connection, row.Timestamp, row.RtkCmd, row.InputTokens, row.OutputTokens, row.ExecTimeMs);
        }

        if (seed == SeedKind.CommandsAndFailures)
        {
            foreach (var row in FailureSeedRows)
            {
                InsertParseFailure(connection, row.Timestamp, row.RawCommand, row.ErrorMessage, row.FallbackSucceeded);
            }
        }
    }

    private static void InsertCommand(
        SqliteConnection connection, string timestamp, string rtkCmd, int inputTokens, int outputTokens, long execTimeMs)
    {
        var saved = Math.Max(0, inputTokens - outputTokens);
        var pct = inputTokens > 0 ? saved / (double)inputTokens * 100.0 : 0.0;

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO commands (timestamp, original_cmd, rtk_cmd, project_path, input_tokens, output_tokens, saved_tokens, savings_pct, exec_time_ms)
            VALUES ($ts, $orig, $rtk, '', $it, $ot, $st, $pct, $et)
            """;
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$orig", rtkCmd.Replace("rtk ", string.Empty, StringComparison.Ordinal));
        cmd.Parameters.AddWithValue("$rtk", rtkCmd);
        cmd.Parameters.AddWithValue("$it", inputTokens);
        cmd.Parameters.AddWithValue("$ot", outputTokens);
        cmd.Parameters.AddWithValue("$st", saved);
        cmd.Parameters.AddWithValue("$pct", pct);
        cmd.Parameters.AddWithValue("$et", execTimeMs);
        cmd.ExecuteNonQuery();
    }

    private static void InsertParseFailure(
        SqliteConnection connection, string timestamp, string rawCommand, string errorMessage, bool fallbackSucceeded)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO parse_failures (timestamp, raw_command, error_message, fallback_succeeded)
            VALUES ($ts, $raw, $err, $fb)
            """;
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$raw", rawCommand);
        cmd.Parameters.AddWithValue("$err", errorMessage);
        cmd.Parameters.AddWithValue("$fb", fallbackSucceeded ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 18 deterministic <c>commands</c> rows spanning April-July 2026 (fixed absolute dates, not tied
    /// to "now", so the battery's byte-exact expectations never drift with the calendar) across 10
    /// distinct <c>rtk_cmd</c> values — enough spread for daily/weekly/monthly grouping, a populated
    /// "By Command" table, a non-trivial "Daily Savings" graph, and 10 distinct "Recent Commands" rows
    /// spanning all three sign-glyph thresholds (▲/■/•).
    /// </summary>
    private static readonly (string Timestamp, string RtkCmd, int InputTokens, int OutputTokens, long ExecTimeMs)[] CommandSeedRows =
    [
        ("2026-04-06T09:15:00.000000+00:00", "rtk git status", 100, 20, 220),
        ("2026-04-06T09:20:00.000000+00:00", "rtk cargo build", 8000, 1200, 45000),
        ("2026-04-07T14:05:00.000000+00:00", "rtk cargo test", 12000, 900, 60500),
        ("2026-04-13T10:00:00.000000+00:00", "rtk gh pr list", 3000, 1800, 900),
        ("2026-05-04T08:30:00.000000+00:00", "rtk npm install", 15000, 14000, 32000),
        ("2026-05-04T08:45:00.000000+00:00", "rtk git status", 120, 25, 210),
        ("2026-05-05T11:10:00.000000+00:00", "rtk pytest", 9000, 2200, 15300),
        ("2026-05-11T09:00:00.000000+00:00", "rtk docker ps", 500, 150, 80),
        ("2026-06-01T08:00:00.000000+00:00", "rtk kubectl get pods", 700, 210, 95),
        ("2026-06-01T08:05:00.000000+00:00", "rtk go build", 4000, 3600, 21000),
        ("2026-06-02T13:30:00.000000+00:00", "rtk ls -la", 250, 40, 15),
        ("2026-06-15T09:45:00.000000+00:00", "rtk grep foo", 1800, 300, 210),
        ("2026-06-16T16:20:00.000000+00:00", "rtk tree", 600, 90, 40),
        ("2026-06-29T07:55:00.000000+00:00", "rtk cargo test", 11000, 950, 58000),
        ("2026-07-01T10:00:00.000000+00:00", "rtk git status", 110, 22, 230),
        ("2026-07-02T15:40:00.000000+00:00", "rtk cargo build", 8200, 1300, 46000),
        ("2026-07-03T09:05:00.000000+00:00", "rtk gh pr list", 3200, 1900, 950),
        ("2026-07-03T18:00:00.000000+00:00", "rtk npm install", 15500, 14200, 33000),
    ];

    /// <summary>
    /// 5 deterministic <c>parse_failures</c> rows for the non-zero <c>--failures</c> entry: one raw
    /// command repeated 3x (to exercise "Top Commands" frequency grouping/ordering) with mixed
    /// <c>fallback_succeeded</c> values (2 of 5 succeeded, for a non-trivial recovery-rate percentage).
    /// </summary>
    private static readonly (string Timestamp, string RawCommand, string ErrorMessage, bool FallbackSucceeded)[] FailureSeedRows =
    [
        ("2026-06-01T08:10:00.000000+00:00", "some --unknown-flag", "unrecognized flag", true),
        ("2026-06-02T09:00:00.000000+00:00", "cargo whatisthis", "unknown subcommand", false),
        ("2026-06-15T10:00:00.000000+00:00", "some --unknown-flag", "unrecognized flag", true),
        ("2026-06-29T11:00:00.000000+00:00", "weird | pipe thing", "parse error", false),
        ("2026-07-01T12:00:00.000000+00:00", "some --unknown-flag", "unrecognized flag", false),
    ];

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
        sb.AppendLine("# `rtk gain` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/GainParityTests.cs` " +
                      "(Phase 5 Task 6 acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (default summary, daily/weekly/monthly/all, " +
                      "graph, history, 4 quota variants, json/csv format x2 each, failures x2, reset)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry seeds a fresh, byte-identical SQLite database (raw SQL " +
                      "insert against the shared `commands`/`parse_failures` schema, never through either " +
                      "binary) into its own per-side temp copy, then runs both binaries with `RTK_DB_PATH` " +
                      "pointed at that copy and `CLAUDE_CONFIG_DIR` redirected to a non-existent per-entry " +
                      "path (so `hook_check::status()` resolves deterministically to `Ok` on both sides), with " +
                      "`StdinContent = \"\"` (forced non-interactive stdin). Stdout (CRLF/LF normalized, " +
                      "trailing-whitespace trimmed) and exit code are compared; stderr is not (matches this " +
                      "project's established parity-harness convention — see `HookParityTests`/`RewriteParityTests`). " +
                      "No `HostRealDirGuard`/junction machinery is used: `RTK_DB_PATH` fully redirects the " +
                      "tracking database on the oracle with no fallback to the real `%LOCALAPPDATA%\\rtk`, and " +
                      "`gain`'s own hook-status check (`hook_check::status()`) never writes to that directory — " +
                      "only `maybe_warn()` does, which `gain` never calls. This was verified empirically (a " +
                      "canary `rtk gain --format json` run with only `RTK_DB_PATH` set left every file under " +
                      "the real `%LOCALAPPDATA%\\rtk` with an identical mtime before and after) before writing " +
                      "this battery — see the class remarks in `GainParityTests.cs` for detail.");
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
        sb.AppendLine("| Entry | Rust exit | .NET exit | Stdout match | Verdict |");
        sb.AppendLine("|-------|-----------|-----------|--------------|---------|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| `{r.Label}` | {r.RustExit} | {r.PortExit} | {(r.StdoutMatches ? "yes" : "no")} | {r.Verdict} |");
        }

        sb.AppendLine();
        var mismatches = results.Where(r => !r.IsMatch).ToList();
        sb.AppendLine("## Mismatch details");
        sb.AppendLine();
        if (mismatches.Count == 0)
        {
            sb.AppendLine("None — every battery entry matched byte-exact stdout and an identical exit code.");
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
    private sealed record Result(string Label, string RustStdout, string PortStdout, int RustExit, int PortExit)
    {
        public bool StdoutMatches => RustStdout == PortStdout;

        public bool ExitsMatch => RustExit == PortExit;

        public bool IsMatch => StdoutMatches && ExitsMatch;

        public string Verdict => IsMatch
            ? "MATCH"
            : !StdoutMatches
                ? "MISMATCH (stdout)"
                : "MISMATCH (exit)";
    }
}
