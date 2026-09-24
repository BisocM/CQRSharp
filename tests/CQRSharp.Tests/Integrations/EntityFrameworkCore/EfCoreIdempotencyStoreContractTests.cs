using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Testing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     Runs the shared <see cref="IdempotencyStoreContractTests" /> conformance suite against the EF Core idempotency
///     store as it ships: registered by <c>AddEntityFrameworkCoreIdempotencyStore</c>, a singleton that opens a context of
///     its own per operation, over a shared in-memory SQLite database - so the suite's concurrent claims race on the
///     database's unique key rather than queueing behind one context. Time is the suite's fake clock.
/// </summary>
public sealed class EfCoreIdempotencyStoreContractTests : IdempotencyStoreContractTests
{
    private readonly SharedSqliteDatabase _database = new();
    private readonly List<ServiceProvider> _providers = [];

    public override async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    protected override FakeTimeProvider Time { get; } = new();

    protected override async Task<IIdempotencyStore> CreateStoreAsync()
    {
        var provider = await EfCoreStoreServices.IdempotencyAsync<TestDbContext>(o => o.UseSqlite(_database.ConnectionString), Time, Retention);
        _providers.Add(provider);
        return provider.GetRequiredService<IIdempotencyStore>();
    }

    [Theory(DisplayName = "EF idempotency store: a key or fingerprint longer than its column is refused with the bound named, before the database is touched")]
    [InlineData(IdempotencyEntityConfiguration.KeyMaxLength + 1, 64, "*450*")]
    [InlineData(IdempotencyEntityConfiguration.KeyMaxLength, IdempotencyEntityConfiguration.FingerprintMaxLength + 1, "*128*")]
    public async Task Over_long_values_are_refused_with_the_bound_named(int keyLength, int fingerprintLength, string message)
    {
        var store = await CreateStoreAsync();

        var act = () => store.TryClaimAsync(new string('k', keyLength), new string('f', fingerprintLength), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        (await store.TryClaimAsync(new string('k', IdempotencyEntityConfiguration.KeyMaxLength), new string('f', IdempotencyEntityConfiguration.FingerprintMaxLength), CancellationToken.None))
            .IsClaimed.Should().BeTrue("values at the bounds fit");
    }

    [Fact(DisplayName = "EF idempotency store: a claim never deletes expired keys; that is the retention service's work, off the request path")]
    public async Task A_claim_leaves_expired_keys_to_the_retention_service()
    {
        var store = await CreateStoreAsync();
        (await store.TryClaimAsync("expired", null, CancellationToken.None)).IsClaimed.Should().BeTrue();
        Time.Advance(Retention + TimeSpan.FromHours(2));

        (await store.TryClaimAsync("fresh", null, CancellationToken.None)).IsClaimed.Should().BeTrue();

        await using var context = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_database.ConnectionString).Options);
        (await context.Set<IdempotencyEntity>().Select(e => e.Key).ToListAsync(TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(["expired", "fresh"]);
    }

    /// <summary>A minimal context that maps only the idempotency entity via the shipped configuration.</summary>
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new IdempotencyEntityConfiguration());
    }
}
