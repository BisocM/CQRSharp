using CQRSharp.Persistence;
using CQRSharp.Redis;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     What the Redis outbox keeps on the server, where memory is the whole budget: nothing of a delivered message, and
///     a dead letter until it is requeued or purged.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisOutboxStoreFootprintTests(RedisFixture fixture) : IAsyncLifetime
{
    private readonly string _prefix = RedisFixture.NewKeyPrefix("footprint");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await fixture.DeleteKeysAsync(_prefix);

    [Fact(DisplayName = "A processed message leaves nothing behind but the store's sequence counter")]
    public async Task A_processed_message_leaves_nothing_behind()
    {
        var store = CreateStore();
        var now = _time.GetUtcNow().UtcDateTime;
        await store.StoreAsync([Message(now.AddSeconds(-2), "order-1"), Message(now.AddSeconds(-1), null)], Cancel);

        foreach (var claimed in await store.ClaimPendingAsync(10, Cancel))
            (await store.MarkAsProcessedAsync(claimed.Claim, Cancel)).Should().BeTrue();

        (await KeysAsync()).Should().Equal([_prefix + "seq"],
            "a processed message is never read again, so its hash, its set entries and its partition bookkeeping are all gone");
    }

    [Fact(DisplayName = "A dead letter keeps its message until it is purged, and the purge leaves nothing behind")]
    public async Task A_dead_letter_is_kept_until_purged()
    {
        var store = CreateStore();
        var message = Message(_time.GetUtcNow().UtcDateTime, "order-1");
        await store.StoreAsync([message], Cancel);
        var claim = (await store.ClaimPendingAsync(10, Cancel)).Single().Claim;
        (await store.MarkAsFailedAsync(claim, "poison", Cancel)).Should().BeTrue();

        (await KeysAsync()).Should().Contain(_prefix + $"msg:{message.Id:N}", "a dead letter is the record of what could not be delivered");

        _time.Advance(TimeSpan.FromSeconds(1));
        (await store.PurgeDeadLettersAsync(_time.GetUtcNow().UtcDateTime, Cancel)).Should().Be(1);
        (await KeysAsync()).Should().Equal([_prefix + "seq"]);
    }

    private static OutboxMessage Message(DateTime createdAt, string? partitionKey)
        => new(Guid.NewGuid(), "n", "h", [1, 2, 3], createdAt, OutboxMessageStatus.Pending, null, null, PartitionKey: partitionKey);

    private async Task<IReadOnlyList<string>> KeysAsync()
    {
        var keys = new List<string>();
        foreach (var server in fixture.Multiplexer.GetServers())
        {
            if (!server.IsConnected || server.IsReplica) continue;
            await foreach (var key in server.KeysAsync(pattern: _prefix + "*"))
                keys.Add(key!);
        }

        return keys;
    }

    private RedisOutboxStore CreateStore()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        return new RedisOutboxStore(fixture.Multiplexer, Options.Create(new RedisOutboxOptions { KeyPrefix = _prefix }), _time);
    }
}
