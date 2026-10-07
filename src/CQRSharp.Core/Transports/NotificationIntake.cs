using System.Data;
using System.Diagnostics;
using System.Text.Json;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Transports;

/// <summary>
///     The application's <see cref="INotificationIntake" />, one per DI scope: takes a received notification into the local
///     outbox, deduplicated through the inbox, as one message per local subscription. What a transport's consumer calls
///     before it acknowledges a message to its source.
/// </summary>
/// <remarks>
///     Every intake is measured (<c>cqrsharp.transport.received</c>) and traced (<c>CQRS Transport Receive</c>, a Consumer
///     span parented to the sender's trace), whatever its outcome, so the transports themselves need no telemetry of their
///     own.
/// </remarks>
internal sealed partial class NotificationIntake(IServiceProvider services) : INotificationIntake
{
    // What an inbox record's handler name holds: the stores keep it in 256 characters.
    private const int MaxSourceLength = 256;

    // The OpenTelemetry messaging attribute for the id a message carries on the wire.
    private const string MessagingMessageId = "messaging.message.id";

    public async Task<IntakeResult> AcceptAsync(InboundNotification message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Transport, nameof(message));
        if (string.IsNullOrWhiteSpace(message.Source) || message.Source.Length > MaxSourceLength)
            throw new ArgumentException(
                $"{nameof(InboundNotification)}.{nameof(InboundNotification.Source)} must be non-empty and at most {MaxSourceLength} characters.",
                nameof(message));
        ArgumentNullException.ThrowIfNull(message.Payload, nameof(message));

        var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var metrics = services.GetService<CqrsMetrics>();
        var startedAt = timeProvider.GetTimestamp();

