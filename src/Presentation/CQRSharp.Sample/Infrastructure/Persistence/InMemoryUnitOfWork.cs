using System.Data;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Outbox;
using CQRSharp.Abstractions.Data.Interfaces.Transactions;
using CQRSharp.Abstractions.Data.Models.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Infrastructure.Persistence;

public sealed class InMemoryUnitOfWork(ILogger<InMemoryUnitOfWork> logger, IServiceProvider serviceProvider)
    : IExplicitUnitOfWork
{
    public bool HasActiveTransaction { get; private set; }

    public async Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        logger.LogInformation("--- UoW: Beginning Transaction (Isolation: {Level}) ---", isolationLevel);
        HasActiveTransaction = true;
        await Task.CompletedTask;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("--- UoW: Committing Transaction ---");
        await SaveChangesAsync(cancellationToken);
        HasActiveTransaction = false;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning("--- UoW: Rolling Back Transaction ---");
        HasActiveTransaction = false;
        await Task.CompletedTask;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("--- UoW: Saving changes... ---");
        var outbox = serviceProvider.GetService<IOutbox>();
        if (outbox is null) return 0;

        var notifications = outbox.GetNotifications();
        if (!notifications.Any())
        {
            logger.LogInformation("--- UoW: No notifications in outbox to save. ---");
            return 0;
        }

        logger.LogInformation("--- UoW: Found {Count} notifications in the outbox. Storing them... ---", notifications.Count);

        var outboxStore = serviceProvider.GetRequiredService<IOutboxStore>();
        var serializer = serviceProvider.GetRequiredService<INotificationSerializer>();

        var messages = notifications.Select(n => new OutboxMessage(
            Guid.NewGuid(),
            serializer.GetNotificationName(n.GetType()),
            serializer.Serialize(n),
            DateTime.UtcNow,
            OutboxMessageStatus.Pending, null, null
        ));

        await outboxStore.StoreAsync(messages, cancellationToken);
        logger.LogInformation("--- UoW: Notifications saved to outbox store. ---");
        return notifications.Count;
    }

    public Task CreateSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    public TService GetService<TService>() where TService : class => serviceProvider.GetRequiredService<TService>();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}