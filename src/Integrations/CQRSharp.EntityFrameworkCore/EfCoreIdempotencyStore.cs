using System.Diagnostics.CodeAnalysis;
using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.EntityFrameworkCore.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     A durable <see cref="IIdempotencyStore" /> backed by an EF Core <typeparamref name="TContext" />. A claim is one
///     row in the idempotency table keyed by the idempotency key. The claim is made race-safe by the table's unique
///     primary key: exactly one concurrent INSERT of a given key can succeed, and the loser of that race
///     (<see cref="DbUpdateException" />) re-evaluates the now-existing row within a bounded retry loop so it correctly
///     reports a duplicate. A row whose <see cref="IdempotencyEntity.ExpiresAt" /> has passed is treated as a free slot
///     and taken over; that take-over is itself made race-safe by an optimistic-concurrency token
///     (<see cref="IdempotencyEntity.RowVersion" />), so two processes reading the same expired row cannot both revive
///     it — the loser's stale token raises <see cref="DbUpdateConcurrencyException" />, re-reads the now-live row, and
///     reports a duplicate. This both deduplicates within the retention window and self-heals a crashed claimant's key.
///     Keys are compared with ordinal, case-sensitive equality (see <see cref="IIdempotencyStore" />); on a database
///     whose column collation folds case the relational key comparison diverges from that contract.
///     All time is read from the injected <see cref="TimeProvider" /> so expiry is deterministic under test.
/// </summary>
/// <typeparam name="TContext">The application's <see cref="DbContext" /> that maps <see cref="IdempotencyEntity" />.</typeparam>
internal sealed class EfCoreIdempotencyStore<TContext> : IIdempotencyStore where TContext : DbContext
{
    private const string AotMessage =
        "EF Core uses runtime query compilation and is not compatible with Native AOT or full trimming.";

    // Bounded retry budget for the INSERT/take-over race. A concurrent caller can at most insert the key once before
    // we re-read it, so a handful of attempts is ample for the unique-key contention to resolve to a single winner.
    private const int MaxClaimAttempts = 3;

    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EfCoreIdempotencyStore<TContext>> _logger;
    private readonly TimeSpan _retention;

    // A DbContext is not thread-safe and forbids overlapping operations. The idempotency behavior may issue concurrent
    // claims against the one shared store/context (the contract suite does exactly this with 16 parallel callers), so
    // this gate serializes every context access. Cross-caller claim safety still rests on the unique primary key; this
    // lock only guards the single shared context from concurrent use within this instance.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store over a <paramref name="context" />, reading time from <paramref name="timeProvider" />.</summary>
    public EfCoreIdempotencyStore(
        TContext context,
        TimeProvider timeProvider,
        IOptions<EfCoreIdempotencyStoreOptions> options,
        ILogger<EfCoreIdempotencyStore<TContext>> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
        _retention = options.Value.Retention;
    }

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public async Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ClaimAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    private async Task<bool> ClaimAsync(string key, CancellationToken cancellationToken)
    {
        // Bounded INSERT/take-over retry. Each attempt: read the row; if absent, INSERT it and win; if present but
        // expired, take it over (refresh ExpiresAt) and win; if present and live, lose as a duplicate. A concurrent
        // INSERT of the same key fails the unique-key save (DbUpdateException), which we treat as "someone beat me to
        // it": detach the rejected copy and re-read on the next attempt, where the now-present row resolves the claim.
        for (var attempt = 1; ; attempt++)
        {
            var now = Now();
            var expiresAt = now + _retention;

            var existing = await _context.Set<IdempotencyEntity>()
                .FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                // No row: try to create the claim. The unique primary key makes this the atomic race point.
                var entity = new IdempotencyEntity { Key = key, ExpiresAt = expiresAt };
                _context.Set<IdempotencyEntity>().Add(entity);

                try
                {
                    await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (DbUpdateException ex) when (attempt < MaxClaimAttempts)
                {
                    // A competing caller inserted the same key between our read and save. Detach our rejected copy and
                    // re-evaluate against the freshly-readable row on the next attempt.
                    _logger.LogDebug(ex,
                        "Idempotency claim of {Key} lost an insert race on attempt {Attempt}/{MaxAttempts}; retrying.",
                        key, attempt, MaxClaimAttempts);

                    _context.Entry(entity).State = EntityState.Detached;
                    continue;
                }
            }

            // A row exists. If it is still live the key is a duplicate; if it has expired, take it over.
            if (existing.ExpiresAt > now)
            {
                // Detach so the live row we only read does not linger as tracked state on the shared context.
                _context.Entry(existing).State = EntityState.Detached;
                return false;
            }

            // Take over the expired row: refresh its expiry and bump the concurrency token so the save only lands for
            // the process that still held the row-version it read. Without this bump the UPDATE carries no version
            // predicate, so two processes reading the same expired row would BOTH save and BOTH wrongly win the claim.
            existing.ExpiresAt = expiresAt;
            existing.RowVersion++;

            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxClaimAttempts)
            {
                // Another caller took over the expired row first (its row-version moved out from under us). Detach and
                // re-read: the row is now live again, so the next attempt correctly resolves this claim as a duplicate.
                _logger.LogDebug(ex,
                    "Idempotency take-over of {Key} lost a concurrency race on attempt {Attempt}/{MaxAttempts}; retrying.",
                    key, attempt, MaxClaimAttempts);

                _context.Entry(existing).State = EntityState.Detached;
            }
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public async Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _context.Set<IdempotencyEntity>()
                .FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
                .ConfigureAwait(false);

            // Releasing an unknown (or already-expired-and-removed) key is a no-op.
            if (existing is null)
                return;

            _context.Set<IdempotencyEntity>().Remove(existing);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;
}
