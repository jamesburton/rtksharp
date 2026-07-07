using System.Linq;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Go;

/// <summary>
/// Go tools RTK provides filtered output for when invoked via <c>go tool &lt;name&gt;</c> from
/// <see cref="GoCommand"/>'s unmatched-subcommand path. Faithful port of Rust's <c>GoTool</c> enum
/// (<c>src/cmds/go/go_cmd.rs</c>:206-219).
/// </summary>
internal enum GoTool
{
    /// <summary><c>go tool golangci-lint</c> — filtered via <see cref="GolangciLintCommand.FilterGolangciJson"/>.</summary>
    GolangciLint,
}

/// <summary>
/// Implements the <c>rtk go</c> CLI verb: <c>test</c> gets ~90% token reduction by parsing
/// <c>go test -json</c>'s NDJSON event stream directly (not block-grouped text); <c>build</c>/<c>vet</c>
/// get compact error/issue summaries; any other subcommand runs as a captured (not streamed)
/// passthrough, with one further interception — <c>go tool golangci-lint</c> — routed through the
/// golangci JSON filter. Faithful port of <c>src/cmds/go/go_cmd.rs</c> plus its dispatch arm in
/// <c>src/main.rs</c> (<c>GoCommands</c> enum, <c>Commands::Go</c> match arm).
/// </summary>
/// <remarks>
/// <para>
/// <b>No executor injection point.</b> Following <see cref="RtkSharp.Commands.Rust.CargoCommand"/>'s
/// buffered subcommands (<c>clippy</c>/<c>install</c>/<c>nextest</c>), every <c>go</c> subcommand here
/// is buffered (never streamed), and <c>CommandRunner.RunFilteredAsync</c>/<c>RunFilteredWithExitAsync</c>
/// always construct their own <see cref="ProcessExecutor"/>. Rust's own <c>#[cfg(test)]</c> suite for
/// <c>go_cmd.rs</c> exercises only the pure filter/helper functions (<c>filter_go_test_json</c>,
/// <c>filter_go_build</c>, <c>filter_go_vet</c>, <c>match_go_tool</c>, <c>has_golangci_format_flag</c>)
/// directly, never the dispatch functions through a live process — so this port follows the same
/// shape and exposes those pure functions (in <see cref="GoFilters"/> and here) for direct testing
/// instead of adding a fake-executor seam that the oracle's own tests never needed.
/// </para>
/// <para>
/// <b>No <c>--</c> restoration.</b> Unlike <c>CargoCommand</c>, Rust's <c>go_cmd.rs</c> never calls
/// <c>args_utils::restore_double_dash</c> for any of <c>Test</c>/<c>Build</c>/<c>Vet</c> — despite all
/// three being declared with the same <c>#[arg(trailing_var_arg = true, allow_hyphen_values = true)]</c>
/// clap shape as cargo's subcommands. This port preserves that asymmetry verbatim rather than "fixing"
/// it: args are forwarded to the underlying <c>go</c> invocation unchanged, with no restoration pass.
/// </para>
/// <para>
/// <b>Quirk preserved verbatim: unmatched <c>go</c> subcommands are captured, not streamed.</b> Rust's
/// <c>run_other</c> calls <c>Command::output()</c> (buffered capture) rather than
/// <c>runner::run_passthrough</c> (which inherits stdio and streams live) — so an unmatched
/// subcommand like <c>go mod tidy</c> loses interactive/TTY behavior (progress spinners, color
/// auto-detection) that a live-streamed passthrough would preserve. This is a real behavior gap in
/// the Rust source, not a design choice this port introduces, and is kept exactly as-is: see
/// <see cref="RunOtherAsync"/>.
/// </para>
/// <para>
/// <b>Tracking label quirk preserved verbatim.</b> Rust's <c>run_other</c> tracks the command as
/// <c>format!("go {}", subcommand)</c> — the subcommand name only, dropping the rest of the
/// arguments from the tracking label (unlike every other passthrough path in this codebase, which
/// includes the full argument string). Reproduced as-is in <see cref="RunOtherAsync"/>.
/// </para>
/// </remarks>
public static class GoCommand
{
    /// <summary>
    /// Registry entry point. Dispatches on the first argument (the go subcommand) exactly as Rust's
    /// <c>GoCommands</c> routing does.
    /// </summary>
    /// <param name="args">The arguments following the <c>go</c> verb (subcommand first).</param>
    /// <returns>The wrapped <c>go</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity);

