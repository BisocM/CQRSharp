using CQRSharp.Abstractions.Models.Outbox;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.EntityFrameworkCore.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     Proves the durable claim is genuinely race-safe across separate store instances and separate connections that
///     all hit ONE shared SQLite database — the realistic shape of two processors competing for the same outbox. A
///     shared-cache, named in-memory SQLite database (kept alive by a master connection) lets multiple
///     <see cref="EfCoreOutboxStore{TContext}" /> instances over independent connections see the same rows.
/// </summary>
public sealed class EfCoreOutboxClaimConcurrencyTests
{
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Two_stores_claiming_in_parallel_never_claim_the_same_message_twice()
    {
        var time = new FakeTimeProvider();
        var dbName = $"outbox-concurrency-{Guid.NewGuid():N}";

        // The master connection keeps the shared in-memory database alive for the whole test.
        await using var master = OpenSharedConnection(dbName);
        await CreateSchemaAsync(master);

        // Seed 50 due Pending messages directly through one context.
        await using var seedConnection = OpenSharedConnection(dbName);
        await using (var seedContext = NewContext(seedConnection))
        {
            var now = time.GetUtcNow().UtcDateTime;
            for (var i = 0; i < 50; i++)
                seedContext.Set<OutboxEntity>().Add(OutboxEntityMapper.FromMessage(NewPending(now.AddSeconds(i))));
            await seedContext.SaveChangesAsync();
        }

        // Two independent stores over two independent connections to the SAME database.
        await using var connectionA = OpenSharedConnection(dbName);
        await using var connectionB = OpenSharedConnection(dbName);
        await using var contextA = NewContext(connectionA);
        await using var contextB = NewContext(connectionB);

        var storeA = NewStore(contextA, time);
        var storeB = NewStore(contextB, time);

        // Both try to claim the entire backlog at once.
        var batches = await Task.WhenAll(
            Task.Run(async () => (await storeA.GetPendingAsync(50, CancellationToken.None)).ToList()),
            Task.Run(async () => (await storeB.GetPendingAsync(50, CancellationToken.None)).ToList()));

        var claimed = batches.SelectMany(b => b).ToList();

        claimed.Select(m => m.Id).Should().OnlyHaveUniqueItems(
            "the optimistic-token claim must never hand the same message to two stores");
        claimed.Should().OnlyContain(m => m.Status == OutboxMessageStatus.InProgress,
            "every claimed message is transitioned to in-progress");
    }

    [Fact]
    public async Task A_message_stuck_in_progress_is_reclaimable_by_another_store_after_the_visibility_timeout()
    {
        var time = new FakeTimeProvider();
        var dbName = $"outbox-reclaim-{Guid.NewGuid():N}";

        await using var master = OpenSharedConnection(dbName);
        await CreateSchemaAsync(master);

        await using var connectionA = OpenSharedConnection(dbName);
        await using var connectionB = OpenSharedConnection(dbName);
        await using var contextA = NewContext(connectionA);
        await using var contextB = NewContext(connectionB);

        var storeA = NewStore(contextA, time);
        var storeB = NewStore(contextB, time);

        var message = NewPending(time.GetUtcNow().UtcDateTime);
        await storeA.StoreAsync([message], CancellationToken.None);

        // Store A claims it, then "crashes" (never marks it processed).
        (await storeA.GetPendingAsync(10, CancellationToken.None)).Should().ContainSingle();

        // Before the lease expires, store B sees nothing claimable.
        (await storeB.GetPendingAsync(10, CancellationToken.None)).Should().BeEmpty();

        // Past the visibility timeout the lease is abandoned and store B reclaims it.
        time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var reclaimed = (await storeB.GetPendingAsync(10, CancellationToken.None)).ToList();
        reclaimed.Should().ContainSingle().Which.Id.Should().Be(message.Id);
        reclaimed[0].Status.Should().Be(OutboxMessageStatus.InProgress);
    }

    private static SqliteConnection OpenSharedConnection(string dbName)
    {
        // Shared-cache + a named in-memory database lets multiple connections in this process address the same rows.
        var connection = new SqliteConnection($"Data Source={dbName};Mode=Memory;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection)
    {
        await using var context = NewContext(connection);
        await context.Database.EnsureCreatedAsync();
    }

    private static TestDbContext NewContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection).Options);

    private static EfCoreOutboxStore<TestDbContext> NewStore(TestDbContext context, FakeTimeProvider time)
        => new(
            context,
            time,
            Options.Create(new EfCoreOutboxStoreOptions { VisibilityTimeout = VisibilityTimeout }),
            NullLogger<EfCoreOutboxStore<TestDbContext>>.Instance);

    private static OutboxMessage NewPending(DateTime createdAt) => new(
        Guid.NewGuid(),
        "TestNotification",
        [1, 2, 3],
        createdAt,
        OutboxMessageStatus.Pending,
        ProcessedAt: null,
        LastError: null);

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new OutboxEntityConfiguration());
    }
}
