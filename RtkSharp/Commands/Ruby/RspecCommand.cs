using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Ruby;

namespace RtkSharp.Commands.Ruby;

/// <summary>
/// Implements the <c>rtk rspec</c> CLI verb: injects <c>--format json</c> to get structured output,
/// parses it to show only failures. Falls back to a state-machine text parser when JSON is
/// unavailable (e.g. the user specified <c>--format documentation</c>) or when the injected JSON
/// output fails to parse. Faithful port of <c>src/cmds/ruby/rspec_cmd.rs</c> and its
/// <c>Commands::Rspec</c> dispatch arm in <c>src/main.rs</c>.
/// </summary>
/// <remarks>
/// Flat command, no subcommand enum — see <see cref="RakeCommand"/>'s remarks for the shared
/// rationale (single <c>trailing_var_arg</c> args vector, buffered <c>run_filtered</c>, no
/// passthrough branch, no <see cref="Cli.CommandArgumentParseException"/> path).
/// </remarks>
public static class RspecCommand
{
    /// <summary>
    /// Registry entry point. Injects <c>--format json</c> unless the user already specified a
    /// format, runs <c>rspec</c> via <see cref="RubySupport.RubyExec"/>, and filters the buffered
    /// output through <see cref="RspecFilters.FilterRspecOutput"/> or (when the user requested a
    /// custom format) <see cref="RspecFilters.StripNoise"/> + <see cref="RspecFilters.FilterRspecText"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>rspec</c> verb.</param>
    /// <returns>The wrapped <c>rspec</c> process's exit code.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var ruby = RubySupport.RubyExec("rspec");

        var hasFormat = args.Any(a =>
            a == "--format"
            || a == "-f"
            || a.StartsWith("--format=", StringComparison.Ordinal)
            || (a.StartsWith("-f", StringComparison.Ordinal) && a.Length > 2 && !a.StartsWith("--", StringComparison.Ordinal)));

        var invocation = new List<string>(ruby.BaseArguments);
        if (!hasFormat)
        {
            invocation.Add("--format");
            invocation.Add("json");
        }

        invocation.AddRange(args);

        if (RuntimeOptions.Verbosity > 0)
        {
            var injected = hasFormat ? string.Empty : " --format json";
            Console.Error.Write($"Running: rspec{injected} {string.Join(' ', args)}\n");
        }

        return CommandRunner.RunFilteredAsync(
            ruby.FileName,
            invocation,
            "rspec",
            string.Join(' ', args),
            stdout => hasFormat
                ? RspecFilters.FilterRspecText(RspecFilters.StripNoise(stdout))
                : RspecFilters.FilterRspecOutput(stdout),
            new RunOptions(FilterStdoutOnly: true, TeeLabel: "rspec"));
    }
}
