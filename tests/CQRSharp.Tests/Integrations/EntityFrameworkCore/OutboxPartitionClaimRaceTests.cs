using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     Two processors never deliver two messages of one partition at once, even when a message that sorts first in the
///     partition commits between one processor's claim read and its claim write: a message stored late (a producer whose
///     clock runs behind, or whose transaction committed late), or a requeued dead letter. The first processor read the
///     old head and claims it; the second reads after the commit, takes the new head, and sees the old head as merely
///     later. The interleaving is forced: the first processor's claim save pauses while the late message commits and the
///     second processor claims.
/// </summary>
public abstract class OutboxPartitionClaimRaceTests<TContext> : IAsyncDisposable where TContext : DbContext
{
    private const string Partition = "order-7";
    private const string Handler = "Tests.OrderProjection";
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly List<TContext> _contexts = [];

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts) await context.DisposeAsync();
    }

    /// <summary>A fresh context over the test's database, with <paramref name="interceptors" /> added.</summary>
    protected abstract TContext NewContext(params IInterceptor[] interceptors);

    /// <summary>Empties the database, or skips the test when it is not available.</summary>
    protected abstract Task ResetAsync();

    [Fact(DisplayName = "A message stored late at the head of its partition, while another processor claims the old head, is the only one of the partition delivered")]
    public async Task A_late_earlier_message_and_the_old_head_are_never_in_flight_together()
    {
        await ResetAsync();
        var now = _time.GetUtcNow().UtcDateTime;
        var head = Message(now.AddSeconds(5));
        var late = Message(now.AddSeconds(4));
        await Store().StoreAsync([head], CancellationToken.None);

        var (first, second) = await RaceAsync(() => Store().StoreAsync([late], CancellationToken.None));

        second.Should().ContainSingle().Which.Message.Id.Should().Be(late.Id, "the late message is the partition's head once it has committed");
        first.Should().BeEmpty("the old head is held back by the late message the other processor is delivering, so its claim hands it back");
        await AssertOnlyInFlightAsync(late.Id, stillPending: head.Id);
    }

    [Fact(DisplayName = "A dead letter requeued at the head of its partition, while another processor claims the old head, is the only one of the partition delivered")]
    public async Task A_requeued_dead_letter_and_the_old_head_are_never_in_flight_together()
    {
        await ResetAsync();
        var now = _time.GetUtcNow().UtcDateTime;
        var deadLetter = Message(now.AddSeconds(1));
        var setup = Store();
        await setup.StoreAsync([deadLetter], CancellationToken.None);
        var claimed = await setup.ClaimPendingAsync(10, CancellationToken.None);
        (await setup.MarkAsFailedAsync(claimed.Single().Claim, "poison", CancellationToken.None)).Should().BeTrue();
        var head = Message(now.AddSeconds(5));
        await setup.StoreAsync([head], CancellationToken.None);

        var (first, second) = await RaceAsync(async () => (await Store().RequeueAsync(deadLetter.Id, CancellationToken.None)).Should().BeTrue());

        second.Should().ContainSingle().Which.Message.Id.Should().Be(deadLetter.Id, "the requeued dead letter sorts first in its partition");
        first.Should().BeEmpty("the old head is held back by the requeued message the other processor is delivering");
        await AssertOnlyInFlightAsync(deadLetter.Id, stillPending: head.Id);
    }

    // The first processor reads the head and, just before its claim is saved, the interleaving runs: the change that
    // makes another message the head commits, and the second processor claims. Then the first processor's save goes on.
    private async Task<(IReadOnlyList<ClaimedOutboxMessage> First, IReadOnlyList<ClaimedOutboxMessage> Second)> RaceAsync(Func<Task> headChanges)
    {
        IReadOnlyList<ClaimedOutboxMessage> second = [];
        var pause = new BeforeFirstSave(async () =>
        {
            await headChanges();
            second = await Store().ClaimPendingAsync(10, CancellationToken.None);
        });

        var first = await Store(pause).ClaimPendingAsync(10, CancellationToken.None);

        pause.Ran.Should().BeTrue("the first processor's claim save is where the other processor steps in");
        return (first, second);
    }

    private async Task AssertOnlyInFlightAsync(Guid inFlight, Guid stillPending)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var context = NewContext();
        var rows = await context.Set<OutboxEntity>().AsNoTracking().Where(e => e.PartitionKey == Partition).ToListAsync();

        rows.Where(e => e.Status == OutboxMessageStatus.InProgress && e.LockedUntil > now).Select(e => e.Id)
            .Should().Equal([inFlight], "at most one message of a partition is being delivered at any time");
        var pending = rows.Single(e => e.Id == stillPending);
        pending.Status.Should().Be(OutboxMessageStatus.Pending, "the handed-back message waits for its turn");
        pending.AttemptCount.Should().Be(0, "handing a claim back is not a delivery attempt");
    }

    // Every store is a processor of its own: a store over a context over a connection of its own.
    private EfCoreOutboxStore<TContext> Store(params IInterceptor[] interceptors)
    {
        var context = NewContext(interceptors);
        _contexts.Add(context);
        return new EfCoreOutboxStore<TContext>(
            context,
            _time,
            Options.Create(new EfCoreOutboxStoreOptions { VisibilityTimeout = VisibilityTimeout }),
            NullLogger<EfCoreOutboxStore<TContext>>.Instance);
    }

    private static OutboxMessage Message(DateTime createdAt)
        => new(Guid.NewGuid(), "tests.order.changed", Handler, [1], createdAt, OutboxMessageStatus.Pending, null, null, PartitionKey: Partition);

    /// <summary>Runs <c>interleave</c> once, when the context's first save begins, and only then lets the save go on.</summary>
    private sealed class BeforeFirstSave(Func<Task> interleave) : SaveChangesInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Ran)
            {
                Ran = true;
                await interleave();
            }

            return result;
        }
    }
}

/// <summary>The claim race on SQLite: each processor on a connection of its own to one shared in-memory database.</summary>
public sealed class SqliteOutboxPartitionClaimRaceTests : OutboxPartitionClaimRaceTests<PartitionRaceDbContext>
{
    private readonly SharedSqliteDatabase _database = new();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
    }

    protected override PartitionRaceDbContext NewContext(params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<PartitionRaceDbContext>().UseSqlite(_database.ConnectionString).AddInterceptors(interceptors).Options);

    protected override async Task ResetAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureCreatedAsync();
    }
}

public sealed class PartitionRaceDbContext(DbContextOptions<PartitionRaceDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCqrsOutbox();
}
