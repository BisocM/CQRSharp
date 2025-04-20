using System.Threading.Channels;

namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
///     Decorates an <see cref="ChannelReader{T}" /> so that each successful read
///     decrements a shared <c>long</c> counter that tracks the queue’s live length.
/// </summary>
/// <remarks>
///     Only the three read‑related methods are overridden; all others are forwarded
///     to <see cref="_inner" />.
/// </remarks>
internal sealed class CountingChannelReader(
    ChannelReader<QueuedTask> inner,
    BackgroundTaskQueue parent)
    : ChannelReader<QueuedTask>
{
    private readonly ChannelReader<QueuedTask> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly BackgroundTaskQueue _parent = parent ?? throw new ArgumentNullException(nameof(parent));

    /// <inheritdoc />
    public override bool TryRead(out QueuedTask item)
    {
        var ok = _inner.TryRead(out item);
        if (ok)
            Interlocked.Decrement(ref _parent.CurrentCount);
        return ok;
    }

    /// <inheritdoc />
    public override async ValueTask<QueuedTask> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var task = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Decrement(ref _parent.CurrentCount);
        return task;
    }

    /// <inheritdoc />
    public override ValueTask<bool> WaitToReadAsync(
        CancellationToken cancellationToken = default)
    {
        return _inner.WaitToReadAsync(cancellationToken);
    }
}