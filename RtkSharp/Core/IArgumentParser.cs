namespace RtkSharp.Core;

/// <summary>
/// Abstraction over command-line argument parsing, allowing tests to substitute
/// custom argument parsing behavior.
/// </summary>
public interface IArgumentParser
{
    /// <summary>
    /// Parses raw process arguments into an <see cref="RtkArguments"/>.
    /// </summary>
    /// <param name="args">The raw command-line arguments passed to the process.</param>
    /// <returns>The parsed <see cref="RtkArguments"/>.</returns>
    RtkArguments Parse(string[] args);
}