        using var activity = StartReceiveActivity(message);
        try
        {
            var result = await AcceptCoreAsync(message, timeProvider, cancellationToken).ConfigureAwait(false);
            var outcome = OutcomeName(result);
            activity?.SetStatus(result.Outcome is IntakeOutcome.UnknownNotification or IntakeOutcome.UnreadablePayload
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok, result.Detail);
            Record(metrics, message, outcome, timeProvider.GetElapsedTime(startedAt));
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Record(metrics, message, "failed", timeProvider.GetElapsedTime(startedAt));
            throw;
        }
    }

    private async Task<IntakeResult> AcceptCoreAsync(InboundNotification message, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var logger = (ILogger?)services.GetService<ILogger<NotificationIntake>>() ?? NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(message.NotificationName))
            return new IntakeResult(IntakeOutcome.UnreadablePayload, 0, "The message carries no notification name.", null);

        var writer = OutboxWriter.Resolve(services);
        var options = services.GetService<IOptions<OutboxProcessorOptions>>()?.Value ?? new OutboxProcessorOptions();

        // Without an id nothing can recognise a redelivery, so the inbox is left out of it altogether.
        Guid? id = string.IsNullOrEmpty(message.MessageId) ? null : MessageIdGuid.From(message.MessageId);
        var inbox = id is not null && options.UseInbox ? services.GetService<IInboxStore>() : null;

        if (inbox is not null && await inbox.IsDeliveredAsync(id!.Value, message.Source, cancellationToken).ConfigureAwait(false))
        {
            LogDuplicate(logger, message.NotificationName, message.MessageId, message.Source);
            return new IntakeResult(IntakeOutcome.Duplicate, 0, "Taken in before.", null);
        }

        INotification? notification;
        try
        {
            notification = writer.Serializer.Deserialize(message.NotificationName, message.Payload);
        }
        catch (JsonException ex)
        {
            // The serializer's contract: an unreadable payload throws JsonException, and no retry can fix it.
            return new IntakeResult(IntakeOutcome.UnreadablePayload, 0, ex.Message, null);
        }

        if (notification is null)
        {
            // The rule the processor applies to a stored message it does not know: a newer instance may know it, until the
            // grace period is over.
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var detail = $"The notification '{message.NotificationName}' is not known to this application.";
            return UnknownRecipientDeferral.TryDefer(options, now, message.SentAt ?? now, out var notBefore)
                ? new IntakeResult(IntakeOutcome.UnknownNotification, 0, detail, notBefore - now)
                : new IntakeResult(IntakeOutcome.UnknownNotification, 0,
                    $"{detail} No instance took it in within the unknown-recipient grace period ({options.UnknownRecipientGracePeriod}).", null);
        }

        // Local handlers only: a notification that came in through a transport is never forwarded back out through one.
        // Nothing is recorded for one nobody here handles, so a handler added later is not denied its successors.
        if (writer.Subscriptions.GetSubscriptions(notification.GetType()).Count == 0)
        {
            LogNoSubscribers(logger, message.NotificationName, message.MessageId, message.Source);
            return new IntakeResult(IntakeOutcome.NoSubscribers, 0, "No local handler receives it.", null);
        }

        OutboxEntry[] entries = [new OutboxEntry(notification)];

        if (inbox is not null &&
            services.GetService<IUnitOfWork>() is { HasActiveTransaction: false } unitOfWork &&
            await TryStoreAtomicallyAsync(unitOfWork, inbox, writer, entries, id!.Value, message, logger, cancellationToken).ConfigureAwait(false) is { } atomic)
            return atomic;

        // Stored first, recorded after: a crash between the two takes the notification in again (a duplicate, confined to
        // that window), and never loses it.
        var stored = await writer.StoreReceivedAsync(entries, cancellationToken).ConfigureAwait(false);
        writer.Signal();

        if (inbox is not null)
            try
            {
                // The messages stand whatever happens now, so the record gets its own budget rather than the caller's
                // token: a record a stopping host cancelled would only mean a second intake after a redelivery.
                using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(10), timeProvider);
                await inbox.RecordDeliveryAsync(id!.Value, message.Source, finalize.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRecordFailed(logger, ex, message.NotificationName, message.MessageId, message.Source);
            }

        LogStored(logger, message.NotificationName, message.MessageId, message.Source, stored);
        return new IntakeResult(IntakeOutcome.Stored, stored, null, null);
    }

    // The exactly-once intake: when the outbox store and the inbox both write through the unit of work's transaction, the
    // messages and the record commit together, and a racing intake of the same message loses at the record and rolls its
    // messages back. Null when either writes elsewhere; the transaction, begun only to ask, is then rolled back untouched.
    private async Task<IntakeResult?> TryStoreAtomicallyAsync(
        IUnitOfWork unitOfWork,
        IInboxStore inbox,
        OutboxWriter writer,
        OutboxEntry[] entries,
        Guid id,
        InboundNotification message,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, cancellationToken).ConfigureAwait(false);

        // Asked with the transaction open, as the processor asks: whether their writes would commit with it.
        if (!writer.Store.JoinsUnitOfWork || !inbox.JoinsUnitOfWork)
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        int stored;
        try
        {
            stored = await writer.StoreReceivedAsync(entries, cancellationToken).ConfigureAwait(false);
            if (!await inbox.RecordDeliveryAsync(id, message.Source, cancellationToken).ConfigureAwait(false))
            {
                await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                LogDuplicate(logger, message.NotificationName, message.MessageId, message.Source);
                return new IntakeResult(IntakeOutcome.Duplicate, 0, "Taken in by a concurrent intake.", null);
            }

            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                // Never the caller's token: it may be what caused the failure.
                await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                LogRollbackFailed(logger, rollbackEx, message.NotificationName, message.MessageId, message.Source);
            }

            throw;
        }

        writer.Signal();
        LogStored(logger, message.NotificationName, message.MessageId, message.Source, stored);
        return new IntakeResult(IntakeOutcome.Stored, stored, null, null);
    }

    private static string OutcomeName(IntakeResult result)
        => result.Outcome switch
        {
            IntakeOutcome.Stored => "stored",
            IntakeOutcome.Duplicate => "duplicate",
            IntakeOutcome.NoSubscribers => "no_subscribers",
            IntakeOutcome.UnknownNotification => result.RetryAfter is null ? "unknown" : "unknown_held",
            _ => "unreadable"
        };

    private static void Record(CqrsMetrics? metrics, InboundNotification message, string outcome, TimeSpan elapsed)
    {
        if (metrics is not null && (metrics.TransportReceived.Enabled || metrics.TransportReceiveDuration.Enabled))
            metrics.RecordTransportReceive(message.Transport, message.NotificationName, outcome, elapsed);
    }

    // Receiving a message from outside the process is the consuming side of what its sender produced: a Consumer span,
    // parented to the sender's trace when the message carries it.
    private static Activity? StartReceiveActivity(InboundNotification message)
    {
        var activity = ActivityContext.TryParse(message.TraceParent, message.TraceState, out var parent)
            ? CqrsActivitySource.Instance.StartActivity(CqrsActivitySource.TransportReceiveOperation, ActivityKind.Consumer, parent)
            : CqrsActivitySource.Instance.StartActivity(CqrsActivitySource.TransportReceiveOperation, ActivityKind.Consumer);
        if (activity is null) return null;

        activity.SetTag(CqrsTelemetry.Tags.Transport, message.Transport);
        activity.SetTag(CqrsTelemetry.Tags.NotificationName, message.NotificationName);
        if (message.MessageId is { Length: > 0 } messageId)
            activity.SetTag(MessagingMessageId, messageId);
        return activity;
    }

    // The 5100 event-id block. What happens to one message is Debug, as a delivery is in the processor; a record that
    // failed, which lets a redelivery store the notification again, is a Warning; a rollback that failed is an Error.
    [LoggerMessage(5100, LogLevel.Debug, "Took notification {NotificationType} (message {MessageId}) in from {Source}: {Count} outbox message(s) stored.")]
    private static partial void LogStored(ILogger logger, string notificationType, string? messageId, string source, int count);

    [LoggerMessage(5101, LogLevel.Debug, "Notification {NotificationType} (message {MessageId}) from {Source} was taken in before; skipped the redelivery.")]
    private static partial void LogDuplicate(ILogger logger, string notificationType, string? messageId, string source);

    [LoggerMessage(5102, LogLevel.Debug, "Notification {NotificationType} (message {MessageId}) from {Source} has no local handler; nothing was stored.")]
    private static partial void LogNoSubscribers(ILogger logger, string notificationType, string? messageId, string source);

    [LoggerMessage(5103, LogLevel.Warning, "Took notification {NotificationType} (message {MessageId}) in from {Source}, but recording it in the inbox failed; a redelivery of it would be stored again.")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string notificationType, string? messageId, string source);

    [LoggerMessage(5104, LogLevel.Error, "Rolling back the intake transaction of notification {NotificationType} (message {MessageId}) from {Source} failed.")]
    private static partial void LogRollbackFailed(ILogger logger, Exception exception, string notificationType, string? messageId, string source);
}
