using CQRSharp.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Testing;

/// <summary>
///     The reusable conformance suite for every <see cref="IInboxStore" /> implementation: a delivery is unknown until
///     recorded, recorded exactly once even under concurrency, independent per message and per handler, and forgotten
///     once the inbox retention has elapsed. Derive from it and supply a fresh store.
/// </summary>
public abstract class InboxStoreContractTests : IAsyncLifetime
{
    /// <summary>Runs before each test; nothing by default.</summary>
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    ///     Runs after each test. Override it to dispose whatever <c>CreateStoreAsync</c> opened (a connection, a
    ///     context) so a long suite does not leak one per test.
    /// </summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The clock the store-under-test reads; the retention test advances it. Unused by a store whose backing service keeps its own time.</summary>
    protected abstract FakeTimeProvider Time { get; }

    /// <summary>Creates a fresh, empty store bound to <see cref="Time" />.</summary>
    protected abstract Task<IInboxStore> CreateStoreAsync();

    /// <summary>The inbox retention the store-under-test is configured with; the retention test advances the clock past it.</summary>
    protected virtual TimeSpan InboxRetention => TimeSpan.FromDays(7);

    /// <summary>
    ///     Whether advancing <see cref="Time" /> ages records out. A store whose expiry is timed by its backing service
    ///     (Redis) returns <c>false</c>, and the retention test is skipped for it.
    /// </summary>
    protected virtual bool SupportsClockDrivenRetention => true;

    private const string Handler = "Tests.Handler";
    private const string OtherHandler = "Tests.OtherHandler";

    /// <summary>
    ///     Contract: with no unit-of-work transaction open, the inbox does not claim to join one. An inbox that did would
    ///     have a delivery recorded before a commit that nothing would roll back if it failed.
    /// </summary>
    [Fact]
    public async Task An_inbox_outside_a_transaction_does_not_join_the_unit_of_work()
    {
        var store = await CreateStoreAsync();

        Assert.False(store.JoinsUnitOfWork, "With no transaction open, nothing the inbox records can roll back with one.");
    }

    /// <summary>Contract: nothing is delivered until it was recorded.</summary>
    [Fact]
    public async Task A_delivery_is_unknown_until_recorded()
    {
        var store = await CreateStoreAsync();
        var messageId = Guid.NewGuid();

        Assert.False(await store.IsDeliveredAsync(messageId, Handler, CancellationToken.None), "An unrecorded delivery must not be reported as delivered.");
        Assert.True(await store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None), "The first record of a delivery must succeed.");
        Assert.True(await store.IsDeliveredAsync(messageId, Handler, CancellationToken.None), "A recorded delivery must be reported as delivered.");
    }

    /// <summary>Contract: recording the same delivery twice fails the second time.</summary>
    [Fact]
    public async Task A_delivery_is_recorded_exactly_once()
    {
        var store = await CreateStoreAsync();
        var messageId = Guid.NewGuid();

        Assert.True(await store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None), "The first record of a delivery must succeed.");
        Assert.False(await store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None), "A second record of the same delivery must be rejected.");
    }

    /// <summary>Contract: deliveries are independent per message and per handler.</summary>
    [Fact]
    public async Task Deliveries_are_independent_per_message_and_per_handler()
    {
        var store = await CreateStoreAsync();
        var messageId = Guid.NewGuid();
        await store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None);

        Assert.False(await store.IsDeliveredAsync(messageId, OtherHandler, CancellationToken.None), "A delivery to one handler must not count for another.");
        Assert.False(await store.IsDeliveredAsync(Guid.NewGuid(), Handler, CancellationToken.None), "A delivery of one message must not count for another.");
        Assert.True(await store.RecordDeliveryAsync(messageId, OtherHandler, CancellationToken.None), "The same message can be recorded for another handler.");
    }

    /// <summary>Contract: of many concurrent records of one delivery, exactly one succeeds.</summary>
    [Fact]
    public async Task Concurrent_records_of_one_delivery_yield_exactly_one_winner()
    {
        var store = await CreateStoreAsync();
        var messageId = Guid.NewGuid();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None))));

        ContractAssert.Equal(1, results.Count(recorded => recorded), "Number of winners among concurrent records of one delivery");
    }

    /// <summary>Contract: a record is forgotten once the inbox retention has elapsed, so the delivery can be recorded again.</summary>
    [Fact]
    public async Task A_record_ages_out_after_the_inbox_retention()
    {
        Assert.SkipUnless(SupportsClockDrivenRetention, "This store's records expire on its backing service's clock.");
        var store = await CreateStoreAsync();
        var messageId = Guid.NewGuid();
        await store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None);

        Time.Advance(InboxRetention + TimeSpan.FromSeconds(1));

        Assert.False(await store.IsDeliveredAsync(messageId, Handler, CancellationToken.None), "A record older than the inbox retention must be forgotten.");
        Assert.True(await store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None), "A forgotten delivery can be recorded again.");
    }
}
