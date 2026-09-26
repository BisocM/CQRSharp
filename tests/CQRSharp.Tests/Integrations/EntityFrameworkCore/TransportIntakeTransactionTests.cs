using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Tests.Core;
using CQRSharp.Transports;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     The notification intake on the EF Core outbox and inbox with the EF Core unit of work over one <c>DbContext</c>: the
///     stored messages and the dedupe record are one commit, so a received notification is taken in exactly once. An intake
///     that loses the record to a concurrent one of the same message takes its messages back with its transaction.
/// </summary>
public sealed class TransportIntakeTransactionTests : IAsyncDisposable
{
    private const string Source = "broker:billing";

    private readonly SqliteFileDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private async Task<ServiceProvider> BuildAsync(bool loseEveryRecord = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<SharedNotificationLog>();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_database.ConnectionString));
        services.AddCqrsGenerated(b => b
            .UseOutbox(o => o.Enabled().UseEntityFrameworkCore<AuditDbContext>())
            .UseEntityFrameworkCoreUnitOfWork<AuditDbContext>());
        if (loseEveryRecord)
        {
            services.RemoveAll<IInboxStore>();
            services.AddScoped<IInboxStore>(sp => new LosingInbox(new EfCoreInboxStore<AuditDbContext>(
                sp.GetRequiredService<AuditDbContext>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<EfCoreOutboxStoreOptions>>())));
        }

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.EnsureCreatedAsync();
        return provider;
    }

    private static async Task<IntakeResult> AcceptAsync(IServiceProvider provider, string messageId)
    {
        await using var scope = provider.CreateAsyncScope();
        var payload = scope.ServiceProvider.GetRequiredService<INotificationSerializer>().Serialize(new SharedNotification(1));
        return await scope.ServiceProvider.GetRequiredService<INotificationIntake>().AcceptAsync(
            new InboundNotification("broker", Source, messageId, "tests.transport.shared", payload, null, null, null), CancellationToken.None);
    }

    private static async Task<(int Outbox, int Inbox)> CountAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        return (await context.Set<OutboxEntity>().CountAsync(), await context.Set<InboxEntity>().CountAsync());
    }

    [Fact(DisplayName = "EF Core intake: the stored messages and the dedupe record commit together, and a redelivery stores nothing")]
    public async Task Messages_and_record_are_one_commit()
    {
        await using var provider = await BuildAsync();

        (await AcceptAsync(provider, "m-1")).Should().Be(new IntakeResult(IntakeOutcome.Stored, 1, null, null));
        (await CountAsync(provider)).Should().Be((1, 1));

        (await AcceptAsync(provider, "m-1")).Outcome.Should().Be(IntakeOutcome.Duplicate);
        (await CountAsync(provider)).Should().Be((1, 1));
    }

    [Fact(DisplayName = "EF Core intake: an intake that loses the record to a concurrent one rolls its messages back")]
    public async Task A_lost_record_rolls_the_messages_back()
    {
        await using var provider = await BuildAsync(loseEveryRecord: true);

        (await AcceptAsync(provider, "m-1")).Outcome.Should().Be(IntakeOutcome.Duplicate);

        (await CountAsync(provider)).Should().Be((0, 0), "the messages were written in the transaction the lost record rolled back");
    }

    // An inbox whose every record finds that another intake recorded the message first, as the loser of a race does.
    private sealed class LosingInbox(IInboxStore inner) : IInboxStore
    {
        public bool JoinsUnitOfWork => inner.JoinsUnitOfWork;

        public Task<bool> IsDeliveredAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
            => inner.IsDeliveredAsync(messageId, handlerName, cancellationToken);

        public Task<bool> RecordDeliveryAsync(Guid messageId, string handlerName, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
