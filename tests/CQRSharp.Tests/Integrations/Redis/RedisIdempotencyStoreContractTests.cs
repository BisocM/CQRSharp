using CQRSharp.Persistence;
using CQRSharp.Redis;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     Runs the shared <see cref="CQRSharp.Testing.IdempotencyStoreContractTests" /> conformance suite against the
///     Redis-backed <see cref="RedisIdempotencyStore" /> on the <see cref="RedisFixture" />'s server. Redis times the
///     deduplication window itself (the keys' PX expiry), so the suite's clock-driven retention tests are skipped; the
///     tests below prove the same guarantees against the server without waiting: they read the expiry Redis holds, and
///     expire a key through Redis (an expiry time in the past) where a test needs it gone.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisIdempotencyStoreContractTests(RedisFixture fixture) : CQRSharp.Testing.IdempotencyStoreContractTests
{
    private readonly string _prefix = RedisFixture.NewKeyPrefix("idemp");

    // The store takes no clock: its expiry is Redis's own PX.
    protected override FakeTimeProvider Time { get; } = new();

    protected override bool SupportsClockDrivenRetention => false;

    protected override Task<IIdempotencyStore> CreateStoreAsync() => Task.FromResult<IIdempotencyStore>(CreateRedisStore());

    public override async ValueTask DisposeAsync()
    {
        await fixture.DeleteKeysAsync(_prefix);
        await base.DisposeAsync();
    }

    [Fact(DisplayName = "A claim and its fingerprint expire on the server after the retention window")]
    public async Task A_claim_and_its_fingerprint_expire_after_the_retention_window()
    {
        var store = CreateRedisStore();
        var key = NewKey();

        (await store.TryClaimAsync(key, "fp", TestContext.Current.CancellationToken)).IsClaimed.Should().BeTrue();

        foreach (var redisKey in new[] { ValueKey(key), FingerprintKey(key) })
        {
            var ttl = await Database.KeyTimeToLiveAsync(redisKey);
            ttl.Should().NotBeNull($"{redisKey} must expire, or an abandoned claim would hold its key forever");
            ttl!.Value.Should().BeGreaterThan(Retention - TimeSpan.FromMinutes(1)).And.BeLessThanOrEqualTo(Retention,
                "the claim is remembered for the retention window, counted from the claim");
        }
    }

    [Fact(DisplayName = "Completing a key keeps the expiry of its claim instead of starting a new window")]
    public async Task Completing_a_key_keeps_the_expiry_Redis_gave_its_claim()
    {
        var store = CreateRedisStore();
        var key = NewKey();
        var claim = await store.TryClaimAsync(key, null, TestContext.Current.CancellationToken);

        // As if the claim had been made half an hour before the retention window ends.
        var remaining = TimeSpan.FromMinutes(30);
        (await Database.KeyExpireAsync(ValueKey(key), remaining)).Should().BeTrue();

        await store.CompleteAsync(key, claim.Token!, [7], TestContext.Current.CancellationToken);

        (await store.TryClaimAsync(key, null, TestContext.Current.CancellationToken)).Status.Should().Be(IdempotencyClaimStatus.Completed);
        var ttl = await Database.KeyTimeToLiveAsync(ValueKey(key));
        ttl.Should().NotBeNull("a completed key is forgotten when its claim's window ends");
        ttl!.Value.Should().BeGreaterThan(remaining - TimeSpan.FromMinutes(1)).And.BeLessThanOrEqualTo(remaining,
            "the retention window runs from the claim, not from the completion");
    }

    [Fact(DisplayName = "Once Redis expires a claim, the key is claimable again and the stale claimant can touch neither the key nor its successor's claim")]
    public async Task An_expired_claim_frees_its_key_and_its_stale_claimant_cannot_touch_the_successor()
    {
        var store = CreateRedisStore();
        var key = NewKey();
        var stale = await store.TryClaimAsync(key, "fp-a", TestContext.Current.CancellationToken);
        stale.IsClaimed.Should().BeTrue("a fresh key is claimable");
        (await store.TryClaimAsync(key, "fp-a", TestContext.Current.CancellationToken)).Status.Should()
            .Be(IdempotencyClaimStatus.InProgress, "the key is held within its retention window");

        // Redis deletes a key whose expiry time is already in the past: the claim's window ends here, on the server.
        await Database.KeyExpireAsync(ValueKey(key), DateTime.UnixEpoch);
        await Database.KeyExpireAsync(FingerprintKey(key), DateTime.UnixEpoch);

        // Another store instance - another process - stands in for the crashed claimant's successor.
        var successorStore = CreateRedisStore();
        var successor = await successorStore.TryClaimAsync(key, "fp-b", TestContext.Current.CancellationToken);
        successor.IsClaimed.Should().BeTrue("the expired claim no longer holds the key, nor does its fingerprint");
        successor.Token.Should().NotBe(stale.Token, "every claim gets its own token");

        await store.ReleaseAsync(key, stale.Token!, TestContext.Current.CancellationToken);
        await store.CompleteAsync(key, stale.Token!, [9], TestContext.Current.CancellationToken);
        (await successorStore.TryClaimAsync(key, "fp-b", TestContext.Current.CancellationToken)).Status.Should()
            .Be(IdempotencyClaimStatus.InProgress, "the successor still holds its claim");

        await successorStore.CompleteAsync(key, successor.Token!, [1], TestContext.Current.CancellationToken);
        var replay = await successorStore.TryClaimAsync(key, "fp-b", TestContext.Current.CancellationToken);
        replay.Status.Should().Be(IdempotencyClaimStatus.Completed);
        replay.StoredResult.Should().Equal(new byte[] { 1 }, "the successor's result, not the stale claimant's");
    }

    private RedisIdempotencyStore CreateRedisStore()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        return new RedisIdempotencyStore(
            fixture.Multiplexer,
            Options.Create(new RedisIdempotencyOptions { KeyPrefix = _prefix, Retention = Retention }));
    }

    private IDatabase Database => fixture.Multiplexer.GetDatabase();

    private RedisKey ValueKey(string key) => _prefix + "k:" + key;

    private RedisKey FingerprintKey(string key) => _prefix + "f:" + key;

    private static string NewKey() => $"redis:{Guid.NewGuid():N}";
}
