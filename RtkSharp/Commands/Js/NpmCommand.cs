using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Js;

/// <summary>
/// Which target an <c>npx</c> invocation was routed to. Mirrors the <c>match args[0].as_str()</c>
/// dispatch inline in Rust's <c>Commands::Npx</c> arm (<c>main.rs</c>:2163-2218).
/// </summary>
public enum NpxRouteKind
{
    /// <summary><c>npx tsc</c>/<c>npx typescript</c> — routes to the future tsc filter (Phase 8 Task 4).</summary>
    Tsc,

    /// <summary><c>npx playwright</c> — delegates to <see cref="PlaywrightCommand.RunSafeAsync"/>.</summary>
    Playwright,

    /// <summary><c>npx prisma generate</c> — delegates to <see cref="PrismaCommand.RunGenerateAsync"/>.</summary>
    PrismaGenerate,

    /// <summary><c>npx prisma db push</c> — delegates to <see cref="PrismaCommand.RunDbPushAsync"/>.</summary>
    PrismaDbPush,

    /// <summary>
    /// Any other <c>npx prisma ...</c> invocation (including bare <c>npx prisma</c>) — raw passthrough,
    /// exactly like Rust's manual <c>TimedExecution</c> passthrough branch (<c>main.rs</c>:2187-2211).
    /// </summary>
    PrismaPassthrough,

    /// <summary><c>npx eslint</c> — delegates to <see cref="LintCommand.RunAsync(string[], int, IProcessExecutor)"/>.</summary>
    Eslint,

    /// <summary><c>npx next</c> — delegates to <see cref="NextCommand.RunNextSafeAsync"/>.</summary>
    Next,

    /// <summary><c>npx prettier</c> — delegates to <see cref="PrettierCommand.RunPrettierSafeAsync"/>.</summary>
    Prettier,

    /// <summary>
    /// Any unrecognized tool — falls through to <c>npm_cmd::exec</c>'s filtered pipeline
    /// (<c>filter_npm_output</c> via <c>npx</c>), exactly like Rust's default arm.
    /// </summary>
    Default,
}

/// <summary>
/// The resolved routing decision for an <c>npx</c> invocation: which target handles it, and the
/// argument vectors each handling strategy needs.
/// </summary>
/// <param name="Kind">Which target the tool name routed to.</param>
/// <param name="RemainingArgs">
/// The arguments after the routed tool name (and, for prisma, after its recognized subcommand) —
/// informational for the stub routes, since no filter exists yet to consume them.
/// </param>
/// <param name="PassthroughArgs">
/// The full argument vector (including the tool name, e.g. <c>["prisma", "migrate", "status"]</c>) to
/// hand to <c>npx</c> unchanged for the passthrough/default routes.
/// </param>
internal readonly record struct NpxRoute(NpxRouteKind Kind, string[] RemainingArgs, string[] PassthroughArgs);

