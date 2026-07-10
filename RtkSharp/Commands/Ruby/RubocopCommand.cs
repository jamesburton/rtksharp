using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Ruby;

namespace RtkSharp.Commands.Ruby;

/// <summary>
/// Implements the <c>rtk rubocop</c> CLI verb: injects <c>--format json</c> for structured output,
/// parses offenses grouped by file and sorted by severity. Falls back to text parsing for
/// autocorrect mode, when the user specifies a custom format, or when the injected JSON output
/// fails to parse. Faithful port of <c>src/cmds/ruby/rubocop_cmd.rs</c> and its <c>Commands::Rubocop</c>
/// dispatch arm in <c>src/main.rs</c>.
/// </summary>
/// <remarks>
/// Flat command, no subcommand enum — see <see cref="RakeCommand"/>'s remarks for the shared
/// rationale (single <c>trailing_var_arg</c> args vector, buffered <c>run_filtered</c>, no
/// passthrough branch, no <see cref="Cli.CommandArgumentParseException"/> path).
/// </remarks>
public static class RubocopCommand
{
    /// <summary>
    /// Registry entry point. Injects <c>--format json</c> unless the user already specified a
    /// format or requested autocorrect, runs <c>rubocop</c> via <see cref="RubySupport.RubyExec"/>,
    /// and filters the buffered output through <see cref="RubocopFilters.FilterRubocopJson"/> or
    /// <see cref="RubocopFilters.FilterRubocopText"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>rubocop</c> verb.</param>
    /// <returns>The wrapped <c>rubocop</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var ruby = RubySupport.RubyExec("rubocop");

        var isAutocorrect = args.Any(a => a is "-a" or "-A" or "--auto-correct" or "--auto-correct-all");
        var hasFormat = args.Any(a => a.StartsWith("--format", StringComparison.Ordinal) || a.StartsWith("-f", StringComparison.Ordinal));

        var invocation = new List<string>(ruby.BaseArguments);
        if (!hasFormat && !isAutocorrect)
        {
            invocation.Add("--format");
            invocation.Add("json");
        }

        invocation.AddRange(args);

        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running: rubocop {string.Join(' ', args)}\n");
        }

        return CommandRunner.RunFilteredAsync(
            ruby.FileName,
            invocation,
            "rubocop",
            string.Join(' ', args),
            stdout => hasFormat || isAutocorrect
                ? RubocopFilters.FilterRubocopText(stdout)
                : RubocopFilters.FilterRubocopJson(stdout),
            new RunOptions(FilterStdoutOnly: true, TeeLabel: "rubocop"));
    }
}
