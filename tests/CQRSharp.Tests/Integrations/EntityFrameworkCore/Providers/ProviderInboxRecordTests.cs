using System.Data;
using System.Data.Common;
using CQRSharp.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;

/// <summary>
///     The EF Core inbox inside a delivery transaction on a real database, when another delivery's record commits
///     between this record's look for an existing one and its insert: the insert fails on the key, and the record still
///     reports the duplicate, leaving the transaction usable for the rollback that follows. On PostgreSQL a failed
///     statement aborts the whole transaction unless it ran under a savepoint, so this holds whether or not the
///     application left EF Core's automatic savepoints on.
/// </summary>
public abstract class ProviderInboxRecordTests(RelationalProviderFixture fixture)
{
    private const string Handler = "Tests.Handler";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    [Theory(DisplayName = "A record that loses the insert race inside a transaction reports a duplicate and leaves the transaction usable")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_record_losing_the_insert_race_in_a_transaction_reports_a_duplicate(bool automaticSavepoints)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await fixture.ResetAsync();
        var messageId = Guid.NewGuid();
        var otherDelivery = new BeforeInboxInsert(async () =>
        {
            await using var other = new ProviderTestDbContext(fixture.CreateOptions());
            other.Add(new InboxEntity { MessageId = messageId, HandlerName = Handler, DeliveredAt = _time.GetUtcNow().UtcDateTime });
            await other.SaveChangesAsync();
        });
        await using var context = new ProviderTestDbContext(
            new DbContextOptionsBuilder<ProviderTestDbContext>(fixture.CreateOptions()).AddInterceptors(otherDelivery).Options);
        context.Database.AutoSavepointsEnabled = automaticSavepoints;
        var unitOfWork = new EfCoreUnitOfWork<ProviderTestDbContext>(context);
        var inbox = new EfCoreInboxStore<ProviderTestDbContext>(context, _time, Options.Create(new EfCoreOutboxStoreOptions()));

        await unitOfWork.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var recorded = await inbox.RecordDeliveryAsync(messageId, Handler, CancellationToken.None);

        otherDelivery.Ran.Should().BeTrue("the other delivery's record committed right before this one's insert");
        recorded.Should().BeFalse("another delivery recorded the message first");
        (await context.Set<InboxEntity>().CountAsync(e => e.MessageId == messageId, TestContext.Current.CancellationToken)).Should().Be(1, "the transaction is still usable after the rejected insert");
        await unitOfWork.RollbackAsync(CancellationToken.None);
    }

    /// <summary>Runs <c>interleave</c> once, right before the context sends its INSERT into the inbox table.</summary>
    private sealed class BeforeInboxInsert(Func<Task> interleave) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await InterleaveBeforeInsertAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await InterleaveBeforeInsertAsync(command);
            return result;
        }

        private async Task InterleaveBeforeInsertAsync(DbCommand command)
        {
            if (Ran || !command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase) ||
                !command.CommandText.Contains("CqrsInboxRecords", StringComparison.Ordinal))
                return;

            Ran = true;
            await interleave();
        }
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlInboxRecordTests(PostgreSqlFixture fixture) : ProviderInboxRecordTests(fixture);

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerInboxRecordTests(SqlServerFixture fixture) : ProviderInboxRecordTests(fixture);