/// <summary>
/// Implements the <c>rtk npm</c> and <c>rtk npx</c> CLI verbs. Hosts both the <c>npm</c> auto-
/// <c>run</c>-injection/filtering pipeline and <c>npx</c>'s tool-name dispatch table, mirroring how
/// Rust's <c>src/cmds/js/npm_cmd.rs</c> hosts <c>run()</c> (npm) and <c>exec()</c> (npx's fallback),
/// while <c>npx</c>'s routing table itself lives inline in <c>main.rs</c>'s <c>Commands::Npx</c> arm
/// (<c>main.rs</c>:2163-2218) rather than in a dedicated Rust module.
/// </summary>
/// <remarks>
/// <para>
/// <b>All future-task routes now wired.</b> Phase 8 Task 2 only ported npm/npx, leaving tsc
/// (Task 4), playwright (Task 6), and prisma (Task 7) as stubs that threw
/// <see cref="NotImplementedException"/> naming the future task. Task 4 landed
/// <see cref="TscCommand"/>, Task 6 landed <see cref="PlaywrightCommand"/>, and Task 7 landed
/// <see cref="PrismaCommand"/> — the <c>npx tsc</c>/<c>npx typescript</c>, <c>npx playwright</c>,
/// <c>npx prisma generate</c>, and <c>npx prisma db push</c> routes all now delegate to their real
/// filter instead of throwing. <c>route.RemainingArgs</c> (already subcommand-stripped) is passed
/// straight through, matching each filter's own argument shape.
/// </para>
/// <para>
/// <b>eslint/next/prettier: now delegate to their real filters (was raw passthrough, resolved).</b>
/// This block previously routed all three to unfiltered <c>npx</c> passthrough, on the premise that
/// Rust would only special-case them if <c>lint_cmd</c>/<c>next_cmd</c>/<c>prettier_cmd</c> existed as
/// ported RtkSharp modules — but <c>main.rs</c>'s <c>Commands::Npx</c> arm (<c>main.rs</c>:2171,
/// 2210-2211) unconditionally routes <c>eslint</c>/<c>next</c>/<c>prettier</c> to
/// <c>lint_cmd::run</c>/<c>next_cmd::run</c>/<c>prettier_cmd::run</c> regardless, since those Rust
/// modules always existed; the premise only held while RtkSharp's own <see cref="LintCommand"/>/
/// <see cref="NextCommand"/>/<see cref="PrettierCommand"/> hadn't been ported yet. Now that they have,
/// <see cref="NpxRouteKind.Eslint"/>/<see cref="NpxRouteKind.Next"/>/<see cref="NpxRouteKind.Prettier"/>
/// delegate to them directly instead of passthrough, matching <c>main.rs</c>'s dispatch exactly.
/// Only an unrecognized <c>prisma</c> subcommand still uses genuine manual-passthrough
/// (<c>TimedExecution</c>/<c>resolved_command</c> rather than the shared <c>run_filtered</c>
/// skeleton, <c>main.rs</c>:2187-2211) — see <see cref="NpxRouteKind.PrismaPassthrough"/>.
/// </para>
/// <para>
/// <b>Implicit tracking via <see cref="CommandRunner"/>.</b> The npm path and npx's default
/// (unrecognized-tool) path both run through <see cref="CommandRunner.RunFilteredAsync"/> exactly like
/// other CommandRunner-based filters (<c>ls</c>/<c>wc</c>/<c>tree</c>) — no manual
/// <see cref="TimedExecution"/> call is made for them, matching Rust's own <c>runner::run_filtered</c>
/// skeleton where tracking happens implicitly inside the shared runner. Only the passthrough branches
/// (eslint/next/prettier/prisma-passthrough) call <see cref="TimedExecution.TrackPassthrough"/>
/// manually, because Rust's own source does exactly that for its one manual-passthrough branch.
/// </para>
/// </remarks>
public static class NpmCommand
{
    /// <summary>
    /// Known npm subcommands that should NOT get "run" injected. Ported verbatim from
    /// <c>NPM_SUBCOMMANDS</c> (<c>npm_cmd.rs</c>:9-74) — shared between production code and tests here
    /// too, to avoid drift the same way the Rust source does.
    /// </summary>
    internal static readonly string[] NpmSubcommands =
    {
        "install",
        "i",
        "ci",
        "uninstall",
        "remove",
        "rm",
        "update",
        "up",
        "list",
        "ls",
        "outdated",
        "init",
        "create",
        "publish",
        "pack",
        "link",
        "audit",
        "fund",
        "exec",
        "explain",
        "why",
        "search",
        "view",
        "info",
        "show",
        "config",
        "set",
        "get",
        "cache",
        "prune",
        "dedupe",
        "doctor",
        "help",
        "version",
        "prefix",
        "root",
        "bin",
        "bugs",
        "docs",
        "home",
        "repo",
        "ping",
        "whoami",
        "token",
        "profile",
        "team",
        "access",
        "owner",
        "deprecate",
        "dist-tag",
        "star",
        "stars",
        "login",
        "logout",
        "adduser",
        "unpublish",
        "pkg",
        "diff",
        "rebuild",
        "test",
        "t",
        "start",
        "stop",
        "restart",
    };

