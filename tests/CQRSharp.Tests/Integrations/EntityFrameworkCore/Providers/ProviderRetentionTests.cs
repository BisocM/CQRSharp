using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;

/// <summary>
///     The EF Core retention services on a real PostgreSQL and SQL Server: the paged deletes (a bounded, ordered
///     subquery of the rows past retention) translate on the provider, cross page boundaries, and delete exactly the rows
///     past each table's retention.
/// </summary>
public abstract class ProviderRetentionTests(RelationalProviderFixture fixture)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int PastRetention = PagedDelete.PageSize + 50;

    [Fact(DisplayName = "Retention deletes the rows past retention of every CQRSharp table in pages, and keeps the rest")]
    public async Task Retention_deletes_in_pages_and_keeps_the_rest()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await fixture.ResetAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var now = time.GetUtcNow().UtcDateTime;
        var old = now - Retention - TimeSpan.FromDays(1);
        var recent = now - TimeSpan.FromDays(1);

        var recentProcessed = Row(OutboxMessageStatus.Processed, recent);
        var recentDeadLetter = Row(OutboxMessageStatus.Failed, recent);
        var pending = Row(OutboxMessageStatus.Pending, now);
        await using (var context = new ProviderTestDbContext(fixture.CreateOptions()))
        {
            context.AddRange(Enumerable.Range(0, PastRetention).Select(_ => Row(OutboxMessageStatus.Processed, old)));
            context.AddRange(Row(OutboxMessageStatus.Failed, old), LegacyDeadLetter(), recentProcessed, recentDeadLetter, pending);
            context.AddRange(Enumerable.Range(0, PastRetention).Select(_ => new InboxEntity { MessageId = Guid.NewGuid(), HandlerName = "Tests.Handler", DeliveredAt = old }));
            context.Add(new InboxEntity { MessageId = recentProcessed.Id, HandlerName = "Tests.Handler", DeliveredAt = recent });
            context.AddRange(Enumerable.Range(0, PastRetention).Select(i => new IdempotencyEntity { Key = $"expired-{i}", ExpiresAt = now.AddMinutes(-1) }));
            context.Add(new IdempotencyEntity { Key = "live", ExpiresAt = now.AddMinutes(30) });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddDbContext<ProviderTestDbContext>(fixture.Configure);
        services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
        services.AddEntityFrameworkCoreOutboxStore<ProviderTestDbContext>(o =>
        {
            o.ProcessedRetention = Retention;
            o.DeadLetterRetention = Retention;
            o.InboxRetention = Retention;
        });
        services.AddEntityFrameworkCoreIdempotencyStore<ProviderTestDbContext>(o => o.Retention = TimeSpan.FromHours(1));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var hosted = provider.GetServices<IHostedService>().ToList();

        await hosted.OfType<EfCoreOutboxRetention<ProviderTestDbContext>>().Single().PurgeAsync(CancellationToken.None);
        await hosted.OfType<EfCoreIdempotencyRetention<ProviderTestDbContext>>().Single().PurgeAsync(CancellationToken.None);

        await using var check = new ProviderTestDbContext(fixture.CreateOptions());
        (await check.Set<OutboxEntity>().Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo([recentProcessed.Id, recentDeadLetter.Id, pending.Id]);
        (await check.Set<InboxEntity>().Select(e => e.MessageId).ToListAsync(TestContext.Current.CancellationToken)).Should().Equal([recentProcessed.Id]);
        (await check.Set<IdempotencyEntity>().Select(e => e.Key).ToListAsync(TestContext.Current.CancellationToken)).Should().Equal(["live"]);
    }

    // A message in the given status, created - and processed or dead-lettered - at the given time.
    private static OutboxEntity Row(OutboxMessageStatus status, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        NotificationType = "tests.retention",
        HandlerName = "Tests.Handler",
        Payload = [1],
        CreatedAt = at,
        Status = status,
        ProcessedAt = status == OutboxMessageStatus.Processed ? at : null,
        FailedAt = status == OutboxMessageStatus.Failed ? at : null
    };

    // A dead letter as a 4.x outbox left it: no failure time and no handler name.
    private static OutboxEntity LegacyDeadLetter()
    {
        var row = Row(OutboxMessageStatus.Failed, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        row.FailedAt = null;
        row.HandlerName = string.Empty;
        return row;
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlRetentionTests(PostgreSqlFixture fixture) : ProviderRetentionTests(fixture);

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerRetentionTests(SqlServerFixture fixture) : ProviderRetentionTests(fixture);
