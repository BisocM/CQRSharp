using System.Diagnostics.CodeAnalysis;
using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     A durable <see cref="IOutboxStore" /> backed by an EF Core <typeparamref name="TContext" />, registered by
///     <c>AddEntityFrameworkCoreOutboxStore&lt;TContext&gt;()</c>. Claims are made race-safe by an optimistic-concurrency
///     token: each candidate row is transitioned to in-progress with a new row version, and the save only succeeds for
///     the processor that still held the row it read, so two processors can never claim the same message. The claim
///     token is that row version (with the row's attempt count), and every later operation under the claim is one
///     conditional UPDATE that matches only while the row still carries it. An in-progress row carries a visibility lease
///     (<see cref="OutboxEntity.LockedUntil" />); once that elapses the row is treated as abandoned and becomes claimable
///     again, which recovers messages whose claimant crashed mid-dispatch. Messages are claimed by
///     <see cref="OutboxEntity.CreatedAt" /> then <see cref="OutboxEntity.Sequence" />, and a partitioned message is held
///     back while an earlier message with the same partition key and handler is still pending or in progress, or while
///     any message of that partition is being delivered. All time is read from the injected <see cref="TimeProvider" />
///     so back-off and visibility are deterministic under test. Retention is not this store's work: the outbox retention
///     service purges on its own schedule.
/// </summary>
/// <typeparam name="TContext">The application's <see cref="DbContext" /> that maps <see cref="OutboxEntity" />.</typeparam>
internal sealed class EfCoreOutboxStore<TContext> : IOutboxStore where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EfCoreOutboxStore<TContext>> _logger;
    private readonly TimeSpan _visibilityTimeout;
    private readonly int _maxClaimAttempts;

    // A DbContext is not thread-safe and forbids overlapping operations. The outbox processor issues concurrent calls
    // against the one scoped store/context (renewals and finalizes of a batch delivered in parallel; the contract suite
    // does the same), so this gate serializes every context access. Cross-instance safety rests entirely on the row
    // version; this lock only guards the single shared context from concurrent use within this instance.
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

    /// <summary>
    ///     <c>true</c> while a transaction is open on the scoped <typeparamref name="TContext" /> this store writes
    ///     through — the unit of work's, when that unit of work is over the same context instance
    ///     (<see cref="EfCoreUnitOfWork{TContext}" />). Its messages then commit and roll back with the handler's changes.
    ///     A unit of work over another context, or none, gives <c>false</c>, and the messages are stored after the commit.
    /// </summary>
    public bool JoinsUnitOfWork => _context.Database.CurrentTransaction is not null;

    /// <inheritdoc />
    /// <remarks>
    ///     Saves through the scoped <typeparamref name="TContext" />, which the caller shares: outside a transaction, a
    ///     store — a publish from outside any request, or the end-of-request store of the <c>Enabled</c> outbox mode —
    ///     also saves every other change tracked on that context. Save or discard your own changes before publishing,
    ///     or publish inside a request that runs in a unit of work.
    /// </remarks>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        // Mapped before anything is tracked: a message the mapper rejects (an over-long handler name) must not leave the
        // ones before it tracked as Added, for the next SaveChanges on this context to insert.
        var entities = messages.Select(OutboxEntityMapper.FromMessage).ToArray();
        if (entities.Length == 0) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _context.Set<OutboxEntity>().AddRange(entities);
            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Stored or rejected, the rows leave the tracker: a rejected insert (a duplicate id, a lost connection)
                // would otherwise be retried, and fail again, by every later save on a context that outlives the request.
                DetachRange(entities);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A claim commits on its own before it is handed out - the partition rule is checked again against what other
    ///     processors committed meanwhile - so it is refused while a transaction is open on the context.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A transaction is open on the context, or an ambient transaction is active.</exception>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Inside a transaction the claim would not be visible to other processors when the partition rule is checked
            // again below, and the check could not see theirs; the processor always claims outside one.
            if (_context.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
                throw new InvalidOperationException(
                    $"The EF Core outbox store cannot claim messages inside a transaction on {typeof(TContext).Name}: a claim commits on its own before it is handed out.");

            var claimed = await ClaimAsync(batchSize, cancellationToken).ConfigureAwait(false);
            if (claimed.Count == 0) return [];

            var uncontested = await HandBackContestedAsync(claimed, cancellationToken).ConfigureAwait(false);
            return uncontested
                .Select(e => new ClaimedOutboxMessage(
                    OutboxEntityMapper.ToMessage(e),
                    new OutboxClaim(e.Id, new OutboxClaimToken(e.RowVersion, e.AttemptCount).ToString(), e.LockedUntil!.Value)))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Bounded optimistic-token retry: load a due batch, flip each row to in-progress with a fresh lease and a new row
    // version, then save. Rows whose row version was changed by a competing processor since we read them lose the
    // SaveChanges race (DbUpdateConcurrencyException); we detach the losers, re-read the batch, and retry up to
    // MaxClaimAttempts. Returns the committed claims, detached: from here the rows belong to the database.
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    private async Task<List<OutboxEntity>> ClaimAsync(int batchSize, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var now = Now();
            var visibleUntil = now + _visibilityTimeout;

            var candidates = await WhereNotHeldBack(
                    _context.Set<OutboxEntity>()
                        // Tracked whatever the context's default: the rows below are mutated and saved.
                        .AsTracking()
                        // Due Pending (NextRetryAt null/past) OR stuck InProgress whose lease has expired.
                        .Where(e =>
                            (e.Status == OutboxMessageStatus.Pending && (e.NextRetryAt == null || e.NextRetryAt <= now)) ||
                            (e.Status == OutboxMessageStatus.InProgress && e.LockedUntil != null && e.LockedUntil <= now)),
                    now)
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.Sequence)
                .Take(batchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (candidates.Count == 0)
                return [];

            foreach (var entity in candidates)
            {
                entity.Status = OutboxMessageStatus.InProgress;
                entity.LockedUntil = visibleUntil;
                entity.RowVersion = unchecked(entity.RowVersion + 1);
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return candidates;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < _maxClaimAttempts)
            {
                EfCoreLog.ClaimRaceLost(_logger, ex, attempt, _maxClaimAttempts);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Out of attempts: the still-pending rows we failed to claim simply remain for the next poll.
                EfCoreLog.ClaimRacesExhausted(_logger, ex, _maxClaimAttempts);
                return [];
            }
            finally
            {
                // Claimed or not, the rows leave the tracker. After a lost race the next attempt re-reads them with their
                // current versions; after any other failure (a provider error, cancellation) a half-claimed row left
                // tracked as Modified would be re-sent by the next SaveChanges on this context.
                DetachRange(candidates);
            }
        }
    }

    // The claim query applies the partition rule to what was committed when it read. A message of the same partition that
    // sorts earlier and commits after that read - stored late by a producer whose clock runs behind or whose transaction
    // committed late, or a requeued dead letter - is invisible to it, while a second processor that reads after that
    // commit takes the late message as the partition's head and the one this claim took as merely later: both claims
    // succeed, each on its own row, and two messages of one partition would be delivered at once. So once the claim has
    // committed the rule is checked again, and a claimed message that another message of its partition now holds back is
    // handed back before anyone delivers it. Every claim commits before it checks, so of two such claims the later check
    // always sees the other's committed claim, or the earlier message still pending: at most one keeps its message, and
    // at worst both hand theirs back and the next poll claims the partition's true head.
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    private async Task<IReadOnlyList<OutboxEntity>> HandBackContestedAsync(List<OutboxEntity> claimed, CancellationToken cancellationToken)
    {
        var partitioned = claimed.Where(e => e.PartitionKey is not null).Select(e => e.Sequence).ToList();
        if (partitioned.Count == 0) return claimed;

        var uncontested = await WhereNotHeldBack(
                _context.Set<OutboxEntity>().AsNoTracking().Where(e => partitioned.Contains(e.Sequence)),
                Now())
            .Select(e => e.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (uncontested.Count == partitioned.Count) return claimed;

        var kept = new List<OutboxEntity>(claimed.Count);
        foreach (var entity in claimed)
        {
            if (entity.PartitionKey is null || uncontested.Contains(entity.Sequence))
            {
                kept.Add(entity);
                continue;
            }

            // Nothing was tried, so no attempt is counted; the partition rule orders it again on the next poll. A hand-back
            // that does not land leaves the lease to run out, which is as safe: the message is not delivered from here.
            await ReleaseCoreAsync(entity.Id, new OutboxClaimToken(entity.RowVersion, entity.AttemptCount), cancellationToken).ConfigureAwait(false);
            EfCoreLog.ContestedClaimHandedBack(_logger, entity.Id);
        }

        return kept;
    }

    // The partition rule: a partitioned message is held back while another unfinished message of its partition (same key
    // and handler) sorts before it, and while any message of its partition is being delivered (holds a live lease) - even
    // one that sorts after it, such as the successor of a requeued dead letter. Evaluated by the database against committed
    // rows. The same rule selects the claim's candidates and re-checks the committed claim.
    private IQueryable<OutboxEntity> WhereNotHeldBack(IQueryable<OutboxEntity> source, DateTime now)
    {
        var messages = _context.Set<OutboxEntity>();
        return source.Where(e => e.PartitionKey == null || !messages.Any(p =>
            p.PartitionKey == e.PartitionKey &&
            p.HandlerName == e.HandlerName &&
            p.Sequence != e.Sequence &&
            (p.Status == OutboxMessageStatus.Pending || p.Status == OutboxMessageStatus.InProgress) &&
            (p.CreatedAt < e.CreatedAt || (p.CreatedAt == e.CreatedAt && p.Sequence < e.Sequence) ||
             (p.Status == OutboxMessageStatus.InProgress && p.LockedUntil > now))));
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        if (!OutboxClaimToken.TryParse(claim.Token, out var token)) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            var next = token.Next();
            return await Claimed(claim.MessageId, token)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Status, OutboxMessageStatus.Processed)
                        .SetProperty(e => e.ProcessedAt, (DateTime?)now)
                        .SetProperty(e => e.LockedUntil, (DateTime?)null)
                        .SetProperty(e => e.RowVersion, next.RowVersion),
                    cancellationToken)
                .ConfigureAwait(false) > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        if (!OutboxClaimToken.TryParse(claim.Token, out var token)) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The token's attempt count is the row's while the token matches it, so the new count needs no read.
            var attempts = token.AttemptCount + 1;
            var next = token.Next();
            var updated = await Claimed(claim.MessageId, token)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.AttemptCount, attempts)
                        .SetProperty(e => e.LastError, error)
                        .SetProperty(e => e.NextRetryAt, nextRetryAt)
                        .SetProperty(e => e.Status, OutboxMessageStatus.Pending)
                        .SetProperty(e => e.LockedUntil, (DateTime?)null)
                        .SetProperty(e => e.RowVersion, next.RowVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            return updated > 0 ? attempts : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<bool> DeferAsync(OutboxClaim claim, DateTime notBefore, string? reason, CancellationToken cancellationToken)
    {
        if (!OutboxClaimToken.TryParse(claim.Token, out var token)) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A failed attempt without the count: nothing was tried. The partition rule puts the message back in its place
            // by (CreatedAt, Sequence).
            var next = token.Next();
            return await Claimed(claim.MessageId, token)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.LastError, reason)
                        .SetProperty(e => e.NextRetryAt, (DateTime?)notBefore)
                        .SetProperty(e => e.Status, OutboxMessageStatus.Pending)
                        .SetProperty(e => e.LockedUntil, (DateTime?)null)
                        .SetProperty(e => e.RowVersion, next.RowVersion),
                    cancellationToken)
                .ConfigureAwait(false) > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken)
    {
        if (!OutboxClaimToken.TryParse(claim.Token, out var token)) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The attempt that exhausted the budget is an attempt too; count it so the dead letter tells the whole story.
            var attempts = token.AttemptCount + 1;
            var now = Now();
            var next = token.Next();
            return await Claimed(claim.MessageId, token)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.AttemptCount, attempts)
                        .SetProperty(e => e.Status, OutboxMessageStatus.Failed)
                        .SetProperty(e => e.LastError, error)
                        .SetProperty(e => e.FailedAt, (DateTime?)now)
                        .SetProperty(e => e.NextRetryAt, (DateTime?)null)
                        .SetProperty(e => e.LockedUntil, (DateTime?)null)
                        .SetProperty(e => e.RowVersion, next.RowVersion),
                    cancellationToken)
                .ConfigureAwait(false) > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        if (!OutboxClaimToken.TryParse(claim.Token, out var token)) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A lease that ran out is not renewed even while the claim still matches the row: the message has been
            // claimable since, and its partition may already be delivering an earlier message that a claim let through.
            var now = Now();
            var leasedUntil = now + _visibilityTimeout;
            var next = token.Next();
            var renewed = await Claimed(claim.MessageId, token)
                .Where(e => e.LockedUntil > now)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.LockedUntil, (DateTime?)leasedUntil)
                        .SetProperty(e => e.RowVersion, next.RowVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            return renewed > 0 ? new OutboxClaim(claim.MessageId, next.ToString(), leasedUntil) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var claim in claims)
                if (OutboxClaimToken.TryParse(claim.Token, out var token))
                    await ReleaseCoreAsync(claim.MessageId, token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Back to pending without counting an attempt: nothing was tried.
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    private Task<int> ReleaseCoreAsync(Guid messageId, OutboxClaimToken token, CancellationToken cancellationToken)
    {
        var next = token.Next();
        return Claimed(messageId, token)
            .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, OutboxMessageStatus.Pending)
                    .SetProperty(e => e.LockedUntil, (DateTime?)null)
                    .SetProperty(e => e.RowVersion, next.RowVersion),
                cancellationToken);
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int limit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await _context.Set<OutboxEntity>()
                .AsNoTracking()
                .Where(e => e.Status == OutboxMessageStatus.Failed)
                // A dead letter without a failure time counts as older than any other, whatever the provider's null order.
                .OrderBy(e => e.FailedAt == null ? 0 : 1)
                .ThenBy(e => e.FailedAt)
                .ThenBy(e => e.Sequence)
                .Take(limit)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return rows.Select(OutboxEntityMapper.ToMessage).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The version is read first so the new one is computed here (see OutboxClaimToken.Next); a requeue or purge
            // that lands in between changes the version or deletes the row, and this one then changes nothing.
            var version = await _context.Set<OutboxEntity>()
                .AsNoTracking()
                .Where(e => e.Id == messageId && e.Status == OutboxMessageStatus.Failed)
                .Select(e => (uint?)e.RowVersion)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (version is not { } current) return false;

            // A fresh budget; LastError stays as the record of why it failed the first time round.
            var next = unchecked(current + 1);
            return await _context.Set<OutboxEntity>()
                .Where(e => e.Id == messageId && e.Status == OutboxMessageStatus.Failed && e.RowVersion == current)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Status, OutboxMessageStatus.Pending)
                        .SetProperty(e => e.AttemptCount, 0)
                        .SetProperty(e => e.NextRetryAt, (DateTime?)null)
                        .SetProperty(e => e.FailedAt, (DateTime?)null)
                        .SetProperty(e => e.LockedUntil, (DateTime?)null)
                        .SetProperty(e => e.RowVersion, next),
                    cancellationToken)
                .ConfigureAwait(false) > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A dead letter without a failure time (one dead-lettered before the outbox table had the column) counts as
    ///     older than any cut-off. Deleted a bounded page at a time.
    /// </remarks>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<int> PurgeDeadLettersAsync(DateTime failedBefore, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PagedDelete.RunAsync(OutboxRetentionRules.DeadLettersFailedBy(_context, failedBefore), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public async Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messages = _context.Set<OutboxEntity>().AsNoTracking();
            var pending = messages.Where(e => e.Status == OutboxMessageStatus.Pending || e.Status == OutboxMessageStatus.InProgress);

            var pendingCount = await pending.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var oldest = pendingCount == 0
                ? null
                : await pending.OrderBy(e => e.CreatedAt).Select(e => (DateTime?)e.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var deadLetters = await messages.LongCountAsync(e => e.Status == OutboxMessageStatus.Failed, cancellationToken).ConfigureAwait(false);

            return new OutboxBacklog(pendingCount, deadLetters, oldest);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The row a claim holds: in progress, under exactly the version and attempt count the claim (or its latest renewal)
    // wrote. Any reclaim, renewal or finalize since has moved the version, so an UPDATE through this matches no row for a
    // lost claim - which is the contract's false/0/null, never an exception.
    private IQueryable<OutboxEntity> Claimed(Guid messageId, OutboxClaimToken token)
        => _context.Set<OutboxEntity>().Where(e =>
            e.Id == messageId &&
            e.Status == OutboxMessageStatus.InProgress &&
            e.RowVersion == token.RowVersion &&
            e.AttemptCount == token.AttemptCount);

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private void DetachRange(IEnumerable<OutboxEntity> entities)
    {
        foreach (var entity in entities)
            _context.Entry(entity).State = EntityState.Detached;
    }
}
