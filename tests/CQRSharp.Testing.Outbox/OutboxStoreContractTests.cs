using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Models.Outbox;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Testing.Outbox;

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

    [SkippableFact]
    public async Task GetPending_claims_a_stored_message_and_transitions_it_to_in_progress()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);

        var claimed = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();

        claimed.Should().ContainSingle();
        claimed[0].Id.Should().Be(message.Id);
        claimed[0].Status.Should().Be(OutboxMessageStatus.InProgress);
    }

    [SkippableFact]
    public async Task GetPending_returns_messages_in_FIFO_order_by_creation_time()
    {
        var store = await CreateStoreAsync();
        var oldest = NewPending(createdAt: Now.AddMinutes(-3));
        var middle = NewPending(createdAt: Now.AddMinutes(-2));
        var newest = NewPending(createdAt: Now.AddMinutes(-1));
        await store.StoreAsync([newest, oldest, middle], CancellationToken.None);

        var claimed = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();

        claimed.Select(m => m.Id).Should().ContainInOrder(oldest.Id, middle.Id, newest.Id);
    }

    [SkippableFact]
    public async Task GetPending_never_claims_more_than_the_batch_size()
    {
        var store = await CreateStoreAsync();
        var messages = Enumerable.Range(0, 5).Select(i => NewPending(createdAt: Now.AddSeconds(i))).ToArray();
        await store.StoreAsync(messages, CancellationToken.None);

        var claimed = (await store.GetPendingAsync(2, CancellationToken.None)).ToList();

        claimed.Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task GetPending_skips_a_message_whose_back_off_has_not_elapsed()
    {
        var store = await CreateStoreAsync();
        var notYetDue = NewPending(nextRetryAt: Now.AddMinutes(10));
        await store.StoreAsync([notYetDue], CancellationToken.None);

        (await store.GetPendingAsync(10, CancellationToken.None)).Should().BeEmpty();

        Time.Advance(TimeSpan.FromMinutes(11));
        var claimed = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();
        claimed.Should().ContainSingle().Which.Id.Should().Be(notYetDue.Id);
    }

    [SkippableFact]
    public async Task A_claimed_message_is_not_returned_again_before_its_visibility_timeout()
    {
        var store = await CreateStoreAsync();
        await store.StoreAsync([NewPending()], CancellationToken.None);

        (await store.GetPendingAsync(10, CancellationToken.None)).Should().ContainSingle();

        // Within the visibility window the message stays leased and is not re-served.
        Time.Advance(VisibilityTimeout - TimeSpan.FromSeconds(1));
        (await store.GetPendingAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task A_message_stuck_in_progress_is_reclaimed_after_the_visibility_timeout()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);

        var first = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();
        first.Should().ContainSingle();

        // The claimant "crashed" without marking the message; past the timeout it becomes claimable again.
        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var reclaimed = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();
        reclaimed.Should().ContainSingle().Which.Id.Should().Be(message.Id);
    }

    [SkippableFact]
    public async Task MarkAsProcessed_makes_a_message_terminal_and_is_idempotent()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        await store.GetPendingAsync(10, CancellationToken.None);

        await store.MarkAsProcessedAsync(message.Id, CancellationToken.None);
        // Marking again must not throw or change anything.
        await store.MarkAsProcessedAsync(message.Id, CancellationToken.None);

        Time.Advance(VisibilityTimeout * 2);
        (await store.GetPendingAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task IncrementAttempt_raises_the_count_reschedules_to_pending_and_returns_the_new_count()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        await store.GetPendingAsync(10, CancellationToken.None);

        var count = await store.IncrementAttemptAsync(message.Id, "boom", Now.AddMinutes(1), CancellationToken.None);

        count.Should().Be(1);
        // Still backing off.
        (await store.GetPendingAsync(10, CancellationToken.None)).Should().BeEmpty();
        // Due again after the back-off, carrying the persisted attempt count and error.
        Time.Advance(TimeSpan.FromMinutes(2));
        var claimed = (await store.GetPendingAsync(10, CancellationToken.None)).ToList();
        claimed.Should().ContainSingle();
        claimed[0].AttemptCount.Should().Be(1);
        claimed[0].LastError.Should().Be("boom");
    }

    [SkippableFact]
    public async Task IncrementAttempt_on_an_unknown_message_returns_zero()
    {
        var store = await CreateStoreAsync();

        var count = await store.IncrementAttemptAsync(Guid.NewGuid(), "boom", null, CancellationToken.None);

        count.Should().Be(0);
    }

    [SkippableFact]
    public async Task IncrementAttempt_on_a_processed_message_does_not_resurrect_it()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        await store.GetPendingAsync(10, CancellationToken.None);
        await store.MarkAsProcessedAsync(message.Id, CancellationToken.None);

        var count = await store.IncrementAttemptAsync(message.Id, "late", null, CancellationToken.None);

        count.Should().Be(0);
        Time.Advance(VisibilityTimeout * 2);
        (await store.GetPendingAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task MarkAsFailed_dead_letters_a_message_so_it_is_never_claimed_again()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        await store.GetPendingAsync(10, CancellationToken.None);

        await store.MarkAsFailedAsync(message.Id, "dead", CancellationToken.None);

        Time.Advance(VisibilityTimeout * 2);
        (await store.GetPendingAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Marking_processed_after_a_reclaim_does_not_let_a_stale_attempt_resurrect_it()
    {
        var store = await CreateStoreAsync();
        var message = NewPending();
        await store.StoreAsync([message], CancellationToken.None);
        await store.GetPendingAsync(10, CancellationToken.None);

        // The message is reclaimed after the timeout, then finishes successfully.
        Time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        (await store.GetPendingAsync(10, CancellationToken.None)).Should().ContainSingle();
        await store.MarkAsProcessedAsync(message.Id, CancellationToken.None);

        // A stale attempt from the original (crashed) claimant must not move it back to pending.
        (await store.IncrementAttemptAsync(message.Id, "stale", null, CancellationToken.None)).Should().Be(0);
        Time.Advance(VisibilityTimeout * 2);
        (await store.GetPendingAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

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
        claimedIds.Should().OnlyHaveUniqueItems("a message must be claimed by at most one concurrent processor");
        claimedIds.Should().HaveCount(50, "every due message is claimed exactly once across the processors");
    }
}
