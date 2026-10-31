using System.Threading.Channels;

namespace ProjectRag.Core.Indexing;

/// <summary>
/// Plans the runs of the indexer: the queue of the sources that wait and the run that goes on. A source is at most once in the queue.
/// </summary>
public sealed class IndexScheduler
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly HashSet<int> _queued = [];
    private volatile IndexRun? _current;

    /// <summary>
    /// Gets the run that goes on now, or <see langword="null"/> when the indexer waits.
    /// </summary>
    public IndexRun? Current
    {
        get => _current;
        internal set => _current = value;
    }

    /// <summary>
    /// Gets the keys of the sources in the queue.
    /// </summary>
    public IReadOnlyList<int> Queued
    {
        get
        {
            lock (_lock)
                return [.. _queued];
        }
    }

    /// <summary>
    /// Gets the reader of the queue for the worker.
    /// </summary>
    internal ChannelReader<int> Reader => _channel.Reader;

    /// <summary>
    /// Puts a source into the queue.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <returns><see langword="true"/> when the source was not in the queue yet.</returns>
    public bool Enqueue(int sourceId)
    {
        lock (_lock)
        {
            if (!_queued.Add(sourceId))
                return false;

            _channel.Writer.TryWrite(sourceId);
            return true;
        }
    }

    /// <summary>
    /// Takes a source off the list of the queued sources when the worker starts its run.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    internal void MarkTaken(int sourceId)
    {
        lock (_lock)
            _queued.Remove(sourceId);
    }
}
