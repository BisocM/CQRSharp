using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;

// The shared contract suites, run against the EF Core stores on a real PostgreSQL and a real SQL Server. SQLite proves
// the logic; these prove the SQL the provider actually generates - the correlated NOT EXISTS of the ordered claim, the
// identity key, the indexed string columns, the bulk deletes - on the databases the outbox ships to.

public abstract class ProviderOutboxStoreContractTests(RelationalProviderFixture fixture) : OutboxStoreContractTests
{
    private ProviderTestDbContext? _context;

    public override async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    protected override async Task<IOutboxStore> CreateStoreAsync()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await fixture.ResetAsync();
        return new EfCoreOutboxStore<ProviderTestDbContext>(
            _context = new ProviderTestDbContext(fixture.CreateOptions()),
            Time,
            Options.Create(new EfCoreOutboxStoreOptions { VisibilityTimeout = VisibilityTimeout }),
            NullLogger<EfCoreOutboxStore<ProviderTestDbContext>>.Instance);
    }
}

// The store as it ships: a singleton over AddDbContext, a context of its own per operation, so the suite's concurrent
// claims race on the real database's unique key.
public abstract class ProviderIdempotencyStoreContractTests(RelationalProviderFixture fixture) : IdempotencyStoreContractTests
{
    private ServiceProvider? _provider;

    public override async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    protected override async Task<IIdempotencyStore> CreateStoreAsync()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await fixture.ResetAsync();
        _provider = await EfCoreStoreServices.IdempotencyAsync<ProviderTestDbContext>(fixture.Configure, Time, Retention);
        return _provider.GetRequiredService<IIdempotencyStore>();
    }
}

public abstract class ProviderInboxStoreContractTests(RelationalProviderFixture fixture) : InboxStoreContractTests
{
    private ProviderTestDbContext? _context;

    public override async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    protected override async Task<IInboxStore> CreateStoreAsync()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await fixture.ResetAsync();
        return new EfCoreInboxStore<ProviderTestDbContext>(
            _context = new ProviderTestDbContext(fixture.CreateOptions()),
            Time,
            Options.Create(new EfCoreOutboxStoreOptions { InboxRetention = InboxRetention }));
    }
}

/// <summary>Two stores over two connections claiming the same rows: the optimistic claim on a real database.</summary>
public abstract class ProviderOutboxConcurrencyTests(RelationalProviderFixture fixture)
{
    [Fact(DisplayName = "Two stores claiming in parallel never claim the same message twice, and partition heads stay exclusive")]
    public async Task Parallel_claims_are_exclusive()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await fixture.ResetAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var now = time.GetUtcNow().UtcDateTime;

        var seed = NewStore(fixture, time);
        var messages = Enumerable.Range(0, 60).Select(i => new OutboxMessage(
            Guid.NewGuid(), "TestNotification", "Tests.Handler", [1], now.AddMilliseconds(i), OutboxMessageStatus.Pending, null, null,
            PartitionKey: i % 3 == 0 ? "shared" : null)).ToArray();
        await seed.StoreAsync(messages, CancellationToken.None);

        var storeA = NewStore(fixture, time);
        var storeB = NewStore(fixture, time);
        var batches = await Task.WhenAll(
            Task.Run(async () => (await storeA.ClaimPendingAsync(60, CancellationToken.None)).ToList()),
            Task.Run(async () => (await storeB.ClaimPendingAsync(60, CancellationToken.None)).ToList()));

        var claimed = batches.SelectMany(b => b.Select(c => c.Message)).ToList();
        claimed.Select(m => m.Id).Should().OnlyHaveUniqueItems("the optimistic-token claim must never hand the same message to two stores");
        claimed.Count(m => m.PartitionKey == "shared").Should().Be(1, "only the head of a partition is claimable at a time");
        claimed.Count.Should().Be(41, "40 unpartitioned messages plus one partition head");
    }

    private static EfCoreOutboxStore<ProviderTestDbContext> NewStore(RelationalProviderFixture fixture, FakeTimeProvider time)
        => new(
            new ProviderTestDbContext(fixture.CreateOptions()),
            time,
            Options.Create(new EfCoreOutboxStoreOptions { VisibilityTimeout = TimeSpan.FromMinutes(5) }),
            NullLogger<EfCoreOutboxStore<ProviderTestDbContext>>.Instance);
}

/// <summary>Several inbox stores over their own connections recording one delivery at once: the primary key on a real database picks the winner, and every loser reports a duplicate instead of throwing.</summary>
public abstract class ProviderInboxConcurrencyTests(RelationalProviderFixture fixture)
{
    [Fact(DisplayName = "Stores recording one delivery in parallel over separate connections yield exactly one winner")]
    public async Task Parallel_records_yield_exactly_one_winner()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await fixture.ResetAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var messageId = Guid.NewGuid();
        var stores = Enumerable.Range(0, 8).Select(_ => new EfCoreInboxStore<ProviderTestDbContext>(
            new ProviderTestDbContext(fixture.CreateOptions()), time, Options.Create(new EfCoreOutboxStoreOptions()))).ToList();

        var results = await Task.WhenAll(stores.Select(store =>
            Task.Run(() => store.RecordDeliveryAsync(messageId, "Tests.Handler", CancellationToken.None))));

        results.Count(recorded => recorded).Should().Be(1, "the primary key admits one record per (message, handler)");
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlOutboxStoreContractTests(PostgreSqlFixture fixture) : ProviderOutboxStoreContractTests(fixture);

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlIdempotencyStoreContractTests(PostgreSqlFixture fixture) : ProviderIdempotencyStoreContractTests(fixture);

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlInboxStoreContractTests(PostgreSqlFixture fixture) : ProviderInboxStoreContractTests(fixture);

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlOutboxConcurrencyTests(PostgreSqlFixture fixture) : ProviderOutboxConcurrencyTests(fixture);

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlInboxConcurrencyTests(PostgreSqlFixture fixture) : ProviderInboxConcurrencyTests(fixture);

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerOutboxStoreContractTests(SqlServerFixture fixture) : ProviderOutboxStoreContractTests(fixture);

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerIdempotencyStoreContractTests(SqlServerFixture fixture) : ProviderIdempotencyStoreContractTests(fixture);

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerInboxStoreContractTests(SqlServerFixture fixture) : ProviderInboxStoreContractTests(fixture);

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerOutboxConcurrencyTests(SqlServerFixture fixture) : ProviderOutboxConcurrencyTests(fixture);

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerInboxConcurrencyTests(SqlServerFixture fixture) : ProviderInboxConcurrencyTests(fixture);
