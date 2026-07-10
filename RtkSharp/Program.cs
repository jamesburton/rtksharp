using System.IO;
using System.Text;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Toml;
using RtkSharp.Hooks;

return await RtkProgram.RunAsync(args);

internal static class RtkProgram
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // The Rust `rtk` binary writes raw UTF-8 bytes to stdout (e.g. the U+2502 `│`
        // line-number gutter in `read -n`, and box-drawing glyphs in `tree`). On Windows
        // .NET defaults Console.OutputEncoding to the system OEM code page, which encodes
        // those glyphs as a single mismatched byte and breaks byte-for-byte parity. Force
        // UTF-8 without a BOM so our output stream matches the oracle's exactly.
        TrySetUtf8Output();

        IArgumentParser argumentParser = new ArgumentParser();
        var parsed = argumentParser.Parse(args);

        // Publish the parsed global --ultra-compact flag for handlers whose registry delegate
        // (Func<string[], Task<int>>) cannot carry it. Mirrors Rust threading cli.ultra_compact
        // into gh_cmd::run (main.rs:1673). See RuntimeOptions for the rationale.
        RuntimeOptions.UltraCompact = parsed.UltraCompact;
        RuntimeOptions.Verbosity = parsed.Verbosity;
        RuntimeOptions.SkipEnv = parsed.SkipEnv;

        if (parsed.Version)
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        if (parsed.Help || string.IsNullOrWhiteSpace(parsed.CommandName))
        {
            Console.WriteLine(GetHelp());
            return 0;
        }

        // Startup hook-status warning, run once before dispatch. Skipped for `gain`, which shows
        // its own inline hook warning in its summary view (mirrors Rust's explicit `Commands::Gain`
        // exclusion, main.rs:1483-1485). `gain` itself doesn't exist as a registered command yet
        // (Phase 5 Task 5) — this string check is written in anticipation and needs no changes once
        // it lands.
        if (!string.Equals(parsed.CommandName, "gain", StringComparison.Ordinal))
        {
            HookCheck.MaybeWarn();
        }

        if (CommandRegistry.TryGet(parsed.CommandName, out var handler))
        {
            try
            {
                return await handler(parsed.CommandArgs).ConfigureAwait(false);
            }
            catch (CommandArgumentParseException)
            {
                // The module's own argument parsing rejected these args in a way Rust's clap
                // would have too (see CommandArgumentParseException's remarks) — re-dispatch
                // through the SAME fallback mechanism used below for a verb with no module at
                // all, using the ORIGINAL raw args, not the module's own usage message.
            }
        }

        // Last-resort TOML filter fallback: for a verb with no dedicated module, OR a dedicated
        // module whose own argument parsing just rejected these args (mirrors Rust's run_fallback
        // TOML branch, main.rs:1213-1292). Runtime hot path: any lookup failure returns null and
        // degrades to the raw passthrough below; RTK_NO_TOML=1 also bypasses it.
        var tomlExit = await TryTomlFallbackAsync(
            parsed.CommandName,
            parsed.CommandArgs,
            Console.Out,
            Console.Error,
            new ProcessExecutor(),
            TrustCommand.AsTrustChecker(),
            cancellationToken).ConfigureAwait(false);
        if (tomlExit is { } handledExit)
        {
            return handledExit;
        }

        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(
            new ExecutionRequest(parsed.CommandName, parsed.CommandArgs),
            cancellationToken
        ).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result.Stdout))
        {
            Console.Out.Write(result.Stdout);
        }

        if (!string.IsNullOrEmpty(result.Stderr))
        {
            Console.Error.Write(result.Stderr);
        }

        if (!result.WasStarted && !string.IsNullOrWhiteSpace(result.Failure))
        {
            Console.Error.WriteLine(result.Failure);
        }

        return result.ExitCode;
    }

    /// <summary>
    /// Applies the last-resort TOML filter fallback for a command that no dedicated module handled:
    /// looks up a matching filter across the built-in/user-global/project tiers, and if one matches,
    /// runs the command, applies the filter to its output, and prints the filtered result. Faithful
    /// port of the TOML branch of Rust <c>run_fallback</c> (<c>main.rs:1213-1292</c>).
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> — signaling the caller to fall through to the unchanged raw
    /// passthrough — when <c>RTK_NO_TOML=1</c> is set, no filter matches, the command could not be
    /// started, or the lookup itself failed (fallback pattern: a bad filter/registry never blocks the
    /// command). The lookup uses the <em>basename</em> of the verb so absolute paths
    /// (<c>/usr/bin/make</c>) still match anchored patterns like <c>^make\b</c>. On a non-zero exit
    /// code, the raw (pre-filter) output is teed to disk and a recovery hint appended to the
    /// filtered output — see <see cref="Tee.TeeAndHint"/> — matching Rust's own
    /// <c>tee_and_hint(&amp;combined_raw, &amp;raw_command, exit_code)</c> call (<c>main.rs:1260-1265</c>);
    /// the tee slug uses the ORIGINAL (non-basename) command text, matching Rust's
    /// <c>raw_command = args.join(" ")</c>.
    /// </remarks>
    /// <param name="commandName">The unrecognized verb (may be an absolute path).</param>
    /// <param name="commandArgs">The verb's arguments.</param>
    /// <param name="stdout">The destination for the filtered output.</param>
    /// <param name="stderr">The destination for the child's stderr (when not merged).</param>
    /// <param name="executor">The process executor used to run the command.</param>
    /// <param name="trustChecker">The project-local trust gate for the filter registry.</param>
    /// <param name="cancellationToken">A token to cancel command execution.</param>
    /// <returns>The command's exit code when a filter handled it; otherwise <see langword="null"/>.</returns>
    internal static async Task<int?> TryTomlFallbackAsync(
        string commandName,
        string[] commandArgs,
        TextWriter stdout,
        TextWriter stderr,
        IProcessExecutor executor,
        TrustChecker trustChecker,
        CancellationToken cancellationToken)
    {
        CompiledFilter? filter;
        try
        {
            // RTK_NO_TOML=1 bypasses the whole engine (also short-circuited in FindMatchingFilter).
            if (Environment.GetEnvironmentVariable("RTK_NO_TOML") == "1")
            {
                return null;
            }

            var basename = Path.GetFileName(commandName);
            if (string.IsNullOrEmpty(basename))
            {
                basename = commandName;
            }

            var lookupCmd = commandArgs.Length > 0
                ? basename + " " + string.Join(' ', commandArgs)
                : basename;

            var registry = TomlFilterRegistry.Load(trustChecker);
            filter = registry.FindMatchingFilter(lookupCmd);
        }
        catch (Exception)
        {
            // Fallback pattern: a registry/lookup failure must never block command execution.
            return null;
        }

        if (filter is null)
        {
            // No filter matched — fall through to the raw passthrough, byte-for-byte unchanged.
            return null;
        }

        var result = await executor.ExecuteAsync(
            new ExecutionRequest(commandName, commandArgs, CaptureMode: ExecutionCaptureMode.Separate),
            cancellationToken).ConfigureAwait(false);

        if (!result.WasStarted)
        {
            // Command not found: nothing ran (no side effects), so let the raw passthrough handle the
            // not-found path exactly as today.
            return null;
        }

        // filter_stderr merges stderr into the text to filter; otherwise stderr is emitted directly so
        // it stays visible (mirrors main.rs:1233-1262).
        string combinedRaw;
        if (filter.FilterStderr)
        {
            combinedRaw = result.Stdout + result.Stderr;
        }
        else
        {
            combinedRaw = result.Stdout;
            if (!string.IsNullOrEmpty(result.Stderr))
            {
                stderr.Write(result.Stderr);
            }
        }

        // Tee raw output BEFORE filtering on failure — lets the LLM re-read it if the filter
        // dropped something relevant. Mirrors Rust's tee_and_hint(&combined_raw, &raw_command,
        // exit_code) call (main.rs:1260-1265), using the ORIGINAL (non-basename) command text as
        // the slug, exactly like Rust's raw_command = args.join(" ").
        string? teeHint = null;
        if (result.ExitCode != 0)
        {
            var rawCommand = commandArgs.Length > 0
                ? commandName + " " + string.Join(' ', commandArgs)
                : commandName;
            teeHint = Tee.TeeAndHint(combinedRaw, rawCommand, result.ExitCode);
        }

        string filtered;
        try
        {
            filtered = TomlFilterEngine.ApplyFilter(filter, combinedRaw);
        }
        catch (Exception)
        {
            // Never hide the command's output over a filter bug: emit it unfiltered.
            filtered = combinedRaw;
        }

        stdout.Write(filtered);
        stdout.Write('\n'); // Rust prints via println! (adds a trailing newline).

        if (teeHint is not null)
        {
            stdout.Write(teeHint);
            stdout.Write('\n');
        }

        return result.ExitCode;
    }

    /// <summary>
    /// Forces <see cref="Console.OutputEncoding"/> to UTF-8 without a byte-order mark.
    /// Internal (not private) so <c>RtkSharp.Tests</c> can assert the encoding directly
    /// without depending on the Rust oracle or spawning a child process.
    /// </summary>
    internal static void TrySetUtf8Output()
    {
        try
        {
            // UTF8Encoding(false) => no byte-order mark, matching Rust's raw byte output.
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // Reassigning the encoding can fail when stdout is a handle that cannot be
            // reopened (rare in redirected/piped scenarios). Fall back silently: ASCII
            // output is unaffected, and blocking the whole command over a gutter glyph
            // would violate the never-block-the-user contract.
        }
    }

    private static string GetVersion()
    {
        var version = typeof(RtkProgram).Assembly.GetName().Version?.ToString(3);
        return $"RtkSharp {version ?? "0.0.0"}";
    }

    private static string GetHelp() =>
        """
        RtkSharp - reduce command output while preserving command behavior.

        Usage:
          rtk [options] -- <command> [args]
          rtk [options] <command> [args]

        Options:
          -v, --verbose         Increase diagnostic output.
          -u, --ultra-compact   Prefer the most compact summaries.
              --no-color        Disable color output.
          -h, --help            Show help.
              --version         Show version.
        """;
}
