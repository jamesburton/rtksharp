using System.Text;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 6 Task 7 acceptance gate: an oracle parity battery for <c>rtk pipe</c>, <c>rtk proxy</c>,
/// <c>rtk run</c>, <c>rtk err</c>, and <c>rtk test</c> — the five commands ported in Phase 6
/// (<c>src/cmds/system/pipe_cmd.rs</c>, <c>main.rs</c>'s <c>Proxy</c>/<c>Run</c> dispatch arms, and
/// <c>src/cmds/rust/runner.rs</c>'s generic <c>Err</c>/<c>Test</c> runner).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hermeticity via <c>RTK_DB_PATH</c> + <c>CLAUDE_CONFIG_DIR</c> + <c>RTK_TEE=0</c> — no
/// <c>HostRealDirGuard</c> needed, empirically verified (not just reasoned about from source).</b>
/// Every entry in this battery sets three environment overrides on both sides: <c>RTK_DB_PATH</c>
/// (redirects <c>Tracker</c>'s SQLite database — used by <c>proxy</c>/<c>err</c>/<c>test</c>'s
/// <c>TimedExecution.Track</c> calls; a no-op for <c>pipe</c>/<c>run</c>, which never track), a
/// per-entry non-existent <c>CLAUDE_CONFIG_DIR</c> path (matching <see cref="GainParityTests"/>'s
/// convention), and <c>RTK_TEE=0</c> (disables <c>err</c>/<c>test</c>'s tee-to-disk write, which
/// otherwise resolves to <c>%LOCALAPPDATA%\rtk\tee</c> via <c>Tee.GetTeeDir</c>/Rust's
/// <c>get_tee_dir</c> unless <c>RTK_TEE_DIR</c> or this kill switch is set).
/// </para>
/// <para>
/// This combination was empirically canary-tested against the real oracle before writing this
/// battery, per the Phase 4/5 precedent of never trusting a hermeticity claim from source-reading
/// alone:
/// </para>
/// <para>
/// <b>Canary 1 (proxy):</b> unlike <c>rtk gain</c> (explicitly excluded from
/// <c>hooks::hook_check::maybe_warn()</c> at <c>main.rs</c>:1484), <c>rtk proxy</c> is <i>not</i>
/// excluded — <c>main.rs</c>:1484's guard only skips <c>maybe_warn()</c> for
/// <c>Commands::Gain</c>, so <c>proxy</c> (and <c>run</c>/<c>pipe</c>/<c>err</c>/<c>test</c>) all
/// reach it unconditionally. This could plausibly have meant <c>proxy</c> touches
/// <c>%LOCALAPPDATA%\rtk\.hook_warn_last</c> in a way <c>gain</c> does not. Reading
/// <c>hook_check.rs</c>'s <c>check_and_warn()</c> (line 96-97) shows the marker file is only ever
/// touched on the <c>HookStatus::Missing</c>/<c>Outdated</c> branches — the <c>Ok</c> branch returns
/// immediately, before the marker path is even resolved — and redirecting <c>CLAUDE_CONFIG_DIR</c>
/// to a non-existent directory makes <c>status()</c> resolve to <c>Ok</c> deterministically (the
/// same mechanism <see cref="GainParityTests"/> already relies on), so the asymmetry turns out not
/// to matter in practice. This was verified empirically, not just argued from source: a real
/// <c>rtk.exe proxy cmd /C "echo hello-canary"</c> invocation was run against the actual
/// <c>target/release/rtk.exe</c> oracle with only <c>RTK_DB_PATH</c> and a non-existent
/// <c>CLAUDE_CONFIG_DIR</c> set (nothing else redirected) — a full recursive listing of the real
/// <c>%LOCALAPPDATA%\rtk</c> directory (including <c>history.db</c>, <c>.hook_warn_last</c>, and
/// every file under <c>tee\</c>) showed byte-identical last-write timestamps before and after the
/// canary run. No <c>HostRealDirGuard</c>/junction machinery is therefore applied to <c>proxy</c>'s
/// entries.
/// </para>
/// <para>
/// <b>Canary 2 (err's tee-to-disk):</b> <c>err</c>/<c>test</c> additionally call
/// <c>Tee.TeeAndHint</c>/Rust's <c>core::tee::tee_raw</c>, which (unlike <c>gain</c>'s read-only
/// hook-status check) genuinely writes to <c>%LOCALAPPDATA%\rtk\tee</c> by default whenever the
/// captured raw output is non-trivial and the command's exit code is non-zero (the default
/// <c>TeeMode.Failures</c>). A second canary — <c>rtk.exe err cmd /C "echo error: something failed"</c>
/// run with <c>RTK_TEE=0</c> added on top of the same two overrides — confirmed the real
/// <c>%LOCALAPPDATA%\rtk\tee</c> directory's file count was unchanged before and after (both
/// <c>rtk_tee.rs</c>'s <c>tee_raw</c> and <c>Tee.TeeRaw</c> check <c>RTK_TEE == "0"</c> as their very
/// first line, verbatim on both sides). Every entry in this file sets <c>RTK_TEE=0</c> unconditionally
/// for exactly this reason, even for <c>pipe</c>/<c>proxy</c>/<c>run</c> entries that never call tee
/// themselves — cheap insurance, matching this project's "assume unsafe until empirically proven
/// otherwise" bar (<c>ConfigFilterParityTests</c>'s class remarks).
/// </para>
/// <para>
/// <b>Stdout only, forced non-interactive stdin (except where <c>pipe</c> itself needs stdin
/// content).</b> Every non-<c>pipe</c> entry passes <c>StdinContent = ""</c> (immediate EOF); every
/// <c>pipe</c> entry passes its scenario's actual stdin payload via <c>StdinContent</c>, matching
/// this file's need to control <c>pipe</c>'s stdin-driven dispatch (the one command in this battery
/// that genuinely reads from stdin). Exit codes are compared for every entry; stderr is never
/// compared (project convention — see <see cref="GainParityTests"/>/<see cref="HookParityTests"/>).
/// </para>
/// <para>
/// <b>The <c>pipe</c> ecosystem-filter-delegation gap is CLOSED.</b> <see cref="PipeCommand"/>'s
/// <c>ResolveFilter</c>/<c>AutoDetectFilter</c> used to fall back to <c>IdentityFilter</c> passthrough
/// for <c>cargo-test</c>/<c>pytest</c>/<c>go-test</c>/<c>go-build</c>/<c>tsc</c>/<c>vitest</c>/
/// <c>mypy</c>/<c>ruff-*</c>/<c>prettier</c>/<c>log</c> because no ecosystem filter module existed yet
/// for those languages in this port; now that Rust/Cargo, Python, Go, and JS are all ported, every one
/// of those aliases delegates to its real ecosystem filter, matching the Rust oracle
/// (<c>docs/parity/compatibility-ledger.md</c>'s "RESOLVED" row for this gap). The auto-detect entries
/// below use the grep-line-shape signature (<c>GrepWrapper</c>) and the path-listing shape signature
/// (<c>FindWrapper</c>) — both fully, symmetrically ported with no ecosystem dependency in either
/// codebase — and the named-filter-alias entries include both a genuinely-delegating alias
/// (<c>git-log</c>, which drives <see cref="RtkSharp.Commands.Git.GitCommand"/>'s already-ported
/// filter logic) and the formerly-disclosed-gap alias (<c>cargo-test</c>, now also genuinely
/// delegating to <c>RtkSharp.Filters.Commands.Rust.CargoFilters.FilterCargoTest</c>) so this battery exercises the
/// named-filter-alias path with two real delegating examples, not an identity-passthrough stand-in.
/// </para>
/// </remarks>
public class PipeProxyRunParityTests
{
    private const double ParityThresholdPercent = 95.0;

