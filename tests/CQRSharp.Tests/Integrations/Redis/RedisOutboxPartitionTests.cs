using CQRSharp.Persistence;
using CQRSharp.Redis;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     The Redis store's partition bookkeeping under shapes the shared contract suite cannot set up: ghosts (a message
///     hash deleted out from under the sets, as an eviction or a manual DEL leaves it) in the due set and at a partition's
///     head, and handler names and keys whose text runs together.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisOutboxPartitionTests(RedisFixture fixture) : IAsyncLifetime
{
    private readonly string _prefix = RedisFixture.NewKeyPrefix("partition");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await fixture.DeleteKeysAsync(_prefix);

    [Fact(DisplayName = "A message arriving ahead of a parked one stays parked while a later message of its partition is in flight")]
    public async Task An_earlier_arrival_waits_for_the_in_flight_delivery()
    {
        var store = CreateStore();
        var later = Message(Now.AddSeconds(-1), "p");
        var mid = Message(Now.AddSeconds(-2), "p");
        var early = Message(Now.AddSeconds(-3), "p");

        await store.StoreAsync([later], Cancel);
        var inFlight = (await store.ClaimPendingAsync(10, Cancel)).Single();
        inFlight.Message.Id.Should().Be(later.Id);

        await store.StoreAsync([mid], Cancel);
        await store.StoreAsync([early], Cancel);

        (await store.ClaimPendingAsync(10, Cancel)).Should().BeEmpty("nothing joins a partition with a delivery in flight");

        (await store.MarkAsProcessedAsync(inFlight.Claim, Cancel)).Should().BeTrue();
        var next = (await store.ClaimPendingAsync(10, Cancel)).Select(c => c.Message.Id).ToArray();
        next.Should().Equal(early.Id);
    }

    [Fact(DisplayName = "A ghost in the due set does not cost the claim a live message")]
    public async Task Ghosts_are_skipped_without_skipping_live_messages()
    {
        var store = CreateStore();
        var ghost = Message(Now.AddSeconds(-3), null);
        var second = Message(Now.AddSeconds(-2), null);
        var third = Message(Now.AddSeconds(-1), null);
        await store.StoreAsync([ghost, second, third], Cancel);

        await DeleteHashAsync(ghost);

        var claimed = (await store.ClaimPendingAsync(2, Cancel)).Select(c => c.Message.Id).ToArray();
        claimed.Should().Equal(second.Id, third.Id);
    }

    [Fact(DisplayName = "A partition whose head became a ghost is not blocked: the next message is claimable")]
    public async Task A_ghost_head_does_not_block_its_partition()
    {
        var store = CreateStore();
        var ghost = Message(Now.AddSeconds(-2), "p");
        var follower = Message(Now.AddSeconds(-1), "p");
        await store.StoreAsync([ghost], Cancel);
        await store.StoreAsync([follower], Cancel);

        await DeleteHashAsync(ghost);

        var claimed = (await store.ClaimPendingAsync(10, Cancel)).Select(c => c.Message.Id).ToArray();
        claimed.Should().Equal(follower.Id);
        (await store.GetBacklogAsync(Cancel)).PendingCount.Should().Be(1, "the ghost left every set");
    }

    [Fact(DisplayName = "A store that finds its partition's head is a ghost makes the next live message due instead of stranding the partition")]
    public async Task A_ghost_head_met_by_a_store_does_not_strand_its_partition()
    {
        var store = CreateStore();
        var ghost = Message(Now.AddSeconds(-3), "p");
        var next = Message(Now.AddSeconds(-2), "p");
        var arrival = Message(Now.AddSeconds(-1), "p");
        await store.StoreAsync([ghost], Cancel);
        await store.StoreAsync([next], Cancel);
        await DeleteHashAsync(ghost);

        // The store walks past the ghost at the head; the message behind it must become due, not wait for a promotion
        // that nothing in flight will ever make.
        await store.StoreAsync([arrival], Cancel);

        var claimed = (await store.ClaimPendingAsync(10, Cancel)).Single();
        claimed.Message.Id.Should().Be(next.Id, "the live head of the partition is due");
        (await store.GetBacklogAsync(Cancel)).PendingCount.Should().Be(2);

        (await store.MarkAsProcessedAsync(claimed.Claim, Cancel)).Should().BeTrue();
        (await store.ClaimPendingAsync(10, Cancel)).Select(c => c.Message.Id).Should().Equal(arrival.Id);
    }

    [Fact(DisplayName = "A requeue that finds its partition's head is a ghost makes the live head due instead of stranding the partition")]
    public async Task A_ghost_head_met_by_a_requeue_does_not_strand_its_partition()
    {
        var store = CreateStore();
        var deadLetter = Message(Now.AddSeconds(-1), "p");
        await store.StoreAsync([deadLetter], Cancel);
        var claim = (await store.ClaimPendingAsync(10, Cancel)).Single().Claim;
        (await store.MarkAsFailedAsync(claim, "poison", Cancel)).Should().BeTrue();

        var ghost = Message(Now.AddSeconds(-3), "p");
        var next = Message(Now.AddSeconds(-2), "p");
        await store.StoreAsync([ghost, next], Cancel);
        await DeleteHashAsync(ghost);

        (await store.RequeueAsync(deadLetter.Id, Cancel)).Should().BeTrue();

        (await store.ClaimPendingAsync(10, Cancel)).Select(c => c.Message.Id).Should()
            .Equal([next.Id], "the live head of the partition is due, and the requeued message waits behind it");
        (await store.GetBacklogAsync(Cancel)).PendingCount.Should().Be(2);
    }

    [Fact(DisplayName = "Handler and key pairs whose text runs together are separate partitions")]
    public async Task Handler_and_key_pairs_that_join_to_the_same_text_are_separate_partitions()
    {
        var store = CreateStore();
        // Joined with ':', both pairs read "orders:email:42".
        var first = Message(Now.AddSeconds(-2), "42", handler: "orders:email");
        var second = Message(Now.AddSeconds(-1), "email:42", handler: "orders");
        await store.StoreAsync([first, second], Cancel);

        (await store.ClaimPendingAsync(10, Cancel)).Select(c => c.Message.Id).Should()
            .Equal([first.Id, second.Id], "deliveries for different handlers or keys never hold each other back");
    }

    private static OutboxMessage Message(DateTime createdAt, string? partitionKey, string handler = "h")
        => new(Guid.NewGuid(), "n", handler, [1], createdAt, OutboxMessageStatus.Pending, null, null, PartitionKey: partitionKey);

    private Task DeleteHashAsync(OutboxMessage message)
        => fixture.Multiplexer.GetDatabase().KeyDeleteAsync($"{_prefix}msg:{message.Id:N}");

    private RedisOutboxStore CreateStore()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        return new RedisOutboxStore(
            fixture.Multiplexer,
            Options.Create(new RedisOutboxOptions { KeyPrefix = _prefix, VisibilityTimeout = VisibilityTimeout }),
            _time);
    }
}
