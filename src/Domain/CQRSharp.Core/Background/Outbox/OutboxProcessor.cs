using System.Diagnostics;
using System.Text.Json;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Models.Outbox;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Background.Outbox;

/// <summary>
///     A background service that periodically polls the <see cref="IOutboxStore" /> for pending
///     notifications and dispatches them.
/// </summary>
internal sealed class OutboxProcessor : BackgroundService
{
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly OutboxProcessorOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OutboxProcessor" /> class.
    /// </summary>
    public OutboxProcessor(
        ILogger<OutboxProcessor> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxProcessorOptions> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox Processor is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxMessagesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "An unhandled exception occurred in the Outbox Processor.");
            }

            await Task.Delay(_options.PollingInterval, stoppingToken);
        }

        _logger.LogInformation("Outbox Processor is stopping.");
    }

    private async Task ProcessOutboxMessagesAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        var outboxStore = provider.GetService<IOutboxStore>();
        var serializer = provider.GetService<INotificationSerializer>();
        var dispatcher = provider.GetService<IDirectNotificationDispatcher>();

        if (outboxStore is null || serializer is null || dispatcher is null)
        {
            _logger.LogWarning("Outbox services (IOutboxStore, INotificationSerializer, IDirectNotificationDispatcher) are not registered. The OutboxProcessor will not run.");
            // Prevent fast spinning by waiting indefinitely. The service will stop on shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        var messages = await outboxStore.GetPendingAsync(_options.BatchSize, stoppingToken);

        var outboxMessages = messages as OutboxMessage[] ?? messages.ToArray();
        if (outboxMessages.Length == 0) return;

        _logger.LogInformation("Fetched {Count} messages from the outbox to process.", outboxMessages.Length);

        foreach (var message in outboxMessages)
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Restore the originating request's trace context so the outbox dispatch links to the same trace.
            using var activity = StartOutboxActivity(message);

            try
            {
                var notification = serializer.Deserialize(message.NotificationType, message.Payload);
                if (notification is null)
                {
                    // An unknown / undeserializable notification can never succeed; fail it immediately (no retry).
                    await outboxStore.MarkAsFailedAsync(
                        message.Id,
                        $"Failed to deserialize notification '{message.NotificationType}'.",
                        stoppingToken).ConfigureAwait(false);
                    _logger.LogError(
                        "Failed to deserialize notification {NotificationType} (ID: {MessageId}). Marked as failed.",
                        message.NotificationType,
                        message.Id);
                    continue;
                }

                await dispatcher.Publish(notification, stoppingToken).ConfigureAwait(false);
                await outboxStore.MarkAsProcessedAsync(message.Id, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Successfully processed and dispatched notification {NotificationType} (ID: {MessageId}).",
                    message.NotificationType, message.Id);
            }
            catch (JsonException jsonEx)
            {
                // A corrupt payload for a known notification type is deterministic; dead-letter it immediately with
                // the real cause rather than wasting retries. (Unknown types deserialize to null and are handled above.)
                _logger.LogError(jsonEx, "Corrupt payload for notification {NotificationType} (ID: {MessageId}); marking as failed.",
                    message.NotificationType, message.Id);
                try
                {
                    await outboxStore.MarkAsFailedAsync(message.Id, jsonEx.ToString(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception storeEx) when (storeEx is not OperationCanceledException)
                {
                    _logger.LogError(storeEx, "Failed to mark corrupt outbox message {MessageId} as failed.", message.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to process notification {NotificationType} (ID: {MessageId}).",
                    message.NotificationType, message.Id);

                // Record the failed attempt durably so retry limits survive restarts, and dead-letter the message once
                // attempts are exhausted. Guard these store calls: if the store itself is the failing dependency we
                // must not abort the rest of the batch.
                try
                {
                    var attempt = message.AttemptCount + 1;
                    var recordedAttempts = await outboxStore
                        .IncrementAttemptAsync(message.Id, ex.ToString(), ComputeNextRetryAt(attempt), stoppingToken)
                        .ConfigureAwait(false);

                    if (recordedAttempts >= _options.MaxRetryAttempts)
                    {
                        await outboxStore.MarkAsFailedAsync(message.Id, ex.ToString(), stoppingToken).ConfigureAwait(false);
                        _logger.LogCritical(
                            "Notification {NotificationType} (ID: {MessageId}) reached {Attempts} attempts and is marked as failed.",
                            message.NotificationType, message.Id, recordedAttempts);
                    }
                }
                catch (Exception storeEx) when (storeEx is not OperationCanceledException)
                {
                    _logger.LogError(storeEx,
                        "Failed to record the outbox delivery attempt for message {MessageId}; it will be retried on a later poll.",
                        message.Id);
                }
            }
        }
    }

    /// <summary>
    ///     Computes the next eligibility time for a failed message using exponential back-off (2^attempt seconds),
    ///     capped at five minutes.
    /// </summary>
    private static DateTime ComputeNextRetryAt(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, Math.Min(attempt, 20)), 300d);
        return DateTime.UtcNow.AddSeconds(seconds);
    }

    private static Activity? StartOutboxActivity(OutboxMessage message)
    {
        var activity = ActivityContext.TryParse(message.TraceParent, null, out var parent)
            ? CqrsActivitySource.Instance.StartActivity("CQRS Outbox Dispatch", ActivityKind.Producer, parent)
            : CqrsActivitySource.Instance.StartActivity("CQRS Outbox Dispatch", ActivityKind.Producer);
        activity?.SetTag("cqrsharp.notification_type", message.NotificationType);
        return activity;
    }
}