    /// <summary>
    /// The exact set of entry labels this battery is known — and allowed — to mismatch. Empty: the
    /// <c>pipe -f cargo-test</c> ecosystem-filter-delegation gap that previously lived here is now
    /// CLOSED (see this class's remarks and <c>docs/parity/compatibility-ledger.md</c>'s "RESOLVED"
    /// row), so every entry in this battery is expected to match the oracle exactly. This must be an
    /// exact set match, not merely a count/percentage: any mismatch is a real regression, asserted
    /// explicitly rather than folded into a single threshold check.
    /// </summary>
    private static readonly IReadOnlySet<string> ExpectedMismatchLabels =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>A single battery entry: a label, the full CLI args (including the leading verb), and optional stdin content.</summary>
    private sealed record Entry(string Label, string[] Args, string Stdin);

    [Fact]
    public async Task PipeProxyRunErrTest_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the pipe/proxy/run parity gate.");
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

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "pipe-proxy-run-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"pipe/proxy/run/err/test parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} stdoutMatch={r.StdoutMatches}");
        }

        // Pin mismatches to an exact, empty set rather than a raw percentage/count: a percentage
        // threshold alone would tolerate any one mismatch, silently masking a real regression.
        // Assert the exact set of mismatching labels instead (empty now that the cargo-test gap
        // that used to live in ExpectedMismatchLabels has closed).
        var actualMismatchLabels = new HashSet<string>(
            results.Where(r => !r.IsMatch).Select(r => r.Label), StringComparer.Ordinal);

        var unexpectedMismatches = actualMismatchLabels.Except(ExpectedMismatchLabels).ToList();
        Assert.True(
            unexpectedMismatches.Count == 0,
            "Unexpected mismatch(es) against the oracle — this is a real regression: " +
            $"{string.Join(", ", unexpectedMismatches)}.\n{detail}");

        var nowPassingGaps = ExpectedMismatchLabels.Except(actualMismatchLabels).ToList();
        Assert.True(
            nowPassingGaps.Count == 0,
            "Expected disclosed-gap entry(ies) now pass against the oracle. Update " +
            "ExpectedMismatchLabels in this file and docs/parity/compatibility-ledger.md to reflect " +
            $"the new state: {string.Join(", ", nowPassingGaps)}.\n{detail}");

        // Belt-and-braces: the exact-set assertions above already pin identity, but keep the
        // percentage floor too as a coarse sanity check consistent with every other battery file.
        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    // ===================== battery definition =====================

    private static IReadOnlyList<Entry> BuildBattery()
    {
        var list = new List<Entry>();

        // ---------- pipe ----------
        list.Add(new("pipe --passthrough", ["pipe", "--passthrough"], "raw stdin bytes, unchanged\nline two\n"));

        list.Add(new(
            "pipe -f git-log (genuinely delegating alias)",
            ["pipe", "-f", "git-log"],
            "abc1234 Fix the thing\ndef5678 Add the other thing\n"));

        list.Add(new(
            "pipe -f cargo-test (genuinely delegating alias)",
            ["pipe", "-f", "cargo-test"],
            "test result: ok. 3 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out\n"));

        list.Add(new(
            "pipe -f grep (mini-filter, grouped/capped)",
            ["pipe", "-f", "grep"],
            "src/foo.rs:10:let x = 1;\nsrc/foo.rs:12:let y = 2;\nsrc/bar.rs:4:println!(\"hi\");\n"));

        list.Add(new(
            "pipe -f find (mini-filter, grouped/capped)",
            ["pipe", "-f", "find"],
            "src/foo.rs\nsrc/bar.rs\ntests/baz.rs\n"));

        list.Add(new(
            "pipe unknown filter -> error",
            ["pipe", "-f", "not-a-real-filter"],
            "irrelevant\n"));

        list.Add(new(
            "pipe auto-detect: grep-line shape",
            ["pipe"],
            "src/foo.rs:10:let x = 1;\nsrc/foo.rs:12:let y = 2;\nsrc/bar.rs:4:println!(\"hi\");\n"));

        list.Add(new(
            "pipe auto-detect: path-listing shape",
            ["pipe"],
            "./src/foo.rs\n./src/bar.rs\n./tests/baz.rs\n"));

        list.Add(new(
            "pipe oversized stdin -> hard bail",
            ["pipe"],
            new string('a', 10_485_761)));

        // ---------- proxy ----------
        list.Add(new("proxy simple command (single arg, no spaces, no resplit)", ["proxy", "hostname"], ""));

        list.Add(new(
            "proxy single-raw-arg quote-split (issue #388)",
            ["proxy", "cmd /C echo issue388-resplit"],
            ""));

        list.Add(new(
            "proxy multi-arg direct (no resplit)",
            ["proxy", "cmd", "/C", "echo multiarg-direct"],
            ""));

        list.Add(new("proxy empty args -> error", ["proxy"], ""));

        list.Add(new(
            "proxy exit-code propagation (failing wrapped command)",
            ["proxy", "cmd", "/C", "exit", "3"],
            ""));

        // ---------- run ----------
        list.Add(new("run -c form", ["run", "-c", "echo run-c-form"], ""));

        list.Add(new("run positional-args form", ["run", "echo", "run-positional-form"], ""));

        list.Add(new("run empty command -> no-op", ["run"], ""));

        list.Add(new(
            "run exit-code propagation",
            ["run", "-c", "exit 7"],
            ""));

        // ---------- err ----------
        list.Add(new(
            "err: synthetic command producing a Rust-style error signature",
            ["err", "cmd", "/C", "echo error: something broke badly"],
            ""));

        list.Add(new(
            "err: synthetic command with no errors",
            ["err", "cmd", "/C", "echo all good, nothing to see here"],
            ""));

        // ---------- test ----------
        // Ecosystem detection matches against the literal joined COMMAND TEXT, not output shape
        // (runner.rs's extract_test_summary) - "rem cargo test"/"rem pytest" are inert cmd.exe
        // comments appended purely so the joined command string contains the detection substring.
        list.Add(new(
            "test: synthetic cargo-test-shaped output",
            [
                "test", "cmd", "/C",
                "echo test result: ok. 3 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s",
                "&", "rem", "cargo", "test",
            ],
            ""));

        list.Add(new(
            "test: synthetic pytest-shaped output",
            [
                "test", "cmd", "/C",
                "echo === test session starts === && echo 4 passed, 1 failed in 0.12s && echo FAILED test_foo.py::test_bar",
                "&", "rem", "pytest",
            ],
            ""));

        return list;
    }

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-p6-parity-oracle-");
        var portTemp = CreateTempDir("rtk-p6-parity-port-");

        try
        {
            var oracleDbPath = Path.Combine(oracleTemp, "history.db");
            var portDbPath = Path.Combine(portTemp, "history.db");

            var oracleClaudeDir = Path.Combine(oracleTemp, "no-claude-dir");
            var portClaudeDir = Path.Combine(portTemp, "no-claude-dir");

            var oracleEnv = new Dictionary<string, string?>
            {
                ["RTK_DB_PATH"] = oracleDbPath,
                ["CLAUDE_CONFIG_DIR"] = oracleClaudeDir,
                ["RTK_TEE"] = "0",
            };
            var portEnv = new Dictionary<string, string?>
            {
                ["RTK_DB_PATH"] = portDbPath,
                ["CLAUDE_CONFIG_DIR"] = portClaudeDir,
                ["RTK_TEE"] = "0",
            };

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                oraclePath, entry.Args, oracleTemp, oracleEnv, stdin: entry.Stdin);

            var portArgs = portPrefixArgs.Concat(entry.Args).ToArray();
            var (portStdout, portExit) = await ParityRunner.RunAsync(
                portFileName, portArgs, portTemp, portEnv, stdin: entry.Stdin);

            return new Result(
                entry.Label, Normalize(oracleStdout), Normalize(portStdout), oracleExit, portExit);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
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
        sb.AppendLine("# `rtk pipe`/`rtk proxy`/`rtk run`/`rtk err`/`rtk test` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/PipeProxyRunParityTests.cs` " +
                      "(Phase 6 Task 7 acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (pipe passthrough/aliases/auto-detect/" +
                      "unknown-filter/oversized-stdin, proxy simple/quote-split/multi-arg/empty-args/exit-code, " +
                      "run -c/positional/empty/exit-code, err error/no-error, test cargo/pytest)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry runs both binaries with `RTK_DB_PATH` pointed at a " +
                      "fresh per-side temp file, `CLAUDE_CONFIG_DIR` redirected to a non-existent per-entry " +
                      "path (so `hook_check::status()`/`HookCheck.Status()` resolve deterministically to " +
                      "`Ok`, matching `GainParityTests`' established convention), and `RTK_TEE=0` (disables " +
                      "`err`/`test`'s tee-to-disk write). Every entry except `pipe`'s own stdin-driven " +
                      "scenarios passes `StdinContent = \"\"` (forced non-interactive stdin); `pipe` entries " +
                      "pass their scenario's actual stdin payload. Stdout (CRLF/LF normalized, trailing-" +
                      "whitespace trimmed) and exit code are compared; stderr is not (project convention — " +
                      "see `GainParityTests`/`HookParityTests`). No `HostRealDirGuard`/junction machinery is " +
                      "used: both empirical canaries (a real `rtk proxy` run and a real `rtk err` run against " +
                      "the actual oracle, with only the three env overrides above set) left the real " +
                      "`%LOCALAPPDATA%\\rtk` directory's contents and timestamps completely unchanged — see " +
                      "the class remarks in `PipeProxyRunParityTests.cs` for the full empirical detail.");
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
