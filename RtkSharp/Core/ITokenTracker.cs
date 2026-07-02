namespace RtkSharp.Core;

/// <summary>
/// Abstraction over token-savings tracking, allowing filters to record before/after
/// output sizes without depending on a concrete storage mechanism.
/// </summary>
public interface ITokenTracker
{
    /// <summary>
    /// Records a filtering event's raw and filtered output lengths for a command.
    /// </summary>
    /// <param name="command">The command that was executed.</param>
    /// <param name="rawLength">The length, in characters, of the raw (unfiltered) output.</param>
    /// <param name="filteredLength">The length, in characters, of the filtered output.</param>
    void Record(string command, int rawLength, int filteredLength);
}

/// <summary>
/// A no-op <see cref="ITokenTracker"/> for contexts that don't need persistence
/// (tests, dry runs).
/// </summary>
public sealed class NoOpTokenTracker : ITokenTracker
{
    /// <summary>
    /// Does nothing; provided for contexts that don't need tracking persistence.
    /// </summary>
    /// <param name="command">The command that was executed.</param>
    /// <param name="rawLength">The length, in characters, of the raw (unfiltered) output.</param>
    /// <param name="filteredLength">The length, in characters, of the filtered output.</param>
    public void Record(string command, int rawLength, int filteredLength)
    {
        // Intentionally no-op: default tracker for contexts that don't need
        // persistence (tests, dry runs). A real tracker implements the same
        // interface and is injected once tracking storage exists.
    }
}
