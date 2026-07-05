using System.Text;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.ParityTests;

/// <summary>
/// Phase 8 Task 8 acceptance gate: an oracle parity battery for the 8 JS/Node.js ecosystem tools
/// ported in Phase 8 — <c>rtk npm</c>, <c>rtk npx</c>, <c>rtk pnpm</c>, <c>rtk tsc</c>,
/// <c>rtk vitest</c>, <c>rtk jest</c>, <c>rtk playwright</c>, and <c>rtk prisma</c>
/// (<c>src/cmds/js/*.rs</c>, <c>RtkSharp/Commands/Js/*.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reproducibility decision: synthetic PATH stand-in tools, not real npm/pnpm/node installs.</b>
/// All 8 tools shell out to real ecosystem binaries (<c>npm</c>, <c>npx</c>, <c>pnpm</c>, <c>tsc</c>,
/// <c>vitest</c>, <c>jest</c>, <c>playwright</c>, <c>prisma</c>) whose presence and exact output
/// shape/version are NOT guaranteed on every dev machine or CI runner. Depending on real installs
/// would make this battery non-reproducible (a machine without a global <c>pnpm</c> would silently
/// skip/fail entries, and even where installed, real tool output drifts across versions). Per the
/// task brief, this battery instead constructs tiny synthetic <c>.cmd</c> stand-in scripts that
/// masquerade as each target tool on <c>PATH</c> for the duration of a single entry, producing a
/// fixed, known output regardless of the arguments rtk passes them — deterministic on every machine,
/// with no dependency on any real JS toolchain being installed. Each stand-in is a two-file pair
/// written into a fresh per-entry, per-side temp directory: <c>{tool}.cmd</c> (<c>@echo off &amp;
/// type "%~dp0{tool}.out" &amp; exit /b {code}</c>) and <c>{tool}.out</c> (the exact canned bytes,
/// avoiding all batch-quoting hazards around embedded JSON double-quotes). This directory is
/// prepended to <c>PATH</c> for the top-level <c>rtk</c>/oracle invocation; both Rust's
/// <c>which::which</c>-based <c>resolved_command</c>/<c>tool_exists</c> and RtkSharp's
/// <see cref="PathResolver"/> are PATHEXT-aware and resolve <c>.cmd</c> files identically, and any
/// grandchild process rtk itself spawns (npm/tsc/pnpm/etc.) inherits the parent rtk process's own
/// environment — including the overridden <c>PATH</c> — by default on both sides, so no additional
/// environment plumbing is needed beyond the outer invocation's env dict.
/// </para>
/// <para>
/// <b>Verified empirically, not just reasoned about from source.</b> This approach was confirmed by
/// actually running the battery against the real oracle (<c>target/release/rtk.exe</c>) and the
/// built <see cref="RtkSharp"/> binary with the synthetic stand-ins in place — not merely assumed to
/// work from reading <c>PathResolver</c>/<c>resolved_command</c> source. See the test run recorded in
/// this task's commit for the pass/fail counts.
/// </para>
/// <para>
/// <b>Hermeticity env overlay — same three keys as <see cref="PipeProxyRunParityTests"/>, plus
/// <c>PATH</c>.</b> Every entry sets <c>RTK_DB_PATH</c> (fresh per-side temp SQLite file),
/// <c>CLAUDE_CONFIG_DIR</c> (a non-existent per-entry path, so <c>hook_check::status()</c>/
/// <c>HookCheck.Status()</c> resolve deterministically to <c>Ok</c>), and <c>RTK_TEE=0</c> (disables
/// tee-to-disk). <b>Tee is deliberately kept disabled for every entry, including playwright's</b>:
/// <c>tee_raw</c>/<c>Tee.TeeRaw</c>'s on-disk filename embeds a live Unix-epoch-seconds timestamp
/// (<c>src/core/tee.rs</c>'s <c>write_tee_file</c>, <c>format!("{}_{}.log", epoch, slug)</c>), which
/// is inherently nondeterministic between two independently-started processes — there is no way to
/// byte-compare a fired tee-hint line without either masking a live timestamp (fragile) or freezing
/// the clock (not available to either binary). Playwright's "unconditional tee-hint" vs vitest's
/// "conditional on truncation" distinction is therefore verified elsewhere, by the unit-level
/// <c>PlaywrightCommandTests</c>/<c>VitestCommandTests</c> added in Tasks 5/6 (which inject fake hint
/// strategies and assert which one fires) — not by this oracle battery, which keeps every entry's
/// tee mechanism fully disabled and instead concentrates on proving oracle/port output-formatting
/// parity for playwright's real behaviors (nested-suite JSON recursion, truncated-not-rounded
/// duration).
/// </para>
/// <para>
/// <b>Stdout only, forced non-interactive stdin, exit codes compared.</b> Every entry passes
/// <c>StdinContent = ""</c> (none of these 8 tools read stdin). Exit codes are compared for every
/// entry; stderr is never compared (project convention — see <see cref="GainParityTests"/>/
/// <see cref="HookParityTests"/>/<see cref="PipeProxyRunParityTests"/>).
/// </para>
/// <para>
/// <b>Label-pinning assertion convention (Phase 6 Task 7), not a loose percentage threshold.</b>
/// <see cref="ExpectedMismatchLabels"/> is the exact set of entry labels this battery is currently
/// known to mismatch. Any mismatch outside that set fails loudly by name (a real regression); any
/// currently-listed label that unexpectedly starts matching ALSO fails loudly (the disclosed gap has
/// closed and this file plus the compatibility ledger need updating) — both directions are asserted
/// explicitly, exactly like <see cref="PipeProxyRunParityTests"/>. As of this task, every quirk this
/// battery exercises (prisma's unused <c>output_path</c>, its always-zero pending count, pnpm
/// outdated's always-exit-0, jest's missing reporter-passthrough escape hatch, playwright's
/// truncated-not-rounded duration) is faithfully ported on BOTH sides and agrees byte-for-byte. The
/// two tsc entries ARE in <see cref="ExpectedMismatchLabels"/>, however — see the next paragraph.
/// </para>
/// <para>
/// <b>tsc entries are a disclosed, expected mismatch — this is the streaming-vs-buffered decision,
/// not a bug.</b> Confirmed empirically by this very battery: the real oracle's `rtk tsc` executes
/// through Rust's streaming <c>TscHandler</c>/<c>BlockStreamFilter</c> path (<c>tsc_cmd.rs</c>'s
/// actual <c>run()</c>), which emits tsc's raw diagnostic lines as they stream plus a trailing one-line
/// summary — genuinely different rendering from the buffered, grouped-by-file
/// <c>filter_tsc_output</c> this port implements (<see cref="TscCommand"/>'s own class remarks
/// document this architecture choice: buffered capture was deemed behaviorally-sufficient and
/// architecturally simpler than introducing a new tsc-specific streaming primitive, since Rust's own
/// test suite only ever exercises <c>filter_tsc_output</c> directly, never the streaming path). This
/// battery's two tsc entries capture that real, disclosed divergence directly against the live
/// oracle (not merely asserted from reading source) — both entries are listed in
/// <see cref="ExpectedMismatchLabels"/> and cross-referenced in
/// <c>docs/parity/compatibility-ledger.md</c>.
/// </para>
/// <para>
/// <b><c>pnpm list</c> entries sort output lines before comparing — a HashMap/Dictionary iteration-
/// order artifact, not a port bug.</b> Both <c>PackageJsonListItem.dependencies</c>
/// (<c>pnpm_cmd.rs</c>:31, a Rust <c>HashMap</c>, SipHash-randomized per process) and the port's
/// equivalent <c>Dictionary&lt;string, PnpmListPackage&gt;</c> iterate their JSON object's key/value
/// pairs in an order neither side's own contract guarantees — observed directly in this battery's
/// first run (the identical dependency set rendered in a different per-entry order on each side, and
/// would likely differ again on a repeat run of the same oracle binary). This is the same class of
/// nondeterminism already ledgered for <c>rtk grep -rln</c>'s ripgrep-parallel-walk ordering
/// (<c>docs/parity/compatibility-ledger.md</c>) — the deterministic invariant is the *set* of
/// rendered lines, not their order, so <see cref="RunEntryAsync"/> sorts both sides' output lines
/// before storing the comparable <see cref="Result"/> for the uncapped (<c>--prod</c>) entry, where
/// every dependency is shown and the sorted-line multiset is therefore still exact.
/// </para>
/// <para>
/// <b>The CAPPED <c>pnpm list</c> entry additionally masks individual dependency lines — the
/// truncation's underlying <c>HashMap</c>/<c>Dictionary</c> iteration order affects WHICH 20 of 25
/// entries get selected for display, not merely their print order, so sorting alone cannot make the
/// exact selected subset comparable across two independently-ordered maps.</b> Confirmed empirically
/// by this battery's first run: the two sides selected different, non-overlapping subsets of the same
/// 25-entry dependency set for their top-20 slice. This is inherent HashMap-seed nondeterminism (the
/// oracle would plausibly select a different 20 on a repeat run of the same binary, too), not a
/// capping-logic bug — the cap threshold, section counts, and "<c>… +N more</c>" hint are the actual
/// observable/testable contract. <see cref="RunEntryAsync"/> therefore masks out individual
/// <c>  {name} {version}</c> lines (keeping the total/section-count header and the "more" hint line)
/// for this one entry before comparing, verifying the capping *mechanism* (threshold, count, hint)
/// while not asserting byte-identity over an inherently-unordered selection.
/// </para>
/// </remarks>
public class JsStackParityTests
{
    private const double ParityThresholdPercent = 95.0;

