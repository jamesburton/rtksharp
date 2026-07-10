using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Js;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Which prisma subcommand an invocation resolved to. Mirrors Rust's <c>PrismaCommand</c>/
/// <c>MigrateSubcommand</c> enums (<c>src/cmds/js/prisma_cmd.rs</c>:9-21).
/// </summary>
public enum PrismaVerb
{
    /// <summary><c>prisma generate</c> — strips ASCII art, extracts model/enum/type counts.</summary>
    Generate,

    /// <summary><c>prisma migrate dev</c> — extracts migration name and change counts.</summary>
    MigrateDev,

    /// <summary><c>prisma migrate status</c> — counts applied/pending migrations.</summary>
    MigrateStatus,

    /// <summary><c>prisma migrate deploy</c> — reports deployed count or failure errors.</summary>
    MigrateDeploy,

    /// <summary><c>prisma db push</c> — counts schema changes pushed to the database.</summary>
    DbPush,
}

/// <summary>
/// Implements the <c>rtk prisma</c> CLI verb: <c>generate</c>/<c>migrate dev|status|deploy</c>/
/// <c>db-push</c>. Faithful port of <c>src/cmds/js/prisma_cmd.rs</c> plus the
/// <c>Commands::Prisma</c>/<c>PrismaCommands</c>/<c>PrismaMigrateCommands</c> dispatch arms living
/// in <c>main.rs</c> (<c>main.rs</c>:1026-1070, 2051-2081).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only JS module with no shared-type/formatter usage.</b> Unlike pnpm/vitest/jest/
/// playwright (Phase 8 Tasks 1/3/5/6), prisma's filters are entirely bespoke line-scanning
/// heuristics — no JSON parsing, no <see cref="RtkSharp.Parser.OutputParser{T}"/>, no
/// <see cref="RtkSharp.Parser.TestResult"/>/<c>TokenFormatter</c> involvement anywhere. This port
/// deliberately does not force-fit those abstractions here.
/// </para>
/// <para>
/// <b>Manual <see cref="TimedExecution"/> calls, not a shared runner skeleton.</b> Matching
/// <c>pnpm</c>'s wiring pattern (not <c>npm</c>/<c>npx</c>/<c>tsc</c>'s), each of
/// <c>run_generate</c>/<c>run_migrate</c>/<c>run_db_push</c> in the Rust source calls
/// <c>tracking::TimedExecution::start()</c>/<c>.track(...)</c> directly, which this port matches
/// exactly (<c>prisma_cmd.rs</c>:42-172).
/// </para>
/// <para>
/// <b>ALL FOUR subcommands skip filtering entirely on a non-zero exit.</b> Every one of
/// <c>run_generate</c>/<c>run_migrate</c> (covering all three migrate subcommands)/<c>run_db_push</c>
/// follows the identical shape: on failure, non-empty trimmed stdout and stderr are each written
/// (unfiltered, verbatim) to STDERR — not stdout — via Rust's <c>eprint!</c>, and the child's exit
/// code is returned directly with no call into any filter function. This is genuinely the same
/// pattern reimplemented four times in the Rust source, not something this port only approximates;
/// <see cref="RunOnFailureOrFilterAsync"/> centralizes it once here.
/// </para>
/// <para>
/// <b>Compatibility-ledger disclosure — three baked-in Rust-source bugs, preserved verbatim, not
/// fixed (see <c>docs/parity/compatibility-ledger.md</c>, to be updated by Task 8):</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b><see cref="FilterPrismaGenerate"/>'s unused <c>output_path</c>.</b> The Rust source
/// (<c>filter_prisma_generate</c>, <c>prisma_cmd.rs</c>:175-231) scans for a line containing both
/// <c>"node_modules"</c> and <c>"@prisma"</c> and stores its trimmed text in <c>output_path</c> — but
/// the only use of that variable anywhere is <c>!output_path.is_empty()</c>, gating whether a
/// HARDCODED literal <c>"  • Output: node_modules/@prisma/client\n"</c> is appended. The actual
/// detected line content is computed and then discarded; it never appears in the output under any
/// circumstances. Ported exactly: this method detects presence/absence of such a line via the same
/// two-substring check, but always emits the same hardcoded string when one is found, never the
/// real detected text.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b><see cref="FilterMigrateDev"/>/<see cref="FilterMigrateStatus"/>'s literal <c>"202"</c>
/// substring search (Y2030-cliff bug).</b> The Rust source extracts a migration name via
/// <c>line.find("202")</c> (<c>prisma_cmd.rs</c>:245, 314) — a literal three-character substring
/// search, not a date parser — assuming Prisma's timestamp-prefixed migration folder names
/// (<c>YYYYMMDDHHMMSS_name</c>) always start with the literal characters <c>"202"</c>. From the year
/// 2030 onward this silently stops matching (a migration named <c>20300115120000_add_x</c> would not
/// be found by this same substring search). Ported exactly, not "fixed" to be year-agnostic.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b><see cref="FilterMigrateDev"/>'s always-zero pending count.</b> The Rust source
/// (<c>prisma_cmd.rs</c>:296-299) prints the literal string <c>"Applied | Pending: 0\n"</c> whenever
/// any line contained <c>"applied"</c> or <c>"✓"</c> — the <c>0</c> is a hardcoded literal, not a
/// computed pending-migration count (there is no pending-counting logic anywhere in
/// <c>filter_migrate_dev</c>). This method reports pending as literal <c>0</c> unconditionally in
/// that branch, regardless of whatever the real migration state might be.
/// </description>
/// </item>
/// </list>
public static class PrismaCommand
{
    /// <summary>
    /// Registry entry point for the <c>prisma</c> verb. Reads
    /// <see cref="RuntimeOptions.Verbosity"/> (the registry delegate cannot receive it as an
    /// argument).
    /// </summary>
    /// <param name="args">The arguments following the <c>prisma</c> verb.</param>
    /// <returns>The resolved subcommand's exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunAsync(string[] args) => RunPrismaSafeAsync(args, RuntimeOptions.Verbosity, executor: null);

