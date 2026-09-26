using CQRSharp.Persistence;
using CQRSharp.Redis;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     Runs the shared <see cref="CQRSharp.Testing.OutboxStoreContractTests" /> conformance suite against the
///     Redis-backed <see cref="RedisOutboxStore" /> on the <see cref="RedisFixture" />'s server. Each test gets a key prefix
///     of its own, which is what keeps tests (and concurrently running test processes) apart, and deletes its keys when it
///     finishes.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisOutboxStoreContractTests(RedisFixture fixture) : CQRSharp.Testing.OutboxStoreContractTests
{
    private readonly string _prefix = RedisFixture.NewKeyPrefix("outbox");

    protected override FakeTimeProvider Time { get; } = new();

    protected override Task<IOutboxStore> CreateStoreAsync()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var options = new RedisOutboxOptions
        {
            KeyPrefix = _prefix,
            VisibilityTimeout = VisibilityTimeout
        };

        return Task.FromResult<IOutboxStore>(new RedisOutboxStore(fixture.Multiplexer, Options.Create(options), Time));
    }

    public override async ValueTask DisposeAsync()
    {
        await fixture.DeleteKeysAsync(_prefix);
        await base.DisposeAsync();
    }
}
