using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Redis.Idempotency;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     Runs the shared <see cref="CQRSharp.Testing.Idempotency.IdempotencyStoreContractTests" /> conformance suite
///     against the Redis-backed <see cref="RedisIdempotencyStore" />. Every test runs against the live server provided
///     by <see cref="RedisFixture" />; when no Redis is reachable each test skips cleanly rather than failing. Each
///     instance gets a unique GUID key prefix and a long retention, so a shared Redis never bleeds state across runs and
///     the dedup window never trips during the contract tests.
/// </summary>
public sealed class RedisIdempotencyStoreContractTests : CQRSharp.Testing.Idempotency.IdempotencyStoreContractTests, IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;
    private readonly string _prefix = $"cqrsharp:test:idemp:{Guid.NewGuid():N}:";

    public RedisIdempotencyStoreContractTests(RedisFixture fixture) => _fixture = fixture;

    protected override Task<IIdempotencyStore> CreateStoreAsync()
    {
        Skip.IfNot(_fixture.Available, "No Redis server is reachable (set CQRSHARP_TEST_REDIS to point at one).");

        var options = new RedisIdempotencyOptions
        {
            KeyPrefix = _prefix,
            // Far longer than any contract test runs, so the dedup window never expires mid-test.
            Retention = TimeSpan.FromHours(1)
        };

        return Task.FromResult<IIdempotencyStore>(
            new RedisIdempotencyStore(_fixture.Multiplexer, Options.Create(options)));
    }

    /// <summary>
    ///     The Redis store self-heals on its own: the claim's EX expiry IS the dedup window, server-timed by Redis, so
    ///     once the short retention TTL elapses the key vanishes and a re-claim succeeds with no explicit release. This
    ///     exercises the EX-expiry self-heal path against a live server (a real wait, since Redis owns the clock — there
    ///     is no client TimeProvider to fast-forward), so it is deliberately short. Skips cleanly when Redis is absent.
    /// </summary>
    [SkippableFact]
    public async Task A_claim_self_heals_once_its_retention_ttl_expires()
    {
        Skip.IfNot(_fixture.Available, "No Redis server is reachable (set CQRSHARP_TEST_REDIS to point at one).");

        var retention = TimeSpan.FromSeconds(2);
        var store = new RedisIdempotencyStore(
            _fixture.Multiplexer,
            Options.Create(new RedisIdempotencyOptions { KeyPrefix = _prefix, Retention = retention }));

        var key = $"selfheal:{Guid.NewGuid():N}";

        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeTrue("a fresh key is claimable");
        (await store.TryClaimAsync(key, CancellationToken.None)).Should()
            .BeFalse("the key is still within its retention TTL");

        // Wait past the real TTL so Redis expires the key, then re-claim must succeed without any explicit release.
        await Task.Delay(retention + TimeSpan.FromMilliseconds(500));

        (await store.TryClaimAsync(key, CancellationToken.None)).Should()
            .BeTrue("the claim's EX TTL expired, so the key self-heals and is claimable again");
    }
}
