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
///     What the EF Core outbox store has to get right beyond the shared contract suite: every operation under a claim is
///     one conditional UPDATE (so a lost claim is an UPDATE that matches no row, never a failed save), claims are
///     refused inside a transaction, and dead letters that carry no failure time are treated as the oldest.
/// </summary>
public sealed class EfCoreOutboxStoreOperationTests : IAsyncDisposable
{
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

    private readonly SharedSqliteDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly List<OperationDbContext> _contexts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts) await context.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact(DisplayName = "Every operation under a claim is one conditional UPDATE, with no read before it")]
    public async Task Every_operation_under_a_claim_is_one_update()
    {
        var commands = new CommandRecorder();
        var store = await StoreAsync(commands);
        await store.StoreAsync([Message(), Message(), Message(), Message(), Message()], CancellationToken.None);
        var claims = (await store.ClaimPendingAsync(10, CancellationToken.None)).Select(c => c.Claim).ToList();

        var renewed = await OneUpdateAsync(commands, () => store.RenewAsync(claims[0], CancellationToken.None));
        renewed.Should().NotBeNull();
        (await OneUpdateAsync(commands, () => store.MarkAsProcessedAsync(renewed!.Value, CancellationToken.None))).Should().BeTrue();
        (await OneUpdateAsync(commands, () => store.IncrementAttemptAsync(claims[1], "boom", _time.GetUtcNow().UtcDateTime, CancellationToken.None))).Should().Be(1);
        (await OneUpdateAsync(commands, () => store.DeferAsync(claims[2], _time.GetUtcNow().UtcDateTime, "unknown here", CancellationToken.None))).Should().BeTrue();
        (await OneUpdateAsync(commands, () => store.MarkAsFailedAsync(claims[3], "poison", CancellationToken.None))).Should().BeTrue();
        await OneUpdateAsync(commands, async () =>
        {
            await store.ReleaseAsync([claims[4]], CancellationToken.None);
            return true;
        });
    }

    [Fact(DisplayName = "A claim lost to another processor is reported by every operation - false, 0 or null - and never thrown, with a single claim attempt configured")]
    public async Task A_lost_claim_is_reported_not_thrown()
    {
        var first = await StoreAsync(maxClaimAttempts: 1);
        var second = await StoreAsync(maxClaimAttempts: 1);
        await first.StoreAsync([Message()], CancellationToken.None);
        var lost = (await first.ClaimPendingAsync(10, CancellationToken.None)).Single().Claim;
        _time.Advance(VisibilityTimeout + TimeSpan.FromSeconds(1));
        var current = (await second.ClaimPendingAsync(10, CancellationToken.None)).Single().Claim;

        (await first.RenewAsync(lost, CancellationToken.None)).Should().BeNull();
        (await first.MarkAsProcessedAsync(lost, CancellationToken.None)).Should().BeFalse();
        (await first.IncrementAttemptAsync(lost, "late", null, CancellationToken.None)).Should().Be(0);
        (await first.DeferAsync(lost, _time.GetUtcNow().UtcDateTime, "late", CancellationToken.None)).Should().BeFalse();
        (await first.MarkAsFailedAsync(lost, "late", CancellationToken.None)).Should().BeFalse();
        await first.ReleaseAsync([lost], CancellationToken.None);

        (await second.MarkAsProcessedAsync(current, CancellationToken.None)).Should().BeTrue("none of the stale operations touched the message another processor holds");
    }

    [Fact(DisplayName = "A claim token whose attempt count does not match the row changes nothing")]
    public async Task A_token_with_another_attempt_count_changes_nothing()
    {
        var store = await StoreAsync();
        await store.StoreAsync([Message()], CancellationToken.None);
        var claim = (await store.ClaimPendingAsync(10, CancellationToken.None)).Single().Claim;
        var version = claim.Token[..claim.Token.IndexOf('.')];

        (await store.IncrementAttemptAsync(claim with { Token = $"{version}.7" }, "forged", null, CancellationToken.None)).Should().Be(0);
        (await store.IncrementAttemptAsync(claim, "real", null, CancellationToken.None)).Should().Be(1);
    }

    [Fact(DisplayName = "Claiming inside a transaction on the store's context is refused")]
    public async Task Claiming_inside_a_transaction_is_refused()
    {
        var context = await ContextAsync();
        var store = Store(context);
        await store.StoreAsync([Message()], CancellationToken.None);
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var claim = () => store.ClaimPendingAsync(10, CancellationToken.None);

        (await claim.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*inside a transaction*");
    }

    [Fact(DisplayName = "A dead letter without a failure time (dead-lettered before the column existed) is listed first and purged by any cut-off")]
    public async Task A_dead_letter_without_a_failure_time_counts_as_the_oldest()
    {
        var context = await ContextAsync();
        var store = Store(context);
        var now = _time.GetUtcNow().UtcDateTime;
        var legacy = new OutboxEntity
        {
            Id = Guid.NewGuid(), NotificationType = "legacy.event", HandlerName = string.Empty, Payload = [1],
            CreatedAt = now.AddDays(-30), Status = OutboxMessageStatus.Failed, AttemptCount = 5, LastError = "4.x"
        };
        context.Add(legacy);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        var recent = Message();
        await store.StoreAsync([recent], CancellationToken.None);
        (await store.MarkAsFailedAsync((await store.ClaimPendingAsync(10, CancellationToken.None)).Single().Claim, "poison", CancellationToken.None)).Should().BeTrue();

        (await store.GetDeadLettersAsync(10, CancellationToken.None)).Select(m => m.Id).Should().Equal([legacy.Id, recent.Id]);

        (await store.PurgeDeadLettersAsync(now.AddDays(-1), CancellationToken.None)).Should().Be(1, "only the dead letter without a failure time is older than the cut-off");
        (await store.GetDeadLettersAsync(10, CancellationToken.None)).Select(m => m.Id).Should().Equal([recent.Id]);
    }

    private static async Task<T> OneUpdateAsync<T>(CommandRecorder commands, Func<Task<T>> operation)
    {
        commands.Clear();
        var result = await operation();
        commands.Commands.Should().ContainSingle().Which.Should().StartWith("UPDATE");
        return result;
    }

    private async Task<OperationDbContext> ContextAsync(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<OperationDbContext>().UseSqlite(_database.ConnectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var context = new OperationDbContext(options.Options);
        _contexts.Add(context);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private async Task<EfCoreOutboxStore<OperationDbContext>> StoreAsync(IInterceptor? interceptor = null, int maxClaimAttempts = 3)
        => Store(await ContextAsync(interceptor), maxClaimAttempts);

    private EfCoreOutboxStore<OperationDbContext> Store(OperationDbContext context, int maxClaimAttempts = 3)
        => new(
            context,
            _time,
            Options.Create(new EfCoreOutboxStoreOptions { VisibilityTimeout = VisibilityTimeout, MaxClaimAttempts = maxClaimAttempts }),
            NullLogger<EfCoreOutboxStore<OperationDbContext>>.Instance);

    private OutboxMessage Message()
        => new(Guid.NewGuid(), "tests.operation", "Tests.Handler", [1], _time.GetUtcNow().UtcDateTime, OutboxMessageStatus.Pending, null, null);

    private sealed class OperationDbContext(DbContextOptions<OperationDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCqrsOutbox();
    }
}
