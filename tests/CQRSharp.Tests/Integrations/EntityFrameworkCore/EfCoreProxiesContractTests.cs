using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

// The CQRSharp tables in an application context that uses EF Core's proxies. Proxies are switched on for the whole
// model, so the CQRSharp entity types must be unsealed (both kinds), have virtual properties and raise change
// notifications (change-tracking proxies, which also track the rows the stores create with new, not as proxies).
// Each shared contract suite runs against the stores over such a context.

/// <summary>A context over the CQRSharp tables alone, configured by the test with the proxies it uses.</summary>
public sealed class ProxiedDbContext(DbContextOptions<ProxiedDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyCqrsOutbox();
        modelBuilder.ApplyCqrsIdempotency();
    }
}

public abstract class ProxiedOutboxStoreContractTests : OutboxStoreContractTests
{
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private ProxiedDbContext? _context;

    public override async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    protected override FakeTimeProvider Time { get; } = new();

    protected abstract void UseProxies(DbContextOptionsBuilder builder);

    protected override async Task<IOutboxStore> CreateStoreAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProxiedDbContext>().UseSqlite(_connection);
        UseProxies(options);
        _context = new ProxiedDbContext(options.Options);
        await _context.Database.EnsureCreatedAsync();

        return new EfCoreOutboxStore<ProxiedDbContext>(
            _context,
            Time,
            Options.Create(new EfCoreOutboxStoreOptions { VisibilityTimeout = VisibilityTimeout }),
            NullLogger<EfCoreOutboxStore<ProxiedDbContext>>.Instance);
    }
}

public sealed class ChangeTrackingProxiesOutboxStoreContractTests : ProxiedOutboxStoreContractTests
{
    protected override void UseProxies(DbContextOptionsBuilder builder) => builder.UseChangeTrackingProxies();
}

public sealed class LazyLoadingProxiesOutboxStoreContractTests : ProxiedOutboxStoreContractTests
{
    protected override void UseProxies(DbContextOptionsBuilder builder) => builder.UseLazyLoadingProxies();
}

public sealed class ChangeTrackingProxiesInboxStoreContractTests : InboxStoreContractTests
{
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private ProxiedDbContext? _context;

    public override async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    protected override FakeTimeProvider Time { get; } = new();

    protected override async Task<IInboxStore> CreateStoreAsync()
    {
        await _connection.OpenAsync();
        _context = new ProxiedDbContext(new DbContextOptionsBuilder<ProxiedDbContext>().UseSqlite(_connection).UseChangeTrackingProxies().Options);
        await _context.Database.EnsureCreatedAsync();

        return new EfCoreInboxStore<ProxiedDbContext>(_context, Time, Options.Create(new EfCoreOutboxStoreOptions { InboxRetention = InboxRetention }));
    }
}

public sealed class ChangeTrackingProxiesIdempotencyStoreContractTests : IdempotencyStoreContractTests
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
        var provider = await EfCoreStoreServices.IdempotencyAsync<ProxiedDbContext>(
            o => o.UseSqlite(_database.ConnectionString).UseChangeTrackingProxies(), Time, Retention);
        _providers.Add(provider);
        return provider.GetRequiredService<IIdempotencyStore>();
    }
}
