using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>A store call that fails leaves nothing tracked behind for the next save on the same context to replay.</summary>
public sealed class EfCoreOutboxStoreTrackingTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private TrackingDbContext? _context;

    public async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact(DisplayName = "A batch the mapper rejects leaves no row tracked, so nothing of it is inserted by a later save")]
    public async Task A_rejected_batch_is_not_left_tracked()
    {
        var store = await CreateStoreAsync();
        var ok = Message("h");
        var bad = Message(new string('h', 300));

        var act = () => store.StoreAsync([ok, bad], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _context!.ChangeTracker.Entries().Should().BeEmpty();
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await store.ClaimPendingAsync(10, CancellationToken.None)).Should().BeEmpty("the batch was rejected as a whole");
    }

    [Fact(DisplayName = "A batch the database rejects is detached, and the store keeps working on the same context")]
    public async Task A_rejected_insert_does_not_poison_the_context()
    {
        var store = await CreateStoreAsync();
        var first = Message("h");
        await store.StoreAsync([first], CancellationToken.None);

        var duplicate = () => store.StoreAsync([first with { CreatedAt = first.CreatedAt.AddSeconds(1) }], CancellationToken.None);
        await duplicate.Should().ThrowAsync<DbUpdateException>();

        _context!.ChangeTracker.Entries().Should().BeEmpty();
        await store.StoreAsync([Message("h")], CancellationToken.None);
        (await store.ClaimPendingAsync(10, CancellationToken.None)).Should().HaveCount(2);
    }

    private static OutboxMessage Message(string handlerName)
        => new(Guid.NewGuid(), "n", handlerName, [1], new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), OutboxMessageStatus.Pending, null, null);

    private async Task<EfCoreOutboxStore<TrackingDbContext>> CreateStoreAsync()
    {
        await _connection.OpenAsync();
        _context = new TrackingDbContext(new DbContextOptionsBuilder<TrackingDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();
        return new EfCoreOutboxStore<TrackingDbContext>(
            _context,
            TimeProvider.System,
            Options.Create(new EfCoreOutboxStoreOptions()),
            NullLogger<EfCoreOutboxStore<TrackingDbContext>>.Instance);
    }

    private sealed class TrackingDbContext(DbContextOptions<TrackingDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCqrsOutbox();
    }
}
