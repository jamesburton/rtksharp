using System.Text;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

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
        catch (Exception ex)
        {
            // Fail-loud, same convention as NpmCommand/PnpmCommand: an rtk-level failure surfaces as
            // `rtk: {message}`.
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

        if (args.Length == 0)
        {
            Console.Error.Write("rtk: prisma requires a subcommand (generate, migrate, db-push)\n");
            return Task.FromResult(2);
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
                Console.Error.Write($"rtk: unknown prisma subcommand '{args[0]}'\n");
                return Task.FromResult(2);
        }
    }

    private static Task<int> DispatchMigrateAsync(string[] args, int verbose, IProcessExecutor? executor)
    {
        if (args.Length == 0)
        {
            Console.Error.Write("rtk: prisma migrate requires a subcommand (dev, status, deploy)\n");
            return Task.FromResult(2);
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
                Console.Error.Write($"rtk: unknown prisma migrate subcommand '{args[0]}'\n");
                return Task.FromResult(2);
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
    // filter_prisma_generate (prisma_cmd.rs:174-231)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma generate</c> output: strips ASCII-art/box-drawing lines, extracts
    /// model/enum/type counts, and reports a hardcoded output path string. See the intentional-quirk
    /// disclosure in the class remarks for the unused-<c>outputPath</c> behavior preserved here.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma generate</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterPrismaGenerate(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var models = 0;
        var enums = 0;
        var types = 0;

        // Detected but, per the disclosed Rust-source quirk (see class remarks item 1), never used
        // for anything beyond its own emptiness check below - the actual line content is discarded.
        var outputPath = string.Empty;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            if (line.Contains('█') || line.Contains('▀') || line.Contains('▄')
                || line.Contains('┌') || line.Contains('└') || line.Contains('│'))
            {
                continue;
            }

            if (line.Contains("model", StringComparison.Ordinal) && line.Contains("generated", StringComparison.Ordinal))
            {
                var num = ExtractNumber(line);
                if (num is not null)
                {
                    models = num.Value;
                }
            }

            if (line.Contains("enum", StringComparison.Ordinal))
            {
                var num = ExtractNumber(line);
                if (num is not null)
                {
                    enums = num.Value;
                }
            }

            if (line.Contains("type", StringComparison.Ordinal))
            {
                var num = ExtractNumber(line);
                if (num is not null)
                {
                    types = num.Value;
                }
            }

            if (line.Contains("node_modules", StringComparison.Ordinal) && line.Contains("@prisma", StringComparison.Ordinal))
            {
                outputPath = line.Trim();
            }
        }

        var result = new StringBuilder();
        result.Append("Prisma Client generated\n");

        if (models > 0 || enums > 0 || types > 0)
        {
            result.Append($"  • {models} models, {enums} enums, {types} types\n");
        }

        if (outputPath.Length > 0)
        {
            // Intentional quirk (class remarks item 1): always the hardcoded string, never
            // `outputPath` itself.
            result.Append("  • Output: node_modules/@prisma/client\n");
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // filter_migrate_dev (prisma_cmd.rs:233-302)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma migrate dev</c> output: extracts migration name (via the disclosed
    /// literal-<c>"202"</c>-substring quirk, class remarks item 2) and change counts, and reports
    /// <c>"Applied | Pending: 0"</c> with the pending count hardcoded to <c>0</c> (class remarks item
    /// 3) whenever any line indicates the migration was applied.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate dev</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterMigrateDev(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var migrationName = string.Empty;
        var tablesAdded = 0;
        var tablesModified = 0;
        var relations = new List<string>();
        var indexes = new List<string>();
        var applied = false;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            // Intentional quirk (class remarks item 2): a literal "202" substring search, not a
            // date-prefix parser - a migration name not literally containing "202" is never
            // extracted, and any string containing "202" anywhere (not necessarily as a timestamp
            // prefix) matches.
            if (line.Contains("migration", StringComparison.Ordinal) && line.Contains('_'))
            {
                var pos = line.IndexOf("202", StringComparison.Ordinal);
                if (pos >= 0)
                {
                    var end = FindWhitespaceOffset(line, pos) ?? (line.Length - pos);
                    migrationName = line.Substring(pos, end);
                }
            }

            if (line.Contains("CREATE TABLE", StringComparison.Ordinal))
            {
                tablesAdded++;
            }

            if (line.Contains("ALTER TABLE", StringComparison.Ordinal))
            {
                tablesModified++;
            }

            if (line.Contains("FOREIGN KEY", StringComparison.Ordinal) || line.Contains("REFERENCES", StringComparison.Ordinal))
            {
                var table = ExtractTableName(line);
                if (table is not null)
                {
                    relations.Add(table);
                }
            }

            if (line.Contains("CREATE INDEX", StringComparison.Ordinal) || line.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal))
            {
                var idx = ExtractIndexName(line);
                if (idx is not null)
                {
                    indexes.Add(idx);
                }
            }

            if (line.Contains("applied", StringComparison.Ordinal) || line.Contains('✓'))
            {
                applied = true;
            }
        }

        var result = new StringBuilder();

        if (migrationName.Length > 0)
        {
            result.Append($"Migration: {migrationName}\n");
        }

        result.Append("Changes:\n");

        if (tablesAdded > 0)
        {
            result.Append($"  + {tablesAdded} table(s)\n");
        }

        if (tablesModified > 0)
        {
            result.Append($"  ~ {tablesModified} table(s) modified\n");
        }

        if (relations.Count > 0)
        {
            result.Append($"  + {relations.Count} relation(s)\n");
        }

        if (indexes.Count > 0)
        {
            result.Append($"  ~ {indexes.Count} index(es)\n");
        }

        result.Append('\n');

        if (applied)
        {
            // Intentional quirk (class remarks item 3): "Pending: 0" is a hardcoded literal, not a
            // computed pending-migration count - this fires unconditionally whenever `applied` is
            // true, regardless of any actual pending-migration state in `output`.
            result.Append("Applied | Pending: 0\n");
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // filter_migrate_status (prisma_cmd.rs:304-336)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma migrate status</c> output: counts <c>"applied"</c>/<c>"pending"</c>/
    /// <c>"unapplied"</c> lines and extracts a <c>"202"</c>-prefixed latest migration name (the same
    /// literal-substring assumption as <see cref="FilterMigrateDev"/>, class remarks item 2).
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate status</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterMigrateStatus(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var appliedCount = 0;
        var pendingCount = 0;
        var latestMigration = string.Empty;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            if (line.Contains("applied", StringComparison.Ordinal))
            {
                appliedCount++;

                if (latestMigration.Length == 0 && line.Contains("202", StringComparison.Ordinal))
                {
                    var pos = line.IndexOf("202", StringComparison.Ordinal);
                    if (pos >= 0)
                    {
                        var end = FindWhitespaceOffset(line, pos) ?? 20;
                        // Faithful port of Rust's `line[pos..pos + end]`: the fallback of 20 is a
                        // literal length, not clamped to the remaining string length, matching the
                        // Rust source exactly (which would panic on an out-of-bounds slice under the
                        // same condition - preserved here via a defensive clamp so this filter never
                        // crashes rtk, per the mandatory fallback pattern).
                        end = Math.Min(end, line.Length - pos);
                        latestMigration = line.Substring(pos, end);
                    }
                }
            }

            if (line.Contains("pending", StringComparison.Ordinal) || line.Contains("unapplied", StringComparison.Ordinal))
            {
                pendingCount++;
            }
        }

        var result = new StringBuilder();
        result.Append($"Migrations: {appliedCount} applied, {pendingCount} pending\n");

        if (latestMigration.Length > 0)
        {
            result.Append($"Latest: {latestMigration}\n");
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // filter_migrate_deploy (prisma_cmd.rs:338-364)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma migrate deploy</c> output: reports up to 5 error lines under a
    /// <c>"[FAIL] Deployment failed:"</c> header if any error lines are found, otherwise
    /// <c>"{N} migration(s) deployed"</c>.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma migrate deploy</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterMigrateDeploy(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var deployed = 0;
        var errors = new List<string>();

        foreach (var line in ReadCommand.SplitLines(output))
        {
            if (line.Contains("applied", StringComparison.Ordinal) || line.Contains('✓'))
            {
                deployed++;
            }

            if (line.Contains("error", StringComparison.Ordinal) || line.Contains("ERROR", StringComparison.Ordinal))
            {
                errors.Add(line.Trim());
            }
        }

        var result = new StringBuilder();

        if (errors.Count == 0)
        {
            result.Append($"{deployed} migration(s) deployed\n");
        }
        else
        {
            result.Append("[FAIL] Deployment failed:\n");
            foreach (var err in errors.Take(5))
            {
                result.Append($"  {err}\n");
            }
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // filter_db_push (prisma_cmd.rs:366-395)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Filters <c>prisma db push</c> output: counts <c>CREATE TABLE</c>/<c>ALTER</c>|<c>ADD COLUMN</c>/
    /// <c>DROP</c> occurrences and always emits the <c>"Schema pushed to database"</c> header.
    /// </summary>
    /// <param name="output">The raw combined stdout+stderr from <c>prisma db push</c>.</param>
    /// <returns>The filtered summary.</returns>
    internal static string FilterDbPush(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var tablesAdded = 0;
        var columnsModified = 0;
        var dropped = 0;

        foreach (var line in ReadCommand.SplitLines(output))
        {
            if (line.Contains("CREATE TABLE", StringComparison.Ordinal))
            {
                tablesAdded++;
            }

            if (line.Contains("ALTER", StringComparison.Ordinal) || line.Contains("ADD COLUMN", StringComparison.Ordinal))
            {
                columnsModified++;
            }

            if (line.Contains("DROP", StringComparison.Ordinal))
            {
                dropped++;
            }
        }

        var result = new StringBuilder();
        result.Append("Schema pushed to database\n");

        if (tablesAdded > 0 || columnsModified > 0 || dropped > 0)
        {
            result.Append($"  + {tablesAdded} tables, ~ {columnsModified} columns, - {dropped} dropped\n");
        }

        return result.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // extract_number / extract_table_name / extract_index_name (prisma_cmd.rs:397-435)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts the first whitespace-delimited token in <paramref name="line"/> that parses as a
    /// non-negative integer. Faithful port of <c>extract_number</c> (<c>prisma_cmd.rs</c>:398-401).
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <returns>The first parseable integer token, or null if none is found.</returns>
    internal static int? ExtractNumber(string line)
    {
        foreach (var word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(word, out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the table name immediately following a bare <c>TABLE</c> token in
    /// <paramref name="line"/>, trimming surrounding backticks/quotes/semicolons. Faithful port of
    /// <c>extract_table_name</c> (<c>prisma_cmd.rs</c>:404-418).
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <returns>The extracted table name, or null if <c>TABLE</c> is absent or has no following token.</returns>
    internal static string? ExtractTableName(string line) => ExtractTokenAfter(line, "TABLE");

    /// <summary>
    /// Extracts the index name immediately following a bare <c>INDEX</c> token in
    /// <paramref name="line"/>, trimming surrounding backticks/quotes/semicolons. Faithful port of
    /// <c>extract_index_name</c> (<c>prisma_cmd.rs</c>:421-435).
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <returns>The extracted index name, or null if <c>INDEX</c> is absent or has no following token.</returns>
    internal static string? ExtractIndexName(string line) => ExtractTokenAfter(line, "INDEX");

    private static string? ExtractTokenAfter(string line, string marker)
    {
        if (!line.Contains(marker, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == marker && i + 1 < parts.Length)
            {
                return parts[i + 1].Trim('`', '"', ';');
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the offset (relative to <paramref name="start"/>) of the first whitespace character in
    /// <paramref name="line"/> at or after <paramref name="start"/>. Mirrors Rust's
    /// <c>line[pos..].find(|c: char| c.is_whitespace())</c>.
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <param name="start">The index to start scanning from.</param>
    /// <returns>The relative offset of the first whitespace character, or null if none is found.</returns>
    private static int? FindWhitespaceOffset(string line, int start)
    {
        for (var i = start; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                return i - start;
            }
        }

        return null;
    }
}