    /// <summary>
    /// Registry entry point for the <c>npm</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/> and
    /// <see cref="RuntimeOptions.SkipEnv"/> (npm's registry delegate cannot receive them as
    /// arguments), applies auto-<c>run</c> injection, and runs the filtered pipeline.
    /// </summary>
    /// <param name="args">The arguments following the <c>npm</c> verb.</param>
    /// <returns><c>npm</c>'s exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> RunAsync(string[] args) =>
        RunNpmSafeAsync(args, RuntimeOptions.Verbosity, RuntimeOptions.SkipEnv);

    /// <summary>
    /// Registry entry point for the <c>npx</c> verb. Reads <see cref="RuntimeOptions.Verbosity"/> and
    /// <see cref="RuntimeOptions.SkipEnv"/>, then dispatches on <c>args[0]</c> per
    /// <see cref="ResolveNpxRoute"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>npx</c> verb.</param>
    /// <returns>The routed target's exit code (or 1 on an rtk-level failure).</returns>
    public static Task<int> ExecAsync(string[] args) =>
        RunNpxSafeAsync(args, RuntimeOptions.Verbosity, RuntimeOptions.SkipEnv, executor: null);

    /// <summary>
    /// Test-friendly overload of the <c>npm</c> entry point taking explicit verbosity/skip-env values
    /// instead of reading <see cref="RuntimeOptions"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>npm</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <param name="skipEnv">Whether to set <c>SKIP_ENV_VALIDATION=1</c> for the child process.</param>
    /// <returns><c>npm</c>'s exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunNpmSafeAsync(string[] args, int verbose, bool skipEnv)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            var effectiveArgs = BuildEffectiveArgs(args);
            return await RunFilteredAsync("npm", effectiveArgs, verbose, skipEnv).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Fail-loud, same convention as RunCommand/ProxyCommand: an rtk-level failure (not a
            // filter failure — CommandRunner already guards those) surfaces as `rtk: {message}`.
            // CommandArgumentParseException must propagate to RtkProgram's dispatch layer instead
            // (re-routed to the TOML-fallback/raw-passthrough path); not thrown from this command
            // today, but guarded proactively — see DockerCommand/PrismaCommand's own remarks for
            // the swallowed-exception bug this prevents.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Test-friendly overload of the <c>npx</c> entry point taking explicit verbosity/skip-env values
    /// and an injectable <see cref="IProcessExecutor"/> for the passthrough routes.
    /// </summary>
    /// <param name="args">The arguments following the <c>npx</c> verb.</param>
    /// <param name="verbose">The verbosity level (mirrors Rust's <c>cli.verbose</c>).</param>
    /// <param name="skipEnv">Whether to set <c>SKIP_ENV_VALIDATION=1</c> for the default route's child process.</param>
    /// <param name="executor">The process executor used by the passthrough routes, or null for the default.</param>
    /// <returns>The routed target's exit code (or 1 on an rtk-level failure).</returns>
    internal static async Task<int> RunNpxSafeAsync(
        string[] args, int verbose, bool skipEnv, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await DispatchNpxAsync(args, verbose, skipEnv, executor).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            // Guarded proactively, not thrown from this command today — see NpmCommand's other
            // catch (above) and DockerCommand/PrismaCommand's remarks for why.
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Dispatches an <c>npx</c> invocation to its routed target. Ports the body of Rust's
    /// <c>Commands::Npx</c> arm (<c>main.rs</c>:2163-2218): an empty argument list bails with the exact
    /// message <c>"npx requires a command argument"</c>; otherwise <see cref="ResolveNpxRoute"/>
    /// determines the target and this method executes (or stub-throws for) it.
    /// </summary>
    /// <param name="args">The arguments following the <c>npx</c> verb.</param>
    /// <param name="verbose">The verbosity level, forwarded to the default route's filtered pipeline.</param>
    /// <param name="skipEnv">Whether to set <c>SKIP_ENV_VALIDATION=1</c> for the default route's child process.</param>
    /// <param name="executor">The process executor used by the passthrough routes, or null for the default.</param>
    /// <returns>The routed target's exit code.</returns>
    internal static Task<int> DispatchNpxAsync(string[] args, int verbose, bool skipEnv, IProcessExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            throw new InvalidOperationException("npx requires a command argument");
        }

        var route = ResolveNpxRoute(args);

        return route.Kind switch
        {
            NpxRouteKind.Tsc => TscCommand.RunTscSafeAsync(route.RemainingArgs, verbose),
            NpxRouteKind.Playwright => PlaywrightCommand.RunSafeAsync(route.RemainingArgs, verbose, executor),
            NpxRouteKind.PrismaGenerate => PrismaCommand.RunGenerateAsync(route.RemainingArgs, verbose, executor),
            NpxRouteKind.PrismaDbPush => PrismaCommand.RunDbPushAsync(route.RemainingArgs, verbose, executor),
            NpxRouteKind.Eslint => LintCommand.RunAsync(route.RemainingArgs, verbose, executor ?? new ProcessExecutor()),
            NpxRouteKind.Next => NextCommand.RunNextSafeAsync(route.RemainingArgs, verbose),
            NpxRouteKind.Prettier => PrettierCommand.RunPrettierSafeAsync(route.RemainingArgs, verbose),
            NpxRouteKind.PrismaPassthrough => RunNpxPassthroughAsync(route.PassthroughArgs, executor),
            NpxRouteKind.Default => RunFilteredAsync("npx", route.PassthroughArgs, verbose, skipEnv),
            _ => throw new ArgumentOutOfRangeException(nameof(args), route.Kind, "Unknown npx route kind."),
        };
    }

    /// <summary>
    /// Resolves which target an <c>npx</c> invocation routes to, without executing anything. Pure
    /// port of the <c>match args[0].as_str()</c> table inline in Rust's <c>Commands::Npx</c> arm
    /// (<c>main.rs</c>:2169-2217), split out as a testable function.
    /// </summary>
    /// <param name="args">The non-empty arguments following the <c>npx</c> verb.</param>
    /// <returns>The resolved route.</returns>
    internal static NpxRoute ResolveNpxRoute(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            throw new ArgumentException("args must be non-empty.", nameof(args));
        }

        var tool = args[0];
        var rest = args.Skip(1).ToArray();
        var full = args.ToArray();

        return tool switch
        {
            "tsc" or "typescript" => new NpxRoute(NpxRouteKind.Tsc, rest, full),
            "eslint" => new NpxRoute(NpxRouteKind.Eslint, rest, full),
            "prisma" => ResolvePrismaRoute(rest, full),
            "next" => new NpxRoute(NpxRouteKind.Next, rest, full),
            "prettier" => new NpxRoute(NpxRouteKind.Prettier, rest, full),
            "playwright" => new NpxRoute(NpxRouteKind.Playwright, rest, full),
            _ => new NpxRoute(NpxRouteKind.Default, rest, full),
        };
    }

