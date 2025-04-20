using System.Threading.Channels;

namespace CQRSharp.Core.BackgroundTasks.Types;

internal sealed class CountingChannelReader(ChannelReader<QueuedTask> inner, BackgroundTaskQueue parent)
    : ChannelReader<QueuedTask>
{
    public override bool TryRead(out QueuedTask item)
    {
        var result = inner.TryRead(out item);
        if (result)
            Interlocked.Decrement(ref parent.CurrentCount);
        return result;
    }

    public override async ValueTask<QueuedTask> ReadAsync(CancellationToken cancellationToken = default)
    {
        var item = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Decrement(ref parent.CurrentCount);
        return item;
    }

    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
        inner.WaitToReadAsync(cancellationToken);
}