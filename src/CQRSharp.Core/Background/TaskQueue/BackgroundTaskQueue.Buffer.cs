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
    private ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken)
    {
        return new ValueTask<QueuedTask>(DequeueCoreAsync(cancellationToken));
    }

    private async Task<QueuedTask> DequeueCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitForItemPermitAsync(cancellationToken).ConfigureAwait(false);

            QueueEntry dequeued;
            lock (_gate)
            {
                if (_count <= 0)
                    continue;

                dequeued = DequeueLocked();
            }

            _freeSlots?.Release();

            if (_metricsEnabled)
            {
                _metrics.ItemDequeued();
                _metrics.RecordLatency(_timeProvider.GetUtcNow().UtcDateTime - dequeued.Task.EnqueueTime);
            }

            return dequeued.Task;
        }
    }

    private async ValueTask WaitForItemPermitAsync(CancellationToken cancellationToken)
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

            await _itemsAvailable.WaitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!_itemsAvailable.Wait(0))
                throw new ChannelClosedException();
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    private void Complete()
    {
        lock (_gate)
        {
            if (_isCompleted) return;
            _isCompleted = true;
        }

        _completion.Cancel();
    }

    private void DrainAndCancelPending()
    {
        QueueEntry[] pending;
        int pendingCount;

        lock (_gate)
        {
            pendingCount = _count;
            if (pendingCount == 0) return;

            pending = new QueueEntry[pendingCount];
            for (var i = 0; i < pendingCount; i++)
            {
                pending[i] = _buffer[_head];
                _buffer[_head] = default;
                _head = (_head + 1) % _buffer.Length;
            }

            _count = 0;
            _head = 0;
            _tail = 0;
        }

        while (_itemsAvailable.Wait(0))
        {
        }

        // Use the completion token, which Complete() has already cancelled, so drained callers observe a genuinely
        // cancelled token rather than _shutdownToken (which may not be cancelled yet on an explicit Dispose).
        for (var i = 0; i < pendingCount; i++)
            pending[i].Cancel(_completion.Token);
    }

    private void EnqueueLocked(QueueEntry entry)
    {
        _buffer[_tail] = entry;
        _tail = (_tail + 1) % _buffer.Length;
        _count++;
    }

    private QueueEntry DequeueLocked()
    {
        var entry = _buffer[_head];
        _buffer[_head] = default;
        _head = (_head + 1) % _buffer.Length;
        _count--;
        return entry;
    }

    private QueueEntry RemoveNewestLocked()
    {
        _tail = (_tail - 1 + _buffer.Length) % _buffer.Length;
        var entry = _buffer[_tail];
        _buffer[_tail] = default;
        _count--;
        return entry;
    }
}