    /// <summary>
    /// Resolves the sub-routing for <c>npx prisma ...</c>. Ports the nested <c>match args[1]</c>
    /// (<c>main.rs</c>:2176-2202): <c>generate</c> and <c>db push</c> route to the future prisma
    /// filter; a bare <c>npx prisma</c> or any other subcommand passes through raw, exactly as Rust's
    /// manual <c>TimedExecution</c> branch does.
    /// </summary>
    /// <param name="rest">The arguments after <c>prisma</c>.</param>
    /// <param name="full">The full <c>npx</c> argument vector (<c>["prisma", ...]</c>), for passthrough.</param>
    /// <returns>The resolved prisma route.</returns>
    private static NpxRoute ResolvePrismaRoute(string[] rest, string[] full)
    {
        if (rest.Length == 0)
        {
            // Bare `npx prisma` (main.rs:2203-2211): passthrough with just "prisma".
            return new NpxRoute(NpxRouteKind.PrismaPassthrough, rest, new[] { "prisma" });
        }

        if (rest[0] == "generate")
        {
            return new NpxRoute(NpxRouteKind.PrismaGenerate, rest.Skip(1).ToArray(), full);
        }

        if (rest[0] == "db" && rest.Length > 1 && rest[1] == "push")
        {
            return new NpxRoute(NpxRouteKind.PrismaDbPush, rest.Skip(2).ToArray(), full);
        }

        // Passthrough other prisma subcommands (main.rs:2187-2201).
        return new NpxRoute(NpxRouteKind.PrismaPassthrough, rest, full);
    }

