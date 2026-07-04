using System.Text;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Execution;

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
