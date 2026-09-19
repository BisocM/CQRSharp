using CQRSharp.Pipelines;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Testing;

/// <summary>
///     The single, reusable conformance suite for every <see cref="IOutboxStore" /> implementation. Each store
///     (in-memory, Redis, EF Core, ...) derives from this class and supplies a fresh store plus the controllable
///     <see cref="FakeTimeProvider" /> the store reads time from; every store therefore proves the exact same
///     claim, visibility-timeout, retry, and dead-letter semantics with no duplicated assertions. All timing is
///     driven by advancing the fake clock, so the suite is deterministic and never sleeps.
/// </summary>
public abstract class OutboxStoreContractTests
{
    /// <summary>The clock the store-under-test reads; tests advance it to exercise back-off and visibility timeouts.</summary>
    protected abstract FakeTimeProvider Time { get; }

    /// <summary>Creates a fresh, empty store bound to <see cref="Time" />.</summary>
    protected abstract Task<IOutboxStore> CreateStoreAsync();

    /// <summary>The visibility timeout the store-under-test is configured with; the suite advances the clock past it.</summary>
    protected virtual TimeSpan VisibilityTimeout => TimeSpan.FromMinutes(5);

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

    private OutboxMessage NewPending(DateTime? createdAt = null, DateTime? nextRetryAt = null, int attempt = 0)
        => new(
            Guid.NewGuid(),
            "TestNotification",
            [1, 2, 3],
            createdAt ?? Now,
            OutboxMessageStatus.Pending,
            ProcessedAt: null,
            LastError: null,
            AttemptCount: attempt,
            NextRetryAt: nextRetryAt);

    // Claims the single due message and returns its claim.
    private static async Task<OutboxClaim> ClaimSingleAsync(IOutboxStore store)
    {
        var claimed = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();
        var message = ContractAssert.Single(claimed, "exactly one message is due");
        Assert.True(message.Claim.HasValue, "GetPendingAsync must issue a claim with every message it hands out.");
        return message.Claim!.Value;
    }

