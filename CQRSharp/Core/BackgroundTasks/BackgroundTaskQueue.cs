using System.Threading.Channels;

namespace CQRSharp.Core.BackgroundTasks;

public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _workItems;

    public BackgroundTaskQueue(int capacity = 100)
    {
        //Bounded channel.
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };

        _workItems = Channel.CreateBounded<Func<CancellationToken, Task>>(options);
    }

    /// <inheritdoc />
    public async Task QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        
        //Earlier, the queue’s writer is invoked via _workItems.Writer.TryWrite(workItem), which does not block if the channel’s capacity is full.
        //If there is no space, TryWrite simply fails, and returns false. This behavior would cause tasks to silently fail, so using something
        //like WriteAsync is a better choice, as it would actually block the thread, but it would ensure that the task is executed and not lost.
        await _workItems.Writer.WriteAsync(workItem, ct);
    }

    /// <inheritdoc />
    public async Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
    {
        var workItem = await _workItems.Reader.ReadAsync(cancellationToken);
        return workItem;
    }
}