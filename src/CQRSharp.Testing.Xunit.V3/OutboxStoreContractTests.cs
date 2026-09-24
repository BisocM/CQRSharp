using CQRSharp.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Testing;

/// <summary>
///     The single, reusable conformance suite for every <see cref="IOutboxStore" /> implementation. Each store
///     (in-memory, Redis, EF Core, ...) derives from this class and supplies a fresh store plus the controllable
///     <see cref="FakeTimeProvider" /> the store reads time from; every store therefore proves the exact same
///     claim, visibility-timeout, retry, deferral, dead-letter, FIFO, partition-ordering, dead-letter operation and
///     backlog semantics with no duplicated assertions. All timing is driven by advancing the fake clock, so the suite is
///     deterministic and never sleeps.
/// </summary>
public abstract class OutboxStoreContractTests : IAsyncLifetime
{
    /// <summary>Runs before each test; nothing by default.</summary>
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    ///     Runs after each test. Override it to dispose whatever <c>CreateStoreAsync</c> opened (a connection, a
    ///     context) so a long suite does not leak one per test.
    /// </summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private const string DefaultHandler = "Tests.TestHandler";
    private const string OtherHandler = "Tests.OtherHandler";

    /// <summary>The clock the store-under-test reads; tests advance it to exercise back-off and visibility timeouts.</summary>
    protected abstract FakeTimeProvider Time { get; }

    /// <summary>Creates a fresh, empty store bound to <see cref="Time" />.</summary>
    protected abstract Task<IOutboxStore> CreateStoreAsync();

    /// <summary>The visibility timeout the store-under-test is configured with; the suite advances the clock past it.</summary>
    protected virtual TimeSpan VisibilityTimeout => TimeSpan.FromMinutes(5);

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

    private OutboxMessage NewPending(
        DateTime? createdAt = null,
        DateTime? nextRetryAt = null,
        int attempt = 0,
        string handler = DefaultHandler,
        string? partitionKey = null,
        Guid? notificationId = null)
        => new(
            Guid.NewGuid(),
            "TestNotification",
            handler,
            [1, 2, 3],
            createdAt ?? Now,
            OutboxMessageStatus.Pending,
            ProcessedAt: null,
            LastError: null,
            AttemptCount: attempt,
            NextRetryAt: nextRetryAt,
            PartitionKey: partitionKey,
            NotificationId: notificationId);

    // Claims due messages and returns just the messages, for the checks that do not need the claims.
    private static async Task<IReadOnlyList<OutboxMessage>> ClaimMessagesAsync(IOutboxStore store, int batchSize)
        => (await store.ClaimPendingAsync(batchSize, CancellationToken.None)).Select(c => c.Message).ToList();

    // Claims the single due message and returns its claim.
    private static async Task<OutboxClaim> ClaimSingleAsync(IOutboxStore store)
    {
        var claimed = await store.ClaimPendingAsync(10, CancellationToken.None);
        return ContractAssert.Single(claimed, "exactly one message is due").Claim;
    }

    // Claims the single due message, checks it is the expected one, and returns its claim.
    private static async Task<OutboxClaim> ClaimSingleAsync(IOutboxStore store, OutboxMessage expected, string because)
    {
        var claimed = await store.ClaimPendingAsync(10, CancellationToken.None);
        var single = ContractAssert.Single(claimed, because);
        ContractAssert.Equal(expected.Id, single.Message.Id, $"Id of the claimed message ({because})");
        return single.Claim;
    }

    /// <summary>
    ///     Contract: with no unit-of-work transaction open, the store does not claim to join one. A store that did would
    ///     have a request's notifications written before a commit that nothing would roll back if it failed.
    /// </summary>
    [Fact]
    public async Task A_store_outside_a_transaction_does_not_join_the_unit_of_work()
    {
        var store = await CreateStoreAsync();

        Assert.False(store.JoinsUnitOfWork, "With no transaction open, nothing the store writes can roll back with one.");
    }

