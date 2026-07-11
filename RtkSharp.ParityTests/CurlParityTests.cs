using System.Text;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Oracle parity battery for <c>rtk curl</c> (<c>src/cmds/cloud/curl_cmd.rs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero network calls — every entry targets curl's own <c>file://</c> scheme against a local
/// fixture file.</b> Real HTTP requests in an automated battery would be non-deterministic (depends
/// on an external service being up, its exact response shape, and network availability in CI) and
/// unsafe by this project's own established caution (see <c>TelemetryParityTests</c>'s remarks on
/// avoiding real network calls in parity code). <c>curl</c> reading a local file via
/// <c>file:///&lt;path&gt;</c> exercises the exact same process-spawn/byte-capture/exit-code pipeline
/// as a real HTTP request with zero non-determinism and no external dependency — this mirrors Rust's
/// own <c>curl_cmd.rs</c> test suite, which never invokes curl or the network at all (only the pure
/// <c>filter_curl_output</c>/<c>is_binary</c> functions are unit-tested upstream).
/// </para>
/// <para>
/// <b>The truncate-with-tee-hint branch is untestable at the process level, on both sides, by
/// construction — not a gap in this battery.</b> <c>filter_curl_output</c>'s truncation path only
/// triggers when stdout is a real interactive terminal (<c>is_tty</c>); any automated harness that
/// captures a child process's stdout (this one included) necessarily redirects it, so both the oracle
/// and the port always see <c>is_tty = false</c> here, taking the "pipes need the full body" passthrough
/// branch regardless of body size — exactly mirroring how a real user piping <c>rtk curl ... | less</c>
/// behaves. The truncation/tee-hint logic itself is fully covered by <c>CurlCommandTests.cs</c>'s
/// direct <c>FilterCurlOutput(raw, isTty: true)</c> unit tests instead, matching Rust's own test
/// suite's approach.
/// </para>
/// </remarks>
public class CurlParityTests
{
    private const double ParityThresholdPercent = 100.0;

    private sealed record Entry(string Label, string FixtureContent, bool Missing = false);

    [Fact]
    public async Task CurlVerb_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cd rust-original && cargo build --release` " +
                "(from PowerShell) before running the curl parity gate.");
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

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "curl-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"curl parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
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
        new("small JSON object -> full passthrough", """{"hello":"world","ok":true}"""),
        new("small non-JSON text -> full passthrough", "Hello, World!\nThis is plain text."),
        new(
            "large non-JSON text under a piped (non-TTY) stdout -> full passthrough, no truncation",
            new string('x', 1000)),
        new(
            "large JSON object -> full passthrough regardless of size (#1536)",
            $$"""{"data":"{{new string('y', 600)}}"}"""),
        new("missing file -> curl failure, non-zero exit, FAILED: message", "", Missing: true),
    ];

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-curl-parity-oracle-");
        var portTemp = CreateTempDir("rtk-curl-parity-port-");

        try
        {
            var oracleFixture = Path.Combine(oracleTemp, "fixture.txt");
            var portFixture = Path.Combine(portTemp, "fixture.txt");

            if (!entry.Missing)
            {
                await File.WriteAllTextAsync(oracleFixture, entry.FixtureContent);
                await File.WriteAllTextAsync(portFixture, entry.FixtureContent);
            }

            var oracleArgs = new[] { "curl", FileUrl(oracleFixture) };
            var portArgs = portPrefixArgs.Concat(["curl", FileUrl(portFixture)]).ToArray();

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(oraclePath, oracleArgs, oracleTemp, null, stdin: "");
            var (portStdout, portExit) = await ParityRunner.RunAsync(portFileName, portArgs, portTemp, null, stdin: "");

            var normalizedOracle = Normalize(oracleStdout).Replace(oracleFixture, "<FIXTURE>");
            var normalizedPort = Normalize(portStdout).Replace(portFixture, "<FIXTURE>");

            return new Result(entry.Label, normalizedOracle, normalizedPort, oracleExit, portExit);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    private static string FileUrl(string path) => "file:///" + path.Replace('\\', '/');

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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RtkSharp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate repo root (no RtkSharp.slnx found in any parent directory).");
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
        sb.AppendLine("# `rtk curl` Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/CurlParityTests.cs`. " +
                      "Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries (small JSON, small text, large text, large JSON, missing-file failure)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: each entry runs `curl file:///<fixture>` (zero network calls, " +
                      "curl's own local-file scheme) against both binaries, comparing stdout (CRLF/LF " +
                      "normalized, trailing whitespace trimmed, temp fixture paths substituted with a " +
                      "placeholder) and exit code. The truncate-with-tee-hint branch is not exercised " +
                      "here — see this file's class remarks for why that's expected, not a gap.");
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
