using System.Data;

namespace CQRSharp.Abstractions.Data.Interfaces.Transactions;

/// <summary>
///     Extends the <see cref="IUnitOfWork" /> to provide explicit control over database transactions,
///     including support for transaction savepoints.
/// </summary>
/// <remarks>
///     This interface is intended for scenarios where the default transactional behavior, managed by
///     <see cref="UnitOfWorkBehavior{TRequest, TResult}" />, is insufficient. It allows handlers to manually
///     begin, commit, or roll back transactions, and to create nested points of recovery within a
///     single transaction using savepoints. When an implementation of this interface is used, the
///     <see cref="UnitOfWorkBehavior{TRequest, TResult}" /> will delegate transaction management to it.
/// </remarks>
public interface IExplicitUnitOfWork : IUnitOfWork
{
    /// <summary>
    ///     Gets a value indicating whether a transaction is currently active.
    /// </summary>
    bool HasActiveTransaction { get; }

    /// <summary>
    ///     Begins a new database transaction asynchronously with the specified isolation level.
    /// </summary>
    /// <param name="isolationLevel">The isolation level to use for the transaction.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="Task" /> representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown if a transaction is already active.</exception>
    Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken);

    /// <summary>
    ///     Commits the active database transaction asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="Task" /> representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no transaction is active.</exception>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Rolls back the active database transaction asynchronously from its current state.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="Task" /> representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no transaction is active.</exception>
    Task RollbackAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Creates a transaction savepoint asynchronously.
    /// </summary>
    /// <remarks>
    ///     A savepoint allows you to mark a specific point in a transaction. You can later roll back
    ///     the transaction to this point without affecting the work done before the savepoint was created.
    ///     This is useful for complex operations where partial rollbacks are required.
    /// </remarks>
    /// <param name="name">The name of the savepoint to create. This name is used to identify the savepoint for later rollback or release operations.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="Task" /> representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no transaction is active.</exception>
    Task CreateSavepointAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    ///     Rolls back the transaction to a previously created savepoint asynchronously.
    /// </summary>
    /// <remarks>
    ///     When you roll back to a savepoint, all changes made in the transaction after the savepoint was created are undone.
    ///     The transaction itself remains active, and you can continue to perform work and commit it later. Any savepoints
    ///     created after the specified savepoint are also rolled back and are no longer valid.
    /// </remarks>
    /// <param name="name">The name of the savepoint to roll back to.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="Task" /> representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no transaction is active or if the specified savepoint does not exist.</exception>
    Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    ///     Releases a specified savepoint asynchronously.
    /// </summary>
    /// <remarks>
    ///     Releasing a savepoint removes it from the transaction. While not always necessary (as some database
    ///     systems automatically release savepoints on commit or rollback), this can be used to free up resources
    ///     associated with the savepoint. Once released, you can no longer roll back to it.
    /// </remarks>
    /// <param name="name">The name of the savepoint to release.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="Task" /> representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no transaction is active or if the specified savepoint does not exist.</exception>
    Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken);
}