    /// <summary>Contract: <c>ClaimPendingAsync</c> hands out a stored message as <c>InProgress</c> together with a claim (message id, non-empty token, lease of now + visibility timeout).</summary>
    [Fact]
    public async Task ClaimPending_claims_a_stored_message_and_transitions_it_to_in_progress()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);

        var claimed = await store.ClaimPendingAsync(10, CancellationToken.None);

        var single = ContractAssert.Single(claimed, "the one stored message is due");
        ContractAssert.Equal(message.Id, single.Message.Id, "Id of the claimed message");
        ContractAssert.Equal(OutboxMessageStatus.InProgress, single.Message.Status, "Status of a claimed message");
        var claim = single.Claim;
        ContractAssert.Equal(message.Id, claim.MessageId, "MessageId of the issued claim");
        Assert.False(string.IsNullOrEmpty(claim.Token), "The issued claim must carry a non-empty token.");
        ContractAssert.CloseTo(Now + VisibilityTimeout, claim.LeasedUntil, TimeSpan.FromSeconds(1),
            "LeasedUntil of a fresh claim (now + visibility timeout)");
    }

    /// <summary>Contract: every transport field survives the store — the handler name, partition key, notification id, payload and trace parent come back exactly as stored.</summary>
    [Fact]
    public async Task A_claimed_message_carries_every_field_it_was_stored_with()
    {
        var store = await CreateStoreAsync();
        var notificationId = Guid.NewGuid();
        var message = NewPending(handler: "Billing.InvoiceHandler", partitionKey: "order-42", notificationId: notificationId)
            with { TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" };
        await store.StoreAsync([message], CancellationToken.None);

        var claimed = ContractAssert.Single(await ClaimMessagesAsync(store, 10), "the one stored message is due");

        ContractAssert.Equal(message.NotificationType, claimed.NotificationType, "NotificationType of the claimed message");
        ContractAssert.Equal("Billing.InvoiceHandler", claimed.HandlerName, "HandlerName of the claimed message");
        ContractAssert.Equal("order-42", claimed.PartitionKey, "PartitionKey of the claimed message");
        ContractAssert.Equal(notificationId, claimed.NotificationId, "NotificationId of the claimed message");
        ContractAssert.Equal(message.TraceParent, claimed.TraceParent, "TraceParent of the claimed message");
        ContractAssert.SequenceEqual(message.Payload, claimed.Payload, "Payload of the claimed message");
        ContractAssert.CloseTo(message.CreatedAt, claimed.CreatedAt, TimeSpan.FromMilliseconds(1), "CreatedAt of the claimed message");
    }

    /// <summary>Contract: due messages are claimed oldest-first by <c>CreatedAt</c>, regardless of the order they were stored in.</summary>
    [Fact]
    public async Task ClaimPending_returns_messages_in_FIFO_order_by_creation_time()
    {
        var store = await CreateStoreAsync();
        var oldest = NewPending(createdAt: Now.AddMinutes(-3));
        var middle = NewPending(createdAt: Now.AddMinutes(-2));
        var newest = NewPending(createdAt: Now.AddMinutes(-1));
        await store.StoreAsync([newest, oldest, middle], CancellationToken.None);

        var claimed = (await ClaimMessagesAsync(store, 10)).ToList();

        ContractAssert.SequenceEqual(
            [oldest.Id, middle.Id, newest.Id],
            claimed.Select(m => m.Id).ToList(),
            "Messages must be claimed oldest-first (FIFO by CreatedAt)");
    }

    /// <summary>Contract: messages with the same <c>CreatedAt</c> are claimed in the order the store received them, whether in one call or across calls.</summary>
    [Fact]
    public async Task ClaimPending_returns_messages_with_the_same_timestamp_in_store_order()
    {
        var store = await CreateStoreAsync();
        var first = NewPending();
        var second = NewPending();
        var third = NewPending();
        var fourth = NewPending();
        await store.StoreAsync([first, second], CancellationToken.None);
        await store.StoreAsync([third], CancellationToken.None);
        await store.StoreAsync([fourth], CancellationToken.None);

        var claimed = (await ClaimMessagesAsync(store, 10)).ToList();

        ContractAssert.SequenceEqual(
            [first.Id, second.Id, third.Id, fourth.Id],
            claimed.Select(m => m.Id).ToList(),
            "Messages with the same CreatedAt must be claimed in the order they were stored");
    }

    /// <summary>Contract: <c>ClaimPendingAsync</c> never claims more messages than the requested batch size.</summary>
    [Fact]
    public async Task ClaimPending_never_claims_more_than_the_batch_size()
    {
        var store = await CreateStoreAsync();
        var messages = Enumerable.Range(0, 5).Select(i => NewPending(createdAt: Now.AddSeconds(i))).ToArray();
        await store.StoreAsync(messages, CancellationToken.None);

        var claimed = (await ClaimMessagesAsync(store, 2)).ToList();

        ContractAssert.Count(2, claimed, "ClaimPendingAsync must never claim more than the requested batch size");
    }

    /// <summary>Contract: a message whose <c>NextRetryAt</c> lies in the future is not claimable until the clock passes it.</summary>
    [Fact]
    public async Task ClaimPending_skips_a_message_whose_back_off_has_not_elapsed()
    {
        var store = await CreateStoreAsync();
        var notYetDue = NewPending(nextRetryAt: Now.AddMinutes(10));
        await store.StoreAsync([notYetDue], CancellationToken.None);

        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "a message whose NextRetryAt is in the future is not due yet");

        Time.Advance(TimeSpan.FromMinutes(11));
        var single = ContractAssert.Single(
            await ClaimMessagesAsync(store, 10),
            "the back-off has elapsed, so the message is due");
        ContractAssert.Equal(notYetDue.Id, single.Id, "Id of the message claimed after its back-off");
    }

    /// <summary>Contract: a claimed message stays leased, and is not served to anyone else, until its visibility timeout elapses.</summary>
    [Fact]
    public async Task A_claimed_message_is_not_returned_again_before_its_visibility_timeout()
    {
        var store = await CreateStoreAsync();
        await store.StoreAsync([NewPending()], CancellationToken.None);

        ContractAssert.Single(await ClaimMessagesAsync(store, 10), "the one stored message is due");

        // Within the visibility window the message stays leased and is not re-served.
        Time.Advance(VisibilityTimeout - TimeSpan.FromSeconds(1));
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "a claimed message stays leased until its visibility timeout elapses");
    }

    /// <summary>Contract: a message whose claimant never finished becomes claimable again once the visibility timeout has passed.</summary>
    [Fact]
    public async Task A_message_stuck_in_progress_is_reclaimed_after_the_visibility_timeout()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);

        ContractAssert.Single(await ClaimMessagesAsync(store, 10), "the one stored message is due");

        // The claimant "crashed" without marking the message; past the timeout it becomes claimable again.
        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var reclaimed = ContractAssert.Single(
            await ClaimMessagesAsync(store, 10),
            "a message left in progress past the visibility timeout must become claimable again");
        ContractAssert.Equal(message.Id, reclaimed.Id, "Id of the reclaimed message");
    }

    /// <summary>Contract: <c>MarkAsProcessedAsync</c> returns true once for the holding claim, false afterwards, and the message is never claimed again.</summary>
    [Fact]
    public async Task MarkAsProcessed_makes_a_message_terminal_and_is_idempotent()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);

        Assert.True(
            await store.MarkAsProcessedAsync(claim, CancellationToken.None),
            "MarkAsProcessedAsync must return true for the claim that currently holds the message.");
        // Marking again must not throw or change anything.
        Assert.False(
            await store.MarkAsProcessedAsync(claim, CancellationToken.None),
            "MarkAsProcessedAsync must return false once the message is already terminal.");

        Time.Advance(VisibilityTimeout * 2);
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "a processed message is terminal and is never claimed again");
    }

    /// <summary>Contract: <c>IncrementAttemptAsync</c> returns the new attempt count, persists the error, and reschedules the message for its <c>NextRetryAt</c>.</summary>
    [Fact]
    public async Task IncrementAttempt_raises_the_count_reschedules_to_pending_and_returns_the_new_count()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);

        var count = await store.IncrementAttemptAsync(claim, "boom", Now.AddMinutes(1), CancellationToken.None);

        ContractAssert.Equal(1, count, "Attempt count returned by the first IncrementAttemptAsync");
        // Still backing off.
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "the rescheduled message is backing off until its NextRetryAt");
        // Due again after the back-off, carrying the persisted attempt count and error.
        Time.Advance(TimeSpan.FromMinutes(2));
        var retried = ContractAssert.Single(
            await ClaimMessagesAsync(store, 10),
            "the back-off has elapsed, so the message is due again");
        ContractAssert.Equal(1, retried.AttemptCount, "Persisted AttemptCount after one failed attempt");
        ContractAssert.Equal("boom", retried.LastError, "Persisted LastError after a failed attempt");
    }

    /// <summary>Contract: <c>IncrementAttemptAsync</c> for a claim on a message the store does not know returns zero instead of throwing.</summary>
    [Fact]
    public async Task IncrementAttempt_on_an_unknown_message_returns_zero()
    {
        var store = await CreateStoreAsync();

        var unknown = new OutboxClaim(Guid.NewGuid(), "no-such-claim", Now.AddMinutes(5));
        var count = await store.IncrementAttemptAsync(unknown, "boom", null, CancellationToken.None);

        ContractAssert.Equal(0, count, "Attempt count returned for a claim on an unknown message");
    }

    /// <summary>Contract: a failed attempt reported after the message was processed returns zero and does not make it claimable again.</summary>
    [Fact]
    public async Task IncrementAttempt_on_a_processed_message_does_not_resurrect_it()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);
        await store.MarkAsProcessedAsync(claim, CancellationToken.None);

        var count = await store.IncrementAttemptAsync(claim, "late", null, CancellationToken.None);

        ContractAssert.Equal(0, count, "Attempt count returned for an already-processed message");
        Time.Advance(VisibilityTimeout * 2);
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "a late failed attempt must not resurrect a processed message");
    }

    /// <summary>Contract: <c>MarkAsFailedAsync</c> dead-letters the message; it is terminal and never claimed again.</summary>
    [Fact]
    public async Task MarkAsFailed_dead_letters_a_message_so_it_is_never_claimed_again()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);

        Assert.True(
            await store.MarkAsFailedAsync(claim, "dead", CancellationToken.None),
            "MarkAsFailedAsync must return true for the claim that currently holds the message.");

        Time.Advance(VisibilityTimeout * 2);
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "a dead-lettered message is terminal and is never claimed again");
    }

    /// <summary>Contract: after a reclaim finishes the message, the original (stale) claimant cannot move it back to pending.</summary>
    [Fact]
    public async Task Marking_processed_after_a_reclaim_does_not_let_a_stale_attempt_resurrect_it()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var stale = await ClaimSingleAsync(store);

        // The message is reclaimed after the timeout, then finishes successfully.
        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var current = await ClaimSingleAsync(store);
        Assert.True(
            await store.MarkAsProcessedAsync(current, CancellationToken.None),
            "MarkAsProcessedAsync must return true for the claim that currently holds the message.");

        // A stale attempt from the original (crashed) claimant must not move it back to pending.
        ContractAssert.Equal(
            0,
            await store.IncrementAttemptAsync(stale, "stale", null, CancellationToken.None),
            "Attempt count returned for a stale claim on a processed message");
        Time.Advance(VisibilityTimeout * 2);
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "a stale attempt must not move a processed message back to pending");
    }

    /// <summary>Contract: once a message is reclaimed, every operation presented with the old claim (attempt, processed, failed, renew, defer, release) is rejected and changes nothing.</summary>
    [Fact]
    public async Task A_stale_claim_cannot_touch_a_message_another_processor_now_holds()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var stale = await ClaimSingleAsync(store);

        // The first processor stalls past its lease; a second one claims the message and is still working on it.
        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var current = await ClaimSingleAsync(store);
        ContractAssert.NotEqual(stale.Token, current.Token, "A reclaim must issue a new claim token");

        // Nothing the stalled processor reports may change the message: not a failed attempt (which would put it back
        // to pending, into a third pair of hands), not a finalize, not a renewal.
        ContractAssert.Equal(
            0,
            await store.IncrementAttemptAsync(stale, "late failure", null, CancellationToken.None),
            "Attempt count returned for a stale claim");
        Assert.False(
            await store.MarkAsProcessedAsync(stale, CancellationToken.None),
            "MarkAsProcessedAsync must reject a stale claim.");
        Assert.False(
            await store.MarkAsFailedAsync(stale, "late", CancellationToken.None),
            "MarkAsFailedAsync must reject a stale claim.");
        Assert.True(
            await store.RenewAsync(stale, CancellationToken.None) is null,
            "RenewAsync must return null for a stale claim.");
        Assert.False(
            await store.DeferAsync(stale, Now.AddMinutes(1), "late deferral", CancellationToken.None),
            "DeferAsync must reject a stale claim.");
        await store.ReleaseAsync([stale], CancellationToken.None);
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "the message is still leased to the second processor (releasing a stale claim must be a no-op)");

        // ...and the processor that does hold it finishes normally.
        Assert.True(
            await store.MarkAsProcessedAsync(current, CancellationToken.None),
            "MarkAsProcessedAsync must return true for the claim that currently holds the message.");
    }

    /// <summary>Contract: <c>RenewAsync</c> extends the lease to now + visibility timeout, so the message is not reclaimed after the original lease would have run out.</summary>
    [Fact]
    public async Task Renew_extends_the_lease_so_a_slow_batch_is_not_reclaimed()
    {
        var store = await CreateStoreAsync();
        await store.StoreAsync([NewPending()], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);

        Time.Advance(VisibilityTimeout - TimeSpan.FromSeconds(10));
        var renewed = await store.RenewAsync(claim, CancellationToken.None);
        Assert.True(renewed.HasValue, "RenewAsync must return the extended claim while the lease is still held.");
        ContractAssert.CloseTo(Now + VisibilityTimeout, renewed!.Value.LeasedUntil, TimeSpan.FromSeconds(1),
            "LeasedUntil of a renewed claim (now + visibility timeout)");

        // Past the ORIGINAL lease, but inside the renewed one: still ours.
        Time.Advance(TimeSpan.FromSeconds(30));
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "a renewed lease outlives the original one");
        Assert.True(
            await store.MarkAsProcessedAsync(renewed.Value, CancellationToken.None),
            "MarkAsProcessedAsync must accept the renewed claim.");
    }

    /// <summary>
    ///     Contract: a claim whose lease ran out cannot be renewed, even while nobody has claimed the message again. Once
    ///     the lease is over the message is claimable, so another processor may be starting on it; the late claimant must
    ///     leave it alone, and the message is claimed afresh.
    /// </summary>
    [Fact]
    public async Task A_claim_whose_lease_ran_out_cannot_be_renewed()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var expired = await ClaimSingleAsync(store);

        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));

        Assert.True(
            await store.RenewAsync(expired, CancellationToken.None) is null,
            "RenewAsync must return null once the claim's lease has run out.");
        var reclaimed = await ClaimSingleAsync(store, message, "the refused renewal left the message claimable");
        ContractAssert.NotEqual(expired.Token, reclaimed.Token, "A reclaim must issue a new claim token");
    }

    /// <summary>
    ///     Contract: a late claimant cannot renew once its expired lease let an earlier message of its partition be
    ///     claimed. The partition is delivering that earlier message; a renewal would put a second delivery of the
    ///     partition in flight. The refused renewal changes nothing, and the message follows the earlier one.
    /// </summary>
    [Fact]
    public async Task A_late_claimant_cannot_renew_once_its_expired_lease_let_an_earlier_message_of_its_partition_be_claimed()
    {
        var store = await CreateStoreAsync();
        var later = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([later], CancellationToken.None);
        var stale = await ClaimSingleAsync(store, later, "the only message of the partition is due");

        var earlier = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        await store.StoreAsync([earlier], CancellationToken.None);
        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var inFlight = await ClaimSingleAsync(store, earlier, "the expired lease freed the partition for its earliest message");

        Assert.True(
            await store.RenewAsync(stale, CancellationToken.None) is null,
            "RenewAsync must refuse a late claimant while an earlier message of its partition is being delivered.");
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "the partition is delivering the earlier message, and the refused renewal changed nothing");

        Assert.True(await store.MarkAsProcessedAsync(inFlight, CancellationToken.None), "MarkAsProcessedAsync must accept the earlier message's claim.");
        await ClaimSingleAsync(store, later, "the message whose lease expired follows the earlier one");
    }

    /// <summary>Contract: <c>ReleaseAsync</c> returns a claimed message to pending immediately, without counting a delivery attempt, and the next claim carries a new token.</summary>
    [Fact]
    public async Task Release_makes_a_claimed_message_immediately_claimable_without_counting_an_attempt()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);

        await store.ReleaseAsync([claim], CancellationToken.None);

        var reclaimed = ContractAssert.Single(
            await store.ClaimPendingAsync(10, CancellationToken.None),
            "a released message is immediately claimable again");
        ContractAssert.Equal(message.Id, reclaimed.Message.Id, "Id of the message claimed after a release");
        ContractAssert.Equal(0, reclaimed.Message.AttemptCount, "AttemptCount after a release (a release is not a delivery attempt)");
        ContractAssert.NotEqual(claim.Token, reclaimed.Claim.Token, "Claiming after a release must issue a new claim token");
    }

    /// <summary>
    ///     Contract: <c>DeferAsync</c> hands a claimed message back without counting an attempt: it is pending again, not
    ///     claimable before its not-before time, claimable after it under a new claim, with its attempt count unchanged and
    ///     the reason kept as its last error.
    /// </summary>
    [Fact]
    public async Task Defer_reschedules_a_message_without_counting_an_attempt()
    {
        var store = await CreateStoreAsync();
        var message = NewPending(attempt: 1);
        await store.StoreAsync([message], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);

        Assert.True(
            await store.DeferAsync(claim, Now.AddMinutes(10), "not known here", CancellationToken.None),
            "DeferAsync must return true for the claim that currently holds the message.");

        ContractAssert.Equal(1L, (await store.GetBacklogAsync(CancellationToken.None)).PendingCount, "PendingCount after a deferral (the message is still undelivered)");
        Time.Advance(TimeSpan.FromMinutes(9));
        ContractAssert.Empty(await store.ClaimPendingAsync(10, CancellationToken.None), "a deferred message is not claimable before its not-before time");

        Time.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = ContractAssert.Single(await store.ClaimPendingAsync(10, CancellationToken.None), "a deferred message is claimable once its not-before time has passed");
        ContractAssert.Equal(message.Id, reclaimed.Message.Id, "Id of the message claimed after its deferral");
        ContractAssert.Equal(1, reclaimed.Message.AttemptCount, "AttemptCount after a deferral (a deferral is not a delivery attempt)");
        ContractAssert.Equal("not known here", reclaimed.Message.LastError, "LastError after a deferral (the reason it was deferred)");
        ContractAssert.NotEqual(claim.Token, reclaimed.Claim.Token, "Claiming after a deferral must issue a new claim token");
    }

    /// <summary>Contract: <c>DeferAsync</c> on a message that was processed, or that the store does not know, returns false and changes nothing.</summary>
    [Fact]
    public async Task Defer_on_a_processed_or_unknown_message_changes_nothing()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);
        Assert.True(await store.MarkAsProcessedAsync(claim, CancellationToken.None), "MarkAsProcessedAsync must accept the holding claim.");

        Assert.False(
            await store.DeferAsync(claim, Now, "late", CancellationToken.None),
            "DeferAsync must return false for a message that is already processed.");
        Assert.False(
            await store.DeferAsync(new OutboxClaim(Guid.NewGuid(), "no-such-claim", Now.AddMinutes(5)), Now, "unknown", CancellationToken.None),
            "DeferAsync must return false for a claim on an unknown message.");

        Time.Advance(VisibilityTimeout * 2);
        ContractAssert.Empty(await store.ClaimPendingAsync(10, CancellationToken.None), "a late deferral must not resurrect a processed message");
    }

    /// <summary>Contract: a deferred partition head keeps its place: its successor stays held back, and the head is claimed again before it.</summary>
    [Fact]
    public async Task A_deferred_partition_head_keeps_holding_its_successor_back()
    {
        var store = await CreateStoreAsync();
        var first = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        var second = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([first, second], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, first, "only the head of the partition is claimable");

        Assert.True(await store.DeferAsync(claim, Now.AddMinutes(10), "not known here", CancellationToken.None), "DeferAsync must accept the head's claim.");

        ContractAssert.Empty(
            await store.ClaimPendingAsync(10, CancellationToken.None),
            "the deferred head still holds its partition, so its successor must not overtake it");

        Time.Advance(TimeSpan.FromMinutes(11));
        var retried = await ClaimSingleAsync(store, first, "the deferred head is claimed again before its successor");
        Assert.True(await store.MarkAsProcessedAsync(retried, CancellationToken.None), "MarkAsProcessedAsync must accept the head's new claim.");
        await ClaimSingleAsync(store, second, "the successor follows once the head is processed");
    }

    /// <summary>Contract: claiming is atomic; concurrent <c>ClaimPendingAsync</c> callers between them claim every due message exactly once.</summary>
    [Fact]
    public async Task Concurrent_ClaimPending_calls_never_claim_the_same_message_twice()
    {
        var store = await CreateStoreAsync();
        var messages = Enumerable.Range(0, 50).Select(i => NewPending(createdAt: Now.AddSeconds(i))).ToArray();
        await store.StoreAsync(messages, CancellationToken.None);

        var batches = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                (await ClaimMessagesAsync(store, 50)).ToList())));

        var claimedIds = batches.SelectMany(b => b.Select(m => m.Id)).ToList();
        ContractAssert.Count(
            claimedIds.Count,
            claimedIds.Distinct().ToList(),
            "a message must be claimed by at most one concurrent processor (distinct ids vs. total claims)");
        ContractAssert.Count(50, claimedIds, "every due message is claimed exactly once across the processors");
    }

    // ---- Per-handler delivery ------------------------------------------------------------------------------------

    /// <summary>Contract: the deliveries of one notification to two handlers are two messages with their own state — one can be processed while the other retries, and only the retrying one comes back.</summary>
    [Fact]
    public async Task Messages_for_different_handlers_are_independent()
    {
        var store = await CreateStoreAsync();
        var notificationId = Guid.NewGuid();
        var toFirst = NewPending(handler: DefaultHandler, partitionKey: "order-1", notificationId: notificationId);
        var toSecond = NewPending(handler: OtherHandler, partitionKey: "order-1", notificationId: notificationId);
        await store.StoreAsync([toFirst, toSecond], CancellationToken.None);

        // Same notification, same partition key, different handlers: both are claimable at once.
        var claimed = await store.ClaimPendingAsync(10, CancellationToken.None);
        ContractAssert.SequenceEqual(
            [toFirst.Id, toSecond.Id],
            claimed.Select(c => c.Message.Id).ToList(),
            "one notification's messages to different handlers must be claimable together");

        // The first handler succeeds; the second fails and backs off.
        Assert.True(await store.MarkAsProcessedAsync(claimed[0].Claim, CancellationToken.None), "MarkAsProcessedAsync must accept the first message's claim.");
        ContractAssert.Equal(1, await store.IncrementAttemptAsync(claimed[1].Claim, "boom", Now.AddMinutes(1), CancellationToken.None),
            "Attempt count of the second handler's message after its failure");

        Time.Advance(TimeSpan.FromMinutes(2));
        var retried = ContractAssert.Single(
            await ClaimMessagesAsync(store, 10),
            "only the failed handler's message is retried; the processed one must not come back");
        ContractAssert.Equal(toSecond.Id, retried.Id, "Id of the retried message");
        ContractAssert.Equal(OtherHandler, retried.HandlerName, "HandlerName of the retried message");
    }

    // ---- Partition ordering --------------------------------------------------------------------------------------

    /// <summary>Contract: of two messages with the same partition key and handler, only the earlier one is claimable; the later one is held back even though it is due and the batch has room.</summary>
    [Fact]
    public async Task A_partitioned_message_is_held_back_while_an_earlier_one_for_the_same_key_and_handler_is_pending()
    {
        var store = await CreateStoreAsync();
        var first = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        var second = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([first, second], CancellationToken.None);

        var claim = await ClaimSingleAsync(store, first, "only the head of the partition is claimable");

        // The head is leased: still nothing else from that partition.
        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "the head is in progress, so its successor is still held back");

        // Once the head is processed, its successor is next.
        Assert.True(await store.MarkAsProcessedAsync(claim, CancellationToken.None), "MarkAsProcessedAsync must accept the head's claim.");
        await ClaimSingleAsync(store, second, "the successor becomes claimable once the head is processed");
    }

    /// <summary>Contract: a partition head that is backing off after a failed attempt keeps holding its successor back, and is retried before it.</summary>
    [Fact]
    public async Task A_partitioned_message_is_held_back_while_the_earlier_one_is_backing_off()
    {
        var store = await CreateStoreAsync();
        var first = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        var second = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([first, second], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, first, "only the head of the partition is claimable");

        ContractAssert.Equal(1, await store.IncrementAttemptAsync(claim, "boom", Now.AddMinutes(1), CancellationToken.None),
            "Attempt count of the head after its failure");

        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "the head is backing off, so its successor must not overtake it even though the successor is due");

        Time.Advance(TimeSpan.FromMinutes(2));
        var retried = await ClaimSingleAsync(store, first, "the head is retried before its successor");
        Assert.True(await store.MarkAsProcessedAsync(retried, CancellationToken.None), "MarkAsProcessedAsync must accept the retried head's claim.");
        await ClaimSingleAsync(store, second, "the successor follows once the head finally succeeds");
    }

    /// <summary>Contract: a dead-lettered head releases its partition, so one poison message stops one delivery, not every later one for that key.</summary>
    [Fact]
    public async Task A_dead_lettered_message_releases_its_partition()
    {
        var store = await CreateStoreAsync();
        var first = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        var second = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([first, second], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, first, "only the head of the partition is claimable");

        Assert.True(await store.MarkAsFailedAsync(claim, "dead", CancellationToken.None), "MarkAsFailedAsync must accept the head's claim.");

        await ClaimSingleAsync(store, second, "a dead-lettered head no longer holds its successor back");
    }

    /// <summary>Contract: releasing a partition head hands the head back, not its successor.</summary>
    [Fact]
    public async Task A_released_message_keeps_its_partition_in_order()
    {
        var store = await CreateStoreAsync();
        var first = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        var second = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([first, second], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, first, "only the head of the partition is claimable");

        await store.ReleaseAsync([claim], CancellationToken.None);

        await ClaimSingleAsync(store, first, "a released head is claimed again before its successor");
    }

    /// <summary>Contract: a partition head whose lease expired is reclaimed before its successor; the crash of its claimant does not let the successor overtake it.</summary>
    [Fact]
    public async Task A_partition_head_whose_lease_expired_is_reclaimed_before_its_successor()
    {
        var store = await CreateStoreAsync();
        var first = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        var second = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([first, second], CancellationToken.None);
        await ClaimSingleAsync(store, first, "only the head of the partition is claimable");

        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));

        await ClaimSingleAsync(store, first, "the abandoned head is reclaimed, and still holds its successor back");
    }

    /// <summary>
    ///     Contract: once the lease of a partition's delivery in flight expires, an earlier message of that partition that
    ///     arrived while it was leased (a requeued dead letter, a producer whose clock runs behind) is the one that becomes
    ///     due, not the expired message, which follows it.
    /// </summary>
    [Fact]
    public async Task An_earlier_arrival_is_claimed_before_a_partition_head_whose_lease_expired()
    {
        var store = await CreateStoreAsync();
        var later = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([later], CancellationToken.None);
        await ClaimSingleAsync(store, later, "the only message of the partition is due");

        var earlier = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        await store.StoreAsync([earlier], CancellationToken.None);
        ContractAssert.Empty(await ClaimMessagesAsync(store, 10), "the partition has a delivery in flight, so the earlier arrival waits");

        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var claim = await ClaimSingleAsync(store, earlier,
            "once the lease expired no delivery of the partition is in flight, and its earliest pending message is the earlier arrival");

        Assert.True(await store.MarkAsProcessedAsync(claim, CancellationToken.None), "MarkAsProcessedAsync must accept the earlier message's claim.");
        await ClaimSingleAsync(store, later, "the message whose lease expired follows the earlier one");
    }

    /// <summary>Contract: different partition keys, and a message without a key, never hold each other back; one batch takes the head of every partition in FIFO order.</summary>
    [Fact]
    public async Task Different_partitions_and_unpartitioned_messages_do_not_block_each_other()
    {
        var store = await CreateStoreAsync();
        var a1 = NewPending(createdAt: Now.AddSeconds(-5), partitionKey: "a");
        var a2 = NewPending(createdAt: Now.AddSeconds(-4), partitionKey: "a");
        var b1 = NewPending(createdAt: Now.AddSeconds(-3), partitionKey: "b");
        var loose = NewPending(createdAt: Now.AddSeconds(-2));
        var b2 = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "b");
        await store.StoreAsync([a1, a2, b1, loose, b2], CancellationToken.None);

        var claimed = (await ClaimMessagesAsync(store, 10)).ToList();

        ContractAssert.SequenceEqual(
            [a1.Id, b1.Id, loose.Id],
            claimed.Select(m => m.Id).ToList(),
            "a batch takes each partition's head plus every unpartitioned message, oldest first");
    }

    /// <summary>Contract: a partitioned message stored after its predecessor was claimed waits for it, and is delivered once the predecessor is processed.</summary>
    [Fact]
    public async Task A_message_stored_while_its_partition_head_is_in_progress_waits_for_it()
    {
        var store = await CreateStoreAsync();
        var first = NewPending(partitionKey: "order-1");
        await store.StoreAsync([first], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, first, "the first message is due");

        Time.Advance(TimeSpan.FromSeconds(1));
        var second = NewPending(partitionKey: "order-1");
        await store.StoreAsync([second], CancellationToken.None);

        ContractAssert.Empty(
            await ClaimMessagesAsync(store, 10),
            "a message stored behind an in-progress head must wait for it");

        Assert.True(await store.MarkAsProcessedAsync(claim, CancellationToken.None), "MarkAsProcessedAsync must accept the head's claim.");
        await ClaimSingleAsync(store, second, "the successor is delivered once the head is processed");
    }

    // ---- Dead-letter operations ----------------------------------------------------------------------------------

    /// <summary>
    ///     Contract: a dead letter records when it failed and is listed by <c>GetDeadLettersAsync</c>, oldest failure
    ///     first (not oldest creation, nor store order), complete with its payload and error, and with the attempt that
    ///     exhausted the budget counted.
    /// </summary>
    [Fact]
    public async Task Dead_letters_are_listed_oldest_first_with_their_failure_time_and_error()
    {
        var store = await CreateStoreAsync();
        var older = NewPending(createdAt: Now.AddSeconds(-2), attempt: 2);
        var younger = NewPending(createdAt: Now.AddSeconds(-1));
        await store.StoreAsync([older, younger], CancellationToken.None);
        var claims = (await store.ClaimPendingAsync(10, CancellationToken.None)).ToDictionary(c => c.Message.Id, c => c.Claim);

        // The younger message fails first, so the failure order differs from both the creation and the store order.
        Assert.True(await store.MarkAsFailedAsync(claims[younger.Id], "younger poison", CancellationToken.None), "MarkAsFailedAsync must accept the holding claim.");
        var youngerFailedAt = Now;
        Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await store.MarkAsFailedAsync(claims[older.Id], "older poison", CancellationToken.None), "MarkAsFailedAsync must accept the holding claim.");

        var deadLetters = await store.GetDeadLettersAsync(10, CancellationToken.None);

        ContractAssert.SequenceEqual([younger.Id, older.Id], deadLetters.Select(m => m.Id).ToList(), "Dead letters must be listed oldest failure first (by FailedAt)");
        var first = deadLetters[0];
        ContractAssert.Equal(OutboxMessageStatus.Failed, first.Status, "Status of a listed dead letter");
        ContractAssert.Equal("younger poison", first.LastError, "LastError of a listed dead letter");
        ContractAssert.SequenceEqual(younger.Payload, first.Payload, "Payload of a listed dead letter");
        ContractAssert.Equal(younger.HandlerName, first.HandlerName, "HandlerName of a listed dead letter");
        Assert.True(first.FailedAt.HasValue, "A dead letter must carry the time it failed.");
        ContractAssert.CloseTo(youngerFailedAt, first.FailedAt!.Value, TimeSpan.FromSeconds(1), "FailedAt of a dead letter (the time it was dead-lettered)");
        ContractAssert.Equal(1, first.AttemptCount, "AttemptCount of a dead letter stored with no attempts (the exhausting attempt counts)");
        ContractAssert.Equal(3, deadLetters[1].AttemptCount, "AttemptCount of a dead letter stored with two attempts (the exhausting attempt counts)");
        ContractAssert.Count(1, await store.GetDeadLettersAsync(1, CancellationToken.None), "GetDeadLettersAsync must honour its limit");
        ContractAssert.Empty(await store.ClaimPendingAsync(10, CancellationToken.None), "listing dead letters must not make them claimable");
    }

    /// <summary>Contract: <c>RequeueAsync</c> gives a dead letter a fresh budget — pending again, zero attempts, no back-off, no failure time, the error kept — and it is claimed again in its original place.</summary>
    [Fact]
    public async Task Requeue_gives_a_dead_letter_a_fresh_budget_and_it_is_claimed_again()
    {
        var store = await CreateStoreAsync();
        var poison = NewPending(createdAt: Now.AddSeconds(-2), attempt: 2, partitionKey: "order-1");
        var successor = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([poison, successor], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, poison, "the partition head is claimable");
        Assert.True(await store.MarkAsFailedAsync(claim, "poison", CancellationToken.None), "MarkAsFailedAsync must accept the holding claim.");

        Assert.True(await store.RequeueAsync(poison.Id, CancellationToken.None), "RequeueAsync must accept a dead letter.");

        ContractAssert.Empty(await store.GetDeadLettersAsync(10, CancellationToken.None), "a requeued message is no longer a dead letter");
        var requeued = await ClaimSingleAsync(store, poison, "the requeued message is claimable again, ahead of its partition successor");
        ContractAssert.Equal(2L, (await store.GetBacklogAsync(CancellationToken.None)).PendingCount, "PendingCount after a requeue (the requeued message and its successor)");
        Assert.True(await store.MarkAsProcessedAsync(requeued, CancellationToken.None), "MarkAsProcessedAsync must accept the requeued message's claim.");
        await ClaimSingleAsync(store, successor, "the partition successor follows the requeued message");
    }

    /// <summary>Contract: a message that failed and backs off rejoins its partition's order: an earlier message that arrived meanwhile is claimed first.</summary>
    [Fact]
    public async Task A_retried_message_does_not_overtake_an_earlier_message_of_its_partition()
    {
        var store = await CreateStoreAsync();
        var later = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([later], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, later, "the only message of the partition is claimable");

        // An earlier-created message arrives while the later one is being delivered (a producer whose clock runs
        // behind), and the delivery then fails without a back-off.
        var earlier = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        await store.StoreAsync([earlier], CancellationToken.None);
        ContractAssert.Equal(1, await store.IncrementAttemptAsync(claim, "boom", null, CancellationToken.None), "AttemptCount after the failed attempt");

        var next = await ClaimSingleAsync(store, earlier, "the earlier message is the partition's head now");
        Assert.True(await store.MarkAsProcessedAsync(next, CancellationToken.None), "MarkAsProcessedAsync must accept the head's claim.");
        await ClaimSingleAsync(store, later, "the retried message follows the earlier one");
    }

    /// <summary>Contract: a released message rejoins its partition's order the same way; it is not handed out ahead of an earlier pending message.</summary>
    [Fact]
    public async Task A_released_message_does_not_overtake_an_earlier_message_of_its_partition()
    {
        var store = await CreateStoreAsync();
        var later = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([later], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, later, "the only message of the partition is claimable");

        var earlier = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        await store.StoreAsync([earlier], CancellationToken.None);
        await store.ReleaseAsync([claim], CancellationToken.None);

        var next = await ClaimSingleAsync(store, earlier, "the earlier message is the partition's head after the release");
        Assert.True(await store.MarkAsProcessedAsync(next, CancellationToken.None), "MarkAsProcessedAsync must accept the head's claim.");
        await ClaimSingleAsync(store, later, "the released message follows the earlier one");
    }

    /// <summary>Contract: FIFO holds across retries and reclaims — a message whose back-off elapsed, or whose lease expired, is claimed before a younger message that was simply stored later.</summary>
    [Fact]
    public async Task A_message_whose_back_off_elapsed_is_claimed_before_a_younger_fresh_message()
    {
        var store = await CreateStoreAsync();
        var old = NewPending(createdAt: Now.AddMinutes(-10));
        await store.StoreAsync([old], CancellationToken.None);
        var claim = await ClaimSingleAsync(store, old, "the old message is claimable");
        ContractAssert.Equal(1, await store.IncrementAttemptAsync(claim, "boom", Now.AddMinutes(1), CancellationToken.None), "AttemptCount after the failed attempt");

        Time.Advance(TimeSpan.FromMinutes(2));
        var young = NewPending();
        await store.StoreAsync([young], CancellationToken.None);

        var claimed = (await ClaimMessagesAsync(store, 1)).ToList();
        var first = ContractAssert.Single(claimed, "one message was asked for");
        ContractAssert.Equal(old.Id, first.Id, "Id of the first claimed message (the older one, although its back-off elapsed after the younger one was stored)");
    }

    /// <summary>Contract: a reclaimed message (an expired lease) keeps its place in the FIFO order ahead of younger messages.</summary>
    [Fact]
    public async Task A_reclaimed_message_is_claimed_before_a_younger_fresh_message()
    {
        var store = await CreateStoreAsync();
        var old = NewPending(createdAt: Now.AddMinutes(-10));
        await store.StoreAsync([old], CancellationToken.None);
        await ClaimSingleAsync(store, old, "the old message is claimable");

        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var young = NewPending();
        await store.StoreAsync([young], CancellationToken.None);

        var claimed = (await ClaimMessagesAsync(store, 1)).ToList();
        var first = ContractAssert.Single(claimed, "one message was asked for");
        ContractAssert.Equal(old.Id, first.Id, "Id of the first claimed message (the abandoned one, not the younger one stored after its lease expired)");
    }

    /// <summary>Contract: a requeued message whose partition successor is being delivered right now waits for it — order cannot be restored for a dead letter, but a partition never has two deliveries in flight.</summary>
    [Fact]
    public async Task A_requeued_message_waits_for_an_in_flight_successor()
    {
        var store = await CreateStoreAsync();
        var poison = NewPending(createdAt: Now.AddSeconds(-2), partitionKey: "order-1");
        var successor = NewPending(createdAt: Now.AddSeconds(-1), partitionKey: "order-1");
        await store.StoreAsync([poison, successor], CancellationToken.None);
        await store.MarkAsFailedAsync(await ClaimSingleAsync(store, poison, "the partition head is claimable"), "poison", CancellationToken.None);
        var inFlight = await ClaimSingleAsync(store, successor, "the dead letter released its partition");

        Assert.True(await store.RequeueAsync(poison.Id, CancellationToken.None), "RequeueAsync must accept a dead letter.");

        ContractAssert.Empty(await ClaimMessagesAsync(store, 10), "the partition has a delivery in flight, so the requeued message waits");
        Assert.True(await store.MarkAsProcessedAsync(inFlight, CancellationToken.None), "MarkAsProcessedAsync must accept the in-flight claim.");
        await ClaimSingleAsync(store, poison, "the requeued message follows the delivery that was in flight");
    }

    /// <summary>Contract: a requeued message comes back with a zero attempt count, no back-off and no failure time, but keeps its last error.</summary>
    [Fact]
    public async Task A_requeued_message_starts_its_attempts_over_and_keeps_its_last_error()
    {
        var store = await CreateStoreAsync();
        var poison = NewPending(attempt: 2);
        await store.StoreAsync([poison], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);
        await store.MarkAsFailedAsync(claim, "poison", CancellationToken.None);

        Assert.True(await store.RequeueAsync(poison.Id, CancellationToken.None), "RequeueAsync must accept a dead letter.");
        var requeued = ContractAssert.Single(await ClaimMessagesAsync(store, 10), "the requeued message is claimable");

        ContractAssert.Equal(0, requeued.AttemptCount, "AttemptCount of a requeued message");
        Assert.True(requeued.NextRetryAt is null, "A requeued message must not be backing off.");
        Assert.True(requeued.FailedAt is null, "A requeued message must not carry a failure time.");
        ContractAssert.Equal("poison", requeued.LastError, "LastError of a requeued message (kept as the record of the failure)");
    }

    /// <summary>Contract: <c>RequeueAsync</c> returns false, and changes nothing, for an unknown message and for one that is not dead-lettered.</summary>
    [Fact]
    public async Task Requeue_rejects_an_unknown_or_undelivered_message()
    {
        var store = await CreateStoreAsync();
        var pending = NewPending();
        await store.StoreAsync([pending], CancellationToken.None);

        Assert.False(await store.RequeueAsync(Guid.NewGuid(), CancellationToken.None), "RequeueAsync must return false for an unknown message.");
        Assert.False(await store.RequeueAsync(pending.Id, CancellationToken.None), "RequeueAsync must return false for a message that is not dead-lettered.");

        var claimed = ContractAssert.Single(await ClaimMessagesAsync(store, 10), "the pending message is still claimable exactly once");
        ContractAssert.Equal(pending.Id, claimed.Id, "Id of the still-pending message");
    }

    /// <summary>Contract: <c>PurgeDeadLettersAsync</c> deletes only the dead letters that failed at or before the cut-off, returns how many, and leaves everything else alone.</summary>
    [Fact]
    public async Task Purge_deletes_only_dead_letters_that_failed_before_the_cutoff()
    {
        var store = await CreateStoreAsync();
        var old = NewPending(createdAt: Now.AddSeconds(-3));
        var recent = NewPending(createdAt: Now.AddSeconds(-2));
        var pending = NewPending(createdAt: Now.AddSeconds(-1), nextRetryAt: Now.AddHours(1));
        await store.StoreAsync([old, recent, pending], CancellationToken.None);
        var claims = (await store.ClaimPendingAsync(10, CancellationToken.None)).ToDictionary(c => c.Message.Id, c => c.Claim);
        await store.MarkAsFailedAsync(claims[old.Id], "old", CancellationToken.None);
        Time.Advance(TimeSpan.FromDays(10));
        await store.MarkAsFailedAsync(claims[recent.Id], "recent", CancellationToken.None);

        var purged = await store.PurgeDeadLettersAsync(Now.AddDays(-1), CancellationToken.None);

        ContractAssert.Equal(1, purged, "Number of dead letters purged (only the one that failed before the cut-off)");
        var remaining = ContractAssert.Single(await store.GetDeadLettersAsync(10, CancellationToken.None), "the recent dead letter survives the purge");
        ContractAssert.Equal(recent.Id, remaining.Id, "Id of the surviving dead letter");
        ContractAssert.Equal(1L, (await store.GetBacklogAsync(CancellationToken.None)).PendingCount, "PendingCount after the purge (the pending message is untouched)");
        ContractAssert.Equal(0, await store.PurgeDeadLettersAsync(Now.AddDays(-1), CancellationToken.None), "A second purge with the same cut-off deletes nothing more");
    }

    // ---- Backlog -------------------------------------------------------------------------------------------------

    /// <summary>Contract: <c>GetBacklogAsync</c> counts undelivered messages (pending, backing off or in progress), counts dead letters, and reports the oldest undelivered message.</summary>
    [Fact]
    public async Task Backlog_counts_undelivered_and_dead_lettered_messages_and_reports_the_oldest_pending()
    {
        var store = await CreateStoreAsync();
        ContractAssert.Equal(OutboxBacklog.Empty, await store.GetBacklogAsync(CancellationToken.None), "Backlog of an empty store");

        var oldest = NewPending(createdAt: Now.AddMinutes(-10));
        var backingOff = NewPending(createdAt: Now.AddMinutes(-5), nextRetryAt: Now.AddHours(1));
        var fresh = NewPending(createdAt: Now.AddMinutes(-1));
        var poison = NewPending(createdAt: Now.AddMinutes(-20));
        await store.StoreAsync([oldest, backingOff, fresh, poison], CancellationToken.None);
        var claims = (await store.ClaimPendingAsync(10, CancellationToken.None)).ToDictionary(c => c.Message.Id, c => c.Claim);
        await store.MarkAsFailedAsync(claims[poison.Id], "poison", CancellationToken.None);

        // oldest and fresh are in progress, backingOff is pending: all three are undelivered; poison is a dead letter.
        var backlog = await store.GetBacklogAsync(CancellationToken.None);

        ContractAssert.Equal(3L, backlog.PendingCount, "PendingCount (in-progress and backing-off messages are undelivered)");
        ContractAssert.Equal(1L, backlog.DeadLetterCount, "DeadLetterCount");
        Assert.True(backlog.OldestPendingCreatedAt.HasValue, "A non-empty backlog must report its oldest undelivered message.");
        ContractAssert.CloseTo(oldest.CreatedAt, backlog.OldestPendingCreatedAt!.Value, TimeSpan.FromMilliseconds(1), "OldestPendingCreatedAt (a dead letter does not count)");

        await store.MarkAsProcessedAsync(claims[oldest.Id], CancellationToken.None);
        var after = await store.GetBacklogAsync(CancellationToken.None);
        ContractAssert.Equal(2L, after.PendingCount, "PendingCount after the oldest message was processed");
        ContractAssert.CloseTo(backingOff.CreatedAt, after.OldestPendingCreatedAt!.Value, TimeSpan.FromMilliseconds(1), "OldestPendingCreatedAt after the oldest message was processed");
    }
}
