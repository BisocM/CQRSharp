using System.Diagnostics.CodeAnalysis;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Models.Outbox;
using CQRSharp.EntityFrameworkCore.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     A durable <see cref="IOutboxStore" /> backed by an EF Core <typeparamref name="TContext" />. Claims are made
///     race-safe by an optimistic-concurrency token: each candidate row is transitioned to in-progress with its
///     row-version bumped, and the save only succeeds for the processor that still held the row it read, so two
///     processors can never claim the same message. An in-progress row carries a visibility lease
///     (<see cref="OutboxEntity.LockedUntil" />); once that elapses the row is treated as abandoned and becomes
///     claimable again, which recovers messages whose claimant crashed mid-dispatch. All time is read from the
///     injected <see cref="TimeProvider" /> so back-off and visibility are deterministic under test.
/// </summary>
/// <typeparam name="TContext">The application's <see cref="DbContext" /> that maps <see cref="OutboxEntity" />.</typeparam>
public sealed class EfCoreOutboxStore<TContext> : IOutboxStore where TContext : DbContext
{
    private const string AotMessage =
        "EF Core uses runtime query compilation and is not compatible with Native AOT or full trimming.";

    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EfCoreOutboxStore<TContext>> _logger;
    private readonly TimeSpan _visibilityTimeout;
    private readonly int _maxClaimAttempts;

    // A DbContext is not thread-safe and forbids overlapping operations. The outbox processor may issue concurrent
    // calls against the one scoped store/context (the contract suite does exactly this), so this gate serializes every
    // context access. Cross-instance claim safety still rests entirely on the optimistic row-version token; this lock
    // only guards the single shared context from concurrent use within this instance.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store over a scoped <paramref name="context" />, reading time from <paramref name="timeProvider" />.</summary>
    public EfCoreOutboxStore(
        TContext context,
        TimeProvider timeProvider,
        IOptions<EfCoreOutboxStoreOptions> options,
        ILogger<EfCoreOutboxStore<TContext>> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
        _visibilityTimeout = options.Value.VisibilityTimeout;
        _maxClaimAttempts = options.Value.MaxClaimAttempts;
    }

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public async Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var message in messages)
                _context.Set<OutboxEntity>().Add(OutboxEntityMapper.FromMessage(message));

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public async Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ClaimAsync(batchSize, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int batchSize, CancellationToken cancellationToken)
    {
        // Bounded optimistic-token retry: load a due batch, flip each row to in-progress with a fresh lease and a
        // bumped row-version, then save. Rows whose row-version was changed by a competing processor since we read
        // them lose the SaveChanges race (DbUpdateConcurrencyException); we detach the losers, re-read the batch, and
        // retry up to MaxClaimAttempts. Only rows we actually persisted are returned as claimed.
        for (var attempt = 1; ; attempt++)
        {
            var now = Now();
            var due = now;
            var visibleUntil = now + _visibilityTimeout;

            var candidates = await _context.Set<OutboxEntity>()
                // Due Pending (NextRetryAt null/past) OR stuck InProgress whose lease has expired.
                .Where(e =>
                    (e.Status == OutboxMessageStatus.Pending && (e.NextRetryAt == null || e.NextRetryAt <= due)) ||
                    (e.Status == OutboxMessageStatus.InProgress && e.LockedUntil != null && e.LockedUntil <= due))
                .OrderBy(e => e.CreatedAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (candidates.Count == 0)
                return [];

            foreach (var entity in candidates)
            {
                entity.Status = OutboxMessageStatus.InProgress;
                entity.LockedUntil = visibleUntil;
                entity.RowVersion++;
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return candidates.Select(OutboxEntityMapper.ToMessage).ToList();
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < _maxClaimAttempts)
            {
                _logger.LogDebug(ex,
                    "Outbox claim lost a concurrency race on attempt {Attempt}/{MaxAttempts}; retrying.",
                    attempt, _maxClaimAttempts);

                // Detach the whole candidate set so the next iteration re-reads fresh rows (with current row-versions
                // and current statuses) rather than re-saving the stale tracked copies.
                DetachRange(candidates);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Exhausted the retry budget. Detach and give up for this call; the still-Pending rows we failed to
                // claim simply remain available for the next poll.
                _logger.LogDebug(ex,
                    "Outbox claim exhausted {MaxAttempts} concurrency retries; yielding the contested batch.",
                    _maxClaimAttempts);

                DetachRange(candidates);
                return [];
            }
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public Task MarkAsProcessedAsync(Guid messageId, CancellationToken cancellationToken)
        // Terminal + idempotent: once Processed or Failed the row is never moved again, so a duplicate or late mark
        // is a no-op and cannot resurrect a finished message.
        => MutateAsync(messageId, e =>
        {
            if (IsTerminal(e)) return false;

            e.Status = OutboxMessageStatus.Processed;
            e.ProcessedAt = Now();
            e.LockedUntil = null;
            return true;
        }, cancellationToken);

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public async Task<int> IncrementAttemptAsync(Guid messageId, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        var newCount = 0;

        await MutateAsync(messageId, e =>
        {
            // A message that already reached a terminal state must not be resurrected by a late/duplicate attempt;
            // newCount stays 0, which the contract uses to mean "not found / not eligible".
            if (IsTerminal(e)) return false;

            newCount = e.AttemptCount + 1;
            e.AttemptCount = newCount;
            e.LastError = error;
            e.NextRetryAt = nextRetryAt;
            e.Status = OutboxMessageStatus.Pending;
            e.LockedUntil = null;
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return newCount;
    }

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public Task MarkAsFailedAsync(Guid messageId, string? error, CancellationToken cancellationToken)
        => MutateAsync(messageId, e =>
        {
            if (IsTerminal(e)) return false;

            e.Status = OutboxMessageStatus.Failed;
            e.LastError = error;
            e.LockedUntil = null;
            return true;
        }, cancellationToken);

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private static bool IsTerminal(OutboxEntity e)
        => e.Status is OutboxMessageStatus.Processed or OutboxMessageStatus.Failed;

    private void DetachRange(IEnumerable<OutboxEntity> entities)
    {
        foreach (var entity in entities)
            _context.Entry(entity).State = EntityState.Detached;
    }

    /// <summary>
    ///     Centralized single-row mutate: load the row by id, apply <paramref name="mutate" /> (which returns false to
    ///     mean "no change, stop"), bump the row-version, save, and retry the whole load-apply-save on a concurrency
    ///     loss up to MaxClaimAttempts. Re-reading inside the loop means a contended mutation re-evaluates terminality
    ///     against the freshest row, so a winner that already finished the message is honoured and never overwritten.
    /// </summary>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    private async Task MutateAsync(Guid id, Func<OutboxEntity, bool> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MutateCoreAsync(id, mutate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    private async Task MutateCoreAsync(Guid id, Func<OutboxEntity, bool> mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var entity = await _context.Set<OutboxEntity>()
                .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
                .ConfigureAwait(false);

            // Missing row: nothing to do (callers treat this as the "not found" case).
            if (entity is null)
                return;

            if (!mutate(entity))
            {
                // No change requested (e.g. already terminal). Detach so a stale tracked copy can't linger.
                _context.Entry(entity).State = EntityState.Detached;
                return;
            }

            entity.RowVersion++;

            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < _maxClaimAttempts)
            {
                _logger.LogDebug(ex,
                    "Outbox mutation of {MessageId} lost a concurrency race on attempt {Attempt}/{MaxAttempts}; retrying.",
                    id, attempt, _maxClaimAttempts);

                _context.Entry(entity).State = EntityState.Detached;
            }
        }
    }
}
