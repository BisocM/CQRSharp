using System.Threading.Channels;

namespace CQRSharp.Core.Background.TaskQueue.Types;

/// <summary>
///     Wraps a channel reader so that each successful read decrements
///     the shared queue count and records time spent in queue.
/// </summary>
internal sealed class CountingChannelReader : ChannelReader<QueuedTask>
{
    private readonly ChannelReader<QueuedTask> _inner;
    private readonly BackgroundTaskQueue _parent;

    /// <summary>
    ///     Initializes a new instance of <see cref="CountingChannelReader" />.
    /// </summary>
    public CountingChannelReader(ChannelReader<QueuedTask> inner, BackgroundTaskQueue parent)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    /// <inheritdoc />
    public override bool TryRead(out QueuedTask item)
    {
        var ok = _inner.TryRead(out item);
        if (ok) _parent.DecrementCountAndRecordLatency(item);
        return ok;
    }

    /// <inheritdoc />
    public override async ValueTask<QueuedTask> ReadAsync(CancellationToken cancellationToken = default)
    {
        var task = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        _parent.DecrementCountAndRecordLatency(task);
        return task;
    }

    /// <inheritdoc />
    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
        _inner.WaitToReadAsync(cancellationToken);
}