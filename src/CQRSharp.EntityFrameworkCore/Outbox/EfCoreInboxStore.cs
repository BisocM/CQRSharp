using System.Diagnostics.CodeAnalysis;
using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The <see cref="IInboxStore" /> that pairs with <see cref="EfCoreOutboxStore{TContext}" />, registered with it by
///     <c>AddEntityFrameworkCoreOutboxStore&lt;TContext&gt;()</c>. Scoped, over the same
///     <typeparamref name="TContext" /> the handler in that scope uses: when a unit of work over that context
///     (<see cref="EfCoreUnitOfWork{TContext}" />) wraps the delivery, the inbox record and the handler's own changes are
///     one commit, which is what turns at-least-once into exactly-once for everything the handler writes through that
///     context.
/// </summary>
/// <remarks>
///     Without a transaction open on the context, the record is bookkeeping written after the handler's work already
///     stands, and it never saves that work on the handler's behalf: a handler that runs without a unit of work saves
///     its own changes. Recording while the context still holds changes nobody saved is refused with an
///     <see cref="InvalidOperationException" />, which the processor logs as a delivery it could not record; those
///     changes are discarded with the delivery's scope.
/// </remarks>
/// <typeparam name="TContext">The application's <see cref="DbContext" /> that maps <see cref="InboxEntity" />.</typeparam>
internal sealed class EfCoreInboxStore<TContext>(TContext context, TimeProvider timeProvider, IOptions<EfCoreOutboxStoreOptions> options) : IInboxStore
    where TContext : DbContext
{

    private const string SavepointName = "CqrsInboxRecord";

    private readonly TimeSpan _retention = options.Value.InboxRetention;

    // A DbContext serves one operation at a time; the gate lets the store be shared the way the processor shares it.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    ///     <c>true</c> while a transaction is open on the scoped <typeparamref name="TContext" /> this store writes
    ///     through — the delivery's unit of work, when it is over the same context instance. The record then commits
    ///     with the handler's changes; otherwise it is written right after their commit.
    /// </summary>
    public bool JoinsUnitOfWork => context.Database.CurrentTransaction is not null;

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<bool> IsDeliveredAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A record older than the retention is already forgotten, whether or not the purge has deleted it yet.
            var horizon = Horizon();
            return await context.Set<InboxEntity>()
                .AsNoTracking()
                .AnyAsync(e => e.MessageId == messageId && e.HandlerName == handlerName && e.DeliveredAt > horizon, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<bool> RecordDeliveryAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RecordAsync(messageId, handlerName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RecordAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        // The save below flushes everything the context tracks. Inside a transaction that is the point: the handler's
        // changes and the record are one commit. Outside one, flushing the handler's unsaved changes would make them
        // succeed or fail with a bookkeeping write whose failure does not count against the delivery.
        if (context.Database.CurrentTransaction is null && context.ChangeTracker.HasChanges())
            throw new InvalidOperationException(
                $"The inbox cannot record the delivery of outbox message {messageId} to {handlerName}: {typeof(TContext).Name} holds changes " +
                "the handler did not save, and no transaction is open to commit them with the record. A handler that runs without a unit of " +
                $"work saves its own changes; register UseEntityFrameworkCoreUnitOfWork<{typeof(TContext).Name}>() to commit them together " +
                "with the inbox record instead.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await context.Set<InboxEntity>()
            .AsTracking()
            .FirstOrDefaultAsync(e => e.MessageId == messageId && e.HandlerName == handlerName, cancellationToken)
            .ConfigureAwait(false);

        InboxEntity record;
        if (existing is null)
        {
            record = new InboxEntity { MessageId = messageId, HandlerName = handlerName, DeliveredAt = now };
            context.Set<InboxEntity>().Add(record);
        }
        else if (existing.DeliveredAt <= Horizon())
        {
            // An aged-out record the purge has not reached yet: the delivery is unknown again, so take the row over.
            record = existing;
            record.DeliveredAt = now;
        }
        else
        {
            context.Entry(existing).State = EntityState.Detached;
            return false;
        }

        // Inside a transaction, a rejected insert must leave the transaction usable for the duplicate check below - and
        // for the rollback the processor follows a duplicate with: on PostgreSQL a failed statement aborts the whole
        // transaction. EF Core saves through a savepoint of its own for exactly that, unless the application switched
        // automatic savepoints off; the record then takes one itself.
        var transaction = context.Database.CurrentTransaction;
        var savepoint = transaction is { SupportsSavepoints: true } && !context.Database.AutoSavepointsEnabled ? transaction : null;
        if (savepoint is not null)
            await savepoint.CreateSavepointAsync(SavepointName, cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (savepoint is not null)
                await savepoint.ReleaseSavepointAsync(SavepointName, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // The primary key rejected the insert (a concurrent recorder won) - or the save failed for another reason.
            // Only a live record that now exists makes this a duplicate; anything else fails the record (inside a
            // transaction, together with the handler's changes it flushed).
            context.Entry(record).State = EntityState.Detached;
            if (savepoint is not null)
                await savepoint.RollbackToSavepointAsync(SavepointName, cancellationToken).ConfigureAwait(false);

            var horizon = Horizon();
            var exists = await context.Set<InboxEntity>()
                .AsNoTracking()
                .AnyAsync(e => e.MessageId == messageId && e.HandlerName == handlerName && e.DeliveredAt > horizon, cancellationToken)
                .ConfigureAwait(false);

            if (exists) return false;
            throw;
        }
    }

    private DateTime Horizon() => timeProvider.GetUtcNow().UtcDateTime - _retention;
}
