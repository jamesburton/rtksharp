using System;
using System.IO;
using System.Linq;
using System.Text;
using RtkSharp.Core;
using RtkSharp.Filters.Toml;

namespace RtkSharp.Hooks;

/// <summary>
/// Implements the <c>rtk verify</c> CLI verb. Faithful port of Rust <c>Commands::Verify</c>'s
/// dispatch arm (<c>main.rs</c>:2523-2536): it runs the hook-integrity check and the TOML filter
/// inline-test battery, and honors <c>--filter &lt;name&gt;</c> / <c>--require-all</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch shape (matches the oracle exactly).</b> With a <c>--filter &lt;name&gt;</c>, only that
/// filter's inline tests run (no integrity check), via <c>hooks::verify_cmd::run(Some(name), require_all)</c>.
/// Without <c>--filter</c>, the integrity check runs first (<c>hooks::integrity::run_verify</c>), then
/// the full inline-test battery (<c>hooks::verify_cmd::run(None, require_all)</c>). The battery is a
/// faithful port of <c>src/hooks/verify_cmd.rs:11-49</c>: it prints per-failure detail to stderr, a
/// <c>"{passed}/{total} tests passed"</c> (or <c>"No inline tests found."</c>) summary to stdout, and
/// bails loudly (non-zero exit) on any test failure or — with <c>--require-all</c> — any filter that
/// declared no inline tests.
/// </para>
/// <para>
/// <b>Verbosity is a top-level flag, not a <c>verify</c>-subcommand flag.</b> Rust's <c>-v</c>/
/// <c>-vv</c>/<c>-vvv</c>/<c>--verbose</c> is the top-level <c>Cli.verbose: u8</c> field
/// (<c>main.rs</c>:67), only recognized <b>before</b> the subcommand (e.g. <c>rtk -v verify</c>),
/// and threaded as <c>cli.verbose</c> into <c>hooks::integrity::run_verify(cli.verbose)</c>
/// (<c>main.rs</c>:2532). <c>rtk verify -v</c> is a clap parse error on the oracle, not a
/// verify-level flag: <see cref="RunCore"/> does not recognize <c>-v</c>/<c>--verbose</c> as a
/// <c>verify</c>-level argument (it falls through to the same "unrecognized verify argument" abort as
/// any other unknown flag), and verbosity is instead read from the ambient
/// <see cref="RuntimeOptions.Verbosity"/>, set once by <c>Program</c> from the top-level flag.
/// </para>
/// <para>
/// <b>Fail-loud contract.</b> A test failure or missing-tests condition is surfaced as a non-zero
/// exit via a thrown bail exception whose message <see cref="Run"/> renders as <c>rtk: {message}</c>
/// on stderr, matching Rust's top-level <c>eprintln!("rtk: {:#}", e)</c> (<c>main.rs</c>:1439) for an
/// <c>anyhow::bail!</c>. This is a user-invoked one-shot command, so unlike the runtime hot paths it
/// deliberately fails loud rather than falling back.
/// </para>
/// </remarks>
public static class VerifyCommand
{
    /// <summary>
    /// Runs <c>rtk verify</c> with the given arguments (the remainder after the <c>verify</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>verify</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        try
        {
            return RunCore(args);
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    private static int RunCore(string[] args)
    {
        var (filterName, requireAll) = ParseArgs(args);

        if (filterName is not null)
        {
            // Filter-specific mode: run only that filter's tests, no integrity check (main.rs:2527-2529).
            return RunInlineTests(filterName, requireAll);
        }

        // Default / --require-all: integrity check first, then the full inline-test battery
        // (main.rs:2531-2533). On the oracle, `hooks::integrity::run_verify` only fails to return
        // control to its caller in the Tampered arm, which calls `std::process::exit(1)` directly
        // (integrity.rs:247) — terminating the process before `hooks::verify_cmd::run` is ever
        // reached. Every other status (Verified, NoBaseline, NotInstalled, OrphanedHash, and the
        // native-binary-registered PASS case) returns normally and falls through to the inline-test
        // battery at main.rs:2533. `Integrity.RunVerify` mirrors this exactly: it returns 1 only for
        // Tampered and 0 for every other status, so `integrityExit != 0` is precisely the Tampered
        // condition — not a general "non-zero skips" heuristic.
        var integrityExit = Integrity.RunVerify(RuntimeOptions.Verbosity);
        if (integrityExit != 0)
        {
            // Tampered: the oracle's process would have already exited here, so stop before
            // printing the inline-test battery's stdout summary.
            return integrityExit;
        }

        return RunInlineTests(null, requireAll);
    }

    /// <summary>
    /// Parses <c>--filter &lt;name&gt;</c> / <c>--filter=&lt;name&gt;</c> and <c>--require-all</c> off
    /// the <c>verify</c> argument remainder. Any other token (including a subcommand-level
    /// <c>-v</c>/<c>--verbose</c>, which is a top-level flag on the oracle) is an
    /// <see cref="InitAbortException"/> "unrecognized verify argument", mirroring
    /// <see cref="InitCommand"/>'s handling of unknown flags.
    /// </summary>
    /// <param name="args">The arguments following the <c>verify</c> verb.</param>
    /// <returns>The parsed filter name (or <see langword="null"/>) and require-all flag.</returns>
    private static (string? FilterName, bool RequireAll) ParseArgs(string[] args)
    {
        string? filterName = null;
        var requireAll = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--require-all")
            {
                requireAll = true;
            }
            else if (arg == "--filter")
            {
                if (i + 1 >= args.Length)
                {
                    throw new InitAbortException("--filter requires a value");
                }

                filterName = args[++i];
            }
            else if (arg.StartsWith("--filter=", StringComparison.Ordinal))
            {
                filterName = arg["--filter=".Length..];
            }
            else
            {
                throw new InitAbortException($"unrecognized verify argument: {arg}");
            }
        }

        return (filterName, requireAll);
    }