    /// <summary>
    /// Test-friendly overload of the <c>prisma</c> entry point taking an explicit verbosity value
    /// and an injectable <see cref="IProcessExecutor"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>prisma</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <param name="executor">The process executor to run the child <c>prisma</c> process with, or null for the default.</param>
    /// <returns>The resolved subcommand's exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunPrismaSafeAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await DispatchAsync(args, verbose, executor).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Fail-loud, same convention as NpmCommand/PnpmCommand: an rtk-level failure surfaces as
            // `rtk: {message}`. CommandArgumentParseException must propagate to RtkProgram's
            // dispatch layer instead (re-routed to the TOML-fallback/raw-passthrough path) — it
            // is not a generic runtime failure, so this safety net must not swallow it.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Parses and dispatches a <c>prisma</c> invocation to its resolved subcommand. Mirrors clap's
    /// <c>PrismaCommands</c>/<c>PrismaMigrateCommands</c> subcommand shape: <c>generate</c>,
    /// <c>migrate dev|status|deploy</c> (with <c>dev</c> accepting an optional <c>--name</c>/<c>-n</c>
    /// value), and <c>db-push</c>.
    /// </summary>
    /// <param name="args">The arguments following the <c>prisma</c> verb.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor for the resolved subcommand, or null for the default.</param>
    /// <returns>The resolved subcommand's exit code.</returns>
    internal static Task<int> DispatchAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        // `prisma`'s subcommand shape mirrors clap's PrismaCommands enum — a closed set, no
        // external-subcommand catch-all — so a missing/unrecognized subcommand is a clap-layer
        // parse failure. `prisma` is Rust-classified PASSTHROUGH (not RTK_META_COMMANDS), so the
        // real oracle never reaches prisma_cmd.rs's body here; it falls back to a raw PATH-exec
        // attempt of "prisma" (exit 127 when no such binary is installed, verified directly
        // against target/release/rtk.exe) rather than a clean clap-style exit 2.
        if (args.Length == 0)
        {
            throw new CommandArgumentParseException("prisma requires a subcommand (generate, migrate, db-push)");
        }

