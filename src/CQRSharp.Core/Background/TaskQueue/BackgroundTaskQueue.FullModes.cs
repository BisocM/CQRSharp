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
    private QueueWriteResult TryEnqueueDropWrite(QueueEntry entry)
    {
        lock (_gate)
        {
            if (_isCompleted)
                return RejectWrite(entry);

            if (_count >= _buffer.Length)
                return RejectWrite(entry);

            EnqueueLocked(entry);
        }

        _itemsAvailable.Release();
        RecordEnqueue(entry.Task.SequenceNumber, entry.Task.WorkItem);
        return new QueueWriteResult(QueueWriteResultCode.Enqueued, entry.Task.SequenceNumber);
    }

    private QueueWriteResult TryEnqueueDropOldest(QueueEntry entry)
    {
        QueueEntry? evicted = null;
        var shouldSignal = false;

        lock (_gate)
        {
            if (_isCompleted)
                return RejectWrite(entry);

            if (_count >= _buffer.Length)
            {
                evicted = DequeueLocked();
                if (_metricsEnabled) _metrics.ItemDroppedOldest();
                shouldSignal = false;
            }
            else
            {
                shouldSignal = true;
            }

            EnqueueLocked(entry);
        }

        if (evicted.HasValue)
            evicted.Value.Reject(new ChannelClosedException());

        if (shouldSignal)
            _itemsAvailable.Release();

        RecordEnqueue(entry.Task.SequenceNumber, entry.Task.WorkItem);
        return new QueueWriteResult(QueueWriteResultCode.Enqueued, entry.Task.SequenceNumber);
    }

    private QueueWriteResult TryEnqueueDropNewest(QueueEntry entry)
    {
        QueueEntry? evicted = null;
        var shouldSignal = false;

        lock (_gate)
        {
            if (_isCompleted)
                return RejectWrite(entry);

            if (_count >= _buffer.Length)
            {
                evicted = RemoveNewestLocked();
                if (_metricsEnabled) _metrics.ItemEvictedNewest();
                shouldSignal = false;
            }
            else
            {
                shouldSignal = true;
            }

            EnqueueLocked(entry);
        }

        if (evicted.HasValue)
            evicted.Value.Reject(new ChannelClosedException());

        if (shouldSignal)
            _itemsAvailable.Release();

        RecordEnqueue(entry.Task.SequenceNumber, entry.Task.WorkItem);
        return new QueueWriteResult(QueueWriteResultCode.Enqueued, entry.Task.SequenceNumber);
    }

    private async Task<QueueWriteResult> EnqueueWaitAsync(QueueEntry entry, CancellationToken cancellationToken)
    {
        if (_freeSlots is null)
            throw new InvalidOperationException("Queue was not configured for Wait mode.");

        var waited = !_freeSlots.Wait(0);
        if (waited)
        {
            CancellationTokenSource? linkedCts = null;
            try
            {
                var waitToken = cancellationToken;
                if (cancellationToken.CanBeCanceled)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _completion.Token);
                    waitToken = linkedCts.Token;
                }
                else
                {
                    waitToken = _completion.Token;
                }

                await _freeSlots.WaitAsync(waitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return RejectWrite(entry);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        lock (_gate)
        {
            if (_isCompleted)
            {
                _freeSlots.Release();
                return RejectWrite(entry);
            }

            EnqueueLocked(entry);
        }

        _itemsAvailable.Release();
        RecordEnqueue(entry.Task.SequenceNumber, entry.Task.WorkItem);
        return new QueueWriteResult(waited ? QueueWriteResultCode.Waited : QueueWriteResultCode.Enqueued, entry.Task.SequenceNumber);
    }

    private QueueWriteResult RejectWrite(QueueEntry entry)
    {
        entry.Reject(new ChannelClosedException());
        if (_metricsEnabled) _metrics.ItemDroppedNewest();
        PublishNotification(new TaskRejectedNotification(entry.Task.SequenceNumber, _options.FullMode));
        return new QueueWriteResult(QueueWriteResultCode.DroppedNewest, entry.Task.SequenceNumber);
    }
}
