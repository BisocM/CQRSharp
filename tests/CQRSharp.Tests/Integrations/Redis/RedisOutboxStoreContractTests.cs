using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Redis.Outbox;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     Runs the shared <see cref="CQRSharp.Testing.Outbox.OutboxStoreContractTests" /> conformance suite against the
///     Redis-backed <see cref="RedisOutboxStore" />. Every test runs against the live server provided by
///     <see cref="RedisFixture" />; when no Redis is reachable each test skips cleanly rather than failing. Each store
///     instance gets a unique GUID key prefix and wipes that prefix's keys on creation, so the xUnit-parallel runs and
///     repeated test runs never bleed state into one another.
/// </summary>
public sealed class RedisOutboxStoreContractTests : CQRSharp.Testing.Outbox.OutboxStoreContractTests, IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;
    private readonly string _prefix = $"cqrsharp:test:{Guid.NewGuid():N}:";

    public RedisOutboxStoreContractTests(RedisFixture fixture) => _fixture = fixture;

    protected override FakeTimeProvider Time { get; } = new();

    protected override async Task<IOutboxStore> CreateStoreAsync()
    {
        Skip.IfNot(_fixture.Available, "No Redis server is reachable (set CQRSHARP_TEST_REDIS to point at one).");
        await ResetAsync();

        var options = new RedisOutboxOptions
        {
            KeyPrefix = _prefix,
            VisibilityTimeout = VisibilityTimeout
        };

        return new RedisOutboxStore(_fixture.Multiplexer, Options.Create(options), Time);
    }

    // Deletes every key under this instance's unique prefix so each fresh store starts empty.
    private async Task ResetAsync()
    {
        var endpoints = _fixture.Multiplexer.GetEndPoints();
        var db = _fixture.Multiplexer.GetDatabase();
        foreach (var endpoint in endpoints)
        {
            var server = _fixture.Multiplexer.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica)
                continue;

            foreach (var key in server.Keys(pattern: $"{_prefix}*"))
                await db.KeyDeleteAsync(key);
        }
    }
}
