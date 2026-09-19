using System.Diagnostics;
using System.Text.Json;
using CQRSharp.Pipelines;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
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
    private readonly TimeProvider _timeProvider;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OutboxProcessor" /> class.
    /// </summary>
    public OutboxProcessor(
        ILogger<OutboxProcessor> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxProcessorOptions> options,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            catch (Exception ex) when (!IsShutdown(ex, stoppingToken))
            {
                _logger.LogError(ex, "An unhandled exception occurred in the Outbox Processor.");
            }

            await Task.Delay(_options.PollingInterval, _timeProvider, stoppingToken);
        }

        _logger.LogInformation("Outbox Processor is stopping.");
    }

    private async Task ProcessOutboxMessagesAsync(CancellationToken stoppingToken)
    {
        // The batch scope owns the store (claiming, marking, attempt bookkeeping). Each message is dispatched in its own
        // scope below, so one handler's scoped state — a DbContext with half-tracked entities after a failure — can
        // never leak into the next message's handlers or into the store's own bookkeeping writes.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var outboxStore = provider.GetService<IOutboxStore>();
        var serializer = provider.GetService<INotificationSerializer>();

        if (outboxStore is null || serializer is null || provider.GetService<IDirectNotificationDispatcher>() is null)
        {
            _logger.LogWarning("Outbox services (IOutboxStore, INotificationSerializer, IDirectNotificationDispatcher) are not registered. The OutboxProcessor will not run.");
            // Prevent fast spinning by waiting indefinitely. The service will stop on shutdown.
            await Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, stoppingToken);
            return;
        }

        var messages = await outboxStore.GetPendingAsync(_options.BatchSize, stoppingToken);

        var outboxMessages = messages as OutboxMessage[] ?? messages.ToArray();
        if (outboxMessages.Length == 0) return;

        _logger.LogInformation("Fetched {Count} messages from the outbox to process.", outboxMessages.Length);

        var claimedAt = _timeProvider.GetUtcNow().UtcDateTime;

        // Declared outside the loop so that a shutdown which interrupts a dispatch can still release the in-flight message
        // and everything after it.
        var index = 0;
        try
        {
            for (; index < outboxMessages.Length; index++)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    // Hand the undispatched rest of the batch back, so a restart does not have to wait out the lease.
                    await ReleaseRemainingAsync(outboxStore, outboxMessages, index).ConfigureAwait(false);
                    break;
                }

                var message = outboxMessages[index];
                if (message.Claim is not { } claim)
                {
                    _logger.LogError(
                        "The outbox store returned message {MessageId} without a claim; it cannot be finalized and is skipped. " +
                        "IOutboxStore.GetPendingAsync must set OutboxMessage.Claim on every message it claims.", message.Id);
                    continue;
                }

                // Restore the originating request's trace context so the outbox dispatch links to the same trace.
                using var activity = StartOutboxActivity(message);

                try
                {
                    // One lease covers the whole batch but messages are dispatched one by one: once a good part of it is
                    // gone, extend this message's lease before starting on it, or another processor may pick it up mid-flight.
                    if (await RenewIfNeededAsync(outboxStore, claim, claimedAt, stoppingToken).ConfigureAwait(false) is not { } current)
                    {
                        _logger.LogInformation("Lost the claim on outbox message {MessageId} before dispatch; another processor owns it.", message.Id);
                        continue;
                    }

                    claim = current;

                    var notification = serializer.Deserialize(message.NotificationType, message.Payload);
                    if (notification is null)
                    {
                        // An unknown / undeserializable notification can never succeed; fail it immediately (no retry).
                        await outboxStore.MarkAsFailedAsync(
                            claim,
                            $"Failed to deserialize notification '{message.NotificationType}'.",
                            stoppingToken).ConfigureAwait(false);
                        _logger.LogError(
                            "Failed to deserialize notification {NotificationType} (ID: {MessageId}). Marked as failed.",
                            message.NotificationType,
                            message.Id);
                        continue;
                    }

                    await using (var messageScope = _scopeFactory.CreateAsyncScope())
                    {
                        var dispatcher = messageScope.ServiceProvider.GetRequiredService<IDirectNotificationDispatcher>();
                        await dispatcher.Publish(notification, stoppingToken).ConfigureAwait(false);
                    }

                    if (await outboxStore.MarkAsProcessedAsync(claim, stoppingToken).ConfigureAwait(false))
                        _logger.LogInformation(
                            "Successfully processed and dispatched notification {NotificationType} (ID: {MessageId}).",
                            message.NotificationType, message.Id);
                    else
                        // Delivered, but the lease ran out during dispatch and someone else holds the message now: this is
                        // the at-least-once case. The other processor's outcome stands; ours must not overwrite it.
                        _logger.LogWarning(
                            "Dispatched notification {NotificationType} (ID: {MessageId}) but its claim had been lost; it may be delivered again.",
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
                        await outboxStore.MarkAsFailedAsync(claim, jsonEx.ToString(), stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception storeEx) when (!IsShutdown(storeEx, stoppingToken))
                    {
                        _logger.LogError(storeEx, "Failed to mark corrupt outbox message {MessageId} as failed.", message.Id);
                    }
                }
                catch (Exception ex) when (!IsShutdown(ex, stoppingToken))
                {
                    _logger.LogError(ex, "Failed to process notification {NotificationType} (ID: {MessageId}).",
                        message.NotificationType, message.Id);

                    // Record the failed attempt durably so retry limits survive restarts, or dead-letter the message when this
                    // was its last allowed attempt. One claim-checked call either way: recording the attempt returns the
                    // message to pending and ends the claim, so a follow-up call under it would be rejected. Guard the store
                    // call: if the store itself is the failing dependency we must not abort the rest of the batch.
                    try
                    {
                        var attempt = message.AttemptCount + 1;
                        if (attempt >= _options.MaxRetryAttempts)
                        {
                            if (await outboxStore.MarkAsFailedAsync(claim, ex.ToString(), stoppingToken).ConfigureAwait(false))
                                _logger.LogCritical(
                                    "Notification {NotificationType} (ID: {MessageId}) reached {Attempts} attempts and is marked as failed.",
                                    message.NotificationType, message.Id, attempt);
                        }
                        else
                        {
                            await outboxStore
                                .IncrementAttemptAsync(claim, ex.ToString(), ComputeNextRetryAt(attempt), stoppingToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception storeEx) when (!IsShutdown(storeEx, stoppingToken))
                    {
                        _logger.LogError(storeEx,
                            "Failed to record the outbox delivery attempt for message {MessageId}; it will be retried on a later poll.",
                            message.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await ReleaseRemainingAsync(outboxStore, outboxMessages, index).ConfigureAwait(false);
            throw;
        }
    }

    // Renews once half of the lease the message was claimed with has elapsed; a short batch never pays for a renewal.
    // Returns the claim to use, or null when it was already lost.
    private async Task<OutboxClaim?> RenewIfNeededAsync(IOutboxStore store, OutboxClaim claim, DateTime claimedAt, CancellationToken stoppingToken)
    {
        var lease = claim.LeasedUntil - claimedAt;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (lease <= TimeSpan.Zero || now < claimedAt + TimeSpan.FromTicks(lease.Ticks / 2))
            return claim;

        return await store.RenewAsync(claim, stoppingToken).ConfigureAwait(false);
    }

    private async Task ReleaseRemainingAsync(IOutboxStore store, OutboxMessage[] batch, int fromIndex)
    {
        var remaining = new List<OutboxClaim>(batch.Length - fromIndex);
        for (var i = fromIndex; i < batch.Length; i++)
            if (batch[i].Claim is { } claim)
                remaining.Add(claim);

        if (remaining.Count == 0) return;

        try
        {
            // The host is already stopping, so its token is cancelled; bound the courtesy call instead.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
            await store.ReleaseAsync(remaining, timeout.Token).ConfigureAwait(false);
            _logger.LogInformation("Released {Count} claimed outbox message(s) on shutdown.", remaining.Count);
        }
        catch (Exception ex)
        {
            // Not fatal: the leases simply expire and the messages are reclaimed after the visibility timeout.
            _logger.LogWarning(ex, "Could not release {Count} claimed outbox message(s) on shutdown.", remaining.Count);
        }
    }

    // Only a cancellation caused by host shutdown may unwind the processor. A handler's own OperationCanceledException
    // (an HttpClient timeout, its own linked token) while the host is running is an ordinary failed attempt: letting
    // it escape would fault the BackgroundService — stopping the host by default — and, because the attempt was never
    // recorded, the same message would do it again on every lease expiry without ever being dead-lettered.
    private static bool IsShutdown(Exception ex, CancellationToken stoppingToken)
        => ex is OperationCanceledException && stoppingToken.IsCancellationRequested;

    /// <summary>
    ///     Computes the next eligibility time for a failed message using exponential back-off (2^attempt seconds),
    ///     capped at five minutes.
    /// </summary>
    private DateTime ComputeNextRetryAt(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, Math.Min(attempt, 20)), 300d);
        return _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(seconds);
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