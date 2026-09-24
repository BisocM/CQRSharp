using System.Data;
using CQRSharp.Persistence;
using CQRSharp.Sample.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Infrastructure.Persistence;

// The sample keeps no data of its own, so its unit of work only marks a transaction open and logs; a real one commits
// its data store's transaction (saving pending changes first) and rolls it back (discarding them).
public sealed class InMemoryUnitOfWork(ILogger<InMemoryUnitOfWork> logger) : IUnitOfWork
{
    public bool HasActiveTransaction { get; private set; }

    public Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        if (HasActiveTransaction) throw new InvalidOperationException("A transaction is already active.");
        HasActiveTransaction = true;
        SampleLog.TransactionBegun(logger, isolationLevel);
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        if (!HasActiveTransaction) throw new InvalidOperationException("No transaction is active.");
        HasActiveTransaction = false;
        SampleLog.TransactionCommitted(logger);
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        HasActiveTransaction = false;
        SampleLog.TransactionRolledBack(logger);
        return Task.CompletedTask;
    }
}
