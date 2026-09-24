using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Tests.Core;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     Delivery through the EF Core inbox end to end on a relational store: the EF Core outbox and inbox, the real
///     processor, and — for exactly-once — the EF Core unit of work over the same <c>DbContext</c>. With the unit of
///     work, the handler's rows and the inbox record are one commit, a failing handler leaves neither (even rows it
///     already saved), a delivery that loses the record to another delivery takes its handler's saved rows back with it,
///     and a redelivery of a recorded message never runs the handler again. Without it, the handler saves its own rows
///     and the record follows them; the inbox never saves on the handler's behalf.
/// </summary>
public sealed class TransactionalInboxDeliveryTests : IAsyncDisposable
{
    private readonly SqliteFileDatabase _database = new();

    // The processor waits and backs off on this clock, so a retry happens only when a test moves it: what a failed
    // attempt left behind can be inspected before the retry runs.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private async Task<ServiceProvider> BuildAsync(bool unitOfWork = true, Func<IServiceProvider, IInboxStore, IInboxStore>? decorateInbox = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<AuditProbe>();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_database.ConnectionString));
        services.AddCqrsGenerated(b =>
        {
            b.UseOutbox(o => o
                .Enabled()
                .UseEntityFrameworkCore<AuditDbContext>()
                .ConfigureProcessor(p =>
                {
                    p.PollingInterval = TimeSpan.FromMilliseconds(25);
                    p.Retry.BaseDelay = RetryDelay;
                    p.Retry.JitterFactor = 0;
                }));
            if (unitOfWork)
                b.UseEntityFrameworkCoreUnitOfWork<AuditDbContext>();
        });
        if (decorateInbox is not null)
        {
            services.RemoveAll<IInboxStore>();
            services.AddScoped(sp => decorateInbox(sp, EfCoreInbox(sp)));
        }

        OutboxDrain.Observe(services);
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.EnsureCreatedAsync();

