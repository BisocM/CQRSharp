using System.Diagnostics.CodeAnalysis;
using CQRSharp.Pipelines;
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
        ILogger<EfCoreOutboxStore<TContext>> logger,
        EfCoreOutboxPurgeSchedule? purgeSchedule = null)
    {
        _purgeSchedule = purgeSchedule ?? new EfCoreOutboxPurgeSchedule();
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
        _visibilityTimeout = options.Value.VisibilityTimeout;
        _maxClaimAttempts = options.Value.MaxClaimAttempts;
        _processedRetention = options.Value.ProcessedRetention;
        _purgeInterval = options.Value.PurgeInterval;
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

    // The store is scoped (one per poll), so "when is the next purge due" lives in a singleton it is handed.
    private readonly EfCoreOutboxPurgeSchedule _purgeSchedule;
    private readonly TimeSpan? _processedRetention;
    private readonly TimeSpan _purgeInterval;

    // Deletes processed messages older than the retention window, at most once per interval per process. Best-effort: a
    // failed purge is logged and retried on a later poll, never allowed to fail the claim it rides on.
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    private async Task PurgeProcessedAsync(CancellationToken cancellationToken)
    {
        if (_processedRetention is not { } retention) return;

        var now = Now();
        if (!_purgeSchedule.TryBegin(now, _purgeInterval)) return;

        try
        {
            var cutoff = now - retention;
            var deleted = await _context.Set<OutboxEntity>()
                .Where(e => e.Status == OutboxMessageStatus.Processed && e.ProcessedAt != null && e.ProcessedAt <= cutoff)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (deleted > 0)
                _logger.LogInformation("Purged {Count} processed outbox message(s) older than {Retention}.", deleted, retention);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Purging processed outbox messages failed; it will be retried on a later poll.");
        }
    }

    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    private async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int batchSize, CancellationToken cancellationToken)
    {
        await PurgeProcessedAsync(cancellationToken).ConfigureAwait(false);

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
                // The claim token is the row-version this claim wrote. Every later writer bumps it, so "the row still
                // carries my token" is exactly "nobody has touched this message since I claimed it".
                return candidates
                    .Select(e => OutboxEntityMapper.ToMessage(e) with { Claim = new OutboxClaim(e.Id, ClaimToken(e), visibleUntil) })
                    .ToList();
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
            catch
            {
                // Any other failed save (a provider error, cancellation) must not leave the half-claimed rows tracked
                // as Modified: the next SaveChanges on this context would silently re-send those stale UPDATEs.
                DetachRange(candidates);
                throw;
            }
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken)
        => MutateAsync(claim.MessageId, e =>
        {
            if (!IsOwnedBy(e, claim)) return false;

            e.Status = OutboxMessageStatus.Processed;
            e.ProcessedAt = Now();
            e.LockedUntil = null;
            return true;
        }, cancellationToken);

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public async Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        var newCount = 0;

        await MutateAsync(claim.MessageId, e =>
        {
            // Unknown, terminal, or claimed by someone else since: change nothing. newCount is reset because a lost
            // concurrency race re-runs this callback against the freshly read row.
            newCount = 0;
            if (!IsOwnedBy(e, claim)) return false;

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
    public Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken)
        => MutateAsync(claim.MessageId, e =>
        {
            if (!IsOwnedBy(e, claim)) return false;

            // The attempt that exhausted the budget is an attempt too; count it so the dead letter tells the whole story.
            e.AttemptCount++;
            e.Status = OutboxMessageStatus.Failed;
            e.LastError = error;
            e.LockedUntil = null;
            return true;
        }, cancellationToken);

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public async Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        OutboxClaim? renewed = null;

        await MutateAsync(claim.MessageId, e =>
        {
            renewed = null;
            if (!IsOwnedBy(e, claim)) return false;

            e.LockedUntil = Now() + _visibilityTimeout;
            // MutateCore bumps the row-version after this callback, so the renewed claim's token is the next value.
            renewed = new OutboxClaim(e.Id, (e.RowVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), e.LockedUntil.Value);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return renewed;
    }

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public async Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken)
    {
        foreach (var claim in claims)
            await MutateAsync(claim.MessageId, e =>
            {
                if (!IsOwnedBy(e, claim)) return false;

                // Back to pending without counting an attempt: nothing was tried.
                e.Status = OutboxMessageStatus.Pending;
                e.LockedUntil = null;
                return true;
            }, cancellationToken).ConfigureAwait(false);
    }

    private static string ClaimToken(OutboxEntity e) => e.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // In progress under exactly the row-version the claim recorded: any reclaim, renewal or finalize since has moved it.
    private static bool IsOwnedBy(OutboxEntity e, OutboxClaim claim)
        => e.Status == OutboxMessageStatus.InProgress && string.Equals(ClaimToken(e), claim.Token, StringComparison.Ordinal);

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

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
    private async Task<bool> MutateAsync(Guid id, Func<OutboxEntity, bool> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MutateCoreAsync(id, mutate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    private async Task<bool> MutateCoreAsync(Guid id, Func<OutboxEntity, bool> mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var entity = await _context.Set<OutboxEntity>()
                .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
                .ConfigureAwait(false);

            // Missing row: nothing to do (callers treat this as the "not found" case).
            if (entity is null)
                return false;

            if (!mutate(entity))
            {
                // No change requested (e.g. already terminal). Detach so a stale tracked copy can't linger.
                _context.Entry(entity).State = EntityState.Detached;
                return false;
            }

            entity.RowVersion++;

            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < _maxClaimAttempts)
            {
                _logger.LogDebug(ex,
                    "Outbox mutation of {MessageId} lost a concurrency race on attempt {Attempt}/{MaxAttempts}; retrying.",
                    id, attempt, _maxClaimAttempts);

                _context.Entry(entity).State = EntityState.Detached;
            }
            catch
            {
                // The retry budget is spent, or the save failed for another reason. Detach before propagating: a stale
                // Modified row left tracked would be re-sent by — and fail — every later mutation in this batch.
                _context.Entry(entity).State = EntityState.Detached;
                throw;
            }
        }
    }
}
