using System.Threading.Channels;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Background.TaskQueue.Types;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Background.TaskQueue;

internal sealed partial class BackgroundTaskQueue
{
    private void RecordEnqueue(long sequence, Func<CancellationToken, Task> workItem)
    {
        if (_metricsEnabled) _metrics.ItemEnqueued();
        PublishNotification(new TaskEnqueuedNotification(sequence, workItem));
    }

    private void PublishNotification(INotification notification)
    {
        _ = _notificationChannel.Writer
            .WriteAsync(notification, _shutdownToken)
            .AsTask()
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(
                        t.Exception,
                        "Failed to enqueue notification for background processing.");
            }, TaskScheduler.Default);
    }

    /// <summary>
    ///     The long-running task that processes notifications from the internal notification channel.
    /// </summary>
    private async Task ProcessNotificationsAsync(CancellationToken token)
    {
        try
        {
            var reader = _notificationChannel.Reader;
            await foreach (var notif in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await _notifSem.WaitAsync(token).ConfigureAwait(false);
                // Dispatch in a fire-and-forget manner to allow concurrent notification processing.
                _ = DispatchAsync(notif, token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Notification pump cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            // This is a last-resort catch block. Individual dispatch errors are handled in DispatchAsync.
            _logger.LogCritical(ex, "A fatal and unexpected error occurred in the notification pump.");
        }
    }

    /// <summary>
    ///     Dispatches a single notification to its handlers, with a retry policy for transient failures.
    /// </summary>
    /// <param name="notif">The notification to dispatch.</param>
    /// <param name="token">The cancellation token.</param>
    private async Task DispatchAsync(INotification notif, CancellationToken token)
    {
        // The matching _notifSem permit was acquired by the pump before invoking this method. Release it exactly once
        // here no matter how we exit — including the early null-dispatcher return and any scope-creation fault —
        // otherwise the pump's permits leak and it eventually blocks forever, silently stopping all queue notifications.
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetService<IDirectNotificationDispatcher>();
            if (dispatcher is null)
            {
                _logger.LogWarning(
                    "IDirectNotificationDispatcher is not registered. Background task queue notifications will not be dispatched.");
                return;
            }

            try
            {
                for (var attempt = 1; attempt <= _options.NotificationMaxRetries; attempt++)
                    try
                    {
                        // Attempt to publish the notification.
                        await PublishNotificationAsync(dispatcher, notif, token).ConfigureAwait(false);
                        return; // On success, exit the method.
                    }
                    catch (OperationCanceledException)
                    {
                        // If cancellation is requested, stop immediately.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Check if this was the last attempt.
                        if (attempt == _options.NotificationMaxRetries)
                        {
                            // Last attempt failed. Log as an error and give up.
                            _logger.LogError(ex,
                                "Notification {NotificationType} failed permanently after {MaxAttempts} attempts and will be discarded.",
                                notif.GetType().Name, _options.NotificationMaxRetries);
                            return; // Exit without rethrowing; the error is handled.
                        }

                        // Not the last attempt. Log as a warning and delay before retrying.
                        _logger.LogWarning(ex,
                            "Publish attempt {Attempt} for {NotificationType} failed; retrying in {Delay}ms.",
                            attempt, notif.GetType().Name, _options.NotificationRetryDelay.TotalMilliseconds);
                        await Task.Delay(_options.NotificationRetryDelay, _timeProvider, token).ConfigureAwait(false);
                    }
            }
            catch (OperationCanceledException)
            {
                // Catches cancellation that happens during the Task.Delay.
                _logger.LogWarning("Notification dispatch for {NotificationType} was canceled during a retry delay.", notif.GetType().Name);
            }
            catch (Exception ex)
            {
                // Safety net for unexpected errors in the dispatch logic itself.
                _logger.LogCritical(ex, "An unexpected error occurred in the notification dispatch loop for {NotificationType}.", notif.GetType().Name);
            }
        }
        finally
        {
            // Always release the semaphore slot. Tolerate disposal during shutdown (the queue may have torn the
            // semaphore down while this dispatch was still in flight).
            try
            {
                _notifSem.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static Task PublishNotificationAsync(
        IDirectNotificationDispatcher dispatcher,
        INotification notification,
        CancellationToken cancellationToken)
    {
        return notification switch
        {
            TaskEnqueuedNotification typed => dispatcher.Publish(typed, cancellationToken),
            TaskRejectedNotification typed => dispatcher.Publish(typed, cancellationToken),
            _ => dispatcher.Publish(notification, cancellationToken)
        };
    }
}
