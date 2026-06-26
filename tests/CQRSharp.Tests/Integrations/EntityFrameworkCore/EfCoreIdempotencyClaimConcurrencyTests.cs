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
///     Proves the durable idempotency claim is genuinely race-safe across SEPARATE store instances and SEPARATE
///     DbContexts that all hit ONE shared SQLite database — the realistic shape of two processes competing for the same
///     key. The shared single-instance contract suite cannot exercise this: its one store serializes every claim behind
///     the store's SemaphoreSlim, so a cross-process race never actually overlaps. A shared-cache, named in-memory
///     SQLite database (kept alive by a master connection) lets multiple <see cref="EfCoreIdempotencyStore{TContext}" />
///     instances over independent connections see the same rows. Covers the INSERT race (unique-PK), the expired-row
///     take-over race (the RowVersion concurrency token), and a single-context expiry take-over end to end.
/// </summary>
public sealed class EfCoreIdempotencyClaimConcurrencyTests
{
    private const int Racers = 8;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    [Fact]
    public async Task Separate_stores_inserting_the_same_fresh_key_in_parallel_yield_exactly_one_winner()
    {
        var time = new FakeTimeProvider();
        var dbName = $"idemp-insert-{Guid.NewGuid():N}";

        // The master connection keeps the shared in-memory database alive for the whole test.
        await using var master = OpenSharedConnection(dbName);
        await CreateSchemaAsync(master);

        var key = $"insert-race:{Guid.NewGuid():N}";

        // Each racer is a fully independent store: its OWN DbContext over its OWN connection to the SAME database, so
        // their concurrent inserts genuinely collide on the unique primary key (no shared SemaphoreSlim serializes
        // them). Exactly one insert lands; every loser hits DbUpdateException, re-reads the live row, and reports false.
        var (connections, stores) = CreateRacers(dbName, time, Racers);
        try
        {
            var results = await Task.WhenAll(
                stores.Select(s => Task.Run(() => s.TryClaimAsync(key, CancellationToken.None))));

            results.Count(claimed => claimed).Should().Be(1,
                "the unique primary key must let exactly one concurrent insert of a fresh key win");
        }
        finally
        {
            await DisposeRacersAsync(connections);
        }
    }

    [Fact]
    public async Task Separate_stores_taking_over_the_same_expired_key_in_parallel_yield_exactly_one_winner()
    {
        var time = new FakeTimeProvider();
        var dbName = $"idemp-takeover-{Guid.NewGuid():N}";

        await using var master = OpenSharedConnection(dbName);
        await CreateSchemaAsync(master);

        var key = $"takeover-race:{Guid.NewGuid():N}";

        // Seed an already-EXPIRED row for the key (ExpiresAt in the past relative to the shared clock), so every racer
        // reads a present-but-expired row and goes down the take-over UPDATE branch rather than the INSERT branch.
        await using (var seedConnection = OpenSharedConnection(dbName))
        await using (var seedContext = NewContext(seedConnection))
        {
            seedContext.Set<IdempotencyEntity>().Add(new IdempotencyEntity
            {
                Key = key,
                ExpiresAt = time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5)
            });
            await seedContext.SaveChangesAsync();
        }

        // N independent stores all try to take over the SAME expired row at once. This is the Finding 1 regression
        // guard: the take-over UPDATE only stays atomic because RowVersion is a concurrency token — without it EF emits
        // an UPDATE with no version predicate, so every racer's save lands and two-or-more callers double-claim. With
        // the token, exactly one save wins; the losers see DbUpdateConcurrencyException, re-read the now-live row, and
        // report false.
        var (connections, stores) = CreateRacers(dbName, time, Racers);
        try
        {
            var results = await Task.WhenAll(
                stores.Select(s => Task.Run(() => s.TryClaimAsync(key, CancellationToken.None))));

            results.Count(claimed => claimed).Should().Be(1,
                "the RowVersion concurrency token must let exactly one caller take over an expired key");
        }
        finally
        {
            await DisposeRacersAsync(connections);
        }
    }

    [Fact]
    public async Task A_single_store_reclaims_a_key_once_it_has_aged_past_retention()
    {
        var time = new FakeTimeProvider();
        var dbName = $"idemp-expiry-{Guid.NewGuid():N}";

        await using var master = OpenSharedConnection(dbName);
        await CreateSchemaAsync(master);

        await using var connection = OpenSharedConnection(dbName);
        await using var context = NewContext(connection);
        var store = NewStore(context, time);

        var key = $"expiry:{Guid.NewGuid():N}";

        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeTrue("a fresh key is claimable");
        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeFalse("still within the retention window");

        // Past retention the row is expired; the next claim drives the take-over UPDATE branch end to end and wins.
        time.Advance(Retention + TimeSpan.FromSeconds(1));
        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeTrue(
            "an expired key is taken over and re-claimed");
    }

    private static (List<SqliteConnection> Connections, List<EfCoreIdempotencyStore<TestDbContext>> Stores) CreateRacers(
        string dbName, FakeTimeProvider time, int count)
    {
        var connections = new List<SqliteConnection>(count);
        var stores = new List<EfCoreIdempotencyStore<TestDbContext>>(count);
        for (var i = 0; i < count; i++)
        {
            var connection = OpenSharedConnection(dbName);
            connections.Add(connection);
            stores.Add(NewStore(NewContext(connection), time));
        }

        return (connections, stores);
    }

    private static async Task DisposeRacersAsync(IEnumerable<SqliteConnection> connections)
    {
        foreach (var connection in connections)
            await connection.DisposeAsync();
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

    private static EfCoreIdempotencyStore<TestDbContext> NewStore(TestDbContext context, FakeTimeProvider time)
        => new(
            context,
            time,
            Options.Create(new EfCoreIdempotencyStoreOptions { Retention = Retention }),
            NullLogger<EfCoreIdempotencyStore<TestDbContext>>.Instance);

    /// <summary>A minimal context that maps only the idempotency entity via the shipped configuration.</summary>
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new IdempotencyEntityConfiguration());
    }
}
