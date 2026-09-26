using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     The EF Core outbox store's retention, run by its hosted service: processed messages, dead letters and inbox
///     records are deleted once past their retention and never before, in bounded pages, when the host starts and then
///     every purge interval - and never by a claim.
/// </summary>
public sealed class EfCoreOutboxRetentionTests : IAsyncDisposable
{
    private readonly SharedSqliteDatabase _database = new();
    private readonly RecordingTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly CommandRecorder _commands = new();
    private ServiceProvider? _provider;

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact(DisplayName = "Processed messages older than ProcessedRetention are purged; younger processed messages and dead letters are kept, and a claim purges nothing")]
    public async Task Processed_messages_are_purged_only_past_their_retention()
    {
        var retention = await BuildAsync(o => o.ProcessedRetention = TimeSpan.FromDays(7));
        var old = Message();
        var deadLettered = Message();
        await WithStoreAsync(async store =>
        {
            await store.StoreAsync([old, deadLettered], CancellationToken.None);
            var claims = (await store.ClaimPendingAsync(10, CancellationToken.None)).ToDictionary(c => c.Message.Id, c => c.Claim);
            (await store.MarkAsProcessedAsync(claims[old.Id], CancellationToken.None)).Should().BeTrue();
            (await store.MarkAsFailedAsync(claims[deadLettered.Id], "poison", CancellationToken.None)).Should().BeTrue();
        });

        _time.Advance(TimeSpan.FromDays(4));
        var recent = Message();
        await WithStoreAsync(async store =>
        {
            await store.StoreAsync([recent], CancellationToken.None);
            (await store.MarkAsProcessedAsync((await store.ClaimPendingAsync(10, CancellationToken.None)).Single().Claim, CancellationToken.None)).Should().BeTrue();
        });

        // Eight days after the first, four after the second: one processed message is past the retention, one is not.
        _time.Advance(TimeSpan.FromDays(4));
        await WithStoreAsync(async store => (await store.ClaimPendingAsync(10, CancellationToken.None)).Should().BeEmpty());
        (await RemainingAsync()).Should().BeEquivalentTo([old.Id, deadLettered.Id, recent.Id], "a claim never purges");

        await retention.PurgeAsync(CancellationToken.None);

        (await RemainingAsync()).Should().BeEquivalentTo([deadLettered.Id, recent.Id]);
    }

    [Fact(DisplayName = "Dead letters older than DeadLetterRetention are purged, as are dead letters without a failure time; younger ones and undelivered messages are kept")]
    public async Task Dead_letters_are_purged_past_their_retention()
    {
        var retention = await BuildAsync(o => o.DeadLetterRetention = TimeSpan.FromDays(3));
        var old = Message();
        await DeadLetterAsync(old);
        await LegacyDeadLetterAsync();

        _time.Advance(TimeSpan.FromDays(2));
        var recent = Message();
        await DeadLetterAsync(recent);
        var pending = Message();
        await WithStoreAsync(store => store.StoreAsync([pending], CancellationToken.None));

        _time.Advance(TimeSpan.FromDays(2));
        await retention.PurgeAsync(CancellationToken.None);

        (await RemainingAsync()).Should().BeEquivalentTo([recent.Id, pending.Id], "the dead letter that failed four days ago and the one with no failure time are past a three-day retention");
    }

    [Fact(DisplayName = "Inbox records older than InboxRetention are purged; younger ones are kept")]
    public async Task Inbox_records_are_purged_past_their_retention()
    {
        var retention = await BuildAsync(o => o.InboxRetention = TimeSpan.FromDays(1));
        var old = Guid.NewGuid();
        await RecordAsync(old);
        _time.Advance(TimeSpan.FromHours(36));
        var recent = Guid.NewGuid();
        await RecordAsync(recent);

        _time.Advance(TimeSpan.FromHours(12));
        await retention.PurgeAsync(CancellationToken.None);

        await using var context = Context();
        (await context.Set<InboxEntity>().Select(e => e.MessageId).ToListAsync(TestContext.Current.CancellationToken)).Should().Equal([recent]);
    }

    [Fact(DisplayName = "A purge deletes in bounded pages, one short statement at a time, until a page comes back short")]
    public async Task A_purge_deletes_in_bounded_pages()
    {
        var retention = await BuildAsync(o => o.ProcessedRetention = TimeSpan.FromDays(1));
        var processedAt = _time.GetUtcNow().UtcDateTime;
        await using (var context = Context())
        {
            context.AddRange(Enumerable.Range(0, (2 * PagedDelete.PageSize) + 50).Select(_ => ProcessedRow(processedAt)));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _time.Advance(TimeSpan.FromDays(2));
        _commands.Clear();
        await retention.PurgeAsync(CancellationToken.None);

        _commands.NonQueries.Where(c => c.Sql.StartsWith("DELETE", StringComparison.Ordinal) && c.Sql.Contains("CqrsOutboxMessages", StringComparison.Ordinal))
            .Select(c => c.Rows).Should().Equal([PagedDelete.PageSize, PagedDelete.PageSize, 50]);
        (await RemainingAsync()).Should().BeEmpty();
    }

    [Fact(DisplayName = "The retention service purges when the host starts, then again every PurgeInterval")]
    public async Task The_service_purges_at_start_and_every_interval()
    {
        var retention = await BuildAsync(o =>
        {
            o.ProcessedRetention = TimeSpan.FromDays(1);
            o.PurgeInterval = TimeSpan.FromMinutes(30);
        });
        var processedAt = _time.GetUtcNow().UtcDateTime;
        _time.Advance(TimeSpan.FromDays(2));
        var first = await SeedProcessedAsync(processedAt);

        // The service waits on the clock once a purge is over, so each timer it creates marks the end of one purge.
        await retention.StartAsync(CancellationToken.None);
        try
        {
            await _time.TimersCreatedAsync(1);
            (await RemainingAsync()).Should().NotContain(first, "the purge at start deleted the old message");

            var second = await SeedProcessedAsync(processedAt);
            _time.Advance(TimeSpan.FromMinutes(30));
            await _time.TimersCreatedAsync(2);
            (await RemainingAsync()).Should().NotContain(second, "the purge an interval later deleted the next old message");
            _time.TimerDueTimes.Should().Equal([TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)], "the service waits PurgeInterval between purges");
        }
        finally
        {
            await retention.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "The retention service does nothing while the outbox is off")]
    public async Task The_service_is_idle_while_the_outbox_is_off()
    {
        var retention = await BuildAsync(o => o.ProcessedRetention = TimeSpan.FromDays(1), OutboxMode.Disabled);
        var processedAt = _time.GetUtcNow().UtcDateTime;
        _time.Advance(TimeSpan.FromDays(2));
        var old = await SeedProcessedAsync(processedAt);

        await retention.StartAsync(CancellationToken.None);
        await retention.ExecuteTask!;

        (await RemainingAsync()).Should().Equal([old]);
    }

    [Fact(DisplayName = "The retention service leaves the tables alone once another outbox store replaced the EF Core one")]
    public async Task The_service_is_idle_once_another_store_replaced_it()
    {
        var retention = await BuildAsync(o => o.ProcessedRetention = TimeSpan.FromDays(1), then: s => s.AddInMemoryOutboxStore());
        var processedAt = _time.GetUtcNow().UtcDateTime;
        _time.Advance(TimeSpan.FromDays(2));
        var old = await SeedProcessedAsync(processedAt);

        await retention.StartAsync(CancellationToken.None);
        await retention.ExecuteTask!;

        (await RemainingAsync()).Should().Equal([old]);
    }

    private async Task<EfCoreOutboxRetention<RetentionDbContext>> BuildAsync(
        Action<EfCoreOutboxStoreOptions> configure,
        OutboxMode mode = OutboxMode.Enabled,
        Action<IServiceCollection>? then = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<RetentionDbContext>(o => o.UseSqlite(_database.ConnectionString).AddInterceptors(_commands));
        services.Configure<OutboxOptions>(o => o.Mode = mode);
        services.AddEntityFrameworkCoreOutboxStore<RetentionDbContext>(configure);
        then?.Invoke(services);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await using (var scope = _provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RetentionDbContext>().Database.EnsureCreatedAsync();

        return _provider.GetServices<IHostedService>().OfType<EfCoreOutboxRetention<RetentionDbContext>>().Single();
    }

    private async Task WithStoreAsync(Func<IOutboxStore, Task> use)
    {
        await using var scope = _provider!.CreateAsyncScope();
        await use(scope.ServiceProvider.GetRequiredService<IOutboxStore>());
    }

    private Task DeadLetterAsync(OutboxMessage message) => WithStoreAsync(async store =>
    {
        await store.StoreAsync([message], CancellationToken.None);
        var claim = (await store.ClaimPendingAsync(10, CancellationToken.None)).Single(c => c.Message.Id == message.Id).Claim;
        (await store.MarkAsFailedAsync(claim, "poison", CancellationToken.None)).Should().BeTrue();
    });

    // A dead letter as a 4.x outbox left it: failed, with no failure time and no handler name.
    private async Task LegacyDeadLetterAsync()
    {
        await using var context = Context();
        var row = ProcessedRow(_time.GetUtcNow().UtcDateTime);
        row.Status = OutboxMessageStatus.Failed;
        row.ProcessedAt = null;
        row.HandlerName = string.Empty;
        context.Add(row);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task RecordAsync(Guid messageId)
    {
        await using var scope = _provider!.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IInboxStore>().RecordDeliveryAsync(messageId, "Tests.Handler", CancellationToken.None)).Should().BeTrue();
    }

    private async Task<Guid> SeedProcessedAsync(DateTime processedAt)
    {
        await using var context = Context();
        var row = ProcessedRow(processedAt);
        context.Add(row);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return row.Id;
    }

    private async Task<List<Guid>> RemainingAsync()
    {
        await using var context = Context();
        return await context.Set<OutboxEntity>().Select(e => e.Id).ToListAsync();
    }

    private RetentionDbContext Context() => new(new DbContextOptionsBuilder<RetentionDbContext>().UseSqlite(_database.ConnectionString).Options);

    private OutboxMessage Message()
        => new(Guid.NewGuid(), "tests.retention", "Tests.Handler", [1], _time.GetUtcNow().UtcDateTime, OutboxMessageStatus.Pending, null, null);

    private static OutboxEntity ProcessedRow(DateTime processedAt) => new()
    {
        Id = Guid.NewGuid(), NotificationType = "tests.retention", HandlerName = "Tests.Handler", Payload = [1],
        CreatedAt = processedAt, Status = OutboxMessageStatus.Processed, ProcessedAt = processedAt
    };

    private sealed class RetentionDbContext(DbContextOptions<RetentionDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCqrsOutbox();
    }
}
