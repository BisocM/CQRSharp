using CQRSharp.Persistence;
using CQRSharp.Redis;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     Runs the shared <see cref="CQRSharp.Testing.InboxStoreContractTests" /> suite against the Redis inbox. Records
///     expire on Redis's own clock, so the suite's clock-driven retention test is skipped here; the test below proves the
///     retention against the server instead, by reading the expiry Redis holds for a record rather than waiting it out.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisInboxStoreContractTests(RedisFixture fixture) : CQRSharp.Testing.InboxStoreContractTests
{
    private readonly string _prefix = RedisFixture.NewKeyPrefix("inbox");

    protected override FakeTimeProvider Time { get; } = new();

    protected override bool SupportsClockDrivenRetention => false;

    protected override Task<IInboxStore> CreateStoreAsync()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        return Task.FromResult<IInboxStore>(new RedisInboxStore(
            fixture.Multiplexer,
            Options.Create(new RedisOutboxOptions { KeyPrefix = _prefix, InboxRetention = InboxRetention })));
    }

    public override async ValueTask DisposeAsync()
    {
        await fixture.DeleteKeysAsync(_prefix);
        await base.DisposeAsync();
    }

    [Fact(DisplayName = "A recorded delivery expires on the server after the inbox retention")]
    public async Task A_recorded_delivery_expires_after_the_inbox_retention()
    {
        var store = await CreateStoreAsync();
        var messageId = Guid.NewGuid();

        (await store.RecordDeliveryAsync(messageId, "Tests.Handler", TestContext.Current.CancellationToken)).Should().BeTrue();

        var ttl = await fixture.Multiplexer.GetDatabase().KeyTimeToLiveAsync($"{_prefix}inbox:{messageId:N}:Tests.Handler");
        ttl.Should().NotBeNull("an inbox record without an expiry would never be forgotten");
        ttl!.Value.Should().BeGreaterThan(InboxRetention - TimeSpan.FromMinutes(1)).And.BeLessThanOrEqualTo(InboxRetention,
            "the record expires after the configured inbox retention, counted from when it was recorded");
    }
}
