using System.Data;

namespace CQRSharp.Persistence;

/// <summary>
///     The unit of work a transactional request runs in: one transaction over the application's data access, begun
///     before the handler runs and committed or rolled back once it is done. The unit-of-work behaviors drive it for
///     requests marked <see cref="ITransactionalCommand" /> or <see cref="ITransactionalQuery" />, and the outbox
///     processor drives it around a delivery that an inbox records.
/// </summary>
/// <remarks>
///     <para>
///         Registered scoped, so every request in a scope shares one instance. A request that finds a transaction
///         already active (<see cref="HasActiveTransaction" />) takes part in it instead of beginning its own: whoever
///         began the transaction commits or rolls it back.
///     </para>
///     <para>
///         An implementation needs no real database transaction to be valid: over an ORM change tracker alone,
///         <see cref="BeginTransactionAsync" /> can just note that a unit of work is open, <see cref="CommitAsync" /> save
///         the tracked changes and <see cref="RollbackAsync" /> clear them. What every implementation must guarantee is
///         the pair of rules on <see cref="CommitAsync" /> (pending changes are persisted) and <see cref="RollbackAsync" />
///         (pending changes are discarded): the pipeline calls nothing else, and a retry or the next request in the scope
///         reuses the same instance.
///     </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    ///     Whether a transaction is open on the unit of work's connection right now, whoever began it. It must reflect
    ///     the real transaction, not only the ones begun through this instance: a request that sees <c>true</c> takes part
    ///     in the open transaction, and the transactional outbox routes a publish into the store only while it is
    ///     <c>true</c>.
    /// </summary>
    bool HasActiveTransaction { get; }

    /// <summary>Begins a transaction.</summary>
    /// <param name="isolationLevel">
    ///     The isolation level to use; <see cref="IsolationLevel.Unspecified" /> asks for the data store's own default.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes once the transaction is open.</returns>
    /// <exception cref="InvalidOperationException">A transaction is already active.</exception>
    Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken);

    /// <summary>
    ///     Persists every pending change (an ORM's tracked changes included), then commits the active transaction. Once
    ///     it returns, <see cref="HasActiveTransaction" /> is <c>false</c>.
    /// </summary>
    /// <remarks>
    ///     The pipeline calls nothing else to save: a commit that does not persist pending changes loses them. When it
    ///     throws, the caller follows with <see cref="RollbackAsync" />.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes once the transaction is committed.</returns>
    /// <exception cref="InvalidOperationException">No transaction is active.</exception>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Rolls back the active transaction <b>and discards every pending, unsaved change</b> (for an ORM, everything
    ///     its change tracker holds), so the unit of work is clean for the next attempt or the next request in the scope.
    ///     Once it returns, <see cref="HasActiveTransaction" /> is <c>false</c>.
    /// </summary>
    /// <remarks>
    ///     Called after a failed <see cref="CommitAsync" /> too, when the transaction may already be over: with no active
    ///     transaction it still discards the pending changes and does not throw. Changes left behind would be written by
    ///     the next save in the scope, so a rolled-back attempt's work would be committed by its retry.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes once the transaction is rolled back and the pending changes are discarded.</returns>
    Task RollbackAsync(CancellationToken cancellationToken);
}