        return provider;
    }

    // The EF Core inbox of a scope, as AddEntityFrameworkCoreOutboxStore registers it.
    private static IInboxStore EfCoreInbox(IServiceProvider scope)
        => new EfCoreInboxStore<AuditDbContext>(
            scope.GetRequiredService<AuditDbContext>(),
            scope.GetRequiredService<TimeProvider>(),
            scope.GetRequiredService<IOptions<EfCoreOutboxStoreOptions>>());

    private static async Task<T> QueryAsync<T>(IServiceProvider provider, Func<AuditDbContext, Task<T>> query)
    {
        await using var scope = provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuditDbContext>());
    }

    [Fact(DisplayName = "The handler's rows and the inbox record commit together; the message is marked processed")]
    public async Task Handler_changes_and_the_inbox_record_are_one_commit()
    {
        await using var provider = await BuildAsync();
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(provider, new AuditedEvent("first"));
            await DrainAsync(provider);
            (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).Status.Should().Be(OutboxMessageStatus.Processed, "the message was processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(1, "the handler's row was committed by the delivery transaction");
        (await QueryAsync(provider, c => c.Set<InboxEntity>().CountAsync())).Should().Be(1, "the delivery was recorded in the same commit");
        provider.GetRequiredService<AuditProbe>().Runs.Should().Be(1);
    }

    [Fact(DisplayName = "A failing handler leaves neither its rows - even those it saved - nor an inbox record, and the message is retried")]
    public async Task A_failing_handler_is_rolled_back_entirely()
    {
        await using var provider = await BuildAsync();
        provider.GetRequiredService<AuditProbe>().FailFirstAttempt = true;
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(provider, new AuditedEvent("second"));
            await DrainAsync(provider);
            var attempted = await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync());
            attempted.AttemptCount.Should().Be(1, "the first attempt was recorded");
            attempted.Status.Should().Be(OutboxMessageStatus.Pending, "the message waits for its retry");

            (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(0, "the failed attempt's row, saved inside the delivery transaction, was rolled back");
            (await QueryAsync(provider, c => c.Set<InboxEntity>().CountAsync())).Should().Be(0, "nothing was recorded for a failed attempt");

            // The retry is due once its back-off has passed, which also ends the processor's polling wait.
            _time.Advance(RetryDelay);
            await DrainAsync(provider);
            (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).Status.Should().Be(OutboxMessageStatus.Processed, "the retry succeeded");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(1);
        provider.GetRequiredService<AuditProbe>().Runs.Should().Be(2);
    }

    [Fact(DisplayName = "A delivery that finds its record written by another delivery at record time rolls its handler's saved rows back, and the message is processed as a duplicate")]
    public async Task A_duplicate_caught_at_record_time_takes_the_handlers_rows_back()
    {
        // Another delivery of the same message (a processor that took over an expired lease) records it after this
        // delivery's inbox check passed and before its handler finishes: committed before this delivery's transaction
        // begins, as it would be on a database that serializes writers.
        await using var provider = await BuildAsync(decorateInbox: (scope, inbox) => new InboxRecordedByAnotherDelivery(inbox, scope));
        provider.GetRequiredService<AuditProbe>().SaveItself = true;
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(provider, new AuditedEvent("raced"));
            await DrainAsync(provider);
            (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).Status.Should().Be(OutboxMessageStatus.Processed, "the message was processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        provider.GetRequiredService<AuditProbe>().Runs.Should().Be(1);
        (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(0, "the losing delivery's saved row was rolled back with its transaction");
        (await QueryAsync(provider, c => c.Set<InboxEntity>().CountAsync())).Should().Be(1, "only the winning delivery's record exists");
        (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).AttemptCount.Should().Be(0, "a duplicate is not a failed attempt");
    }

    [Fact(DisplayName = "A record that fails inside the delivery transaction rolls the handler's saved rows back, and the message is retried")]
    public async Task A_failed_record_fails_the_attempt()
    {
        await using var provider = await BuildAsync(decorateInbox: (scope, inbox) => new InboxFailingItsFirstRecord(inbox, scope.GetRequiredService<AuditProbe>()));
        provider.GetRequiredService<AuditProbe>().SaveItself = true;
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(provider, new AuditedEvent("unrecorded"));
            await DrainAsync(provider);
            var attempted = await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync());
            attempted.AttemptCount.Should().Be(1, "the failed attempt was recorded");
            attempted.Status.Should().Be(OutboxMessageStatus.Pending, "the message waits for its retry");

            (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(0, "the attempt's saved row was rolled back with the record that failed");
            (await QueryAsync(provider, c => c.Set<InboxEntity>().CountAsync())).Should().Be(0);

            _time.Advance(RetryDelay);
            await DrainAsync(provider);
            (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).Status.Should().Be(OutboxMessageStatus.Processed, "the retry succeeded");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(1);
        (await QueryAsync(provider, c => c.Set<InboxEntity>().CountAsync())).Should().Be(1);
        provider.GetRequiredService<AuditProbe>().Runs.Should().Be(2);
    }

    [Fact(DisplayName = "A message whose delivery is already recorded is skipped, not run again")]
    public async Task A_recorded_delivery_is_skipped()
    {
        await using var provider = await BuildAsync();
        await PublishAsync(provider, new AuditedEvent("third"));
        var stored = await QueryAsync(provider, c => c.Set<OutboxEntity>().AsNoTracking().SingleAsync());
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IInboxStore>().RecordDeliveryAsync(stored.Id, stored.HandlerName, CancellationToken.None)).Should().BeTrue();

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).Status.Should().Be(OutboxMessageStatus.Processed, "the message was marked processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        provider.GetRequiredService<AuditProbe>().Runs.Should().Be(0, "a delivery the inbox already knows is a no-op");
        (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(0);
    }

    [Fact(DisplayName = "Without a unit of work, a handler that saves its own rows is delivered and recorded once")]
    public async Task Without_a_unit_of_work_the_handler_saves_and_the_record_follows()
    {
        await using var provider = await BuildAsync(unitOfWork: false);
        provider.GetRequiredService<AuditProbe>().SaveItself = true;
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(provider, new AuditedEvent("fourth"));
            await DrainAsync(provider);
            (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).Status.Should().Be(OutboxMessageStatus.Processed, "the message was processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(1);
        (await QueryAsync(provider, c => c.Set<InboxEntity>().CountAsync())).Should().Be(1, "the delivery was recorded after the handler's own save");
        provider.GetRequiredService<AuditProbe>().Runs.Should().Be(1);
    }

    [Fact(DisplayName = "Without a unit of work, the inbox does not save what the handler left unsaved; the delivery is not retried")]
    public async Task Without_a_unit_of_work_the_inbox_never_saves_for_the_handler()
    {
        await using var provider = await BuildAsync(unitOfWork: false);
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(provider, new AuditedEvent("fifth"));
            await DrainAsync(provider);
            (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).Status.Should().Be(OutboxMessageStatus.Processed, "the message was processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        (await QueryAsync(provider, c => c.AuditRows.CountAsync())).Should().Be(0, "nothing commits changes the handler did not save");
        (await QueryAsync(provider, c => c.Set<InboxEntity>().CountAsync())).Should().Be(0, "the inbox refused to save them with its record");
        (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).AttemptCount.Should().Be(0, "an unrecorded delivery is not a failed attempt");
        provider.GetRequiredService<AuditProbe>().Runs.Should().Be(1);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditRow> AuditRows => Set<AuditRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyCqrsOutbox();
        modelBuilder.Entity<AuditRow>().HasKey(r => r.Id);
    }
}

public sealed class AuditRow
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class AuditProbe
{
    private int _runs;
    public int Runs => _runs;
    public bool FailFirstAttempt { get; set; }

    /// <summary>The handler saves its row itself, as a handler that runs without a unit of work does.</summary>
    public bool SaveItself { get; set; }

    public int Next() => Interlocked.Increment(ref _runs);

    private int _records;

    /// <summary>Counts the inbox records attempted through a decorator that fails the first.</summary>
    public int NextRecord() => Interlocked.Increment(ref _records);
}

[NotificationName("tests.audited.event")]
public sealed record AuditedEvent(string Text) : INotification;

public sealed class AuditedEventHandler(AuditDbContext context, AuditProbe probe) : INotificationHandler<AuditedEvent>
{
    public async Task Handle(AuditedEvent notification, CancellationToken cancellationToken)
    {
        var run = probe.Next();
        context.AuditRows.Add(new AuditRow { Text = notification.Text });
        var fails = probe.FailFirstAttempt && run == 1;

        // A failing attempt saves before it fails: only a rollback of the delivery transaction takes the row back.
        if (probe.SaveItself || fails)
            await context.SaveChangesAsync(cancellationToken);

        if (fails)
            throw new InvalidOperationException("first attempt fails after writing");
    }
}

/// <summary>
///     The EF Core inbox, with another delivery of the same message recording it right after this delivery's check
///     found it undelivered: the record commits through a scope and context of its own before this delivery goes on.
/// </summary>
internal sealed class InboxRecordedByAnotherDelivery(IInboxStore inner, IServiceProvider scope) : IInboxStore
{
    public bool JoinsUnitOfWork => inner.JoinsUnitOfWork;

    public async Task<bool> IsDeliveredAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        if (await inner.IsDeliveredAsync(messageId, handlerName, cancellationToken)) return true;

        await using var otherDelivery = scope.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var context = otherDelivery.ServiceProvider.GetRequiredService<AuditDbContext>();
        var now = otherDelivery.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        context.Add(new InboxEntity { MessageId = messageId, HandlerName = handlerName, DeliveredAt = now });
        await context.SaveChangesAsync(cancellationToken);
        return false;
    }

    public Task<bool> RecordDeliveryAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
        => inner.RecordDeliveryAsync(messageId, handlerName, cancellationToken);
}

/// <summary>The EF Core inbox, failing the first record of the test with a database error.</summary>
internal sealed class InboxFailingItsFirstRecord(IInboxStore inner, AuditProbe probe) : IInboxStore
{
    public bool JoinsUnitOfWork => inner.JoinsUnitOfWork;

    public Task<bool> IsDeliveredAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
        => inner.IsDeliveredAsync(messageId, handlerName, cancellationToken);

    public Task<bool> RecordDeliveryAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
        => probe.NextRecord() == 1
            ? throw new DbUpdateException("The inbox record failed.")
            : inner.RecordDeliveryAsync(messageId, handlerName, cancellationToken);
}