    /// <summary>Contract: <c>GetPendingAsync</c> hands out a stored message as <c>InProgress</c> together with a claim (message id, non-empty token, lease of now + visibility timeout).</summary>
    [SkippableFact]
    public async Task GetPending_claims_a_stored_message_and_transitions_it_to_in_progress()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);

        var claimed = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();

        var single = ContractAssert.Single(claimed, "the one stored message is due");
        ContractAssert.Equal(message.Id, single.Id, "Id of the claimed message");
        ContractAssert.Equal(OutboxMessageStatus.InProgress, single.Status, "Status of a claimed message");
        Assert.True(single.Claim.HasValue, "GetPendingAsync must issue a claim with every message it hands out.");
        var claim = single.Claim!.Value;
        ContractAssert.Equal(message.Id, claim.MessageId, "MessageId of the issued claim");
        Assert.False(string.IsNullOrEmpty(claim.Token), "The issued claim must carry a non-empty token.");
        ContractAssert.CloseTo(Now + VisibilityTimeout, claim.LeasedUntil, TimeSpan.FromSeconds(1),
            "LeasedUntil of a fresh claim (now + visibility timeout)");
    }

    /// <summary>Contract: due messages are claimed oldest-first by <c>CreatedAt</c>, regardless of the order they were stored in.</summary>
    [SkippableFact]
    public async Task GetPending_returns_messages_in_FIFO_order_by_creation_time()
    {
        var store = await CreateStoreAsync();
        var oldest = NewPending(createdAt: Now.AddMinutes(-3));
        var middle = NewPending(createdAt: Now.AddMinutes(-2));
        var newest = NewPending(createdAt: Now.AddMinutes(-1));
        await store.StoreAsync([newest, oldest, middle], CancellationToken.None);

        var claimed = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();

        ContractAssert.SequenceEqual(
            [oldest.Id, middle.Id, newest.Id],
            claimed.Select(m => m.Id).ToList(),
            "Messages must be claimed oldest-first (FIFO by CreatedAt)");
    }

    /// <summary>Contract: <c>GetPendingAsync</c> never claims more messages than the requested batch size.</summary>
    [SkippableFact]
    public async Task GetPending_never_claims_more_than_the_batch_size()
    {
        var store = await CreateStoreAsync();
        var messages = Enumerable.Range(0, 5).Select(i => NewPending(createdAt: Now.AddSeconds(i))).ToArray();
        await store.StoreAsync(messages, CancellationToken.None);

        var claimed = (await store.GetPendingAsync(2, CancellationToken.None)).ToList();

        ContractAssert.Count(2, claimed, "GetPendingAsync must never claim more than the requested batch size");
    }

    /// <summary>Contract: a message whose <c>NextRetryAt</c> lies in the future is not claimable until the clock passes it.</summary>
    [SkippableFact]
    public async Task GetPending_skips_a_message_whose_back_off_has_not_elapsed()
    {
        var store = await CreateStoreAsync();
        var notYetDue = NewPending(nextRetryAt: Now.AddMinutes(10));
        await store.StoreAsync([notYetDue], CancellationToken.None);

        ContractAssert.Empty(
            await store.GetPendingAsync(10, CancellationToken.None),
            "a message whose NextRetryAt is in the future is not due yet");

        Time.Advance(TimeSpan.FromMinutes(11));
        var single = ContractAssert.Single(
            await store.GetPendingAsync(10, CancellationToken.None),
            "the back-off has elapsed, so the message is due");
        ContractAssert.Equal(notYetDue.Id, single.Id, "Id of the message claimed after its back-off");
    }

    /// <summary>Contract: a claimed message stays leased, and is not served to anyone else, until its visibility timeout elapses.</summary>
    [SkippableFact]
    public async Task A_claimed_message_is_not_returned_again_before_its_visibility_timeout()
    {
        var store = await CreateStoreAsync();
        await store.StoreAsync([NewPending()], CancellationToken.None);

        ContractAssert.Single(await store.GetPendingAsync(10, CancellationToken.None), "the one stored message is due");

        // Within the visibility window the message stays leased and is not re-served.
        Time.Advance(VisibilityTimeout - TimeSpan.FromSeconds(1));
        ContractAssert.Empty(
            await store.GetPendingAsync(10, CancellationToken.None),
            "a claimed message stays leased until its visibility timeout elapses");
    }

    /// <summary>Contract: a message whose claimant never finished becomes claimable again once the visibility timeout has passed.</summary>
    [SkippableFact]
    public async Task A_message_stuck_in_progress_is_reclaimed_after_the_visibility_timeout()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);

        ContractAssert.Single(await store.GetPendingAsync(10, CancellationToken.None), "the one stored message is due");

        // The claimant "crashed" without marking the message; past the timeout it becomes claimable again.
        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var reclaimed = ContractAssert.Single(
            await store.GetPendingAsync(10, CancellationToken.None),
            "a message left in progress past the visibility timeout must become claimable again");
        ContractAssert.Equal(message.Id, reclaimed.Id, "Id of the reclaimed message");
    }

    /// <summary>Contract: <c>MarkAsProcessedAsync</c> returns true once for the holding claim, false afterwards, and the message is never claimed again.</summary>
    [SkippableFact]
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
            await store.GetPendingAsync(10, CancellationToken.None),
            "a processed message is terminal and is never claimed again");
    }

    /// <summary>Contract: <c>IncrementAttemptAsync</c> returns the new attempt count, persists the error, and reschedules the message for its <c>NextRetryAt</c>.</summary>
    [SkippableFact]
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
            await store.GetPendingAsync(10, CancellationToken.None),
            "the rescheduled message is backing off until its NextRetryAt");
        // Due again after the back-off, carrying the persisted attempt count and error.
        Time.Advance(TimeSpan.FromMinutes(2));
        var retried = ContractAssert.Single(
            await store.GetPendingAsync(10, CancellationToken.None),
            "the back-off has elapsed, so the message is due again");
        ContractAssert.Equal(1, retried.AttemptCount, "Persisted AttemptCount after one failed attempt");
        ContractAssert.Equal("boom", retried.LastError, "Persisted LastError after a failed attempt");
    }

    /// <summary>Contract: <c>IncrementAttemptAsync</c> for a claim on a message the store does not know returns zero instead of throwing.</summary>
    [SkippableFact]
    public async Task IncrementAttempt_on_an_unknown_message_returns_zero()
    {
        var store = await CreateStoreAsync();

        var unknown = new OutboxClaim(Guid.NewGuid(), "no-such-claim", Now.AddMinutes(5));
        var count = await store.IncrementAttemptAsync(unknown, "boom", null, CancellationToken.None);

        ContractAssert.Equal(0, count, "Attempt count returned for a claim on an unknown message");
    }

    /// <summary>Contract: a failed attempt reported after the message was processed returns zero and does not make it claimable again.</summary>
    [SkippableFact]
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
            await store.GetPendingAsync(10, CancellationToken.None),
            "a late failed attempt must not resurrect a processed message");
    }

    /// <summary>Contract: <c>MarkAsFailedAsync</c> dead-letters the message; it is terminal and never claimed again.</summary>
    [SkippableFact]
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
            await store.GetPendingAsync(10, CancellationToken.None),
            "a dead-lettered message is terminal and is never claimed again");
    }

    /// <summary>Contract: after a reclaim finishes the message, the original (stale) claimant cannot move it back to pending.</summary>
    [SkippableFact]
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
            await store.GetPendingAsync(10, CancellationToken.None),
            "a stale attempt must not move a processed message back to pending");
    }

    /// <summary>Contract: once a message is reclaimed, every operation presented with the old claim (attempt, processed, failed, renew, release) is rejected and changes nothing.</summary>
    [SkippableFact]
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
        await store.ReleaseAsync([stale], CancellationToken.None);
        ContractAssert.Empty(
            await store.GetPendingAsync(10, CancellationToken.None),
            "the message is still leased to the second processor (releasing a stale claim must be a no-op)");

        // ...and the processor that does hold it finishes normally.
        Assert.True(
            await store.MarkAsProcessedAsync(current, CancellationToken.None),
            "MarkAsProcessedAsync must return true for the claim that currently holds the message.");
    }

    /// <summary>Contract: <c>RenewAsync</c> extends the lease to now + visibility timeout, so the message is not reclaimed after the original lease would have run out.</summary>
    [SkippableFact]
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
            await store.GetPendingAsync(10, CancellationToken.None),
            "a renewed lease outlives the original one");
        Assert.True(
            await store.MarkAsProcessedAsync(renewed.Value, CancellationToken.None),
            "MarkAsProcessedAsync must accept the renewed claim.");
    }

    /// <summary>Contract: <c>ReleaseAsync</c> returns a claimed message to pending immediately, without counting a delivery attempt, and the next claim carries a new token.</summary>
    [SkippableFact]
    public async Task Release_makes_a_claimed_message_immediately_claimable_without_counting_an_attempt()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        var claim = await ClaimSingleAsync(store);

        await store.ReleaseAsync([claim], CancellationToken.None);

        var reclaimed = ContractAssert.Single(
            await store.GetPendingAsync(10, CancellationToken.None),
            "a released message is immediately claimable again");
        ContractAssert.Equal(message.Id, reclaimed.Id, "Id of the message claimed after a release");
        ContractAssert.Equal(0, reclaimed.AttemptCount, "AttemptCount after a release (a release is not a delivery attempt)");
        Assert.True(reclaimed.Claim.HasValue, "GetPendingAsync must issue a claim with every message it hands out.");
        ContractAssert.NotEqual(claim.Token, reclaimed.Claim!.Value.Token, "Claiming after a release must issue a new claim token");
    }

    /// <summary>Contract: claiming is atomic; concurrent <c>GetPendingAsync</c> callers between them claim every due message exactly once.</summary>
    [SkippableFact]
    public async Task Concurrent_GetPending_calls_never_claim_the_same_message_twice()
    {
        var store = await CreateStoreAsync();
        var messages = Enumerable.Range(0, 50).Select(i => NewPending(createdAt: Now.AddSeconds(i))).ToArray();
        await store.StoreAsync(messages, CancellationToken.None);

        var batches = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                (await store.GetPendingAsync(50, CancellationToken.None)).ToList())));

        var claimedIds = batches.SelectMany(b => b.Select(m => m.Id)).ToList();
        ContractAssert.Count(
            claimedIds.Count,
            claimedIds.Distinct().ToList(),
            "a message must be claimed by at most one concurrent processor (distinct ids vs. total claims)");
        ContractAssert.Count(50, claimedIds, "every due message is claimed exactly once across the processors");
    }
}
