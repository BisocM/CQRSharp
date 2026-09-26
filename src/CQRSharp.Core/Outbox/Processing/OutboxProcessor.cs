using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     A background service that claims due notifications from the <see cref="IOutboxStore" /> and delivers each one to
///     the single handler it is addressed to — up to <see cref="OutboxProcessorOptions.MaxDegreeOfParallelism" /> at a
///     time, each in its own DI scope, deduplicated through the <see cref="IInboxStore" /> when one is registered. What a
///     handler publishes during its delivery is settled with the delivery, as a request's notifications are with the
///     request: stored once it succeeded, discarded when it fails. It claims batch after batch while messages are due, and
///     only then waits for the polling interval, which the <see cref="IOutboxSignal" /> cuts short whenever this process
///     stores a message.
/// </summary>
internal sealed partial class OutboxProcessor : BackgroundService
{
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly OutboxProcessorOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NotificationPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxSignal? _signal;
    private readonly IOptions<OutboxOptions>? _outboxOptions;
    private readonly CqrsMetrics? _metrics;
    private readonly Random _jitter = new();
    private DateTime _nextBacklogSampleAt = DateTime.MinValue;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OutboxProcessor" /> class.
    /// </summary>
    public OutboxProcessor(
        ILogger<OutboxProcessor> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxProcessorOptions> options,
        NotificationPublisher publisher,
        TimeProvider? timeProvider = null,
        OutboxSignal? signal = null,
        IOptions<OutboxOptions>? outboxOptions = null,
        CqrsMetrics? metrics = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _publisher = publisher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _signal = signal;
        _outboxOptions = outboxOptions;
        _metrics = metrics;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Registered unconditionally, the processor reads the effective mode - whatever configured it, in whatever
        // order - once the host starts, and stays idle while the outbox is off.
        if (_outboxOptions is not null && _outboxOptions.Value.Mode == OutboxMode.Disabled)
        {
            LogIdle(_logger);
            return;
        }

        LogStarting(_logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var dispatched = 0;
                try
                {
                    dispatched = await ProcessOutboxMessagesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsShutdown(ex, stoppingToken))
                {
                    LogUnhandled(_logger, ex);
                }

                // A poll that found work claims again at once: a full batch leaves a backlog behind it, and a partition
                // head that just finished makes its successor due. Only a poll that found nothing due waits. That cannot
                // spin: a failed message backs off, a deferred one waits, and one leased elsewhere is not claimable.
                if (dispatched == 0)
                    await WaitForNextPollAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // The gauges report this processor's last sample; once it stops they must not keep reporting a frozen one.
            _metrics?.ClearBacklog();
            LogStopping(_logger);
        }
    }

    // The polling delay doubles as the wake-up: a message stored by this process signals the processor, and the wait
    // returns early. Without a signal (a processor constructed by hand) it is a plain delay on the clock seam.
    private Task WaitForNextPollAsync(CancellationToken stoppingToken)
        => _signal is null
            ? Task.Delay(_options.PollingInterval, _timeProvider, stoppingToken)
            : _signal.WaitAsync(_options.PollingInterval, _timeProvider, stoppingToken);

    // Returns how many messages the poll claimed and dispatched.
    private async Task<int> ProcessOutboxMessagesAsync(CancellationToken stoppingToken)
    {
        // The batch scope owns the store (claiming, marking, attempt bookkeeping). Each message is delivered in its own
        // scope below, so one handler's scoped state — a DbContext with half-tracked entities after a failure — can
        // never leak into the next message's handler or into the store's own bookkeeping writes.
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var provider = scope.ServiceProvider;

            var outboxStore = provider.GetService<IOutboxStore>();
            var serializer = provider.GetService<INotificationSerializer>();
            var subscriptions = provider.GetService<INotificationSubscriptionRegistry>();

            if (outboxStore is null || serializer is null || subscriptions is null)
            {
                LogServicesMissing(_logger);
                // Prevent fast spinning by waiting indefinitely. The service will stop on shutdown.
                await Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, stoppingToken).ConfigureAwait(false);
                return 0;
            }

            // Sampled before the claim: a shutdown that cancels the sample must not leave a claimed batch unreleased.
            await SampleBacklogIfDueAsync(outboxStore, stoppingToken).ConfigureAwait(false);

            var claimedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var claimed = await outboxStore.ClaimPendingAsync(_options.BatchSize, stoppingToken).ConfigureAwait(false);
            if (claimed.Count == 0) return 0;

            LogFetched(_logger, claimed.Count);

            var batch = new BatchContext(outboxStore, serializer, subscriptions, claimedAt);

