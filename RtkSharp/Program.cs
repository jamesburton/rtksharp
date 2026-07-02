using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Execution;

return await RtkProgram.RunAsync(args);

internal static class RtkProgram
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        IArgumentParser argumentParser = new ArgumentParser();
        var parsed = argumentParser.Parse(args);

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

        if (CommandRegistry.TryGet(parsed.CommandName, out var handler))
        {
            return await handler(parsed.CommandArgs).ConfigureAwait(false);
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
