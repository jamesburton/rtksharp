namespace RtkSharp.Core;

public interface ITokenTracker
{
    void Record(string command, int rawLength, int filteredLength);
}

public sealed class NoOpTokenTracker : ITokenTracker
{
    public void Record(string command, int rawLength, int filteredLength)
    {
        // Intentionally no-op: default tracker for contexts that don't need
        // persistence (tests, dry runs). A real tracker implements the same
        // interface and is injected once tracking storage exists.
    }
}