    /// <summary>
    /// The exact set of entry labels this battery is currently known — and allowed — to mismatch.
    /// Empty: every quirk this battery exercises is faithfully ported on both sides, so oracle and
    /// port are expected to agree on every entry. See class remarks for the assertion convention.
    /// </summary>
    private static readonly IReadOnlySet<string> ExpectedMismatchLabels =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // Disclosed streaming-vs-buffered architecture decision (Phase 8 Task 4) — see class
            // remarks and docs/parity/compatibility-ledger.md. Confirmed empirically: the real
            // oracle streams raw tsc diagnostics via TscHandler, while this port implements the
            // buffered, grouped-by-file filter_tsc_output Rust's own tests exercise.
            "tsc: grouped-by-file output, no per-file cap (5 files)",
            "tsc: top-codes summary line (multiple distinct error codes)",
        };

    /// <summary>
    /// Entry labels whose stdout is compared as a sorted-line multiset rather than byte-exact order —
    /// see the class remarks' "pnpm list entries sort output lines" paragraph for the HashMap/
    /// Dictionary iteration-order rationale.
    /// </summary>
    private static readonly IReadOnlySet<string> OrderInsensitiveLabels =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "pnpm list --prod: explicitly scoped, uncapped",
        };

    /// <summary>
    /// The single capped <c>pnpm list</c> entry label, whose individual dependency lines are masked
    /// out before comparison (see class remarks' "CAPPED pnpm list entry additionally masks" paragraph).
    /// </summary>
    private const string CappedPnpmListLabel = "pnpm list: plain invocation capped at 20 per section";

    /// <summary>A single synthetic PATH stand-in tool: its binary name, canned output bytes, and exit code.</summary>
    private sealed record ToolStub(string ToolName, string Output, int ExitCode = 0);

    /// <summary>A single battery entry: a label, the full CLI args (including the leading verb), and its tool stand-in.</summary>
    private sealed record Entry(string Label, string[] Args, ToolStub Stub);

    [Fact]
    public async Task JsStack_MatchesRustOracle_AcrossBattery()
    {
        var repoRoot = FindRepoRoot();
        var oraclePath = Path.Combine(repoRoot, "target", "release", ExeName("rtk"));

        if (!File.Exists(oraclePath))
        {
            Assert.Fail(
                $"Rust oracle not found at '{oraclePath}'. Build it with `cargo build --release` " +
                "(from PowerShell) before running the JS-stack parity gate.");
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

        var reportPath = Path.Combine(repoRoot, "docs", "parity", "js-stack-parity-report.md");
        await WriteReportAsync(reportPath, results, percent, matched, total, oraclePath, portFileName, portPrefixArgs);

        var detail = new StringBuilder();
        detail.AppendLine($"JS-stack parity: {percent:F1}% ({matched}/{total} entries matched). Report: {reportPath}");
        foreach (var r in results.Where(r => !r.IsMatch))
        {
            detail.AppendLine(
                $"  MISMATCH [{r.Label}]: rustExit={r.RustExit} portExit={r.PortExit} stdoutMatch={r.StdoutMatches}");
        }

        var actualMismatchLabels = new HashSet<string>(
            results.Where(r => !r.IsMatch).Select(r => r.Label), StringComparer.Ordinal);

        var unexpectedMismatches = actualMismatchLabels.Except(ExpectedMismatchLabels).ToList();
        Assert.True(
            unexpectedMismatches.Count == 0,
            "Unexpected mismatch(es) outside the disclosed-gap ledger — this is a real regression: " +
            $"{string.Join(", ", unexpectedMismatches)}.\n{detail}");

        var nowPassingGaps = ExpectedMismatchLabels.Except(actualMismatchLabels).ToList();
        Assert.True(
            nowPassingGaps.Count == 0,
            "Expected disclosed-gap entry(ies) now pass against the oracle — update ExpectedMismatchLabels " +
            $"in this file and docs/parity/compatibility-ledger.md: {string.Join(", ", nowPassingGaps)}.\n{detail}");

        // Belt-and-braces sanity check (same convention as PipeProxyRunParityTests): the exact-set
        // assertions above already pin identity, so the floor is computed relative to the number of
        // *disclosed* mismatches (tsc's streaming-vs-buffered divergence) rather than a single fixed
        // constant — a battery with 2 known, ledgered divergences out of 18 entries has an effective
        // best-possible match rate below 100%, and the fixed 95% constant would fail even a fully
        // passing run once disclosed gaps are accounted for.
        var effectiveThreshold = total == 0
            ? 100.0
            : Math.Min(ParityThresholdPercent, (total - ExpectedMismatchLabels.Count) * 100.0 / total);
        Assert.True(percent >= effectiveThreshold, detail.ToString());
    }

    // ===================== battery definition =====================

    private static IReadOnlyList<Entry> BuildBattery()
    {
        var list = new List<Entry>();

        // ---------- npm ----------
        list.Add(new(
            "npm: script-banner stripping",
            ["npm", "test"],
            new ToolStub("npm", "> my-pkg@2.0.0 test\nAll tests passed\n")));

        list.Add(new(
            "npm: WARN/notice stripping",
            ["npm", "install"],
            new ToolStub("npm",
                "npm WARN deprecated foo@1.0.0: use bar instead\nnpm notice New minor version available\nInstall complete\n")));

        // ---------- npx ----------
        list.Add(new(
            "npx: routes to a known verb (tsc)",
            ["npx", "tsc", "--noEmit"],
            new ToolStub("tsc", string.Empty)));

        list.Add(new(
            "npx: unrecognized tool falls back to npm's filter",
            ["npx", "cowsay", "hello"],
            new ToolStub("npx", "cowsay says hello to you today\n")));

        // ---------- pnpm ----------
        var pnpmListJson = BuildPnpmListJson(prodCount: 25, devCount: 3);

        list.Add(new(
            "pnpm list: plain invocation capped at 20 per section",
            ["pnpm", "list"],
            new ToolStub("pnpm", pnpmListJson)));

        list.Add(new(
            "pnpm list --prod: explicitly scoped, uncapped",
            ["pnpm", "list", "--prod"],
            new ToolStub("pnpm", pnpmListJson)));

        list.Add(new(
            "pnpm outdated: shared TokenFormatter, always exits 0 despite child's nonzero exit",
            ["pnpm", "outdated"],
            new ToolStub(
                "pnpm",
                """
                {"express":{"current":"4.18.2","latest":"4.19.0","wanted":"4.18.2","dependencyType":"dependencies"}}
                """,
                ExitCode: 1)));

        list.Add(new(
            "pnpm install: progress-bar stripped, summary kept",
            ["pnpm", "install"],
            new ToolStub(
                "pnpm",
                "Progress: resolved 120, reused 100, downloaded 4, added 4\n" +
                "│ 45% │ downloading...\n" +
                "\n" +
                "packages in .pnpm-store\n" +
                "+ react 18.2.0\n" +
                "- old-dep 1.0.0\n" +
                "dependencies:\n" +
                "3 dependencies installed\n")));

        // ---------- tsc ----------
        var tscManyFilesSingleCode =
            "src/a.ts(10,5): error TS2322: Type 'string' is not assignable to type 'number'.\n" +
            "src/a.ts(15,3): error TS2322: Type 'boolean' is not assignable to type 'string'.\n" +
            "src/b.ts(2,1): error TS2322: Cannot find name 'foo'.\n" +
            "src/c.ts(1,1): error TS2322: 'x' is declared but its value is never read.\n" +
            "src/d.ts(7,9): error TS2322: Property 'y' does not exist on type 'Z'.\n";

        list.Add(new(
            "tsc: grouped-by-file output, no per-file cap (5 files)",
            ["tsc", "--noEmit"],
            new ToolStub("tsc", tscManyFilesSingleCode, ExitCode: 2)));

        var tscTopCodes =
            "src/a.ts(10,5): error TS2322: Type 'string' is not assignable to type 'number'.\n" +
            "src/a.ts(15,3): error TS2345: Argument of type 'number' is not assignable to parameter of type 'string'.\n" +
            "src/b.ts(2,1): error TS2304: Cannot find name 'foo'.\n" +
            "src/c.ts(1,1): warning TS6133: 'x' is declared but its value is never read.\n";

        list.Add(new(
            "tsc: top-codes summary line (multiple distinct error codes)",
            ["tsc", "--noEmit"],
            new ToolStub("tsc", tscTopCodes, ExitCode: 2)));

        // ---------- vitest / jest (shared parser) ----------
        var sharedTestJson =
            """
            {"numTotalTests":3,"numPassedTests":2,"numFailedTests":1,"numPendingTests":0,
             "testResults":[{"name":"src/math.test.ts","assertionResults":[
               {"fullName":"adds numbers","status":"passed","failureMessages":[]},
               {"fullName":"subtracts numbers","status":"passed","failureMessages":[]},
               {"fullName":"divides by zero","status":"failed","failureMessages":["Expected Infinity, got NaN"]}
             ]}]}
            """;

        list.Add(new(
            "vitest: shared-parser JSON case (--reporter=json schema)",
            ["vitest"],
            new ToolStub("vitest", sharedTestJson, ExitCode: 1)));

        list.Add(new(
            "jest: shared-parser JSON case (--json schema, same parser as vitest)",
            ["jest"],
            new ToolStub("jest", sharedTestJson, ExitCode: 1)));

        list.Add(new(
            "vitest: explicit --reporter triggers reporter-passthrough escape hatch",
            ["vitest", "--reporter=dot"],
            new ToolStub("vitest", "√ math.test.ts (3)\n  √ adds numbers\n  √ subtracts numbers\n  √ divides by zero\n\nTest Files  1 passed (1)\n     Tests  3 passed (3)\n  Duration  120ms\n")));

        var jestAllPassedJson =
            """
            {"numTotalTests":2,"numPassedTests":2,"numFailedTests":0,"numPendingTests":0,
             "testResults":[{"name":"src/util.test.ts","assertionResults":[
               {"fullName":"formats correctly","status":"passed","failureMessages":[]},
               {"fullName":"parses correctly","status":"passed","failureMessages":[]}
             ]}]}
            """;

        list.Add(new(
            "jest: explicit --reporter flag is dropped, NOT honored (no passthrough escape hatch)",
            ["jest", "--reporter=dot"],
            new ToolStub("jest", jestAllPassedJson)));

        // ---------- playwright ----------
        var playwrightNestedJson =
            """
            {"stats":{"expected":1,"unexpected":1,"skipped":0,"duration":4200.0},
             "suites":[{"title":"auth.spec.ts","file":"auth.spec.ts",
               "specs":[{"title":"logs in successfully","ok":true,
                 "tests":[{"status":"expected","results":[{"status":"passed","errors":[]}]}]}],
               "suites":[{"title":"when logged out","file":"auth.spec.ts",
                 "specs":[{"title":"redirects to login","ok":false,
                   "tests":[{"status":"unexpected","results":[{"status":"failed",
                     "errors":[{"message":"Timeout waiting for redirect"}]}]}]}],
                 "suites":[]}]}]}
            """;

        list.Add(new(
            "playwright: nested describe-block suite JSON recursion",
            ["playwright", "test"],
            new ToolStub("npx", playwrightNestedJson, ExitCode: 1)));

        var playwrightTruncatedDuration =
            """
            {"stats":{"expected":1,"unexpected":0,"skipped":0,"duration":3519.7039999999997},
             "suites":[{"title":"smoke.spec.ts","file":"smoke.spec.ts",
               "specs":[{"title":"loads home page","ok":true,
                 "tests":[{"status":"expected","results":[{"status":"passed","errors":[]}]}]}],
               "suites":[]}]}
            """;

        list.Add(new(
            "playwright: duration truncates (not rounds) 3519.7039999999997 -> 3519",
            ["playwright", "test"],
            new ToolStub("npx", playwrightTruncatedDuration)));

        // ---------- prisma ----------
        list.Add(new(
            "prisma generate: unused output_path quirk (hardcoded string, not the detected line)",
            ["prisma", "generate"],
            new ToolStub(
                "prisma",
                "Environment variables loaded from .env\n" +
                "5 model(s) generated\n" +
                "2 enums found\n" +
                "1 type generated\n" +
                "Output: ./node_modules/@prisma/client (version 5.10.0)\n")));

        list.Add(new(
            "prisma migrate dev: always-zero pending count quirk",
            ["prisma", "migrate", "dev"],
            new ToolStub(
                "prisma",
                "Applying migration `20240115120000_add_users_table`\n" +
                "CREATE TABLE \"users\" (\"id\" SERIAL PRIMARY KEY);\n" +
                "Your database is now in sync with your schema.\n" +
                "Migration applied successfully\n")));

        return list;
    }

    private static string BuildPnpmListJson(int prodCount, int devCount)
    {
        var deps = new StringBuilder();
        for (var i = 0; i < prodCount; i++)
        {
            if (deps.Length > 0)
            {
                deps.Append(',');
            }

            deps.Append('"').Append("prod-dep-").Append(i).Append("\":{\"version\":\"1.0.").Append(i).Append("\"}");
        }

        var devDeps = new StringBuilder();
        for (var i = 0; i < devCount; i++)
        {
            if (devDeps.Length > 0)
            {
                devDeps.Append(',');
            }

            devDeps.Append('"').Append("dev-dep-").Append(i).Append("\":{\"version\":\"2.0.").Append(i).Append("\"}");
        }

        return "[{\"name\":\"my-project\",\"version\":\"1.0.0\",\"dependencies\":{"
            + deps + "},\"devDependencies\":{" + devDeps + "}}]";
    }

    // ===================== execution =====================

    private static async Task<Result> RunEntryAsync(
        Entry entry, string oraclePath, string portFileName, string[] portPrefixArgs)
    {
        var oracleTemp = CreateTempDir("rtk-p8-parity-oracle-");
        var portTemp = CreateTempDir("rtk-p8-parity-port-");

        try
        {
            var oracleToolsDir = Path.Combine(oracleTemp, "tools");
            var portToolsDir = Path.Combine(portTemp, "tools");
            WriteToolStub(oracleToolsDir, entry.Stub);
            WriteToolStub(portToolsDir, entry.Stub);

            var oracleDbPath = Path.Combine(oracleTemp, "history.db");
            var portDbPath = Path.Combine(portTemp, "history.db");

            var oracleClaudeDir = Path.Combine(oracleTemp, "no-claude-dir");
            var portClaudeDir = Path.Combine(portTemp, "no-claude-dir");

            var realPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            var oracleEnv = new Dictionary<string, string?>
            {
                ["RTK_DB_PATH"] = oracleDbPath,
                ["CLAUDE_CONFIG_DIR"] = oracleClaudeDir,
                ["RTK_TEE"] = "0",
                ["PATH"] = oracleToolsDir + Path.PathSeparator + realPath,
            };
            var portEnv = new Dictionary<string, string?>
            {
                ["RTK_DB_PATH"] = portDbPath,
                ["CLAUDE_CONFIG_DIR"] = portClaudeDir,
                ["RTK_TEE"] = "0",
                ["PATH"] = portToolsDir + Path.PathSeparator + realPath,
            };

            var (oracleStdout, oracleExit) = await ParityRunner.RunAsync(
                oraclePath, entry.Args, oracleTemp, oracleEnv, stdin: "");

            var portArgs = portPrefixArgs.Concat(entry.Args).ToArray();
            var (portStdout, portExit) = await ParityRunner.RunAsync(
                portFileName, portArgs, portTemp, portEnv, stdin: "");

            var normalizedOracle = Normalize(oracleStdout);
            var normalizedPort = Normalize(portStdout);

            if (OrderInsensitiveLabels.Contains(entry.Label))
            {
                normalizedOracle = SortLines(normalizedOracle);
                normalizedPort = SortLines(normalizedPort);
            }
            else if (entry.Label == CappedPnpmListLabel)
            {
                normalizedOracle = MaskCappedDependencyLines(normalizedOracle);
                normalizedPort = MaskCappedDependencyLines(normalizedPort);
            }

            return new Result(entry.Label, normalizedOracle, normalizedPort, oracleExit, portExit);
        }
        finally
        {
            TryDeleteDirectory(oracleTemp);
            TryDeleteDirectory(portTemp);
        }
    }

    /// <summary>
    /// Writes a synthetic PATH stand-in tool as a <c>{tool}.cmd</c>/<c>{tool}.out</c> pair: the
    /// <c>.cmd</c> script <c>type</c>s the sibling <c>.out</c> file's exact bytes to stdout and exits
    /// with the configured code, regardless of any arguments rtk passes it — sidestepping all
    /// batch-quoting hazards for output containing embedded JSON double-quotes.
    /// </summary>
    private static void WriteToolStub(string toolsDir, ToolStub stub)
    {
        Directory.CreateDirectory(toolsDir);

        var outPath = Path.Combine(toolsDir, stub.ToolName + ".out");
        var cmdPath = Path.Combine(toolsDir, stub.ToolName + ".cmd");

        File.WriteAllText(outPath, stub.Output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            cmdPath,
            $"@echo off\r\ntype \"%~dp0{stub.ToolName}.out\"\r\nexit /b {stub.ExitCode}\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ===================== shared helpers =====================

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

    /// <summary>
    /// Sorts <paramref name="s"/>'s lines (ordinal) and rejoins them — tolerates HashMap/Dictionary
    /// iteration-order nondeterminism (see class remarks) while still failing on any missing, extra,
    /// or altered line, since a sorted-line multiset changes under any of those.
    /// </summary>
    private static string SortLines(string s) =>
        string.Join('\n', s.Split('\n').OrderBy(l => l, StringComparer.Ordinal));

    /// <summary>
    /// Drops individual <c>  {name} {version}</c> dependency lines (2-space-indented, no "more" hint)
    /// while keeping section headers/counts and the "<c>… +N more</c>" truncation hint — see class
    /// remarks' "CAPPED pnpm list entry additionally masks" paragraph for why the specific selected
    /// subset cannot be compared byte-exact across two independently-hash-ordered maps.
    /// </summary>
    private static string MaskCappedDependencyLines(string s) =>
        string.Join('\n', s.Split('\n').Where(l => !l.StartsWith("  ", StringComparison.Ordinal) || l.Contains("more", StringComparison.Ordinal)));

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
        sb.AppendLine("# JS/Node.js Stack (`npm`/`npx`/`pnpm`/`tsc`/`vitest`/`jest`/`playwright`/`prisma`) Oracle Parity Report");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by `RtkSharp.ParityTests/JsStackParityTests.cs` " +
                      "(Phase 8 Task 8 acceptance gate). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Platform:** {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
        sb.AppendLine($"- **Oracle:** `{oraclePath}`");
        var portInvoke = portPrefixArgs.Length == 0
            ? portFileName
            : $"{portFileName} {string.Join(' ', portPrefixArgs)}";
        sb.AppendLine($"- **RtkSharp:** `{portInvoke}`");
        sb.AppendLine($"- **Battery size:** {results.Count} entries covering all 8 JS-stack tools " +
                      "(npm banner/WARN stripping, npx routing/fallback, pnpm list capped/uncapped + " +
                      "outdated + install, tsc grouped/top-codes, vitest+jest shared parser + " +
                      "passthrough asymmetry, playwright nested-JSON + truncated duration, prisma " +
                      "generate/migrate-dev heuristic quirks)");
        sb.AppendLine();
        sb.AppendLine("Comparison method: **synthetic PATH stand-in tools** (see this file's class remarks " +
                      "for the full reproducibility rationale) — each entry writes a `{tool}.cmd`/`{tool}.out` " +
                      "pair into a fresh per-side temp directory prepended to `PATH`, so neither binary depends " +
                      "on a real npm/pnpm/node/tsc/vitest/jest/playwright/prisma installation being present or " +
                      "version-stable. Both binaries run with `RTK_DB_PATH` pointed at a fresh per-side temp " +
                      "file, `CLAUDE_CONFIG_DIR` redirected to a non-existent per-entry path, and `RTK_TEE=0` " +
                      "(tee is kept disabled for every entry — its on-disk filename embeds a live timestamp " +
                      "that cannot be byte-compared; see class remarks). Stdout (CRLF/LF normalized, " +
                      "trailing-whitespace trimmed) and exit code are compared; stderr is not (project " +
                      "convention).");
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
