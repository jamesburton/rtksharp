using System.Text.Json;
using RtkSharp.Core;
using RtkSharp.Filters.Commands.Cloud;
using RtkSharp.Filters.Commands.Dotnet;
using RtkSharp.Filters.Commands.Gh;
using RtkSharp.Filters.Commands.Git;
using RtkSharp.Filters.Commands.Go;
using RtkSharp.Filters.Commands.Js;
using RtkSharp.Filters.Commands.Jvm;
using RtkSharp.Filters.Commands.Python;
using RtkSharp.Filters.Commands.Ruby;
using RtkSharp.Filters.Commands.Rust;
using RtkSharp.Filters.Commands.System;
using RtkSharp.Parser;

namespace RtkSharp.Filters;

/// <summary>
/// Internal dispatch table mapping a command name to the <c>*Filters</c> method that best filters
/// its already-captured output. Backs <see cref="RtkFilters.IsRegistered"/>/
/// <see cref="RtkFilters.Filter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every entry below was reconciled against the real, current signature of its target method (Tasks
/// 4-14) — none of them are the placeholder literals (<c>default</c>, <c>""</c>, <c>0</c>,
/// <c>false</c>) from the task brief's first-pass sample. Where a real argument (a bool flag, an
/// enum, a numeric cap) can be derived from <c>args</c>/<c>rawStdout</c>/<c>rawStderr</c>/
/// <c>exitCode</c>, it is; where a handler needs more than a one-line lambda, it delegates to a
/// private <c>Dispatch*</c> helper below the table.
/// </para>
/// <para>
/// <b>Judgment calls, documented per entry (see also the Task 15 report):</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>git</b> → <see cref="GitFilters.FilterStatusWithArgs"/>. <c>GitFilters</c> exposes ~15 filter
/// methods (log/diff/status/push/pull/branch/fetch/stash/worktree); <c>git status</c> is the most
/// common single default for a flat one-command-one-handler dispatch, matching the brief's own pick.
/// </description></item>
/// <item><description>
/// <b>diff</b> → <see cref="DiffFilters.CondenseUnifiedDiff"/>, NOT the brief's
/// <c>RenderFileDiff</c>. <c>RenderFileDiff</c> needs two file paths AND two file contents
/// (<c>(file1, file2, content1, content2)</c>) that this dispatch table's single
/// captured-stdout/stderr shape cannot supply. <c>CondenseUnifiedDiff(string diff)</c> takes exactly
/// one already-captured diff text and is the correct fit for <c>rtk diff</c> piped from e.g.
/// <c>git diff</c>.
/// </description></item>
/// <item><description>
/// <b>glab</b>/<b>gh</b> → <c>FormatMrList</c>/<c>FormatPrList</c> take <see cref="JsonElement"/>,
/// not <c>string</c> — <c>stdout</c> is parsed via <see cref="JsonDocument.Parse(string)"/> first.
/// <c>ultraCompact</c> is derived from the real <c>--ultra-compact</c> flag (confirmed against
/// <c>GhCommand.cs</c>) rather than hardcoded.
/// </description></item>
/// <item><description>
/// <b>golangci-lint</b> → <see cref="GoFilters.FilterGolangciJson"/>'s second parameter is
/// <c>uint version</c> (the golangci-lint major version, 1 or 2), not the brief's literal <c>0</c>.
/// The real value comes from a separate <c>golangci-lint --version</c> probe this dispatch table has
/// no channel for, so <c>1u</c> (the method's own v1 fallback behavior) is the least-bad default.
/// </description></item>
/// <item><description>
/// <b>ruff</b> → <c>isCheck</c>/<c>isFormat</c> are derived from <c>args</c> using the exact
/// heuristic <c>RuffCommand.RunAsync</c> itself uses (ruff_cmd.rs:35-39 port), not the brief's
/// hardcoded <c>true</c>/<c>false</c>.
/// </description></item>
/// <item><description>
/// <b>pip</b> → branches on <c>args[0]</c> (<c>"list"</c> vs <c>"outdated"</c>) between
/// <see cref="PipFilters.FilterPipList"/> and <see cref="PipFilters.FilterPipOutdated"/>, since
/// <c>PipCommand</c> itself only filters those two subcommands (others are raw passthrough).
/// </description></item>
/// <item><description>
/// <b>rubocop</b> → <see cref="RubocopFilters.FilterRubocopJson"/> (not the brief's
/// <c>FilterRubocopText</c>, which is RuboCop's own text-fallback path). <c>RubocopCommand</c>
/// injects <c>--format json</c> by default, so JSON is the primary/structured path per the class's
/// own XML doc; text is a fallback for autocorrect/custom-format/parse-failure, not the default.
/// </description></item>
/// <item><description>
/// <b>rspec</b> → <see cref="RspecFilters.FilterRspecOutput"/> (not the brief's
/// <c>FilterRspecText</c>, which is <c>internal</c> and is itself only RSpec's text-fallback path,
/// invoked internally by <c>FilterRspecOutput</c> when JSON parsing fails). <c>RspecCommand</c> also
/// injects <c>--format json</c> by default.
/// </description></item>
/// <item><description>
/// <b>vitest</b>/<b>jest</b> → both dispatch through the identical
/// <see cref="VitestFilters.VitestParser"/>/<see cref="VitestFilters.FormatVitestSummary"/>/
/// <see cref="VitestFilters.RenderTestOutputWithHints"/> pipeline (confirmed: the shared JSON schema
/// is framework-agnostic). The brief's single-arg <c>RenderTestOutputWithHints(stdout)</c> does not
/// exist — the real signature needs a pre-parsed/formatted <c>FormattedTestOutput</c> plus tee-hint
/// delegates. Since this library does no file I/O (see <see cref="RtkFilters"/>'s own doc), the hint
/// delegates are wired as no-ops (always return <see langword="null"/>) rather than performing a
/// real tee lookup.
/// </description></item>
/// <item><description>
/// <b>playwright</b> → same shape problem as vitest/jest: <see cref="PlaywrightFilters.RenderOutput"/>
/// needs a pre-parsed/formatted string, not raw stdout, so this also runs the parse→format→render
/// pipeline with a no-op tee-hint delegate.
/// </description></item>
/// <item><description>
/// <b>prisma</b> → <see cref="PrismaFilters.FilterPrismaGenerate"/> only covers the <c>generate</c>
/// subcommand (of five: generate/migrate dev/migrate status/migrate deploy/db push). A flat
/// one-command dispatch cannot pick correctly among all five without inspecting <c>args</c> for the
/// subcommand tokens; <c>generate</c> (the brief's pick, and the most common default) is kept as the
/// single representative handler — a documented limitation, not an oversight.
/// </description></item>
/// <item><description>
/// <b>az</b> → no single <c>AzFilters</c> method both compacts and redacts generic JSON (all of
/// <c>AzFilters</c>'s named formatters are subcommand-specific, same structural issue as
/// <c>aws</c>/<c>prisma</c> above). Per <c>AzFilters.Redact</c>'s own doc — "applied ... to the
/// generic JSON-compaction fallback's output, before printing" — the correct composition is
/// <c>Redact(AwsFilters.FilterJsonCompact(stdout, 6))</c>, reusing the shared <c>aws</c> fallback.
/// </description></item>
/// <item><description>
/// <b>read</b> → <see cref="ReadFilters.Render"/> needs a <c>Language</c> (from the file extension)
/// and a <c>FilterLevel</c> (from a <c>--level</c> flag) that this dispatch table's
/// captured-stdout-only shape cannot reliably derive — <c>RtkFilters.Filter</c> has no file path
/// parameter. <see cref="Language.Unknown"/> + <see cref="FilterLevel.None"/> (i.e. verbatim
/// passthrough, plus optional line-numbering derived from <c>args</c>) is the least-bad default: it
/// never mis-filters content it cannot correctly classify.
/// </description></item>
/// <item><description>
/// <b>find</b> → <see cref="FindFilters.FormatFindResults"/>'s raw input is a newline-joined path
/// list (the caller's filesystem walk already happened), not literal <c>find</c> command text, so
/// splitting <c>rawStdout</c> on <c>\n</c> is the correct adapter. Its second parameter (a
/// <see cref="FindArgs"/> record) is not derivable from <c>args</c> without duplicating
/// <c>FindCommand</c>'s own argument parser, so <c>FindArgs.Default()</c> (the class's own documented
/// default) is used — this only affects the pattern-echo/cap display, not the grouping logic itself.
/// </description></item>
/// <item><description>
/// <b>deps</b> → <c>DepsFilters</c> exposes five ecosystem-specific summarizers with no dispatcher of
/// its own. The manifest file name (inferred from the first non-flag <c>args</c> token, matching
/// <c>rtk deps &lt;path&gt;</c>'s positional) selects the summarizer; an unrecognized/absent file name
/// falls back to <see cref="DepsFilters.SummarizeCargo"/> (an arbitrary but harmless default, since a
/// non-Cargo manifest simply produces an empty summary rather than mis-parsing).
/// </description></item>
/// </list>
/// </remarks>
internal static class FilterRegistry
{
    /// <summary>A single command's filter: raw args/stdout/stderr/exit code in, filtered text out.</summary>
    /// <param name="args">The command's arguments.</param>
    /// <param name="rawStdout">The command's captured stdout.</param>
    /// <param name="rawStderr">The command's captured stderr.</param>
    /// <param name="exitCode">The command's exit code.</param>
    /// <returns>The filtered output.</returns>
    internal delegate string Handler(string[] args, string rawStdout, string rawStderr, int exitCode);

    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.Ordinal)
    {
        // ----- Git ecosystem -----
        ["git"] = (args, stdout, stderr, exitCode) => GitFilters.FilterStatusWithArgs(stdout),
        ["diff"] = (args, stdout, stderr, exitCode) => DiffFilters.CondenseUnifiedDiff(stdout),
        ["gt"] = (args, stdout, stderr, exitCode) => GtFilters.FilterGtLogEntries(stdout),
        ["glab"] = (args, stdout, stderr, exitCode) =>
            GlabFilters.FormatMrList(JsonDocument.Parse(stdout).RootElement, HasUltraCompactFlag(args)),
        ["gh"] = (args, stdout, stderr, exitCode) =>
            GhFilters.FormatPrList(JsonDocument.Parse(stdout).RootElement, HasUltraCompactFlag(args)),

        // ----- .NET -----
        ["dotnet"] = (args, stdout, stderr, exitCode) => DotnetFilters.FilterBuild(stdout + stderr, exitCode == 0),

        // ----- JavaScript/TypeScript -----
        ["npm"] = (args, stdout, stderr, exitCode) => NpmFilters.FilterNpmOutput(stdout + stderr),
        ["pnpm"] = (args, stdout, stderr, exitCode) => DispatchPnpm(args, stdout, stderr),
        ["tsc"] = (args, stdout, stderr, exitCode) => TscFilters.FilterTscOutput(stdout + stderr),
        ["vitest"] = (args, stdout, stderr, exitCode) => DispatchVitestOrJest(stdout, stderr, exitCode, "vitest"),
        ["jest"] = (args, stdout, stderr, exitCode) => DispatchVitestOrJest(stdout, stderr, exitCode, "jest"),
        ["playwright"] = (args, stdout, stderr, exitCode) => DispatchPlaywright(stdout, stderr, exitCode),
        ["prisma"] = (args, stdout, stderr, exitCode) => DispatchPrisma(args, stdout, stderr),

        // ----- Rust -----
        ["cargo"] = (args, stdout, stderr, exitCode) => CargoFilters.FilterCargoBuild(stdout + stderr),

        // ----- Go -----
        ["go"] = (args, stdout, stderr, exitCode) => GoFilters.FilterGoBuildWithExit(stdout + stderr, exitCode),
        ["golangci-lint"] = (args, stdout, stderr, exitCode) => GoFilters.FilterGolangciJson(stdout, version: 1u),

        // ----- Python -----
        ["ruff"] = (args, stdout, stderr, exitCode) => DispatchRuff(args, stdout),
        ["pytest"] = (args, stdout, stderr, exitCode) => PytestFilters.FilterPytestOutput(stdout),
        ["mypy"] = (args, stdout, stderr, exitCode) => MypyFilters.FilterMypyOutput(Utils.StripAnsi(stdout + stderr)),
        ["pip"] = (args, stdout, stderr, exitCode) => DispatchPip(args, stdout),

        // ----- Ruby -----
        ["rake"] = (args, stdout, stderr, exitCode) => RakeFilters.FilterMinitestOutput(stdout),
        ["rubocop"] = (args, stdout, stderr, exitCode) => RubocopFilters.FilterRubocopJson(stdout),
        ["rspec"] = (args, stdout, stderr, exitCode) => RspecFilters.FilterRspecOutput(stdout),

        // ----- JVM -----
        ["gradlew"] = (args, stdout, stderr, exitCode) => GradlewFilters.FilterTest(stdout),
        ["mvn"] = (args, stdout, stderr, exitCode) => MvnFilters.FilterSurefire(stdout),

        // ----- Cloud -----
        ["aws"] = (args, stdout, stderr, exitCode) => AwsFilters.FilterJsonCompact(stdout, maxDepth: 6),
        ["az"] = (args, stdout, stderr, exitCode) => AzFilters.Redact(AwsFilters.FilterJsonCompact(stdout, maxDepth: 6)),
        ["docker"] = (args, stdout, stderr, exitCode) => DockerFilters.FormatPsSummary(stdout),
        ["kubectl"] = (args, stdout, stderr, exitCode) => ContainerFilters.FormatKubectlPods(JsonDocument.Parse(stdout).RootElement),
        ["oc"] = (args, stdout, stderr, exitCode) => ContainerFilters.FormatKubectlPods(JsonDocument.Parse(stdout).RootElement),
        // NOTE: unlike every other entry in this table, curl's filter has a disclosed disk-write
        // side effect — see RtkFilters' class remarks and Tee.ForceTeeHint.
        ["curl"] = (args, stdout, stderr, exitCode) => DispatchCurl(stdout),
        ["wget"] = (args, stdout, stderr, exitCode) => DispatchWget(args, stdout, stderr, exitCode),
        ["psql"] = (args, stdout, stderr, exitCode) => PsqlFilters.FilterPsqlOutput(stdout),

        // ----- System -----
        ["ls"] = (args, stdout, stderr, exitCode) =>
            LsFilters.FilterLs(stdout, LsFilters.DetectShowAll(args), LsFilters.DetectShowLong(args)),
        ["read"] = (args, stdout, stderr, exitCode) => DispatchRead(args, stdout),
        ["wc"] = (args, stdout, stderr, exitCode) => WcFilters.FilterWcOutput(stdout, WcFilters.DetectMode(args)),
        ["tree"] = (args, stdout, stderr, exitCode) => TreeFilters.FilterTreeOutput(stdout),
        ["find"] = (args, stdout, stderr, exitCode) =>
            FindFilters.FormatFindResults(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), FindArgs.Default()),
        ["grep"] = (args, stdout, stderr, exitCode) => DispatchGrep(args, stdout),
        ["pipe"] = (args, stdout, stderr, exitCode) => PipeFilters.AutoDetectFilter(stdout)(stdout),
        ["test"] = (args, stdout, stderr, exitCode) => TestFilters.ExtractTestSummary(stdout + stderr, string.Join(' ', args)),
        ["format"] = (args, stdout, stderr, exitCode) => FormatFilters.FilterBlackOutput(stdout + stderr),
        ["json"] = (args, stdout, stderr, exitCode) => DispatchJson(args, stdout),
        ["deps"] = (args, stdout, stderr, exitCode) => DispatchDeps(args, stdout),
        ["log"] = (args, stdout, stderr, exitCode) => LogFilters.AnalyzeLogs(stdout),
        ["summary"] = (args, stdout, stderr, exitCode) =>
            SummaryFilters.SummarizeOutput(stdout + stderr, string.Join(' ', args), exitCode == 0),
    };

    /// <summary>Looks up the handler registered for <paramref name="command"/>.</summary>
    /// <param name="command">The command name.</param>
    /// <param name="handler">The registered handler, if found.</param>
    /// <returns>True if a handler is registered for <paramref name="command"/>.</returns>
    internal static bool TryGet(string command, out Handler handler) => Handlers.TryGetValue(command, out handler!);

    /// <summary>True if a handler is registered for <paramref name="command"/>.</summary>
    /// <param name="command">The command name.</param>
    /// <returns>True if a handler is registered for <paramref name="command"/>.</returns>
    internal static bool IsRegistered(string command) => Handlers.ContainsKey(command);

    /// <summary>Whether <c>--ultra-compact</c> is present in <paramref name="args"/> (gh/glab).</summary>
    private static bool HasUltraCompactFlag(string[] args) => args.Contains("--ultra-compact");

    /// <summary>
    /// Shared vitest/jest pipeline: parses via the framework-agnostic <see cref="VitestFilters.VitestParser"/>,
    /// formats compactly, then renders with no-op tee-hint delegates (this library performs no file
    /// I/O — see <see cref="RtkFilters"/>'s own doc).
    /// </summary>
    private static string DispatchVitestOrJest(string stdout, string stderr, int exitCode, string teeLabel)
    {
        var raw = stdout + stderr;
        var parsed = new VitestFilters.VitestParser().Parse(raw);
        if (parsed is ParseResult<TestResult>.Passthrough passthrough)
        {
            return passthrough.Raw;
        }

        var formatted = VitestFilters.FormatVitestSummary(parsed.Unwrap(), FormatMode.Compact);
        return VitestFilters.RenderTestOutputWithHints(
            formatted, raw, teeLabel, exitCode,
            forceHint: (r, label) => null,
            teeHint: (r, label, code) => null);
    }

    /// <summary>
    /// Playwright pipeline: parses via <see cref="PlaywrightFilters.PlaywrightParser"/>, formats
    /// compactly, then renders with a no-op tee-hint delegate (same I/O-free reasoning as vitest/jest).
    /// </summary>
    private static string DispatchPlaywright(string stdout, string stderr, int exitCode)
    {
        var raw = stdout + stderr;
        var parsed = new PlaywrightFilters.PlaywrightParser().Parse(raw);
        if (parsed is ParseResult<TestResult>.Passthrough passthrough)
        {
            return passthrough.Raw;
        }

        var formatted = PlaywrightFilters.FormatFullResults(parsed.Unwrap(), FormatMode.Compact);
        return PlaywrightFilters.RenderOutput(formatted, raw, exitCode, teeHint: (r, label, code) => null);
    }

    /// <summary>Derives ruff's <c>is_check</c>/<c>is_format</c> mode exactly as <c>RuffCommand.RunAsync</c> does.</summary>
    private static string DispatchRuff(string[] args, string stdout)
    {
        var isCheck = args.Length == 0
            || args[0] == "check"
            || (!args[0].StartsWith('-') && args[0] != "format" && args[0] != "version");
        var isFormat = args.Any(a => a == "format");
        return RuffFilters.FilterRuffOutput(stdout, isCheck, isFormat);
    }

    /// <summary>
    /// Branches on prisma's subcommand tokens (<c>generate</c>/<c>migrate dev|status|deploy</c>/
    /// <c>db-push</c>), mirroring the real subcommand shape confirmed against
    /// <c>RtkSharp.Commands.Js.PrismaCommand.DispatchAsync</c>/<c>DispatchMigrateAsync</c>
    /// (<c>args[0] == "migrate"</c> then a nested <c>args[1] == "dev"|"status"|"deploy"</c>, and
    /// <c>args[0] == "db-push"</c> as a single flat token, not <c>"db" "push"</c>). Falls back to
    /// <see cref="PrismaFilters.FilterPrismaGenerate"/> for <c>generate</c>/unrecognized input,
    /// matching the previous default.
    /// </summary>
    private static string DispatchPrisma(string[] args, string stdout, string stderr)
    {
        var raw = stdout + stderr;
        if (args.Length == 0)
        {
            return PrismaFilters.FilterPrismaGenerate(raw);
        }

        switch (args[0])
        {
            case "db-push":
                return PrismaFilters.FilterDbPush(raw);

            case "migrate" when args.Length > 1:
                return args[1] switch
                {
                    "dev" => PrismaFilters.FilterMigrateDev(raw),
                    "status" => PrismaFilters.FilterMigrateStatus(raw),
                    "deploy" => PrismaFilters.FilterMigrateDeploy(raw),
                    _ => PrismaFilters.FilterPrismaGenerate(raw),
                };

            default:
                return PrismaFilters.FilterPrismaGenerate(raw);
        }
    }

    /// <summary>
    /// Branches on pnpm's subcommand (<c>list</c>/<c>outdated</c>/else) using each subcommand's own
    /// parse→format pipeline, mirroring <see cref="DispatchVitestOrJest"/>'s multi-step shape: a
    /// dedicated <see cref="RtkSharp.Parser.OutputParser{T}"/> parses <paramref name="stdout"/> into a
    /// <see cref="DependencyState"/>, which is then formatted via <see cref="PnpmFilters.FormatPnpmList"/>/
    /// <see cref="PnpmFilters.FormatPnpmOutdated"/>. Falls back to <see cref="PnpmFilters.FilterPnpmInstall"/>
    /// (the previous default) for <c>install</c>/unrecognized subcommands, or if parsing yields a
    /// passthrough result.
    /// </summary>
    private static string DispatchPnpm(string[] args, string stdout, string stderr)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;

        switch (sub)
        {
            case "list":
                var listParsed = new PnpmFilters.PnpmListParser().Parse(stdout);
                return listParsed is ParseResult<DependencyState>.Passthrough listPassthrough
                    ? listPassthrough.Raw
                    : PnpmFilters.FormatPnpmList(listParsed.Unwrap(), isFiltered: args.Contains("--prod") || args.Contains("-P") || args.Contains("--dev") || args.Contains("-D"));

            case "outdated":
                var outdatedParsed = new PnpmFilters.PnpmOutdatedParser().Parse(stdout);
                return outdatedParsed is ParseResult<DependencyState>.Passthrough outdatedPassthrough
                    ? outdatedPassthrough.Raw
                    : PnpmFilters.FormatPnpmOutdated(outdatedParsed.Unwrap(), FormatMode.Compact);

            default:
                return PnpmFilters.FilterPnpmInstall(stdout + stderr);
        }
    }

    /// <summary>Branches between <c>pip list</c> and <c>pip list --outdated</c> filtering by the first arg.</summary>
    private static string DispatchPip(string[] args, string stdout)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;
        return sub switch
        {
            "outdated" => PipFilters.FilterPipOutdated(stdout),
            "list" => PipFilters.FilterPipList(stdout),
            _ => stdout,
        };
    }

    /// <summary>Concatenates <see cref="CurlFilters.FilterCurlOutput"/>'s content and optional tee hint.</summary>
    private static string DispatchCurl(string stdout)
    {
        var result = CurlFilters.FilterCurlOutput(stdout, isTty: !Console.IsOutputRedirected);
        return result.TeeHint is null ? result.Content : $"{result.Content}\n{result.TeeHint}";
    }

    /// <summary>
    /// Renders a wget result from the URL (first non-flag arg): the success message from captured
    /// stdout when <paramref name="exitCode"/> is 0, otherwise the failure message built from
    /// <see cref="WgetFilters.ParseError"/>'s parsed <paramref name="stderr"/>/<paramref name="stdout"/>.
    /// </summary>
    private static string DispatchWget(string[] args, string stdout, string stderr, int exitCode)
    {
        var url = args.FirstOrDefault(a => !a.StartsWith('-')) ?? string.Empty;
        return exitCode == 0
            ? WgetFilters.FormatWgetStdoutOutput(url, stdout)
            : WgetFilters.FormatWgetFailure(url, WgetFilters.ParseError(stderr, stdout));
    }

    /// <summary>
    /// <c>rtk read</c>'s filter needs a <see cref="Language"/> (from a file extension) and
    /// <see cref="FilterLevel"/> (from a <c>--level</c> flag) this dispatch table cannot reliably
    /// derive without a file path parameter — see class remarks. Falls back to verbatim passthrough
    /// (<see cref="Language.Unknown"/> + <see cref="FilterLevel.None"/>), honoring only line
    /// numbering, which args can express directly.
    /// </summary>
    private static string DispatchRead(string[] args, string stdout)
    {
        var lineNumbers = args.Contains("--line-numbers") || args.Contains("-N");
        return ReadFilters.Render(stdout, Language.Unknown, FilterLevel.None, null, null, lineNumbers);
    }

    /// <summary>Derives <c>rtk grep</c>'s pattern display and context-only flag from <paramref name="args"/>.</summary>
    private static string DispatchGrep(string[] args, string stdout)
    {
        var patternDisplay = args.FirstOrDefault(a => !a.StartsWith('-')) ?? string.Empty;
        var contextOnly = args.Contains("--context-only");
        return GrepFilters.BuildGroupedOutput(stdout, patternDisplay, maxLen: 80, maxResults: 200, contextOnly);
    }

    /// <summary>Derives <c>rtk json</c>'s <c>--keys-only</c> flag and real default max depth (5).</summary>
    private static string DispatchJson(string[] args, string stdout)
    {
        const int defaultMaxDepth = 5;
        return args.Contains("--keys-only")
            ? JsonFilters.FilterJsonSchema(stdout, defaultMaxDepth)
            : JsonFilters.FilterJsonCompact(stdout, defaultMaxDepth);
    }

    /// <summary>Picks the ecosystem-specific summarizer by the manifest file name (first non-flag arg).</summary>
    private static string DispatchDeps(string[] args, string stdout)
    {
        var file = args.FirstOrDefault(a => !a.StartsWith('-')) ?? string.Empty;
        return Path.GetFileName(file) switch
        {
            "package.json" => DepsFilters.SummarizePackageJson(stdout),
            "requirements.txt" => DepsFilters.SummarizeRequirements(stdout),
            "pyproject.toml" => DepsFilters.SummarizePyproject(stdout),
            "go.mod" => DepsFilters.SummarizeGoMod(stdout),
            _ => DepsFilters.SummarizeCargo(stdout),
        };
    }
}
