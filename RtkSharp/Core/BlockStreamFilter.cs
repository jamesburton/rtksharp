namespace RtkSharp.Core;

/// <summary>
/// Streaming filter that groups a line-oriented stream into "blocks" via a pluggable
/// <see cref="IBlockHandler"/>, emitting each completed block as it closes and a final summary via
/// <see cref="IStreamFilter.OnExit"/>. Faithful port of Rust's <c>BlockStreamFilter&lt;H&gt;</c>
/// (<c>src/core/stream.rs</c>:24-86), used by <c>cargo build</c>/<c>cargo test</c>/<c>cargo check</c>'s
/// streaming handlers.
/// </summary>
/// <typeparam name="THandler">The block-grouping strategy.</typeparam>
public sealed class BlockStreamFilter<THandler> : IStreamFilter
    where THandler : IBlockHandler
{
    private readonly List<string> _currentBlock = [];
    private bool _inBlock;

    /// <summary>
    /// Creates a filter driven by <paramref name="handler"/>.
    /// </summary>
    /// <param name="handler">The block-grouping strategy.</param>
    public BlockStreamFilter(THandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Handler = handler;
    }

    /// <summary>
    /// The block-grouping strategy driving this filter, exposed so callers (and tests) can inspect
    /// accumulated counters after the stream ends.
    /// </summary>
    public THandler Handler { get; }

    /// <inheritdoc />
    public string? FeedLine(string line)
    {
        if (Handler.ShouldSkip(line))
        {
            return null;
        }

        if (Handler.IsBlockStart(line))
        {
            var prev = EmitBlock();
            _currentBlock.Add(line);
            _inBlock = true;
            return prev;
        }

        if (_inBlock)
        {
            if (Handler.IsBlockContinuation(line, _currentBlock))
            {
                _currentBlock.Add(line);
                return null;
            }

            _inBlock = false;
            return EmitBlock();
        }

        return null;
    }

    /// <inheritdoc />
    public string Flush() => EmitBlock() ?? string.Empty;

    /// <inheritdoc />
    public string? OnExit(int exitCode, string raw) => Handler.FormatSummary(exitCode, raw);

    private string? EmitBlock()
    {
        if (_currentBlock.Count == 0)
        {
            return null;
        }

        var block = string.Join('\n', _currentBlock);
        _currentBlock.Clear();
        return block + "\n";
    }
}
