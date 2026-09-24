using System.Data;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;

/// <summary>
///     <see cref="EfCoreUnitOfWork{TContext}" /> on a real PostgreSQL and a real SQL Server: isolation levels reach the
///     provider's transaction, the EF Core outbox and inbox stores over the same context join it (their writes commit and
///     roll back with it), and a retrying execution strategy is refused before it can reject every query.
/// </summary>
public abstract class ProviderUnitOfWorkTests(RelationalProviderFixture fixture)
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Options for the provider with its built-in retrying execution strategy switched on.</summary>
    protected abstract DbContextOptions<ProviderTestDbContext> RetryingOptions(string connectionString);

    private async Task<ProviderTestDbContext> ContextAsync()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await fixture.ResetAsync();
        return new ProviderTestDbContext(fixture.CreateOptions());
    }

    private EfCoreOutboxStore<ProviderTestDbContext> OutboxStore(ProviderTestDbContext context)
        => new(context, _time, Options.Create(new EfCoreOutboxStoreOptions()), NullLogger<EfCoreOutboxStore<ProviderTestDbContext>>.Instance);

    private OutboxMessage Message()
        => new(Guid.NewGuid(), "tests.provider.uow", "Tests.Handler", [1], _time.GetUtcNow().UtcDateTime, OutboxMessageStatus.Pending, null, null);

    private async Task<int> StoredCountAsync()
    {
        await using var check = new ProviderTestDbContext(fixture.CreateOptions());
        return await check.Set<OutboxEntity>().CountAsync();
    }

    [Theory(DisplayName = "The isolation level reaches the provider's transaction")]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Isolation_level_reaches_the_transaction(IsolationLevel level)
    {
        await using var context = await ContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<ProviderTestDbContext>(context);

        await unitOfWork.BeginTransactionAsync(level, CancellationToken.None);

        context.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel.Should().Be(level);
        await unitOfWork.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "The outbox store over the unit of work's context joins its transaction: stored messages commit with it")]
    public async Task Joined_outbox_commits_with_the_transaction()
    {
        await using var context = await ContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<ProviderTestDbContext>(context);
        var store = OutboxStore(context);

        store.JoinsUnitOfWork.Should().BeFalse("no transaction is open yet");
        await unitOfWork.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        store.JoinsUnitOfWork.Should().BeTrue();
        await store.StoreAsync([Message()], CancellationToken.None);
        await unitOfWork.CommitAsync(CancellationToken.None);

        (await StoredCountAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "The outbox store over the unit of work's context joins its transaction: stored messages roll back with it")]
    public async Task Joined_outbox_rolls_back_with_the_transaction()
    {
        await using var context = await ContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<ProviderTestDbContext>(context);
        var store = OutboxStore(context);

        await unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, CancellationToken.None);
        await store.StoreAsync([Message()], CancellationToken.None);
        await unitOfWork.RollbackAsync(CancellationToken.None);

        (await StoredCountAsync()).Should().Be(0);
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact(DisplayName = "The inbox over the unit of work's context joins its transaction: a rolled-back delivery is not recorded")]
    public async Task Joined_inbox_rolls_back_with_the_transaction()
    {
        await using var context = await ContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<ProviderTestDbContext>(context);
        var inbox = new EfCoreInboxStore<ProviderTestDbContext>(context, _time, Options.Create(new EfCoreOutboxStoreOptions()));
        var messageId = Guid.NewGuid();

        await unitOfWork.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        inbox.JoinsUnitOfWork.Should().BeTrue();
        (await inbox.RecordDeliveryAsync(messageId, "Tests.Handler", CancellationToken.None)).Should().BeTrue();
        await unitOfWork.RollbackAsync(CancellationToken.None);

        await using var check = new ProviderTestDbContext(fixture.CreateOptions());
        var fresh = new EfCoreInboxStore<ProviderTestDbContext>(check, _time, Options.Create(new EfCoreOutboxStoreOptions()));
        (await fresh.IsDeliveredAsync(messageId, "Tests.Handler", CancellationToken.None)).Should().BeFalse();
    }

    [Fact(DisplayName = "A context configured with the provider's retry-on-failure strategy is refused with a clear message")]
    public async Task Retrying_execution_strategy_is_refused()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await using var context = new ProviderTestDbContext(RetryingOptions(fixture.ConnectionString));
        var unitOfWork = new EfCoreUnitOfWork<ProviderTestDbContext>(context);

        var act = () => unitOfWork.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("EnableRetryOnFailure");
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlUnitOfWorkTests(PostgreSqlFixture fixture) : ProviderUnitOfWorkTests(fixture)
{
    protected override DbContextOptions<ProviderTestDbContext> RetryingOptions(string connectionString)
        => new DbContextOptionsBuilder<ProviderTestDbContext>().UseNpgsql(connectionString, o => o.EnableRetryOnFailure()).Options;
}

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerUnitOfWorkTests(SqlServerFixture fixture) : ProviderUnitOfWorkTests(fixture)
{
    protected override DbContextOptions<ProviderTestDbContext> RetryingOptions(string connectionString)
        => new DbContextOptionsBuilder<ProviderTestDbContext>().UseSqlServer(connectionString, o => o.EnableRetryOnFailure()).Options;
}