            // Bounded parallelism: the gate admits MaxDegreeOfParallelism deliveries at once; at 1 this is exactly the
            // sequential loop. A batch never holds two messages of one partition (the store hands out partition heads
            // only), so concurrent deliveries cannot reorder a key. Whatever a shutdown interrupts — the messages not
            // yet started, and those whose delivery was cancelled mid-flight — is handed back so a restart does not
            // wait out the lease.
            var parallelism = _options.MaxDegreeOfParallelism;
            using var gate = new SemaphoreSlim(parallelism, parallelism);
            var inFlight = new List<Task>(Math.Min(claimed.Count, parallelism));
            var interrupted = new ConcurrentBag<OutboxClaim>();

            var index = 0;
            try
            {
                for (; index < claimed.Count; index++)
                {
                    await gate.WaitAsync(stoppingToken).ConfigureAwait(false);

                    // The slot may have been freed by a delivery the shutdown interrupted: nothing new starts once the
                    // host is stopping, however the wait and the cancellation raced.
                    stoppingToken.ThrowIfCancellationRequested();

                    var item = claimed[index];
                    inFlight.Add(DeliverGuardedAsync(batch, item.Message, item.Claim, gate, interrupted, stoppingToken));
                    inFlight.RemoveAll(t => t.IsCompletedSuccessfully);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Fall through: release the undispatched rest of the batch below, then unwind.
            }

            await Task.WhenAll(inFlight).ConfigureAwait(false);

            var remaining = new List<OutboxClaim>(interrupted);
            for (var i = index; i < claimed.Count; i++)
                remaining.Add(claimed[i].Claim);

            if (remaining.Count > 0)
                await ReleaseAsync(outboxStore, remaining).ConfigureAwait(false);

            stoppingToken.ThrowIfCancellationRequested();
            return claimed.Count;
        }
    }

    // One delivery, isolated from the rest of the batch: its own outcome bookkeeping never escapes, and a shutdown that
    // cancels it mid-flight hands its claim back with the others.
    private async Task DeliverGuardedAsync(
        BatchContext batch,
        OutboxMessage message,
        OutboxClaim claim,
        SemaphoreSlim gate,
        ConcurrentBag<OutboxClaim> interrupted,
        CancellationToken stoppingToken)
    {
        // Leave the batch loop first: otherwise the sequential part of this method would run on the loop's thread and a
        // slow synchronous handler would starve the batch loop of the chance to observe a shutdown. ForceYielding,
        // unlike Task.Yield, continues on the thread pool even when the first batch runs on a host-starting thread with
        // a SynchronizationContext (Microsoft.Extensions.Hosting before 10.0 starts a BackgroundService on the thread
        // that starts the host).
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var held = new StrongBox<OutboxClaim>(claim);
        try
        {
            // A delivery the shutdown overtook before it began is handed back untouched rather than started.
            stoppingToken.ThrowIfCancellationRequested();
            await DeliverAsync(batch, message, held, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The claim as last renewed: a store that rotates the token on renewal would not recognise the original.
            interrupted.Add(held.Value);
        }
        catch (Exception ex)
        {
            LogUnhandled(_logger, ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DeliverAsync(BatchContext batch, OutboxMessage message, StrongBox<OutboxClaim> held, CancellationToken stoppingToken)
    {
        var (outboxStore, serializer, subscriptions, claimedAt) = batch;
        var claim = held.Value;

        // Restore the originating request's trace context so the outbox dispatch links to the same trace. The span's
        // status follows the delivery: Ok once the outcome is recorded, Error for a failed attempt or a dead letter.
        using var activity = StartOutboxActivity(message);
        var startedAt = _timeProvider.GetTimestamp();

        try
        {
            // One lease covers the whole batch, but its messages wait their turn for a delivery slot: once a good part
            // of it is gone, extend this message's lease before starting on it, or another processor may pick it up
            // mid-flight. A lease that already ran out is not extended, and the message is left to its next claim.
            if (await RenewIfNeededAsync(outboxStore, claim, claimedAt, stoppingToken).ConfigureAwait(false) is not { } current)
            {
                LogClaimLostBeforeDispatch(_logger, message.Id);
                RecordOutcome(message, "claim_lost", startedAt);
                return;
            }

            claim = held.Value = current;

            INotification? notification;
            try
            {
                notification = serializer.Deserialize(message.NotificationType, message.Payload);
            }
            catch (JsonException jsonEx)
            {
                // A corrupt payload for a known notification type is deterministic; dead-letter it immediately with
                // the real cause rather than wasting retries. Only the payload is judged here: a JsonException thrown
                // by the handler itself is an ordinary failed attempt.
                LogCorruptPayload(_logger, jsonEx, message.NotificationType, message.Id);
                await DeadLetterNowAsync(jsonEx.ToString()).ConfigureAwait(false);
                return;
            }

            // A notification name (here) or handler name (below) this instance does not know is not necessarily one no
            // instance knows: in a fleet running mixed versions, a newer instance may have stored the message for a
            // notification or handler it added. Such a message is deferred, and dead-lettered only once the grace
            // period is over.
            if (notification is null)
            {
                if (TryDeferUnknownRecipient(message, out var notBefore))
                {
                    LogUnknownNotificationDeferred(_logger, message.NotificationType, message.Id, notBefore);
                    await DeferAsync(notBefore,
                        $"The notification '{message.NotificationType}' is not known to the instance that claimed it; deferred for an instance that knows it.").ConfigureAwait(false);
                    return;
                }

                LogDeserializationFailed(_logger, message.NotificationType, message.Id, _options.UnknownRecipientGracePeriod);
                await DeadLetterNowAsync(
                    $"The notification '{message.NotificationType}' is not known to the instance that claimed it, and no instance delivered it within " +
                    $"the unknown-recipient grace period ({_options.UnknownRecipientGracePeriod}). The notification's name changed, or no instance " +
                    "that processes the outbox serializes it any more.").ConfigureAwait(false);
                return;
            }

            // The message is addressed to one handler by its stable name.
            if (!subscriptions.TryGetSubscription(notification.GetType(), message.HandlerName, out var subscription))
            {
                if (TryDeferUnknownRecipient(message, out var notBefore))
                {
                    LogUnknownHandlerDeferred(_logger, message.HandlerName, message.NotificationType, message.Id, notBefore);
                    await DeferAsync(notBefore,
                        $"No notification handler named '{message.HandlerName}' subscribes to '{message.NotificationType}' on the instance that claimed it; " +
                        "deferred for an instance that has it.").ConfigureAwait(false);
                    return;
                }

                LogHandlerMissing(_logger, message.HandlerName, message.NotificationType, message.Id, _options.UnknownRecipientGracePeriod);
                await DeadLetterNowAsync(
                    $"No notification handler named '{message.HandlerName}' subscribes to '{message.NotificationType}', and no instance delivered it within " +
                    $"the unknown-recipient grace period ({_options.UnknownRecipientGracePeriod}). The handler was removed, renamed or moved; pin a " +
                    "handler's name with [NotificationHandlerName] before refactoring it.").ConfigureAwait(false);
                return;
            }

            var outcome = await DeliverToHandlerAsync(message, notification, subscription, stoppingToken).ConfigureAwait(false);
            if (outcome == DeliveryOutcome.Duplicate)
            {
                // Delivered before — by an earlier attempt of this message whose mark never landed, or by another
                // processor that took over its lease. The handler's work is done; only the bookkeeping was missing.
                LogDuplicate(_logger, message.NotificationType, message.Id, message.HandlerName);
                if (await TryMarkProcessedAsync(outboxStore, message, claim).ConfigureAwait(false) == true)
                    RecordOutcome(message, "duplicate", startedAt);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return;
            }

            // The handler's work is done, so its outcome is recorded even while the host is stopping: a mark cancelled
            // here would mean a redelivery after the restart. Bounded by its own timeout, not by the stopping token.
            var marked = await TryMarkProcessedAsync(outboxStore, message, claim).ConfigureAwait(false);
            if (marked is null)
            {
                // Delivered, but the outcome could not be recorded: the handler's work stands, so this is not a failed
                // attempt. The lease runs out and the message is delivered again (an inbox recognises it).
                RecordOutcome(message, "unrecorded", startedAt);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else if (marked == true)
            {
                LogDelivered(_logger, message.NotificationType, message.Id, message.HandlerName);
                RecordOutcome(message, "processed", startedAt);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                // Delivered, but the lease ran out during dispatch and someone else holds the message now: this is
                // the at-least-once case. The other processor's outcome stands; ours must not overwrite it. With an
                // inbox the redelivery is recognised and skipped.
                LogClaimLostAfterDispatch(_logger, message.NotificationType, message.Id, message.HandlerName);
                RecordOutcome(message, "claim_lost", startedAt);
            }
        }
        catch (DeliveryNotStartedException notStarted)
        {
            // The processor's own bookkeeping or the delivery's unit of work failed before the handler ran: not an
            // attempt of the handler's, so none is charged. The message stays under its lease, which is its back-off:
            // it is claimable again once the lease runs out.
            var cause = notStarted.InnerException!;
            LogDeliveryNotStarted(_logger, cause, message.Id, message.HandlerName);
            activity?.SetStatus(ActivityStatusCode.Error, cause.Message);
            RecordOutcome(message, "not_started", startedAt);
        }
        catch (Exception ex) when (!IsShutdown(ex, stoppingToken))
        {
            var attempt = message.AttemptCount + 1;
            LogHandlerFailed(_logger, ex, message.HandlerName, message.NotificationType, message.Id, attempt, _options.MaxAttempts);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            // Record the failed attempt durably so retry limits survive restarts, or dead-letter the message when this
            // was its last allowed attempt. One claim-checked call either way: recording the attempt returns the
            // message to pending and ends the claim, so a follow-up call under it would be rejected. Guard the store
            // call: if the store itself is the failing dependency we must not abort the rest of the batch.
            try
            {
                // The outcome is recorded once the store accepted it: a lost claim means another processor owns the
                // message now and reports what becomes of it.
                if (attempt >= _options.MaxAttempts)
                {
                    if (await outboxStore.MarkAsFailedAsync(claim, ex.ToString(), stoppingToken).ConfigureAwait(false))
                    {
                        RecordOutcome(message, "dead_letter", startedAt);
                        LogDeadLettered(_logger, message.NotificationType, message.Id, attempt, message.HandlerName);
                    }
                }
                else if (await outboxStore.IncrementAttemptAsync(claim, ex.ToString(), ComputeNextRetryAt(attempt), stoppingToken).ConfigureAwait(false) > 0)
                {
                    RecordOutcome(message, "retry", startedAt);
                }
            }
            catch (Exception storeEx) when (!IsShutdown(storeEx, stoppingToken))
            {
                LogAttemptRecordingFailed(_logger, storeEx, message.Id);
            }
        }

        // Handed back undelivered, without an attempt: another instance may deliver it. Guarded like the dead letter
        // below; a deferral the store could not record is simply claimable again once the lease runs out.
        async Task DeferAsync(DateTime notBefore, string reason)
        {
            try
            {
                if (await outboxStore.DeferAsync(claim, notBefore, reason, stoppingToken).ConfigureAwait(false))
                    RecordOutcome(message, "deferred", startedAt);
            }
            catch (Exception storeEx) when (!IsShutdown(storeEx, stoppingToken))
            {
                LogDeferFailed(_logger, storeEx, message.Id);
            }
        }

        // A message that can never be delivered, whatever its attempt count, is dead-lettered on the spot. The store
        // call is guarded: a store that is itself failing must not abort the rest of the batch.
        async Task DeadLetterNowAsync(string reason)
        {
            activity?.SetStatus(ActivityStatusCode.Error, reason);
            try
            {
                if (await outboxStore.MarkAsFailedAsync(claim, reason, stoppingToken).ConfigureAwait(false))
                    RecordOutcome(message, "dead_letter", startedAt);
            }
            catch (Exception storeEx) when (!IsShutdown(storeEx, stoppingToken))
            {
                LogDeadLetterFailed(_logger, storeEx, message.Id);
            }
        }
    }

    private enum DeliveryOutcome
    {
        Delivered,
        Duplicate
    }

    // Runs the handler in its own scope, as the owner of what it publishes, exactly as a request owns what it
    // publishes: buffered while the delivery runs, stored once it succeeded, discarded when it fails, so an attempt
    // that fails and the retry after it never both publish. With an inbox in that scope, the delivery is checked
    // against it first and recorded after. With a unit of work in the scope as well, the handler runs in a transaction
    // of its own, and what it publishes and the record go where they cannot outlive a rolled-back handler: inside that
    // transaction when the store (or the inbox) joins it, right after the commit otherwise (a crash between the two
    // means a redelivery, never a loss). Only a record inside the processor's own transaction is part of the attempt;
    // every other record is written once the handler's work, and what it published, already stands.
    private async Task<DeliveryOutcome> DeliverToHandlerAsync(
        OutboxMessage message,
        INotification notification,
        NotificationSubscription subscription,
        CancellationToken stoppingToken)
    {
        var messageScope = _scopeFactory.CreateAsyncScope();
        await using (messageScope.ConfigureAwait(false))
        {
            var services = messageScope.ServiceProvider;

            // Begun here, synchronously, so the delivery's owner is current for everything the handler does.
            var publishes = RequestOutboxScope.Begin(services);
            try
            {
                var inbox = _options.UseInbox ? services.GetService<IInboxStore>() : null;
                if (inbox is null)
                {
                    await _publisher.Deliver(services, subscription, notification, stoppingToken).ConfigureAwait(false);
                    await SettleAsync(publishes).ConfigureAwait(false);
                    return DeliveryOutcome.Delivered;
                }

                if (await BeforeHandlerAsync(inbox.IsDeliveredAsync(message.Id, message.HandlerName, stoppingToken), stoppingToken).ConfigureAwait(false))
                {
                    publishes?.Abandon();
                    return DeliveryOutcome.Duplicate;
                }

                var unitOfWork = services.GetService<IUnitOfWork>();
                if (unitOfWork is null or { HasActiveTransaction: true })
                {
                    // No transaction of the processor's own: once the handler returns, its work is as final as the
                    // handler (or whoever owns the transaction it ran in) made it, and nothing the record does can take
                    // it back. What it published is stored before the record, so a crash between the two is a
                    // redelivery, never a delivery recorded without its notifications.
                    await _publisher.Deliver(services, subscription, notification, stoppingToken).ConfigureAwait(false);
                    await SettleAsync(publishes).ConfigureAwait(false);
                    return await RecordDeliveredAsync(inbox, message).ConfigureAwait(false);
                }

                return await DeliverInTransactionAsync(unitOfWork, inbox, publishes, services, message, notification, subscription, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                publishes?.Abandon();
                throw;
            }
        }
    }

    // The delivery in the processor's own transaction. Runs in the delivery's flow, so its owner is current here too.
    private async Task<DeliveryOutcome> DeliverInTransactionAsync(
        IUnitOfWork unitOfWork,
        IInboxStore inbox,
        RequestOutboxScope? publishes,
        IServiceProvider services,
        OutboxMessage message,
        INotification notification,
        NotificationSubscription subscription,
        CancellationToken stoppingToken)
    {
        await BeforeHandlerAsync(unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, stoppingToken), stoppingToken).ConfigureAwait(false);
        bool joined;
        var recorded = false;
        OutboxCommit? committed = null;
        try
        {
            await _publisher.Deliver(services, subscription, notification, stoppingToken).ConfigureAwait(false);

            // Asked while the transaction is open: whether the record would commit and roll back with it.
            joined = inbox.JoinsUnitOfWork;
            if (joined)
                recorded = await inbox.RecordDeliveryAsync(message.Id, message.HandlerName, stoppingToken).ConfigureAwait(false);

            if (!joined || recorded)
            {
                // What the handler published commits with its work, exactly as a request's notifications commit with it.
                committed = publishes is { } owned ? await owned.PrepareCommitAsync(stoppingToken).ConfigureAwait(false) : null;
                await unitOfWork.CommitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                // Never the stopping token: it may be what caused the failure.
                await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                LogRollbackFailed(_logger, rollbackEx, message.Id);
            }

            throw;
        }

        if (!joined || recorded)
        {
            // The handler's work is committed and stands: notifications that could not be stored after the commit are
            // lost, not retried, since a retry would run the committed work again.
            if (committed is not null && await committed.CompleteAsync().ConfigureAwait(false) is { } storeFailure)
                LogPublishesLostAfterCommit(_logger, storeFailure, message.Id, message.HandlerName, committed.Notifications.Count,
                    string.Join(", ", committed.Notifications.Select(n => n.GetType().Name)));

            await SettleAsync(publishes).ConfigureAwait(false);
            return joined ? DeliveryOutcome.Delivered : await RecordDeliveredAsync(inbox, message).ConfigureAwait(false);
        }

        // Another delivery of this message finished first (a lost lease taken over mid-flight). Its handler's work
        // stands; ours is undone with the transaction, and so is what it published.
        publishes?.Abandon();
        await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        return DeliveryOutcome.Duplicate;
    }

    // The delivery succeeded: stores what it published and no transaction already took.
    private static Task SettleAsync(RequestOutboxScope? publishes)
        => publishes is { } owned ? owned.CompleteAsync() : Task.CompletedTask;

    // Records a delivery whose work already stands. It is written even while the host is stopping, bounded by its own
    // timeout like the processed mark: a record cancelled here would mean a redelivery after the restart. A record that
    // cannot be written costs at most a redelivery, which the handler has to tolerate anyway; counting it as a failed
    // attempt would run finished work again, and could dead-letter a message that was delivered.
    private async Task<DeliveryOutcome> RecordDeliveredAsync(IInboxStore inbox, OutboxMessage message)
    {
        try
        {
            using var finalize = FinalizeTimeout();
            return await inbox.RecordDeliveryAsync(message.Id, message.HandlerName, finalize.Token).ConfigureAwait(false)
                ? DeliveryOutcome.Delivered
                // Another delivery of this message finished first; ours was a duplicate side effect.
                : DeliveryOutcome.Duplicate;
        }
        catch (Exception ex)
        {
            LogDeliveryNotRecorded(_logger, ex, message.Id, message.HandlerName);
            return DeliveryOutcome.Delivered;
        }
    }

    private void RecordOutcome(OutboxMessage message, string outcome, long startedAt)
    {
        if (_metrics is { } metrics && (metrics.OutboxMessages.Enabled || metrics.OutboxDispatchDuration.Enabled))
            metrics.RecordOutbox(message.NotificationType, message.HandlerName, outcome, _timeProvider.GetElapsedTime(startedAt));
    }

    // The backlog gauges are observable: they report the last sample the processor took, at most once per sample
    // interval and only while something listens, so an idle meter never costs a COUNT query.
    private async Task SampleBacklogIfDueAsync(IOutboxStore store, CancellationToken stoppingToken)
    {
        if (_metrics is not { OutboxBacklogEnabled: true } metrics) return;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (now < _nextBacklogSampleAt) return;
        _nextBacklogSampleAt = now + _options.BacklogSampleInterval;

        try
        {
            metrics.RecordBacklog(await store.GetBacklogAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (!IsShutdown(ex, stoppingToken))
        {
            LogBacklogSampleFailed(_logger, ex);
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

        return await BeforeHandlerAsync(store.RenewAsync(claim, stoppingToken), stoppingToken).ConfigureAwait(false);
    }

    // A step the processor takes before the handler runs (renewing the claim, checking the inbox, beginning the
    // delivery's transaction). Its failure is not the handler's, so it is marked as a delivery that never started.
    private static async Task<T> BeforeHandlerAsync<T>(Task<T> step, CancellationToken stoppingToken)
    {
        try
        {
            return await step.ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsShutdown(ex, stoppingToken))
        {
            throw new DeliveryNotStartedException(ex);
        }
    }

    private static async Task BeforeHandlerAsync(Task step, CancellationToken stoppingToken)
    {
        try
        {
            await step.ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsShutdown(ex, stoppingToken))
        {
            throw new DeliveryNotStartedException(ex);
        }
    }

    // Carries the failure of a step before the handler out of the delivery, to the one place that decides its outcome.
    private sealed class DeliveryNotStartedException(Exception cause) : Exception(cause.Message, cause);

    // Records a delivered message as processed. True: recorded; false: the claim was lost (someone else holds it now);
    // null: the store failed or timed out. Never throws: a failure to record is not a failure of the delivery, and must
    // not be counted as a failed attempt (which would dead-letter a message whose handler succeeded).
    private async Task<bool?> TryMarkProcessedAsync(IOutboxStore store, OutboxMessage message, OutboxClaim claim)
    {
        try
        {
            using var finalize = FinalizeTimeout();
            return await store.MarkAsProcessedAsync(claim, finalize.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOutcomeNotRecorded(_logger, ex, message.Id, message.HandlerName);
            return null;
        }
    }

    // The budget for recording a delivered message's outcome: its own, so a host that is stopping cannot cancel the
    // mark of a handler that just completed (the message would be delivered again after the restart).
    private CancellationTokenSource FinalizeTimeout() => new(TimeSpan.FromSeconds(10), _timeProvider);

    private async Task ReleaseAsync(IOutboxStore store, IReadOnlyCollection<OutboxClaim> claims)
    {
        try
        {
            // The host is already stopping, so its token is cancelled; bound the courtesy call instead.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
            await store.ReleaseAsync(claims, timeout.Token).ConfigureAwait(false);
            LogReleased(_logger, claims.Count);
        }
        catch (Exception ex)
        {
            // Not fatal: the leases simply expire and the messages are reclaimed after the visibility timeout.
            LogReleaseFailed(_logger, ex, claims.Count);
        }
    }

    // Only a cancellation caused by host shutdown may unwind the processor. A handler's own OperationCanceledException
    // (an HttpClient timeout, its own linked token) while the host is running is an ordinary failed attempt: letting
    // it escape would fault the BackgroundService — stopping the host by default — and, because the attempt was never
    // recorded, the same message would do it again on every lease expiry without ever being dead-lettered.
    private static bool IsShutdown(Exception ex, CancellationToken stoppingToken)
        => ex is OperationCanceledException && stoppingToken.IsCancellationRequested;

    // Whether a message addressed to a notification or handler this instance does not know is still within the grace
    // period other instances get for it, and if so until when to defer it. The delay grows with the message's age,
    // between one polling interval and the longest retry back-off: a message a newer instance is about to deliver waits
    // one poll, while one no instance knows is revisited a handful of times (its age about doubling each time), and
    // never deferred past the end of the grace period, so it is dead-lettered on time.
    private bool TryDeferUnknownRecipient(OutboxMessage message, out DateTime notBefore)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var age = now - message.CreatedAt;
        var grace = _options.UnknownRecipientGracePeriod;
        if (age >= grace)
        {
            notBefore = default;
            return false;
        }

        var shortest = _options.PollingInterval;
        var longest = _options.Retry.MaxDelay > shortest ? _options.Retry.MaxDelay : shortest;
        var delay = age < shortest ? shortest : age > longest ? longest : age;
        var remaining = grace - age;
        notBefore = now + (delay < remaining ? delay : remaining);
        return true;
    }

    /// <summary>Computes the next eligibility time for a failed message from the configured back-off.</summary>
    private DateTime ComputeNextRetryAt(int failedAttempts)
    {
        TimeSpan delay;
        lock (_jitter)
        {
            delay = RetryDelay(_options.Retry, failedAttempts, _jitter);
        }

        return _timeProvider.GetUtcNow().UtcDateTime + delay;
    }

    /// <summary>
    ///     The delay before the next attempt of a message that has failed <paramref name="failedAttempts" /> times (at least
    ///     1): <see cref="OutboxRetryOptions.BaseDelay" /> grown by <see cref="OutboxRetryOptions.BackoffMultiplier" /> per
    ///     further attempt, capped at <see cref="OutboxRetryOptions.MaxDelay" />, then jittered by
    ///     <see cref="OutboxRetryOptions.JitterFactor" /> in either direction.
    /// </summary>
    internal static TimeSpan RetryDelay(OutboxRetryOptions retry, int failedAttempts, Random random)
    {
        // The options were validated at start (MaxDelay within ten years, a finite multiplier of at least 1), so the only
        // arithmetic hazard left is growth past double's range: +infinity, which the cap turns into MaxDelay, unless the
        // base delay is zero, where 0 * infinity would be NaN rather than the zero every attempt then waits.
        var baseDelay = retry.BaseDelay.TotalMilliseconds;
        var delay = baseDelay == 0
            ? 0
            : Math.Min(retry.MaxDelay.TotalMilliseconds, baseDelay * Math.Pow(retry.BackoffMultiplier, failedAttempts - 1));

        if (retry.JitterFactor > 0)
            delay *= 1 + retry.JitterFactor * (random.NextDouble() * 2 - 1);

        return TimeSpan.FromMilliseconds(delay);
    }

    // Delivering a stored message to its handler is the consuming side of the message: a Consumer span, parented to the
    // span that published it when the message carries its trace context.
    private static Activity? StartOutboxActivity(OutboxMessage message)
    {
        var activity = ActivityContext.TryParse(message.TraceParent, null, out var parent)
            ? CqrsActivitySource.Instance.StartActivity("CQRS Outbox Dispatch", ActivityKind.Consumer, parent)
            : CqrsActivitySource.Instance.StartActivity("CQRS Outbox Dispatch", ActivityKind.Consumer);
        activity?.SetTag(CqrsTelemetry.Tags.NotificationName, message.NotificationType);
        activity?.SetTag(CqrsTelemetry.Tags.NotificationHandler, message.HandlerName);
        if (message.PartitionKey is { } partitionKey)
            activity?.SetTag(CqrsTelemetry.Tags.PartitionKey, partitionKey);
        return activity;
    }

    private sealed record BatchContext(
        IOutboxStore Store,
        INotificationSerializer Serializer,
        INotificationSubscriptionRegistry Subscriptions,
        DateTime ClaimedAt);

    // The 5000 event-id block. Per-message success is Debug: at volume an Information line per delivery is noise. A
    // decision is Information (a deferral in a fleet running mixed versions included), a store failure the next claim
    // repairs is a Warning, and Error is kept for what needs an operator: a dead letter, lost notifications, a rollback
    // that failed, the processor's own failure.
    [LoggerMessage(5000, LogLevel.Information, "Outbox processor is starting.")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(5021, LogLevel.Debug, "Outbox processor idle: the outbox mode is Disabled.")]
    private static partial void LogIdle(ILogger logger);

    [LoggerMessage(5022, LogLevel.Warning, "Outbox message {MessageId} was delivered to {HandlerName}, but recording it failed; it will be delivered again once its lease runs out.")]
    private static partial void LogOutcomeNotRecorded(ILogger logger, Exception exception, Guid messageId, string handlerName);

    [LoggerMessage(5001, LogLevel.Information, "Outbox processor is stopping.")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(5002, LogLevel.Error, "An unhandled exception occurred in the outbox processor.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    [LoggerMessage(5003, LogLevel.Warning, "Outbox services (IOutboxStore, INotificationSerializer, INotificationSubscriptionRegistry) are not registered. The outbox processor will not run.")]
    private static partial void LogServicesMissing(ILogger logger);

    [LoggerMessage(5004, LogLevel.Debug, "Fetched {Count} messages from the outbox to process.")]
    private static partial void LogFetched(ILogger logger, int count);

    [LoggerMessage(5006, LogLevel.Warning, "Lost the claim on outbox message {MessageId} before dispatch; another processor owns it. A lease that expires this early means the visibility timeout is too short for the batch.")]
    private static partial void LogClaimLostBeforeDispatch(ILogger logger, Guid messageId);

    [LoggerMessage(5007, LogLevel.Error, "Notification {NotificationType} (ID: {MessageId}) is not known to this instance, and no instance delivered it within {GracePeriod}; dead-lettered.")]
    private static partial void LogDeserializationFailed(ILogger logger, string notificationType, Guid messageId, TimeSpan gracePeriod);

    [LoggerMessage(5008, LogLevel.Error, "No notification handler named {HandlerName} subscribes to {NotificationType} (ID: {MessageId}), and no instance delivered it within {GracePeriod}; dead-lettered.")]
    private static partial void LogHandlerMissing(ILogger logger, string handlerName, string notificationType, Guid messageId, TimeSpan gracePeriod);

    [LoggerMessage(5009, LogLevel.Debug, "Delivered notification {NotificationType} (ID: {MessageId}) to {HandlerName}.")]
    private static partial void LogDelivered(ILogger logger, string notificationType, Guid messageId, string handlerName);

    [LoggerMessage(5010, LogLevel.Debug, "Notification {NotificationType} (ID: {MessageId}) was already delivered to {HandlerName}; skipped the redelivery.")]
    private static partial void LogDuplicate(ILogger logger, string notificationType, Guid messageId, string handlerName);

    [LoggerMessage(5011, LogLevel.Warning, "Delivered notification {NotificationType} (ID: {MessageId}) to {HandlerName} but its claim had been lost; it may be delivered again.")]
    private static partial void LogClaimLostAfterDispatch(ILogger logger, string notificationType, Guid messageId, string handlerName);

    [LoggerMessage(5012, LogLevel.Error, "The payload of notification {NotificationType} (ID: {MessageId}) cannot be read; dead-lettering it.")]
    private static partial void LogCorruptPayload(ILogger logger, Exception exception, string notificationType, Guid messageId);

    [LoggerMessage(5013, LogLevel.Warning, "Dead-lettering outbox message {MessageId} failed; it is claimable again once its lease runs out.")]
    private static partial void LogDeadLetterFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(5014, LogLevel.Warning, "Handler {HandlerName} failed to process notification {NotificationType} (ID: {MessageId}) on attempt {Attempt} of {MaxAttempts}.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string handlerName, string notificationType, Guid messageId, int attempt, int maxAttempts);

    [LoggerMessage(5015, LogLevel.Error, "Notification {NotificationType} (ID: {MessageId}) failed at {HandlerName} on attempt {Attempt}, its last, and is dead-lettered.")]
    private static partial void LogDeadLettered(ILogger logger, string notificationType, Guid messageId, int attempt, string handlerName);

    [LoggerMessage(5016, LogLevel.Warning, "Recording the failed delivery attempt of outbox message {MessageId} failed; it is claimable again once its lease runs out.")]
    private static partial void LogAttemptRecordingFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(5017, LogLevel.Information, "Released {Count} claimed outbox message(s) on shutdown.")]
    private static partial void LogReleased(ILogger logger, int count);

    [LoggerMessage(5018, LogLevel.Warning, "Could not release {Count} claimed outbox message(s) on shutdown.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(5019, LogLevel.Warning, "Measuring the outbox backlog failed; the outbox gauges keep their last sample.")]
    private static partial void LogBacklogSampleFailed(ILogger logger, Exception exception);

    [LoggerMessage(5020, LogLevel.Error, "Rolling back the delivery transaction of outbox message {MessageId} failed.")]
    private static partial void LogRollbackFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(5023, LogLevel.Warning, "Outbox message {MessageId} was delivered to {HandlerName}, but recording the delivery in the inbox failed; the delivery stands, and a redelivery would run the handler again.")]
    private static partial void LogDeliveryNotRecorded(ILogger logger, Exception exception, Guid messageId, string handlerName);

    [LoggerMessage(5024, LogLevel.Information, "Notification {NotificationType} (ID: {MessageId}) is not known to this instance; deferred until {NotBefore} for an instance that knows it.")]
    private static partial void LogUnknownNotificationDeferred(ILogger logger, string notificationType, Guid messageId, DateTime notBefore);

    [LoggerMessage(5025, LogLevel.Information, "No notification handler named {HandlerName} subscribes to {NotificationType} on this instance (ID: {MessageId}); deferred until {NotBefore} for an instance that has it.")]
    private static partial void LogUnknownHandlerDeferred(ILogger logger, string handlerName, string notificationType, Guid messageId, DateTime notBefore);

    [LoggerMessage(5026, LogLevel.Warning, "Deferring outbox message {MessageId} failed; it is claimable again once its lease runs out.")]
    private static partial void LogDeferFailed(ILogger logger, Exception exception, Guid messageId);

    // An Error with the exception: the delivery counts as done, so nothing else reports that its notifications are gone.
    [LoggerMessage(5027, LogLevel.Error, "Outbox message {MessageId} was delivered to {HandlerName} and its transaction committed, but storing the {Count} notification(s) it published failed; they are lost: {NotificationNames}.")]
    private static partial void LogPublishesLostAfterCommit(ILogger logger, Exception exception, Guid messageId, string handlerName, int count, string notificationNames);

    // A Warning: nothing is lost and no attempt is charged, but a step that keeps failing keeps the message from its
    // handler indefinitely, which only this line reports.
    [LoggerMessage(5028, LogLevel.Warning,
        "Outbox message {MessageId} was not delivered to {HandlerName}: a step before the handler (renewing the claim, checking the inbox or beginning the transaction) failed. No attempt is charged; the message is claimable again once its lease runs out.")]
    private static partial void LogDeliveryNotStarted(ILogger logger, Exception exception, Guid messageId, string handlerName);
}