    /// <summary>
    /// Reports whether <c>args</c>' first element needs a <c>"run"</c> subcommand injected in front of
    /// it: true when it is neither the explicit <c>"run"</c> subcommand, a known
    /// <see cref="NpmSubcommands"/> entry, nor a flag. Ports the routing predicate inline in
    /// Rust's <c>run()</c> (<c>npm_cmd.rs</c>:79-83).
    /// </summary>
    /// <param name="args">The arguments following the <c>npm</c> verb.</param>
    /// <returns>True when <c>"run"</c> should be injected.</returns>
    internal static bool NeedsRunInjection(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var first = args.Count > 0 ? args[0] : null;
        var isRunExplicit = first == "run";
        var isNpmSubcommand = first is not null && (NpmSubcommands.Contains(first) || first.StartsWith('-'));

        return !isRunExplicit && !isNpmSubcommand;
    }

    /// <summary>
    /// Builds the effective argument vector for <c>npm</c>, injecting <c>"run"</c> in front when
    /// <see cref="NeedsRunInjection"/> says to. Ports the effective-args assembly in Rust's <c>run()</c>
    /// (<c>npm_cmd.rs</c>:85-92).
    /// </summary>
    /// <param name="args">The raw arguments following the <c>npm</c> verb.</param>
    /// <returns>The effective arguments to pass to <c>npm</c>.</returns>
    internal static string[] BuildEffectiveArgs(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!NeedsRunInjection(args))
        {
            return args;
        }

