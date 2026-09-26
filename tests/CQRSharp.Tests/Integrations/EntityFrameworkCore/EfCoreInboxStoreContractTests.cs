using System.Data;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Testing;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     Runs the shared <see cref="InboxStoreContractTests" /> suite against <see cref="EfCoreInboxStore{TContext}" /> on
///     in-memory SQLite, plus what only this store has to get right: it writes through the handler's own context, so it
///     saves the handler's tracked changes with its record inside a transaction, and never outside one.
/// </summary>
public sealed class EfCoreInboxStoreContractTests : InboxStoreContractTests
{
    private const string Handler = "Tests.Handler";

    private SqliteConnection? _connection;
    private TestDbContext? _context;

    public override async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    protected override FakeTimeProvider Time { get; } = new();

    protected override async Task<IInboxStore> CreateStoreAsync()
    {
        var connection = _connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var context = _context = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        return new EfCoreInboxStore<TestDbContext>(context, Time, Options.Create(new EfCoreOutboxStoreOptions { InboxRetention = InboxRetention }));
    }

    [Fact(DisplayName = "EF Core inbox: outside a transaction, a record is refused while the context holds changes the handler did not save, and none of them is saved")]
    public async Task Outside_a_transaction_the_record_never_saves_the_handlers_changes()
    {
        var store = await CreateStoreAsync();
        var messageId = Guid.NewGuid();
        _context!.Notes.Add(new Note { Text = "left unsaved by the handler" });

        var record = () => store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None);

        (await record.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{nameof(TestDbContext)}*UseEntityFrameworkCoreUnitOfWork*");
        await using var check = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection!).Options);
        (await check.Notes.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await check.Set<InboxEntity>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact(DisplayName = "EF Core inbox: inside a transaction, a record saves the handler's tracked changes with it, in one commit")]
    public async Task Inside_a_transaction_the_record_commits_with_the_handlers_changes()
    {
        var store = await CreateStoreAsync();
        var unitOfWork = new EfCoreUnitOfWork<TestDbContext>(_context!);
        var messageId = Guid.NewGuid();

        await unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, CancellationToken.None);
        _context!.Notes.Add(new Note { Text = "the handler's change" });
        (await store.RecordDeliveryAsync(messageId, Handler, CancellationToken.None)).Should().BeTrue();
        await unitOfWork.CommitAsync(CancellationToken.None);

        await using var check = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection!).Options);
        (await check.Notes.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await check.Set<InboxEntity>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<Note> Notes => Set<Note>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new InboxEntityConfiguration());
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
        }
    }

    private sealed class Note
    {
        public int Id { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}
