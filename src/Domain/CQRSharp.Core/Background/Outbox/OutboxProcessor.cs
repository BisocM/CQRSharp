using System.Collections.Concurrent;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Outbox;
using CQRSharp.Abstractions.Data.Models.Outbox;
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
    private readonly ConcurrentDictionary<Guid, int> _retryTracker = new();
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

        _logger.LogInformation("Fetched {Count} messages from the outbox to process.", outboxMessages.Count());

        foreach (var message in outboxMessages)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                var notification = serializer.Deserialize(message.NotificationType, message.Payload);
                await dispatcher.Publish(notification, stoppingToken);
                await outboxStore.MarkAsProcessedAsync(message.Id, stoppingToken);
                _retryTracker.TryRemove(message.Id, out _);
                _logger.LogInformation("Successfully processed and dispatched notification {NotificationType} (ID: {MessageId}).", message.NotificationType, message.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process notification {NotificationType} (ID: {MessageId}).", message.NotificationType, message.Id);
                _retryTracker.AddOrUpdate(message.Id, 1, (_, count) => count + 1);

                if (_retryTracker.TryGetValue(message.Id, out var attempts) && attempts >= _options.MaxRetryAttempts)
                {
                    await outboxStore.MarkAsFailedAsync(message.Id, ex.ToString(), stoppingToken);
                    _retryTracker.TryRemove(message.Id, out _);
                    _logger.LogCritical("Notification {NotificationType} (ID: {MessageId}) has reached max retry attempts and is marked as failed.", message.NotificationType,
                        message.Id);
                }
            }
        }
    }
}