        // "rtk npm build" -> "npm run build" (assume script name).
        var effective = new string[args.Length + 1];
        effective[0] = "run";
        Array.Copy(args, 0, effective, 1, args.Length);
        return effective;
    }

    /// <summary>
    /// Shared command-execution path for npm's <c>run</c> and npx's default (unrecognized-tool) route.
    /// Applies <c>SKIP_ENV_VALIDATION</c>, emits the verbose log line, and routes through
    /// <see cref="CommandRunner.RunFilteredAsync"/> with <see cref="FilterNpmOutput"/> — tracking
    /// happens implicitly inside that shared skeleton, matching Rust's <c>run_filtered</c>
    /// (<c>npm_cmd.rs</c>:111-133).
    /// </summary>
    /// <param name="name">The binary to run (<c>"npm"</c> or <c>"npx"</c>).</param>
    /// <param name="args">The effective arguments to pass to <paramref name="name"/>.</param>
    /// <param name="verbose">The verbosity level; a nonzero value logs the command being run.</param>
    /// <param name="skipEnv">Whether to set <c>SKIP_ENV_VALIDATION=1</c> for the child process.</param>
    /// <returns>The child process's exit code.</returns>
    private static Task<int> RunFilteredAsync(string name, IReadOnlyList<string> args, int verbose, bool skipEnv)
    {
        var argsDisplay = string.Join(' ', args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {name} {argsDisplay}\n");
        }

        return CommandRunner.RunFilteredAsync(
            name,
            args,
            name,
            argsDisplay,
            FilterNpmOutput,
            new RunOptions(),
            BuildEnvironment(skipEnv)
        );
    }

    /// <summary>
    /// Builds the child-process environment overrides for <see cref="skipEnv"/>: when true, sets
    /// <c>SKIP_ENV_VALIDATION=1</c> (mirrors <c>cmd.env("SKIP_ENV_VALIDATION", "1")</c>,
    /// <c>npm_cmd.rs</c>:117-119); when false, no overrides (inherit the parent's environment).
    /// Extracted as a pure, directly testable function since <see cref="CommandRunner.RunFilteredAsync"/>
    /// spawns a real child process and cannot be safely exercised with a fake in unit tests.
    /// </summary>
    /// <param name="skipEnv">Whether the caller requested <c>SKIP_ENV_VALIDATION=1</c>.</param>
    /// <returns>The environment overrides to apply, or null to inherit unchanged.</returns>
    internal static IReadOnlyDictionary<string, string?>? BuildEnvironment(bool skipEnv) =>
        skipEnv
            ? new Dictionary<string, string?>(StringComparer.Ordinal) { ["SKIP_ENV_VALIDATION"] = "1" }
            : null;

    /// <summary>
    /// Runs <c>npx &lt;args&gt;</c> as an unfiltered, inherited-stdio passthrough, tracking it manually
    /// via <see cref="TimedExecution.TrackPassthrough"/> exactly like Rust's manual passthrough branch
    /// for an unrecognized <c>prisma</c> subcommand (<c>main.rs</c>:2187-2211) — no capture, no filter,
    /// since the caller (eslint/next/prettier/prisma-passthrough) has no dedicated RtkSharp filter.
    /// </summary>
    /// <param name="args">
    /// The full argument vector to pass to <c>npx</c> (including the tool name, e.g.
    /// <c>["prisma", "migrate", "status"]</c>).
    /// </param>
    /// <param name="executor">The process executor to spawn <c>npx</c> with, or null for the default.</param>
    /// <returns><c>npx</c>'s exit code.</returns>
    private static async Task<int> RunNpxPassthroughAsync(IReadOnlyList<string> args, IProcessExecutor? executor)
    {
        // Timer starts before spawning, mirroring Rust's `TimedExecution::start()` placement.
        var timer = TimedExecution.Start();

        var exec = executor ?? new ProcessExecutor();
        var request = new ExecutionRequest("npx", args, CaptureMode: ExecutionCaptureMode.Inherit);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        var argsStr = string.Join(' ', args);

        if (!result.WasStarted)
        {
            var detail = string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}";
            throw new InvalidOperationException($"Failed to run npx {argsStr}{detail}");
        }

        timer.TrackPassthrough($"npx {argsStr}", $"rtk npx {argsStr} (passthrough)");

        return Utils.ExitCodeFromStatus(result.ExitCode, "npx");
    }

    /// <summary>
    /// Filters npm/npx run output: strips script banners, npm's own <c>WARN</c>/<c>notice</c> lines,
    /// progress-spinner glyphs, short lines, and blank lines. If everything gets filtered out, prints
    /// the literal <c>"ok"</c>. Ports <c>filter_npm_output</c> (<c>npm_cmd.rs</c>:136-168) exactly.
    /// </summary>
    /// <param name="output">The raw <c>npm</c>/<c>npx</c> output to filter.</param>
    /// <returns>The filtered output, or <c>"ok"</c> when nothing remained.</returns>
    internal static string FilterNpmOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new List<string>();

        foreach (var line in ReadCommand.SplitLines(output))
        {
            // Skip npm boilerplate (script banners like "> project@1.0.0 build").
            if (line.StartsWith('>') && line.Contains('@'))
            {
                continue;
            }

            var trimmedStart = line.TrimStart();

            // Skip npm lifecycle noise.
            if (trimmedStart.StartsWith("npm WARN", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmedStart.StartsWith("npm notice", StringComparison.Ordinal))
            {
                continue;
            }

            // Skip progress indicators. Rust's `&&` binds tighter than `||` here: the third condition
            // is "contains '...' AND is under 10 chars", not "contains '...' and (separately) under 10".
            if (line.Contains('⸩') || line.Contains('⸨') || (line.Contains("...", StringComparison.Ordinal) && line.Length < 10))
            {
                continue;
            }

            // Skip empty lines.
            if (line.Trim().Length == 0)
            {
                continue;
            }

            result.Add(line);
        }

        return result.Count == 0 ? "ok" : string.Join("\n", result);
    }
}
