namespace RtkSharp.Core;

/// <summary>
/// Represents the parsed command-line invocation of rtk: its own flags
/// (verbosity, ultra-compact mode, color, help/version) plus the wrapped
/// command name and the arguments to forward to it.
/// </summary>
public record RtkArguments(
    int Verbosity,
    bool UltraCompact,
    bool NoColor,
    bool Help,
    bool Version,
    string? CommandName,
    string[] CommandArgs
)
{
    /// <summary>
    /// Parses raw process arguments into an <see cref="RtkArguments"/>, separating rtk's own
    /// flags from the wrapped command name and its arguments.
    /// </summary>
    /// <param name="args">The raw command-line arguments passed to the process.</param>
    /// <returns>The parsed <see cref="RtkArguments"/>.</returns>
    public static RtkArguments Parse(string[] args)
    {
        int verbosity = 0;
        bool ultraCompact = false;
        bool noColor = false;
        bool help = false;
        bool version = false;
        string? commandName = null;
        var commandArgs = new List<string>();

        int i = 0;
        for (; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "--")
            {
                i++; // Skip --
                if (i < args.Length)
                {
                    commandName = args[i];
                    i++;
                }
                break;
            }

            if (arg.StartsWith('-'))
            {
                if (arg == "-v" || arg == "--verbose")
                {
                    verbosity = 1;
                }
                else if (arg == "-vv")
                {
                    verbosity = 2;
                }
                else if (arg == "-vvv")
                {
                    verbosity = 3;
                }
                else if (arg == "-u" || arg == "--ultra-compact")
                {
                    ultraCompact = true;
                }
                else if (arg == "--no-color")
                {
                    noColor = true;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    help = true;
                }
                else if (arg == "--version")
                {
                    version = true;
                }
                else
                {
                    // Unrecognized option, or belongs to the command
                    // If we haven't seen a command name yet, it might be an unrecognized RTK option.
                    // But to be safe, we stop parsing RTK options at the first unrecognized thing
                    // if it doesn't look like a standard RTK option.
                    commandName = arg;
                    i++;
                    break;
                }
            }
            else
            {
                commandName = arg;
                i++;
                break;
            }
        }

        // Add the rest of the arguments to commandArgs
        for (; i < args.Length; i++)
        {
            commandArgs.Add(args[i]);
        }

        return new RtkArguments(
            verbosity,
            ultraCompact,
            noColor,
            help,
            version,
            commandName,
            commandArgs.ToArray()
        );
    }
}

/// <summary>
/// Default <see cref="IArgumentParser"/> implementation backed by <see cref="RtkArguments.Parse"/>.
/// </summary>
public sealed class ArgumentParser : IArgumentParser
{
    /// <summary>
    /// Parses raw process arguments into an <see cref="RtkArguments"/>.
    /// </summary>
    /// <param name="args">The raw command-line arguments passed to the process.</param>
    /// <returns>The parsed <see cref="RtkArguments"/>.</returns>
    public RtkArguments Parse(string[] args) => RtkArguments.Parse(args);
}
