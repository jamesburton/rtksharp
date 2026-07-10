using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Ruby;

namespace RtkSharp.Commands.Ruby;

/// <summary>
/// Implements the <c>rtk rake</c> CLI verb: parses standard Minitest output produced by both
/// <c>rake test</c> and <c>rails test</c>, filtering down to failures/errors and the summary line.
/// Faithful port of <c>src/cmds/ruby/rake_cmd.rs</c> and its <c>Commands::Rake</c> dispatch arm in
/// <c>src/main.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat command, no subcommand enum.</b> Unlike <see cref="RtkSharp.Commands.Rust.CargoCommand"/>
/// or <see cref="RtkSharp.Commands.Dotnet.DotnetCommand"/>, Rust's <c>rake_cmd.rs</c> has no
/// nested-subcommand dispatch — it is declared with a single <c>#[arg(trailing_var_arg = true,
/// allow_hyphen_values = true)] args: Vec&lt;String&gt;</c> in <c>main.rs</c>'s <c>Commands::Rake</c>
/// and unconditionally routes through <c>runner::run_filtered</c> (buffered, not streamed). This port
/// therefore has no passthrough branch and no <see cref="CommandArgumentParseException"/> path: clap
/// accepts any token stream for this arm, so there is no clap-equivalent rejection to reproduce.
/// </para>
/// <para>
/// <b><c>rake test</c> vs <c>rails test</c> selection.</b> <c>rake test</c> only supports a single
/// file via <c>TEST=path</c> and ignores positional file args; when any positional test file paths
/// are detected, Rust switches to <c>rails test</c>, which handles single files, multiple files, and
/// line-number syntax (<c>file.rb:15</c>) natively. See <see cref="SelectRunner"/>.
/// </para>
/// </remarks>
public static class RakeCommand
{
    /// <summary>
    /// Registry entry point. Selects <c>rake</c> or <c>rails</c> per <see cref="SelectRunner"/>, runs
    /// it via <see cref="RubySupport.RubyExec"/> (auto-detecting <c>bundle exec</c>), and filters the
    /// buffered output through <see cref="RakeFilters.FilterMinitestOutput"/>.
    /// </summary>
    /// <param name="args">The arguments following the <c>rake</c> verb.</param>
    /// <returns>The wrapped process's exit code.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var (tool, effectiveArgs) = SelectRunner(args);
        var ruby = RubySupport.RubyExec(tool);

        if (RuntimeOptions.Verbosity > 0)
        {
            Console.Error.Write($"Running: {ruby.FileName} {string.Join(' ', effectiveArgs)}\n");
        }

        var invocation = new List<string>(ruby.BaseArguments);
        invocation.AddRange(effectiveArgs);

        return CommandRunner.RunFilteredAsync(
            ruby.FileName,
            invocation,
            "rake",
            string.Join(' ', args),
            RakeFilters.FilterMinitestOutput,
            new RunOptions(TeeLabel: "rake"));
    }

    /// <summary>
    /// Decides whether to invoke <c>rake test</c> or <c>rails test</c> based on <paramref
    /// name="args"/>. Faithful port of Rust <c>select_runner</c> (<c>rake_cmd.rs</c>:20-41). Both
    /// branches return the argument vector unchanged — only the tool name differs.
    /// </summary>
    /// <param name="args">The arguments following the <c>rake</c> verb.</param>
    /// <returns>The tool name (<c>"rake"</c> or <c>"rails"</c>) and the (unchanged) effective arguments.</returns>
    internal static (string Tool, IReadOnlyList<string> Args) SelectRunner(IReadOnlyList<string> args)
    {
        var hasTestSubcommand = args.Count > 0 && args[0] == "test";
        if (!hasTestSubcommand)
        {
            return ("rake", args);
        }

        var afterTest = args.Skip(1);

        var needsRails = afterTest
            .Where(a => !a.Contains('=') && !a.StartsWith('-'))
            .Any(LooksLikeTestPath);

        return (needsRails ? "rails" : "rake", args);
    }

    /// <summary>
    /// Heuristically detects whether <paramref name="arg"/> looks like a positional test-file path
    /// (as opposed to a flag or <c>KEY=value</c> pair). Faithful port of Rust
    /// <c>looks_like_test_path</c> (<c>rake_cmd.rs</c>:43-50).
    /// </summary>
    /// <param name="arg">The candidate argument.</param>
    /// <returns>True if <paramref name="arg"/> looks like a test-file path.</returns>
    internal static bool LooksLikeTestPath(string arg)
    {
        var path = arg.Split(':')[0];
        return path.EndsWith(".rb", StringComparison.Ordinal)
            || path.StartsWith("test/", StringComparison.Ordinal)
            || path.StartsWith("spec/", StringComparison.Ordinal)
            || path.Contains("_test.rb", StringComparison.Ordinal)
            || path.Contains("_spec.rb", StringComparison.Ordinal);
    }
}