        switch (args[0])
        {
            case "generate":
                return RunGenerateAsync(args[1..], verbose, executor);

            case "db-push":
                return RunDbPushAsync(args[1..], verbose, executor);

            case "migrate":
                return DispatchMigrateAsync(args[1..], verbose, executor);

            default:
                throw new CommandArgumentParseException($"unknown prisma subcommand '{args[0]}'");
        }
    }

    private static Task<int> DispatchMigrateAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        if (args.Length == 0)
        {
            throw new CommandArgumentParseException("prisma migrate requires a subcommand (dev, status, deploy)");
        }

        switch (args[0])
        {
            case "dev":
                var (name, devArgs) = ExtractMigrationName(args[1..]);
                return RunMigrateDevAsync(name, devArgs, verbose, executor);

            case "status":
                return RunMigrateStatusAsync(args[1..], verbose, executor);

            case "deploy":
                return RunMigrateDeployAsync(args[1..], verbose, executor);

            default:
                throw new CommandArgumentParseException($"unknown prisma migrate subcommand '{args[0]}'");
        }
    }

    /// <summary>
    /// Extracts an optional <c>--name</c>/<c>-n</c> value (in <c>--name X</c>, <c>--name=X</c>,
    /// <c>-n X</c>, or <c>-n=X</c> form) from <c>migrate dev</c>'s arguments, mirroring clap's
    /// <c>#[arg(short, long)] name: Option&lt;String&gt;</c> (<c>main.rs</c>:1052-1053).
    /// </summary>
    /// <param name="args">The arguments following <c>migrate dev</c>.</param>
    /// <returns>The resolved migration name (or null if absent) and the remaining arguments.</returns>
    internal static (string? Name, string[] Args) ExtractMigrationName(string[] args)
    {
        string? name = null;
        var rest = new List<string>();
        var i = 0;

        while (i < args.Length)
        {
            var token = args[i];

            if (token is "--name" or "-n")
            {
                if (i + 1 < args.Length)
                {
                    name = args[i + 1];
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (token.StartsWith("--name=", StringComparison.Ordinal))
            {
                name = token["--name=".Length..];
                i++;
                continue;
            }

            if (token.StartsWith("-n=", StringComparison.Ordinal))
            {
                name = token[3..];
                i++;
                continue;
            }

            rest.Add(token);
            i++;
        }

        return (name, rest.ToArray());
    }

    // -----------------------------------------------------------------------
    // create_prisma_command (prisma_cmd.rs:32-40)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reports whether <paramref name="name"/> is directly resolvable on <c>PATH</c>. Mirrors Rust's
    /// <c>tool_exists</c> (<c>src/core/utils.rs</c>:361-363), the same convention <c>TscCommand</c>
    /// uses for its own <c>tool_exists("tsc")</c> port.
    /// </summary>
    /// <param name="name">The tool binary name to check.</param>
    /// <returns>True if <paramref name="name"/> resolves to a real path on <c>PATH</c>.</returns>
    internal static bool ToolExists(string name) => PathResolver.Resolve(name) != name;

    /// <summary>
    /// Resolves the file name and leading arguments for invoking prisma: global <c>prisma</c> if
    /// present on <c>PATH</c>, otherwise <c>npx prisma</c>. Faithful port of
    /// <c>create_prisma_command</c> (<c>prisma_cmd.rs</c>:32-40).
    /// </summary>
    /// <returns>The resolved file name and base arguments (empty for a direct <c>prisma</c> resolution, <c>["prisma"]</c> for the <c>npx</c> fallback).</returns>
    internal static (string FileName, string[] BaseArgs) CreatePrismaCommand() =>
        ToolExists("prisma") ? ("prisma", []) : ("npx", ["prisma"]);

    // -----------------------------------------------------------------------
    // Shared exec + failure-path helper
    // -----------------------------------------------------------------------

    private static async Task<ExecutionResult> ExecuteAsync(IProcessExecutor exec, IReadOnlyList<string> args, string failureLabel)
    {
        var (fileName, baseArgs) = CreatePrismaCommand();
        var fullArgs = new List<string>(baseArgs);
        fullArgs.AddRange(args);

        var request = new ExecutionRequest(fileName, fullArgs, CaptureMode: ExecutionCaptureMode.Separate);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run {failureLabel}{detail}");
        }

        return result;
    }

    /// <summary>
    /// Runs the shared non-zero-exit failure path: echoes non-empty trimmed stdout and stderr
    /// verbatim to STDERR (matching Rust's <c>eprint!</c> calls, not stdout) and tracks the raw
    /// (unfiltered) text as both input and output, then returns the child's exit code. Faithful port
    /// of the identical <c>if !result.success() {{ ... }}</c> block repeated at the top of
    /// <c>run_generate</c>/<c>run_migrate</c>/<c>run_db_push</c> (<c>prisma_cmd.rs</c>:61-70, 115-124,
    /// 156-165) — genuinely the same shared pattern reimplemented per subcommand in the Rust source,
    /// not four independently-drifting copies.
    /// </summary>
    /// <param name="result">The child process's execution result.</param>
    /// <param name="raw">The combined <c>stdout + "\n" + stderr</c> text, as tracked on both sides of the failure ledger entry.</param>
    /// <param name="timer">The in-flight timed execution to record against.</param>
    /// <param name="originalCmd">The original-command label passed to <see cref="TimedExecution.Track"/>.</param>
    /// <param name="rtkCmd">The rtk-command label passed to <see cref="TimedExecution.Track"/>.</param>
    /// <returns>The child's exit code.</returns>
    private static int ReportFailure(ExecutionResult result, string raw, TimedExecution timer, string originalCmd, string rtkCmd)
    {
        if (result.Stdout.Trim().Length > 0)
        {
            Console.Error.Write(result.Stdout);
        }

        if (result.Stderr.Trim().Length > 0)
        {
            Console.Error.Write(result.Stderr);
        }

        timer.Track(originalCmd, rtkCmd, raw, raw);
        return result.ExitCode;
    }

    // -----------------------------------------------------------------------
    // run_generate (prisma_cmd.rs:42-77)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs <c>prisma generate [args]</c>: on failure, the shared failure path applies (see class
    /// remarks); on success, filters via <see cref="FilterPrismaGenerate"/>.
    /// </summary>
    /// <param name="args">The trailing arguments to pass to <c>generate</c>.</param>
    /// <param name="verbose">The verbosity level; a nonzero value logs the resolved command being run.</param>
    /// <param name="executor">The process executor to run <c>prisma</c> with, or null for the default.</param>
    /// <returns><c>0</c> on success, or the child's exit code on failure.</returns>
    internal static async Task<int> RunGenerateAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();
        var exec = executor ?? new ProcessExecutor();

        if (verbose > 0)
        {
            Console.Error.Write("Running: prisma generate\n");
        }

        var cmdArgs = new List<string> { "generate" };
        cmdArgs.AddRange(args);

        var result = await ExecuteAsync(exec, cmdArgs, "prisma generate (try: npm install -g prisma)").ConfigureAwait(false);
        var raw = result.Stdout + "\n" + result.Stderr;

        if (result.ExitCode != 0)
        {
            return ReportFailure(result, raw, timer, "prisma generate", "rtk prisma generate");
        }

        var filtered = FilterPrismaGenerate(raw);
        Console.Out.Write(filtered + "\n");
        timer.Track("prisma generate", "rtk prisma generate", raw, filtered);

        return 0;
    }

    // -----------------------------------------------------------------------
    // run_migrate (prisma_cmd.rs:79-136)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs <c>prisma migrate dev [--name X] [args]</c>: on failure, the shared failure path applies;
    /// on success, filters via <see cref="FilterMigrateDev"/>.
    /// </summary>
    /// <param name="name">The optional <c>--name</c>/<c>-n</c> migration name.</param>
    /// <param name="args">The remaining trailing arguments.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run <c>prisma</c> with, or null for the default.</param>
    /// <returns><c>0</c> on success, or the child's exit code on failure.</returns>
    internal static Task<int> RunMigrateDevAsync(string? name, string[] args, int verbose, IProcessExecutor? executor)
    {
        var cmdArgs = new List<string> { "dev" };
        if (name is not null)
        {
            cmdArgs.Add("--name");
            cmdArgs.Add(name);
        }

        return RunMigrateAsync(cmdArgs, args, "prisma migrate dev", FilterMigrateDev, verbose, executor);
    }

    /// <summary>
    /// Runs <c>prisma migrate status [args]</c>: on failure, the shared failure path applies; on
    /// success, filters via <see cref="FilterMigrateStatus"/>.
    /// </summary>
    /// <param name="args">The trailing arguments to pass to <c>status</c>.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run <c>prisma</c> with, or null for the default.</param>
    /// <returns><c>0</c> on success, or the child's exit code on failure.</returns>
    internal static Task<int> RunMigrateStatusAsync(string[] args, int verbose, IProcessExecutor? executor) =>
        RunMigrateAsync(["status"], args, "prisma migrate status", FilterMigrateStatus, verbose, executor);

    /// <summary>
    /// Runs <c>prisma migrate deploy [args]</c>: on failure, the shared failure path applies; on
    /// success, filters via <see cref="FilterMigrateDeploy"/>.
    /// </summary>
    /// <param name="args">The trailing arguments to pass to <c>deploy</c>.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run <c>prisma</c> with, or null for the default.</param>
    /// <returns><c>0</c> on success, or the child's exit code on failure.</returns>
    internal static Task<int> RunMigrateDeployAsync(string[] args, int verbose, IProcessExecutor? executor) =>
        RunMigrateAsync(["deploy"], args, "prisma migrate deploy", FilterMigrateDeploy, verbose, executor);

    private static async Task<int> RunMigrateAsync(
        List<string> subcommandArgs,
        string[] trailingArgs,
        string cmdName,
        Func<string, string> filter,
        int verbose,
        IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();
        var exec = executor ?? new ProcessExecutor();

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {cmdName}\n");
        }

        var cmdArgs = new List<string> { "migrate" };
        cmdArgs.AddRange(subcommandArgs);
        cmdArgs.AddRange(trailingArgs);

        var result = await ExecuteAsync(exec, cmdArgs, "prisma migrate").ConfigureAwait(false);
        var raw = result.Stdout + "\n" + result.Stderr;

        if (result.ExitCode != 0)
        {
            return ReportFailure(result, raw, timer, cmdName, $"rtk {cmdName}");
        }

        var filtered = filter(raw);
        Console.Out.Write(filtered + "\n");
        timer.Track(cmdName, $"rtk {cmdName}", raw, filtered);

        return 0;
    }

    // -----------------------------------------------------------------------
    // run_db_push (prisma_cmd.rs:138-172)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs <c>prisma db push [args]</c>: on failure, the shared failure path applies; on success,
    /// filters via <see cref="FilterDbPush"/>.
    /// </summary>
    /// <param name="args">The trailing arguments to pass to <c>db push</c>.</param>
    /// <param name="verbose">The verbosity level.</param>
    /// <param name="executor">The process executor to run <c>prisma</c> with, or null for the default.</param>
    /// <returns><c>0</c> on success, or the child's exit code on failure.</returns>
    internal static async Task<int> RunDbPushAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        var timer = TimedExecution.Start();
        var exec = executor ?? new ProcessExecutor();

        if (verbose > 0)
        {
            Console.Error.Write("Running: prisma db push\n");
        }

        var cmdArgs = new List<string> { "db", "push" };
        cmdArgs.AddRange(args);

        var result = await ExecuteAsync(exec, cmdArgs, "prisma db push").ConfigureAwait(false);
        var raw = result.Stdout + "\n" + result.Stderr;

        if (result.ExitCode != 0)
        {
            return ReportFailure(result, raw, timer, "prisma db push", "rtk prisma db push");
        }

        var filtered = FilterDbPush(raw);
        Console.Out.Write(filtered + "\n");
        timer.Track("prisma db push", "rtk prisma db push", raw, filtered);

        return 0;
    }

    // -----------------------------------------------------------------------
    // Filter methods below all delegate to RtkSharp.Filters.Commands.Js.PrismaFilters (moved in Task 7
    // of the filters-library extraction - pure text filtering, no process execution or file I/O). See
    // PrismaFilters' class remarks for the three disclosed compatibility-ledger quirks preserved there.
    // -----------------------------------------------------------------------

    /// <summary>Filters <c>prisma generate</c> output. Delegates to <see cref="PrismaFilters.FilterPrismaGenerate"/>.</summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma generate</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterPrismaGenerate(string output) => PrismaFilters.FilterPrismaGenerate(output);

    /// <summary>Filters <c>prisma migrate dev</c> output. Delegates to <see cref="PrismaFilters.FilterMigrateDev"/>.</summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate dev</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterMigrateDev(string output) => PrismaFilters.FilterMigrateDev(output);

    /// <summary>Filters <c>prisma migrate status</c> output. Delegates to <see cref="PrismaFilters.FilterMigrateStatus"/>.</summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate status</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterMigrateStatus(string output) => PrismaFilters.FilterMigrateStatus(output);

    /// <summary>Filters <c>prisma migrate deploy</c> output. Delegates to <see cref="PrismaFilters.FilterMigrateDeploy"/>.</summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate deploy</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterMigrateDeploy(string output) => PrismaFilters.FilterMigrateDeploy(output);

    /// <summary>Filters <c>prisma db push</c> output. Delegates to <see cref="PrismaFilters.FilterDbPush"/>.</summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma db push</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterDbPush(string output) => PrismaFilters.FilterDbPush(output);

    /// <summary>Extracts the first parseable integer token in <paramref name="line"/>. Delegates to <see cref="PrismaFilters.ExtractNumber"/>.</summary>
    /// <param name="line">The line to scan.</param>
    /// <returns>The first parseable integer token, or null if none is found.</returns>
    internal static int? ExtractNumber(string line) => PrismaFilters.ExtractNumber(line);
}
