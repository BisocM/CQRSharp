using CQRSharp.Pipelines;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.EntityFrameworkCore.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations;

/// <summary>Store housekeeping and registration guards added in 5.0.</summary>
public sealed class StoreMaintenanceTests
{
    [Fact(DisplayName = "EF outbox: processed messages older than ProcessedRetention are purged; recent and dead-lettered ones are kept")]
    public async Task Ef_outbox_purges_old_processed_messages()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<OutboxDbContext>().UseSqlite(connection).Options;
        await using var context = new OutboxDbContext(dbOptions);
        await context.Database.EnsureCreatedAsync();

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new EfCoreOutboxStore<OutboxDbContext>(
            context,
            time,
            Options.Create(new EfCoreOutboxStoreOptions { ProcessedRetention = TimeSpan.FromDays(7), PurgeInterval = TimeSpan.FromHours(1) }),
            NullLogger<EfCoreOutboxStore<OutboxDbContext>>.Instance);

        var old = Message(time);
        var deadLettered = Message(time);
        await store.StoreAsync([old, deadLettered], CancellationToken.None);
        var firstBatch = (await store.GetPendingAsync(10, CancellationToken.None)).ToDictionary(m => m.Id, m => m.Claim!.Value);
        await store.MarkAsProcessedAsync(firstBatch[old.Id], CancellationToken.None);
        await store.MarkAsFailedAsync(firstBatch[deadLettered.Id], "poison", CancellationToken.None);

        time.Advance(TimeSpan.FromDays(8));
        var recent = Message(time);
        await store.StoreAsync([recent], CancellationToken.None);
        var secondBatch = (await store.GetPendingAsync(10, CancellationToken.None)).ToList(); // the purge rides on the poll
        await store.MarkAsProcessedAsync(secondBatch.Single().Claim!.Value, CancellationToken.None);

        var remaining = await context.Set<OutboxEntity>().AsNoTracking().Select(e => e.Id).ToListAsync();
        remaining.Should().BeEquivalentTo([deadLettered.Id, recent.Id]);
    }

    [Fact(DisplayName = "Redis: two different connection strings are rejected instead of the second being silently ignored")]
    public void Redis_rejects_conflicting_connection_strings()
    {
        var services = new ServiceCollection();
        services.AddRedisOutboxStore("redis-a:6379");

        var sameAgain = () => services.AddRedisIdempotencyStore("redis-a:6379");
        sameAgain.Should().NotThrow();

        var different = () => new ServiceCollection()
            .AddRedisOutboxStore("redis-a:6379")
            .AddRedisIdempotencyStore("redis-b:6379");
        different.Should().Throw<InvalidOperationException>().WithMessage("*two different Redis connection strings*");
    }

    private static OutboxMessage Message(TimeProvider time)
        => new(Guid.NewGuid(), "test.notification", [1, 2, 3], time.GetUtcNow().UtcDateTime, OutboxMessageStatus.Pending, null, null);

    private sealed class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new OutboxEntityConfiguration());
    }
}
