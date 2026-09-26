using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     The EF Core idempotency store's retention, run by its hosted service rather than by the claims of requests:
///     expired keys are deleted in bounded pages, live ones are kept.
/// </summary>
public sealed class EfCoreIdempotencyRetentionTests : IAsyncDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly SharedSqliteDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly CommandRecorder _commands = new();
    private ServiceProvider? _provider;

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact(DisplayName = "Expired idempotency keys are purged in bounded pages; live keys are kept")]
    public async Task Expired_keys_are_purged_in_pages()
    {
        var retention = await BuildAsync();
        var now = _time.GetUtcNow().UtcDateTime;
        await using (var context = Context())
        {
            context.AddRange(Enumerable.Range(0, PagedDelete.PageSize + 50).Select(i => new IdempotencyEntity { Key = $"expired-{i}", ExpiresAt = now.AddMinutes(-1) }));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var store = _provider!.GetRequiredService<IIdempotencyStore>();
        (await store.TryClaimAsync("live", null, CancellationToken.None)).IsClaimed.Should().BeTrue();

        _commands.Clear();
        await retention.PurgeAsync(CancellationToken.None);

        _commands.NonQueries.Where(c => c.Sql.StartsWith("DELETE", StringComparison.Ordinal)).Select(c => c.Rows)
            .Should().Equal([PagedDelete.PageSize, 50]);
        await using (var context = Context())
            (await context.Set<IdempotencyEntity>().Select(e => e.Key).ToListAsync(TestContext.Current.CancellationToken)).Should().Equal(["live"]);
    }

    [Fact(DisplayName = "The idempotency retention service leaves the table alone once another store replaced the EF Core one")]
    public async Task The_service_is_idle_once_another_store_replaced_it()
    {
        var retention = await BuildAsync(s => s.AddInMemoryIdempotencyStore());
        await using (var context = Context())
        {
            context.Add(new IdempotencyEntity { Key = "expired", ExpiresAt = _time.GetUtcNow().UtcDateTime.AddMinutes(-1) });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await retention.StartAsync(CancellationToken.None);
        await retention.ExecuteTask!;

        await using (var context = Context())
            (await context.Set<IdempotencyEntity>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    private async Task<EfCoreIdempotencyRetention<RetentionDbContext>> BuildAsync(Action<IServiceCollection>? then = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<RetentionDbContext>(o => o.UseSqlite(_database.ConnectionString).AddInterceptors(_commands));
        services.AddEntityFrameworkCoreIdempotencyStore<RetentionDbContext>(o => o.Retention = Retention);
        then?.Invoke(services);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await using (var scope = _provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RetentionDbContext>().Database.EnsureCreatedAsync();

        return _provider.GetServices<IHostedService>().OfType<EfCoreIdempotencyRetention<RetentionDbContext>>().Single();
    }

    private RetentionDbContext Context() => new(new DbContextOptionsBuilder<RetentionDbContext>().UseSqlite(_database.ConnectionString).Options);

    private sealed class RetentionDbContext(DbContextOptions<RetentionDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCqrsIdempotency();
    }
}
