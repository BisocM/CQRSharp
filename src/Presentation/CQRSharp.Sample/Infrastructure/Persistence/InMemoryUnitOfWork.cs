using System.Data;
using CQRSharp.Abstractions.Interfaces.Transactions;
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

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("--- UoW: Saving changes... ---");
        return Task.FromResult(0);
    }

    public Task CreateSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    public TService GetService<TService>() where TService : class => serviceProvider.GetRequiredService<TService>();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
