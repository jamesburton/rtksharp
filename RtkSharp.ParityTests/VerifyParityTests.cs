using System.Text;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Final-review acceptance gate: an oracle parity battery for <c>rtk verify</c>'s full stdout
/// surface (hook-integrity check plus the TOML inline-test battery), comparing stdout and exit code
/// against the real Rust oracle. Follows the exact hermetic pattern established in
/// <see cref="InitParityTests"/> (temp CWD, <c>CLAUDE_CONFIG_DIR</c> environment overlay,
/// <c>StdinContent = ""</c>, oracle-vs-port comparison, auto-written Markdown report) — this file
/// adds no new plumbing, it reuses that entry's approach for a distinct verb.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Phase 4 Task 5 closed the previously-ledgered gap where bare <c>rtk verify</c>
/// omitted the oracle's unconditional <c>hooks::verify_cmd::run(None, require_all)</c> inline-test
/// battery (see <c>docs/parity/compatibility-ledger.md</c>). Both battery entries below now compare
/// the <b>full</b> stdout byte-for-byte (after masking the sandbox temp path), matching
/// <see cref="InitParityTests"/>'s comparison strictness — there is no longer a documented
/// divergence to work around. Neither entry seeds a project-local <c>.rtk/filters.toml</c>, so both
/// sides run only the built-in inline-test battery (deterministic count on both sides).
/// </para>
/// <para>
/// <b>Battery.</b> (a) "native binary hook registered": <c>settings.json</c> contains
/// <c>rtk hook claude</c> and no legacy script — both sides take the "PASS native binary hook
/// registered" branch. (b) "not installed": no hook registered at all (empty <c>.claude</c>
/// directory) — both sides take the "SKIP RTK hook not installed" branch.
/// </para>
/// </remarks>
public class VerifyParityTests
{
    private const double ParityThresholdPercent = 95.0;

    /// <summary>A single battery entry: a label, optional settings.json seed content, and the args to run.</summary>
    private sealed record Entry(string Label, string? SettingsJsonSeed, string[] Args);

    [Fact]
    public async Task VerifyVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the verify-parity gate.");
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
        var results = new List<VerifyResult>();

        foreach (var entry in battery)
        {
            results.Add(await RunEntryAsync(entry, oraclePath, portFileName, portPrefixArgs));
        }

        var matched = results.Count(r => r.IsMatch);
        var total = results.Count;
        var percent = total == 0 ? 100.0 : matched * 100.0 / total;

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "verify-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"verify parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} " +
                $"stdoutMatch={r.StdoutMatches}");
        }

        Assert.True(percent >= ParityThresholdPercent, detail.ToString());
    }

    /// <summary>Builds the 2-entry battery: the native-binary-hook-registered PASS case and the not-installed SKIP case.</summary>
    private static IReadOnlyList<Entry> BuildBattery() =>
    [
        new(
            "native binary hook registered (PASS)",
            SettingsJsonSeed: """{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"rtk hook claude"}]}]}}""",
            Args: ["verify"]),
        new(
            "not installed (SKIP)",
            SettingsJsonSeed: null,
            Args: ["verify"]),
    ];

    // ===================== execution =====================

    private static async Task<VerifyResult> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-verify-parity-oracle-");
        var portTemp = CreateTempDir("rtk-verify-parity-port-");

        try
        {
            var oracleClaudeDir = Path.Combine(oracleTemp, ".claude");
            var portClaudeDir = Path.Combine(portTemp, ".claude");
            Directory.CreateDirectory(oracleClaudeDir);
            Directory.CreateDirectory(portClaudeDir);

            if (entry.SettingsJsonSeed is not null)
            {
                File.WriteAllText(Path.Combine(oracleClaudeDir, "settings.json"), entry.SettingsJsonSeed);
                File.WriteAllText(Path.Combine(portClaudeDir, "settings.json"), entry.SettingsJsonSeed);
            }

            var oracleEnv = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = oracleClaudeDir };
            var portEnv = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = portClaudeDir };

            var (oracleStdout, oracleExit) =
                await ParityRunner.RunAsync(oraclePath, entry.Args, oracleTemp, oracleEnv, stdin: "");

            var portArgs = portPrefixArgs.Concat(entry.Args).ToArray();
            var (portStdout, portExit) =
                await ParityRunner.RunAsync(portFileName, portArgs, portTemp, portEnv, stdin: "");

            var maskedOracleStdout = Normalize(MaskPaths(oracleStdout, oracleTemp));
            var maskedPortStdout = Normalize(MaskPaths(portStdout, portTemp));

            return new VerifyResult(
                entry.Label, maskedOracleStdout, maskedPortStdout, oracleExit, portExit);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    // ===================== helpers (mirrors InitParityTests' hermeticity plumbing) =====================

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
        IReadOnlyList<VerifyResult> results,
        double percent,
        int matched,
        int total,
        string oraclePath,
        string portFileName,
        string[] portPrefixArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("# `rtk verify` Hook-Integrity Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/VerifyParityTests.cs` " +
                      "(final-review acceptance gate, Finding 2). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry runs both binaries in a fresh, isolated temp directory " +
                      "with `CLAUDE_CONFIG_DIR` redirected for both sides and `StdinContent = \"\"` (forced " +
                      "non-interactive stdin). Full stdout is compared byte-for-byte (after masking the " +
                      "sandbox temp path) — the oracle's TOML inline-filter self-test block is included on " +
                      "both sides now that Phase 4 Task 5 closed the previously-ledgered gap (see " +
                      "`docs/parity/compatibility-ledger.md`).");
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
            sb.AppendLine("None — every battery entry matched on full stdout and exit code.");
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

    /// <summary>Escapes a cell value for a Markdown table (pipes, newlines, empties).</summary>
    private static string Md(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "*(empty)*";
        }

        return "`" + s.Replace("\n", " ").Replace("|", "\\|") + "`";
    }

    /// <summary>The parity outcome for a single verify battery entry.</summary>
    private sealed record VerifyResult(string Label, string RustStdout, string PortStdout, int RustExit, int PortExit)
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
