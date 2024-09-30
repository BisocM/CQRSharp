using System.Threading.Channels;

namespace CQRSharp.Core.BackgroundTasks
{
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

        public void QueueBackgroundWorkItem(Func<CancellationToken, Task>? workItem)
        {
            if (workItem != null)
                _workItems.Writer.TryWrite(workItem);
            else
                throw new ArgumentNullException(nameof(workItem));
        }

        public async Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
        {
            var workItem = await _workItems.Reader.ReadAsync(cancellationToken);
            return workItem;
        }
    }
}