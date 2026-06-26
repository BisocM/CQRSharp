using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.EntityFrameworkCore.Persistence;
using CQRSharp.Testing.Idempotency;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     Runs the shared <see cref="IdempotencyStoreContractTests" /> conformance suite against
///     <see cref="EfCoreIdempotencyStore{TContext}" />. Each store is backed by a private, kept-alive in-memory SQLite
///     database so the tests are hermetic (no shared file, no external server) and CI-safe (the connection lives for
///     the store's lifetime, which is how a <c>Filename=:memory:</c> database survives between commands). Retention is
///     set far longer than any test runs so the contract's duplicate/concurrency assertions never trip claim expiry.
/// </summary>
public sealed class EfCoreIdempotencyStoreContractTests : IdempotencyStoreContractTests
{
    // A long retention keeps every claimed key live for the whole test, so the contract suite exercises pure
    // duplicate-rejection and single-winner concurrency rather than expiry/take-over behaviour.
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly FakeTimeProvider _time = new();

    protected override async Task<IIdempotencyStore> CreateStoreAsync()
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

        var storeOptions = new EfCoreIdempotencyStoreOptions { Retention = Retention };

        return new EfCoreIdempotencyStore<TestDbContext>(
            context,
            _time,
            Options.Create(storeOptions),
            NullLogger<EfCoreIdempotencyStore<TestDbContext>>.Instance);
    }

    /// <summary>A minimal context that maps only the idempotency entity via the shipped configuration.</summary>
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new IdempotencyEntityConfiguration());
    }
}
