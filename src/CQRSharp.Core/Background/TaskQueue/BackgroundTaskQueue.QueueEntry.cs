using System.Threading.Channels;
using CQRSharp.Pipelines;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Background.TaskQueue.Types;
using CQRSharp.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Background.TaskQueue;

internal sealed partial class BackgroundTaskQueue
{
    private readonly struct QueueEntry
    {
        public QueueEntry(
            QueuedTask task,
            Action<Exception>? setException,
            Action<CancellationToken>? setCanceled)
        {
            Task = task;
            SetException = setException;
            SetCanceled = setCanceled;
        }

        public QueuedTask Task { get; }
        public Action<Exception>? SetException { get; }
        public Action<CancellationToken>? SetCanceled { get; }

        public void Reject(Exception exception) => SetException?.Invoke(exception);
        public void Cancel(CancellationToken cancellationToken) => SetCanceled?.Invoke(cancellationToken);
    }
}