    /// <summary>Test-friendly overload accepting an explicit verbosity level.</summary>
    /// <param name="args">The arguments following the <c>go</c> verb (subcommand first).</param>
    /// <param name="verbose">The rtk-level verbosity count (<c>-v</c>/<c>-vv</c>/...).</param>
    /// <returns>The wrapped <c>go</c> process's exit code.</returns>
    internal static Task<int> RunAsync(string[] args, int verbose)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            Console.Error.Write("go: no subcommand specified\n");
            return Task.FromResult(1);
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "test" => RunTestAsync(rest, verbose),
            "build" => RunBuildAsync(rest, verbose),
            "vet" => RunVetAsync(rest, verbose),
            _ => RunOtherAsync(args, verbose),
        };
    }

    /// <summary>
    /// Runs <c>go test</c>, parsing <c>-json</c> NDJSON output for a compact pass/fail summary unless
    /// the caller already passed <c>-json</c>/<c>-bench*</c> (which RTK leaves unfiltered — benchmark
    /// output and pre-formatted JSON aren't parsed). Faithful port of Rust's <c>run_test</c>
    /// (<c>go_cmd.rs</c>:46-81).
    /// </summary>
    private static Task<int> RunTestAsync(string[] args, int verbose)
    {
        var skipJson = args.Any(a => a == "-json" || a.StartsWith("-bench", StringComparison.Ordinal));

        var invocation = new List<string> { "test" };
        if (!skipJson)
        {
            invocation.Add("-json");
        }

        invocation.AddRange(args);

        if (RuntimeOptions.Verbosity > 0 || verbose > 0)
        {
            Console.Error.Write($"Running: go test {(!skipJson ? "-json " : "")}{string.Join(' ', args)}\n");
        }

        Func<string, string> filter = skipJson ? (static s => s) : GoFilters.FilterGoTestJson;

        return CommandRunner.RunFilteredAsync(
            "go", invocation, "go test", string.Join(' ', args), filter,
            new RunOptions(TeeLabel: "go_test", FilterStdoutOnly: true));
    }

    /// <summary>
    /// Runs <c>go build</c>, showing only compiler/config errors (or a failure dump when nothing
    /// recognizable was found on a non-zero exit). Faithful port of Rust's <c>run_build</c>
    /// (<c>go_cmd.rs</c>:83-102).
    /// </summary>
    private static Task<int> RunBuildAsync(string[] args, int verbose)
    {
        var invocation = new List<string> { "build" };
        invocation.AddRange(args);

        if (RuntimeOptions.Verbosity > 0 || verbose > 0)
        {
            Console.Error.Write($"Running: go build {string.Join(' ', args)}\n");
        }

        return CommandRunner.RunFilteredWithExitAsync(
            "go", invocation, "go build", string.Join(' ', args), GoFilters.FilterGoBuildWithExit,
            new RunOptions(TeeLabel: "go_build"));
    }

    /// <summary>
    /// Runs <c>go vet</c>, showing only recognized <c>file.go:line:col</c> issue lines. Faithful port
    /// of Rust's <c>run_vet</c> (<c>go_cmd.rs</c>:104-123).
    /// </summary>
    private static Task<int> RunVetAsync(string[] args, int verbose)
    {
        var invocation = new List<string> { "vet" };
        invocation.AddRange(args);

        if (RuntimeOptions.Verbosity > 0 || verbose > 0)
        {
            Console.Error.Write($"Running: go vet {string.Join(' ', args)}\n");
        }

        return CommandRunner.RunFilteredAsync(
            "go", invocation, "go vet", string.Join(' ', args), GoFilters.FilterGoVet,
            new RunOptions(TeeLabel: "go_vet"));
    }

    /// <summary>
    /// Runs an unmatched <c>go</c> subcommand, intercepting <c>go tool golangci-lint</c> for filtered
    /// output. Faithful port of Rust's <c>run_other</c> (<c>go_cmd.rs</c>:125-170) — see the type-level
    /// remarks for the captured-not-streamed and tracking-label quirks preserved here verbatim.
    /// </summary>
    private static async Task<int> RunOtherAsync(string[] args, int verbose)
    {
        if (args.Length == 0)
        {
            throw new InvalidOperationException("go: no subcommand specified");
        }

        if (MatchGoTool(args) is (GoTool.GolangciLint, var toolArgs))
        {
            return await RunGoToolGolangciLintAsync(toolArgs, verbose).ConfigureAwait(false);
        }

        var timer = TimedExecution.Start();

        var subcommand = args[0];
        var invocation = new List<string> { subcommand };
        invocation.AddRange(args.Skip(1));

        if (RuntimeOptions.Verbosity > 0 || verbose > 0)
        {
            Console.Error.Write($"Running: go {subcommand} ...\n");
        }

        var exec = new ProcessExecutor();
        var request = new ExecutionRequest("go", invocation, CaptureMode: ExecutionCaptureMode.Separate);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        var raw = result.Stdout + "\n" + result.Stderr;

        Console.Out.Write(result.Stdout);
        Console.Error.Write(result.Stderr);

        // Quirk preserved verbatim: Rust's tracking label is "go {subcommand}" only — the rest of
        // the arguments are not included, unlike every other passthrough tracking label in this port.
        timer.Track($"go {subcommand}", $"rtk go {subcommand}", raw, raw);

        return result.ExitCode;
    }

    /// <summary>
    /// Detects the golangci-lint major version when invoked via <c>go tool golangci-lint --version</c>.
    /// Faithful port of Rust's <c>detect_go_tool_golangci_version</c> (<c>go_cmd.rs</c>:172-194).
    /// Returns 1 on any failure — the safe v1-behaviour fallback.
    /// </summary>
    private static async Task<uint> DetectGoToolGolangciVersionAsync()
    {
        try
        {
            var exec = new ProcessExecutor();
            var request = new ExecutionRequest(
                "go", ["tool", "golangci-lint", "--version"], CaptureMode: ExecutionCaptureMode.Separate);
            var result = await exec.ExecuteAsync(request).ConfigureAwait(false);
            var versionText = string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout;
            return GolangciLintCommand.ParseMajorVersion(versionText);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Detects whether <paramref name="args"/> already contains an explicit golangci-lint output
    /// format flag (v1's <c>--out-format</c> or v2's <c>--output.json.path</c>). Faithful port of
    /// Rust's <c>has_golangci_format_flag</c> (<c>go_cmd.rs</c>:196-204).
    /// </summary>
    /// <param name="args">The arguments passed after <c>go tool golangci-lint</c>.</param>
    /// <returns>True if an explicit output-format flag was already specified.</returns>
    internal static bool HasGolangciFormatFlag(IReadOnlyList<string> args) => args.Any(a =>
        a == "--out-format"
        || a.StartsWith("--out-format=", StringComparison.Ordinal)
        || a == "--output.json.path"
        || a.StartsWith("--output.json.path=", StringComparison.Ordinal));

    /// <summary>
    /// If <paramref name="args"/> starts with <c>tool &lt;known-tool&gt;</c>, returns the matched tool
    /// and the remaining arguments after it. Faithful port of Rust's <c>match_go_tool</c>
    /// (<c>go_cmd.rs</c>:222-231).
    /// </summary>
    /// <param name="args">The full unmatched-subcommand argument list.</param>
    /// <returns>The matched tool and remaining arguments, or null if the first tokens don't match a known <c>go tool</c> invocation.</returns>
    internal static (GoTool Tool, string[] ToolArgs)? MatchGoTool(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0] == "tool" && args.Count > 1 && args[1] == "golangci-lint")
        {
            return (GoTool.GolangciLint, args.Skip(2).ToArray());
        }

        return null;
    }

    /// <summary>
    /// Runs <c>go tool golangci-lint</c>, injecting JSON-output flags (unless the caller already
    /// specified a format) and filtering the result through
    /// <see cref="GolangciLintCommand.FilterGolangciJson"/>. Faithful port of Rust's
    /// <c>run_go_tool_golangci_lint</c> (<c>go_cmd.rs</c>:233-300).
    /// </summary>
    private static async Task<int> RunGoToolGolangciLintAsync(string[] args, int verbose)
    {
        var timer = TimedExecution.Start();

        var version = await DetectGoToolGolangciVersionAsync().ConfigureAwait(false);

        var invocation = new List<string> { "tool", "golangci-lint" };
        var hasFormat = HasGolangciFormatFlag(args);

        if (!hasFormat)
        {
            invocation.Add("run");
            if (version >= 2)
            {
                invocation.Add("--output.json.path");
                invocation.Add("stdout");
            }
            else
            {
                invocation.Add("--out-format=json");
            }
        }
        else
        {
            invocation.Add("run");
        }

        invocation.AddRange(args);

        if (RuntimeOptions.Verbosity > 0 || verbose > 0)
        {
            Console.Error.Write(version >= 2
                ? "Running: go tool golangci-lint run --output.json.path stdout\n"
                : "Running: go tool golangci-lint run --out-format=json\n");
        }

        var exec = new ProcessExecutor();
        var request = new ExecutionRequest("go", invocation, CaptureMode: ExecutionCaptureMode.Separate);
        var result = await exec.ExecuteAsync(request).ConfigureAwait(false);

        var raw = result.Stdout + "\n" + result.Stderr;

        // v2 outputs JSON on first line + trailing text; v1 outputs just JSON.
        var jsonOutput = version >= 2 ? FirstLine(result.Stdout) : result.Stdout;

        var filtered = GolangciLintCommand.FilterGolangciJson(jsonOutput, version);
        Console.Out.Write(filtered + "\n");

        if (!string.IsNullOrWhiteSpace(result.Stderr) && (RuntimeOptions.Verbosity > 0 || verbose > 0))
        {
            Console.Error.Write(result.Stderr.Trim() + "\n");
        }

        timer.Track("go tool golangci-lint", "rtk go tool golangci-lint", raw, filtered);

        var exitCode = result.ExitCode;
        // golangci-lint: exit 0 = clean, exit 1 = lint issues found (not an error),
        // exit 2+ = config/build error.
        return exitCode == 1 ? 0 : exitCode;
    }

    private static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        return idx >= 0 ? text[..idx] : text;
    }
}