    /// <summary>
    /// Runs the inline-test battery for <paramref name="filterName"/> (or all filters) against the
    /// currently loaded built-in and trust-gated project filters, then formats the outcome. See
    /// <see cref="RunInlineTestsCore"/> for the exact stdout/stderr contract.
    /// </summary>
    /// <param name="filterName">The filter to test, or <see langword="null"/> for all.</param>
    /// <param name="requireAll">Whether to bail if any filter declared no inline tests.</param>
    /// <returns>0 when all tests passed (and no missing-tests bail); the call throws otherwise.</returns>
    private static int RunInlineTests(string? filterName, bool requireAll)
    {
        var results = TomlFilterEngine.RunFilterTests(filterName, TrustCommand.AsTrustChecker(), Console.Error);
        return RunInlineTestsCore(results, requireAll, Console.Out, Console.Error);
    }

    /// <summary>
    /// Formats a <see cref="VerifyResults"/> into the exact stdout/stderr contract of Rust
    /// <c>hooks::verify_cmd::run</c> (<c>verify_cmd.rs:11-49</c>) and returns the success exit code,
    /// throwing on a bail. Exposed (internal) so the formatting can be tested with synthetic results.
    /// </summary>
    /// <param name="results">The aggregated inline-test outcomes.</param>
    /// <param name="requireAll">Whether a filter lacking inline tests is a fatal condition.</param>
    /// <param name="stdout">The destination for the summary line.</param>
    /// <param name="stderr">The destination for per-failure detail and missing-tests lines.</param>
    /// <returns>0 when everything passed.</returns>
    /// <exception cref="VerifyBailException">
    /// Thrown (fail-loud) when <paramref name="requireAll"/> is set and one or more filters lack tests,
    /// or when one or more tests failed — carrying Rust's exact <c>anyhow::bail!</c> message text.
    /// </exception>
    internal static int RunInlineTestsCore(VerifyResults results, bool requireAll, TextWriter stdout, TextWriter stderr)
    {
        var total = results.Outcomes.Count;
        var passed = results.Outcomes.Count(o => o.Passed);
        var failed = total - passed;

        // Print failures with details (verify_cmd.rs:19-26).
        foreach (var outcome in results.Outcomes)
        {
            if (!outcome.Passed)
            {
                stderr.Write(
                    $"FAIL [{outcome.FilterName}] {outcome.TestName}\n" +
                    $"  expected: {DebugString(outcome.Expected)}\n" +
                    $"  actual:   {DebugString(outcome.Actual)}\n");
            }
        }

        if (total == 0)
        {
            stdout.Write("No inline tests found.\n");
        }
        else
        {
            stdout.Write($"{passed}/{total} tests passed\n");
        }

        if (requireAll && results.FiltersWithoutTests.Count > 0)
        {
            foreach (var name in results.FiltersWithoutTests)
            {
                stderr.Write($"MISSING tests for filter: {name}\n");
            }

            throw new VerifyBailException(
                $"{results.FiltersWithoutTests.Count} filter(s) have no inline tests (use --require-all in CI)");
        }

        if (failed > 0)
        {
            throw new VerifyBailException($"{failed} test(s) failed");
        }

        return 0;
    }

    /// <summary>
    /// Renders <paramref name="s"/> the way Rust's <c>{:?}</c> (<c>Debug</c>) formatter renders a
    /// <c>&amp;str</c>: double-quoted, with <c>"</c>, <c>\</c>, and the common control characters
    /// escaped — matching how <c>verify_cmd.rs</c> prints <c>expected</c>/<c>actual</c> via <c>{:?}</c>.
    /// </summary>
    /// <param name="s">The string to debug-format.</param>
    /// <returns>The quoted, escaped representation.</returns>
    private static string DebugString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>
/// A fail-loud bail from the <c>rtk verify</c> inline-test battery, carrying Rust's exact
/// <c>anyhow::bail!</c> message. <see cref="VerifyCommand.Run"/> renders it as <c>rtk: {message}</c>
/// on stderr and returns exit code 1, matching the oracle's top-level error handling.
/// </summary>
internal sealed class VerifyBailException(string message) : Exception(message);
