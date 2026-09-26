using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     The EF Core idempotency claim across separate applications - separate containers, each with its own store and
///     contexts - over one shared SQLite database: the realistic shape of several processes competing for one key.
///     Covers the expired-row take-over race (the RowVersion concurrency token) and the insert race across stores; the
///     contract suite covers concurrent claims within one store.
/// </summary>
public sealed class EfCoreIdempotencyClaimConcurrencyTests : IAsyncDisposable
{
    private const int Racers = 8;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly SharedSqliteDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<ServiceProvider> _providers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact(DisplayName = "Separate stores inserting the same fresh key at once yield exactly one winner")]
    public async Task Separate_stores_inserting_the_same_fresh_key_in_parallel_yield_exactly_one_winner()
    {
        var stores = await RacersAsync();
        var key = $"insert-race:{Guid.NewGuid():N}";

        var results = await Task.WhenAll(stores.Select(s => Task.Run(() => s.TryClaimAsync(key, null, CancellationToken.None))));

        results.Count(claim => claim.IsClaimed).Should().Be(1, "the unique primary key lets exactly one concurrent insert of a fresh key win");
    }

    [Fact(DisplayName = "Separate stores taking over the same expired key at once yield exactly one winner")]
    public async Task Separate_stores_taking_over_the_same_expired_key_in_parallel_yield_exactly_one_winner()
    {
        var stores = await RacersAsync();
        var key = $"takeover-race:{Guid.NewGuid():N}";

        // An already-expired row for the key, so every racer takes the take-over UPDATE branch rather than the INSERT one.
        await using (var seed = NewContext())
        {
            seed.Set<IdempotencyEntity>().Add(new IdempotencyEntity { Key = key, ExpiresAt = _time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5) });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The take-over UPDATE only stays atomic because RowVersion is a concurrency token: without it every racer's save
        // would land and several callers would win. With it one save wins; the losers re-read the now-live row.
        var results = await Task.WhenAll(stores.Select(s => Task.Run(() => s.TryClaimAsync(key, null, CancellationToken.None))));

        results.Count(claim => claim.IsClaimed).Should().Be(1, "the RowVersion concurrency token lets exactly one caller take over an expired key");
    }

    private async Task<List<IIdempotencyStore>> RacersAsync()
    {
        var stores = new List<IIdempotencyStore>(Racers);
        for (var i = 0; i < Racers; i++)
        {
            var provider = await EfCoreStoreServices.IdempotencyAsync<TestDbContext>(o => o.UseSqlite(_database.ConnectionString), _time, Retention);
            _providers.Add(provider);
            stores.Add(provider.GetRequiredService<IIdempotencyStore>());
        }

        return stores;
    }

    private TestDbContext NewContext() => new(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_database.ConnectionString).Options);

    /// <summary>A minimal context that maps only the idempotency entity via the shipped configuration.</summary>
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new IdempotencyEntityConfiguration());
    }
}
