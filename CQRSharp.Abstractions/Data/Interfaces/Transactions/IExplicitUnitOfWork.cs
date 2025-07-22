using System.Data;

namespace CQRSharp.Abstractions.Data.Interfaces.Transactions;

/// <summary>
/// Extends IUnitOfWork to provide explicit, manual control over the transaction lifecycle.
/// </summary>
public interface IExplicitUnitOfWork : IUnitOfWork
{
    /// <summary>
    /// Explicitly begins a new transaction with a specified isolation level.
    /// </summary>
    Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken);

    /// <summary>
    /// Explicitly commits the active transaction.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Explicitly rolls back the active transaction.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}