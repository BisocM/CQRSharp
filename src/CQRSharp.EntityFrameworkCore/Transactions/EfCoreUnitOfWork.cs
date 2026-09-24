using System.Data;
using System.Diagnostics.CodeAnalysis;
using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The <see cref="IUnitOfWork" /> over an EF Core <typeparamref name="TContext" />: a request's transaction is the
///     context's database transaction, a commit saves the tracked changes before it commits, and a rollback clears the
///     change tracker. Over the same scoped context as the EF Core outbox and inbox
///     (<c>UseOutbox(o =&gt; o.UseEntityFrameworkCore&lt;TContext&gt;())</c>), a handler's changes, the outbox messages it
///     publishes and a delivery's inbox record are one commit.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="HasActiveTransaction" /> follows the context's current transaction, so a transaction the
///         application began on the context itself counts: a transactional request takes part in it, and the transactional
///         outbox stores into it.
///     </para>
///     <para>
///         A rollback clears the whole change tracker, not only what the failed request changed: whatever the scope had
///         tracked is detached, so the next attempt, or the next request in the scope, starts from the database.
///     </para>
///     <para>
///         A retrying execution strategy (<c>EnableRetryOnFailure</c>) rejects a transaction the application begins itself,
///         so <see cref="BeginTransactionAsync" /> refuses to run under one. Retry whole requests with the resilience
///         behavior instead: it runs outside the unit of work, and every attempt gets a transaction of its own.
///     </para>
/// </remarks>
/// <typeparam name="TContext">The application's <see cref="DbContext" />; the scoped instance the handlers use.</typeparam>
/// <param name="context">The scoped context whose transaction this unit of work drives.</param>
public sealed class EfCoreUnitOfWork<TContext>(TContext context) : IUnitOfWork where TContext : DbContext
{
    private readonly TContext _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public bool HasActiveTransaction => _context.Database.CurrentTransaction is not null;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="IsolationLevel.Unspecified" /> begins the provider's default transaction; any other level needs a
    ///     relational provider.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     A transaction is already open on the context, or the context's execution strategy retries on failure.
    /// </exception>
    /// <exception cref="NotSupportedException">An isolation level was asked of a provider that is not relational.</exception>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        var database = _context.Database;
        if (database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                $"A transaction is already open on {typeof(TContext).Name}; a unit of work takes part in it rather than beginning another.");

        if (database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException(
                $"{nameof(EfCoreUnitOfWork<TContext>)} cannot begin a transaction on {typeof(TContext).Name}: its execution strategy retries " +
                "on failure (EnableRetryOnFailure), and EF Core rejects a transaction begun outside that strategy. Configure the " +
                "context without a retrying execution strategy and retry whole requests with the resilience behavior " +
                "(UseResilience), which runs outside the unit of work.");

        if (isolationLevel == IsolationLevel.Unspecified)
        {
            await database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!database.IsRelational())
            throw new NotSupportedException(
                $"The isolation level {isolationLevel} needs a relational provider, and {typeof(TContext).Name} uses " +
                $"'{database.ProviderName}'. Leave the isolation level unspecified to use the provider's own transaction.");

        await database.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        var transaction = _context.Database.CurrentTransaction
                          ?? throw new InvalidOperationException($"No transaction is open on {typeof(TContext).Name}; there is nothing to commit.");

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Only a committed transaction is let go here: one whose commit threw stays current for the rollback that follows.
        await transaction.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        var transaction = _context.Database.CurrentTransaction;
        try
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Disposed even when the rollback threw (a transaction a failed commit left behind): a transaction left
                // current would make every later request in the scope take part in it.
                if (transaction is not null)
                    await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // The rolled-back changes are still tracked as Added or Modified; the next SaveChanges in this scope - a
                // retry's, the next request's - would write them.
                _context.ChangeTracker.Clear();
            }
        }
    }
}
