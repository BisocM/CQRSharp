using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.EntityFrameworkCore.Persistence;
using CQRSharp.Testing.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     Runs the shared <see cref="OutboxStoreContractTests" /> conformance suite against
///     <see cref="EfCoreOutboxStore{TContext}" />. Each store is backed by a private, kept-alive in-memory SQLite
///     database so the tests are hermetic (no shared file, no external server) and CI-safe (the connection lives for
///     the store's lifetime, which is how a <c>Filename=:memory:</c> database survives between commands).
/// </summary>
public sealed class EfCoreOutboxStoreContractTests : OutboxStoreContractTests
{
    protected override FakeTimeProvider Time { get; } = new();

    protected override async Task<IOutboxStore> CreateStoreAsync()
    {
        // A :memory: SQLite database exists only while a connection to it is open. Open and keep this one for the
        // lifetime of the test so the schema and rows persist across the store's individual operations.
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new TestDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var storeOptions = new EfCoreOutboxStoreOptions { VisibilityTimeout = VisibilityTimeout };

        return new EfCoreOutboxStore<TestDbContext>(
            context,
            Time,
            Options.Create(storeOptions),
            NullLogger<EfCoreOutboxStore<TestDbContext>>.Instance);
    }

    /// <summary>A minimal context that maps only the outbox entity via the shipped configuration.</summary>
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new OutboxEntityConfiguration());
    }
}
