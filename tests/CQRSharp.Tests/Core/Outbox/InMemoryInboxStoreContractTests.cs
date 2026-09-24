using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Testing;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Runs the shared <see cref="InboxStoreContractTests" /> suite against the in-process inbox store, plus an
///     in-memory-specific test of the sweep that keeps it from growing without bound.
/// </summary>
public sealed class InMemoryInboxStoreContractTests : InboxStoreContractTests
{
    private const string Handler = "Tests.Handler";

    protected override FakeTimeProvider Time { get; } = new();

    protected override Task<IInboxStore> CreateStoreAsync() => Task.FromResult<IInboxStore>(NewStore());

    private InMemoryInboxStore NewStore()
        => new(Time, Options.Create(new InMemoryOutboxStoreOptions { InboxRetention = InboxRetention }));

    [Fact(DisplayName = "The in-memory inbox sweep drops the records whose retention has passed and keeps the ones still inside it")]
    public async Task The_sweep_removes_expired_records_and_keeps_live_ones()
    {
        var store = NewStore();
        var ct = TestContext.Current.CancellationToken;
        var expired = Guid.NewGuid();
        var live = Guid.NewGuid();

        // The first record also sets the next sweep to one retention window from now.
        (await store.RecordDeliveryAsync(expired, Handler, ct)).Should().BeTrue();
        Time.Advance(InboxRetention / 2);
        (await store.RecordDeliveryAsync(live, Handler, ct)).Should().BeTrue();

        // Past the first record's retention, not the second's; the next lookup runs the sweep.
        Time.Advance(InboxRetention / 2 + TimeSpan.FromSeconds(1));
        (await store.IsDeliveredAsync(Guid.NewGuid(), Handler, ct)).Should().BeFalse();

        store.Count.Should().Be(1, "the sweep removed the expired record and kept the live one");
        (await store.IsDeliveredAsync(live, Handler, ct)).Should().BeTrue("a record still inside its retention survives the sweep");
        (await store.RecordDeliveryAsync(expired, Handler, ct)).Should().BeTrue("the expired delivery can be recorded again");
    }
}
