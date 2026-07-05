using System.Diagnostics;
using System.Text;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 7 Task 2 acceptance gate: an oracle parity battery for <c>rtk env</c>
/// (<c>src/cmds/system/env_cmd.rs</c>) covering the default view, <c>--filter</c> (match and
/// zero-match), <c>--show-all</c>, and the &gt;100-byte truncation display.
/// </summary>
/// <remarks>
/// <para>
/// <b>Environment isolation is this battery's own distinct safety concern — not the same class as
/// Phase 4/5/6's real-file-system-mutation concerns.</b> <c>rtk env</c> reads and displays the
/// *actual* process environment (<c>std::env::vars()</c>/<see cref="Environment.GetEnvironmentVariables"/>).
/// Since both child processes (the oracle and RtkSharp) are, by default, spawned inheriting the real
/// ambient environment of whatever machine runs this test suite, two distinct risks exist if that
/// default is left unaddressed: (a) a committed <c>docs/parity/env-parity-report.md</c> could leak
/// real host environment content (real <c>PATH</c> entries, the real Windows username via
/// <c>USERPROFILE</c>, any real CI secrets present on the runner) directly into a file this project
/// commits to source control; (b) results could be non-deterministic across different developer
/// machines, since the bucket contents/counts/total would depend on whatever happens to be set on
/// that machine at that moment.
/// </para>
/// <para>
/// <b>Chosen approach: full <c>ProcessStartInfo.EnvironmentVariables.Clear()</c> then re-seed with a
/// small, fully-synthetic, deterministic set — never an overlay on top of the inherited environment.</b>
/// This is a deliberate deviation from every other parity battery in this project (see
/// <see cref="GainParityTests"/>/<see cref="PipeProxyRunParityTests"/>), which use
/// <c>ProcessExecutor</c>'s established "apply env vars as overlays on top of the real inherited
/// environment" convention (<c>ProcessExecutor.CreateStartInfo</c> only ever sets
/// <c>startInfo.Environment[key] = value</c> for the handful of keys it's given — it never clears).
/// That overlay convention is safe for every other parity battery because none of those commands echo
/// arbitrary ambient env-var content back to stdout; <c>env</c> is the one command in this port whose
/// entire contract is to print environment variable content, which makes the overlay convention
/// actively unsafe for this specific command (the ambient host environment would show through as
/// unlisted extra vars, defeating determinism and risking a content leak into the committed report).
/// This file therefore does not call into <see cref="ParityRunner"/> at all for process execution;
/// see <see cref="RunIsolatedAsync"/>, which builds its own <see cref="ProcessStartInfo"/>, explicitly
/// clears <see cref="ProcessStartInfo.EnvironmentVariables"/>, and re-seeds only the deliberately
/// chosen synthetic set below.
/// </para>
/// <para>
/// <b>Verified empirically, not just reasoned about from source, in three separate steps before this
/// battery was written:</b>
/// </para>
/// <para>
/// <b>Step 1 — a fully-cleared environment (no ambient vars at all, not even <c>SystemRoot</c>) is
/// sufficient for both binaries to start and run correctly.</b> A canary <c>rtk.exe env</c> and
/// <c>RtkSharp.exe env</c> invocation, each launched with <c>EnvironmentVariables.Clear()</c> and only
/// a single synthetic <c>CARGO_HOME</c> variable re-seeded (no <c>SystemRoot</c>, no <c>PATH</c>, no
/// <c>TEMP</c>), exited 0 on both sides with matching stdout. A second canary with the full 7-variable
/// synthetic set (see below) but still zero ambient vars, including no <c>SystemRoot</c>, also
/// succeeded identically on both binaries. This means no "bootstrap" exception for any real host
/// variable is needed — the environment handed to each child process is <i>entirely</i> synthetic,
/// with no real-host value surviving into either invocation.
/// </para>
/// <para>
/// <b>Step 2 — <c>env</c>'s own tracking side-effect (<c>TimedExecution.Track</c>/Rust's
/// <c>Tracker</c>) still resolves to the real <c>%LOCALAPPDATA%\rtk\history.db</c> if
/// <c>RTK_DB_PATH</c> is left unset, even under a fully-cleared environment.</b> A canary run with
/// only the 7 synthetic content vars (no <c>RTK_DB_PATH</c>) left the real
/// <c>%LOCALAPPDATA%\rtk\history.db</c> file's last-write time changed on both sides — confirming
/// that <c>dirs::data_local_dir()</c>/.NET's local-app-data resolution do not depend on any
/// environment variable on Windows (they call the Win32 known-folder API directly), so clearing the
/// environment does <i>not</i>, by itself, redirect this write. <c>RTK_DB_PATH</c> must therefore
/// still be explicitly set, exactly as <see cref="GainParityTests"/>/<see cref="PipeProxyRunParityTests"/>
/// already established for their own batteries.
/// </para>
/// <para>
/// <b>Step 3 — the hermeticity overrides themselves must not leak real host paths into the compared
/// stdout, and must use IDENTICAL literal string values on both sides despite pointing at two
/// different physical files.</b> This is a problem unique to this battery: every other parity file's
/// <c>RTK_DB_PATH</c>/<c>CLAUDE_CONFIG_DIR</c> values are never echoed to stdout, so an absolute,
/// per-side temp path (e.g. under <c>%TEMP%</c>, which embeds the real Windows username) is harmless
/// there. <c>env</c> displays every variable's value verbatim, including these two — and
/// <c>RTK_DB_PATH</c> substring-matches the <c>PATH</c> bucket (env_cmd.rs's PATH-bucket check is a
/// substring `Contains`, not an exact-key match) while <c>CLAUDE_CONFIG_DIR</c> substring-matches the
/// Tools bucket (contains "CLAUDE"), so both are always displayed whenever a battery entry has no
/// active <c>--filter</c> excluding them. Two requirements follow: the values must never contain a
/// real absolute host path (avoiding both the username-leak risk and, independently, an oracle/port
/// path mismatch if each side's absolute temp directory differed in length or content), and they must
/// be byte-identical between the oracle and RtkSharp invocations for the byte-exact stdout comparison
/// to be meaningful at all. The solution: both sides are given the exact same <i>relative</i>, literal,
/// synthetic values (<c>"rtk-env-parity-tracking.db"</c> and <c>"no-claude-dir"</c>) for these two
/// variables, and isolation is instead achieved by launching each side's process from its own distinct
/// per-side <see cref="ProcessStartInfo.WorkingDirectory"/> (which is not itself an environment
/// variable and is therefore never part of the compared stdout) — the relative path then resolves to
/// two different physical files on disk, one per side, while the literal environment-variable string
/// compared between the two invocations is identical. This was verified empirically: after running
/// both binaries this way, a full recursive listing of the real <c>%LOCALAPPDATA%\rtk</c> directory
/// showed zero changed files/timestamps, and each side's own temp working directory contained its own
/// independent <c>rtk-env-parity-tracking.db</c> file, confirming genuine per-side isolation despite
/// the shared literal value.
/// </para>
/// <para>
/// <b>The synthetic content environment (identical for every battery entry; only the CLI args vary):</b>
/// <c>PATH</c> = a semicolon-joined, Windows-shaped, under-100-byte value
/// (<c>"C:\synthetic\bin1;C:\synthetic\bin2;C:\synthetic\bin3"</c>) — deliberately kept
/// <i>under</i> the 100-byte truncation threshold so this entry isolates the PATH-bucket
/// literal-<c>':'</c>-split quirk on its own, uncoupled from the truncation interaction (which Task 1's
/// own unit tests already cover directly via <c>EnvCommandTests</c>'s "truncate-then-split PATH" case;
/// re-deriving that same interaction here at the parity level would be redundant, not additive).
/// <c>CARGO_HOME</c> (lang bucket), <c>AWS_REGION</c> (cloud bucket, non-sensitive),
/// <c>AWS_SECRET_ACCESS_KEY</c> (cloud bucket <i>and</i> sensitive — Task 1's implementer discovered
/// this dual membership; a bare <c>API_KEY</c> would instead be silently dropped from every bucket
/// since it matches no bucket keyword, which is why this battery deliberately does not use a bare
/// <c>API_KEY</c> as its sensitive-var exemplar), <c>EDITOR</c> (tool bucket), <c>HOME</c>
/// (other/interesting bucket, via <c>StartsWith</c>), and <c>NODE_OPTIONS</c> set to a 130-<c>x</c>-character
/// ASCII value (lang bucket via the "NODE" substring, and &gt;100 UTF-8 bytes since every character is
/// single-byte ASCII) — this is the dedicated non-<c>PATH</c> long-value entry proving the truncation
/// display (first 50 chars + <c>"... (130 chars)"</c>).
/// </para>
/// <para>
/// <b>Stdout only, forced non-interactive stdin, exit code compared.</b> Every entry closes the
/// child's stdin immediately (no piped content — <c>env</c> never reads stdin). Stdout (CRLF/LF
/// normalized, trailing-whitespace trimmed) and exit code are compared; stderr is not (this project's
/// established convention — see <see cref="GainParityTests"/>/<see cref="HookParityTests"/>).
/// </para>
/// </remarks>
public class EnvParityTests
{
    /// <summary>
    /// The synthetic "content" environment shared by every battery entry: one variable per bucket
    /// (PATH/lang/cloud/tool/other) plus a dual cloud-and-sensitive variable and a dedicated
    /// non-PATH long value for the truncation entry. See the class remarks for the rationale behind
    /// each specific value.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SyntheticContentEnv = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PATH"] = "C:\\synthetic\\bin1;C:\\synthetic\\bin2;C:\\synthetic\\bin3",
        ["CARGO_HOME"] = "C:\\synthetic\\cargo",
        ["AWS_REGION"] = "us-east-1",
        ["AWS_SECRET_ACCESS_KEY"] = "AKIASYNTH1234567890EXAMPLEVALUE",
        ["EDITOR"] = "vim",
        ["HOME"] = "C:\\synthetic\\home",
        ["NODE_OPTIONS"] = new string('x', 130),
    };

    // Deliberately relative, literal, synthetic values - never a real absolute host path. See the
    // class remarks (Step 3) for why these must be byte-identical strings on both sides while still
    // resolving to two independent physical files via each side's own WorkingDirectory.
    private const string RelativeDbPathValue = "rtk-env-parity-tracking.db";
    private const string RelativeClaudeConfigDirValue = "no-claude-dir";

    /// <summary>A single battery entry: a label and the CLI args after <c>env</c>.</summary>
    private sealed record Entry(string Label, string[] Args);

    [Fact]
    public async Task EnvVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the env-parity gate.");
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

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "env-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        // Per the task brief: structure the assertion so it fails clearly and specifically on any
        // one mismatching entry, rather than tolerating a mismatch under a loose aggregate
        // percentage/count threshold. No mismatch is anticipated for this command (Task 1 was fully
        // reviewed before this battery was written) - the expected-mismatch set is therefore empty,
        // and any entry landing outside it is reported by name.
        var mismatchLabels = results.Where(r => !r.IsMatch).Select(r => r.Label).ToList();
        var detail = new StringBuilder();
        detail.AppendLine($"env parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} stdoutMatch={r.StdoutMatches}");
        }

        Assert.True(
            mismatchLabels.Count == 0,
            $"Unexpected mismatch(es) - no gap is disclosed for `rtk env`, so any mismatch here is a " +
            $"real regression: {string.Join(", ", mismatchLabels)}.\n{detail}");
    }

    // ===================== battery definition =====================

    private static IReadOnlyList<Entry> BuildBattery() =>
    [
        new("default view (synthetic environment: one var per bucket + sensitive cloud var masked)", []),
        new("--filter AWS (matches two seeded cloud vars, summary line suppressed)", ["--filter", "AWS"]),
        new("--filter zero matches (no bucket headers, summary line suppressed)", ["--filter", "ZZZ_NO_MATCH_TOKEN"]),
        new("--show-all (sensitive cloud var shown unmasked)", ["--show-all"]),
        new("--filter NODE_OPTIONS (isolates >100-byte truncation display, summary line suppressed)", ["--filter", "NODE_OPTIONS"]),
    ];

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-env-parity-oracle-");
        var portTemp = CreateTempDir("rtk-env-parity-port-");

        try
        {
            var env = new Dictionary<string, string>(SyntheticContentEnv, StringComparer.Ordinal)
            {
                ["RTK_DB_PATH"] = RelativeDbPathValue,
                ["CLAUDE_CONFIG_DIR"] = RelativeClaudeConfigDirValue,
                ["RTK_TEE"] = "0",
            };

            var (oracleStdout, _, oracleExit) = await RunIsolatedAsync(
                oraclePath, ["env", .. entry.Args], oracleTemp, env);

            var portArgs = portPrefixArgs.Concat(["env"]).Concat(entry.Args).ToArray();
            var (portStdout, _, portExit) = await RunIsolatedAsync(
                portFileName, portArgs, portTemp, env);

            return new Result(
                entry.Label, Normalize(oracleStdout), Normalize(portStdout), oracleExit, portExit);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    /// <summary>
    /// Runs a single binary once with a FULLY CLEARED and re-seeded environment (never an overlay on
    /// top of the real inherited environment - see this file's class remarks for why <c>env</c>
    /// specifically requires this, unlike every other parity battery in this project). Deliberately
    /// bypasses <see cref="ParityRunner"/>/<c>ProcessExecutor</c>, since neither supports clearing the
    /// inherited environment - both only ever overlay additional keys on top of it.
    /// </summary>
    /// <param name="fileName">Executable to run (or the <c>dotnet</c> muxer).</param>
    /// <param name="args">Arguments to pass to the executable.</param>
    /// <param name="workingDirectory">
    /// The child's working directory. Also where <see cref="RelativeDbPathValue"/>/
    /// <see cref="RelativeClaudeConfigDirValue"/> physically resolve to, since both are relative
    /// paths (see class remarks, Step 3).
    /// </param>
    /// <param name="environment">
    /// The COMPLETE environment to give the child process - this is re-seeded from scratch after
    /// clearing, not merged with the inherited environment.
    /// </param>
    /// <returns>The captured stdout, stderr, and exit code.</returns>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunIsolatedAsync(
        string fileName, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // The safety-critical line: clear whatever the child would otherwise inherit from this test
        // runner's own real ambient environment, then re-seed only the deliberately chosen synthetic
        // set. No overlay - a full replacement.
        startInfo.EnvironmentVariables.Clear();
        foreach (var (key, value) in environment)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return (stdout, stderr, process.ExitCode);
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
        sb.AppendLine("# `rtk env` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/EnvParityTests.cs` " +
                      "(Phase 7 Task 2 acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (default view, --filter match, " +
                      "--filter zero-match, --show-all, --filter isolating >100-byte truncation)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: every entry runs both binaries against a FULLY CLEARED and " +
                      "re-seeded child-process environment (`ProcessStartInfo.EnvironmentVariables.Clear()` " +
                      "then explicit re-seed - never an overlay on the real inherited environment, unlike " +
                      "every other parity battery in this project), covering one variable per bucket " +
                      "(PATH/lang/cloud/tool/other) plus a dual cloud-and-sensitive variable and a dedicated " +
                      "non-PATH >100-byte value for the truncation entry, with `RTK_DB_PATH`/" +
                      "`CLAUDE_CONFIG_DIR` set to identical relative literal values on both sides (isolated " +
                      "per-side via each process's own `WorkingDirectory`, not via differing environment-" +
                      "variable content, since `env` echoes every variable's value verbatim to stdout). " +
                      "Stdout (CRLF/LF normalized, trailing-whitespace trimmed) and exit code are compared; " +
                      "stderr is not (project convention — see `GainParityTests`/`HookParityTests`). This " +
                      "environment-isolation approach was verified empirically (a fully-cleared canary run " +
                      "left the real `%LOCALAPPDATA%\\rtk` directory completely unchanged, and each side's " +
                      "relative `RTK_DB_PATH` resolved to two independent physical files) — see the class " +
                      "remarks in `EnvParityTests.cs` for the full empirical detail.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|--------|--------|");
        sb.AppendLine($"| **Overall parity (headline)** | **{percent:F1}%** |");
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
