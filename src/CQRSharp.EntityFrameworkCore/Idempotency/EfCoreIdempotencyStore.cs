using System.Diagnostics.CodeAnalysis;
using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
///     All time is read from the injected <see cref="TimeProvider" /> so expiry is deterministic under test. Expired rows
///     are deleted by the idempotency retention service, off the claim path.
/// </summary>
/// <typeparam name="TContext">The application's <see cref="DbContext" /> that maps <see cref="IdempotencyEntity" />.</typeparam>
internal sealed class EfCoreIdempotencyStore<TContext> : IIdempotencyStore where TContext : DbContext
{
    // Bounded retry budget for the INSERT/take-over race. A concurrent caller can at most insert the key once before
    // we re-read it, so a handful of attempts is ample for the unique-key contention to resolve to a single winner.
    private const int MaxClaimAttempts = 3;

    // The store is a singleton that opens a short-lived scope - and so a fresh TContext - per operation: a DbContext is
    // scoped, so holding one here would be a captive dependency (a startup failure under scope validation; otherwise one
    // root context, tracking every claim, for the process lifetime). A private context also means a claim or release
    // can never flush the caller's own pending changes, and concurrent claims each have a context of their own.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EfCoreIdempotencyStore<TContext>> _logger;
    private readonly TimeSpan _retention;

    /// <summary>Creates the store, which resolves a fresh <typeparamref name="TContext" /> per operation.</summary>
    public EfCoreIdempotencyStore(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<EfCoreIdempotencyStoreOptions> options,
        ILogger<EfCoreIdempotencyStore<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _retention = options.Value.Retention;
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public Task<IdempotencyClaim> TryClaimAsync(string key, string? fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        // The relational columns are bounded. A value the provider would truncate or reject is refused here with the
        // cause named; the failed insert would otherwise read as a lost insert race, be retried, and fail each time.
        if (key.Length > IdempotencyEntityConfiguration.KeyMaxLength)
            throw new InvalidOperationException(
                $"The idempotency key is {key.Length} characters long, more than the {IdempotencyEntityConfiguration.KeyMaxLength} the EF Core idempotency store holds; hash longer natural keys down before using them as idempotency keys.");
        if (fingerprint is { Length: > IdempotencyEntityConfiguration.FingerprintMaxLength })
            throw new InvalidOperationException(
                $"The payload fingerprint is {fingerprint.Length} characters long, more than the {IdempotencyEntityConfiguration.FingerprintMaxLength} the EF Core idempotency store holds; pass a digest.");

        return WithContextAsync(context => ClaimAsync(context, key, fingerprint, cancellationToken));
    }

    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    private async Task<TResult> WithContextAsync<TResult>(Func<TContext, Task<TResult>> operation)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
            return await operation(scope.ServiceProvider.GetRequiredService<TContext>()).ConfigureAwait(false);
    }

    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    private async Task<IdempotencyClaim> ClaimAsync(TContext context, string key, string? fingerprint, CancellationToken cancellationToken)
    {
        // Bounded INSERT/take-over retry. Each attempt: read the row; if absent, INSERT it and win; if present but
        // expired, take it over (refresh ExpiresAt) and win; if present and live, lose as a duplicate. A concurrent
        // INSERT of the same key fails the unique-key save (DbUpdateException), which we treat as "someone beat me to
        // it": detach the rejected copy and re-read on the next attempt, where the now-present row resolves the claim.
        // The context is this operation's own and is disposed with it; the detaches only keep the next attempt's read
        // from meeting the rejected copy in the change tracker.
        for (var attempt = 1; ; attempt++)
        {
            var now = Now();

            // Whole milliseconds: the expiry doubles as the claim's identity on release, so it has to survive a round
            // trip through providers that store less than .NET's 100 ns tick precision.
            var expiresAt = new DateTime((now + _retention).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

            var existing = await context.Set<IdempotencyEntity>()
                .AsTracking()
                .FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                // No row: try to create the claim. The unique primary key makes this the atomic race point.
                var entity = new IdempotencyEntity { Key = key, ExpiresAt = expiresAt, Fingerprint = fingerprint };
                context.Set<IdempotencyEntity>().Add(entity);

                try
                {
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return IdempotencyClaim.ClaimedWith(TokenFor(expiresAt));
                }
                catch (DbUpdateException ex) when (attempt < MaxClaimAttempts)
                {
                    // A competing caller inserted the same key between our read and save. Detach our rejected copy and
                    // re-evaluate against the freshly-readable row on the next attempt.
                    EfCoreLog.IdempotencyInsertRaceLost(_logger, ex, key, attempt, MaxClaimAttempts);

                    context.Entry(entity).State = EntityState.Detached;
                    continue;
                }
            }

            // A row exists. If it is still live the key is a duplicate; if it has expired, take it over.
            if (existing.ExpiresAt > now)
            {
                // The same key for a different request: neither the original's outcome nor a second run is the answer.
                if (fingerprint is not null && existing.Fingerprint is not null &&
                    !string.Equals(fingerprint, existing.Fingerprint, StringComparison.Ordinal))
                    return IdempotencyClaim.PayloadMismatch;

                return existing.Completed ? IdempotencyClaim.Completed(existing.Result) : IdempotencyClaim.InProgress;
            }

            // Take over the expired row: refresh its expiry and bump the concurrency token so the save only lands for
            // the process that still held the row-version it read. Without this bump the UPDATE carries no version
            // predicate, so two processes reading the same expired row would BOTH save and BOTH wrongly win the claim.
            existing.ExpiresAt = expiresAt;
            existing.Completed = false;
            existing.Result = null;
            existing.Fingerprint = fingerprint;
            existing.RowVersion++;

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return IdempotencyClaim.ClaimedWith(TokenFor(expiresAt));
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxClaimAttempts)
            {
                // Another caller took over the expired row first (its row-version moved out from under us). Detach and
                // re-read: the row is now live again, so the next attempt correctly resolves this claim as a duplicate.
                EfCoreLog.IdempotencyTakeOverRaceLost(_logger, ex, key, attempt, MaxClaimAttempts);

                context.Entry(existing).State = EntityState.Detached;
            }
        }
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public Task CompleteAsync(string key, string claimToken, byte[]? result, CancellationToken cancellationToken)
    {
        // Same ownership rule as ReleaseAsync: only while the row is still the caller's claim.
        if (!TryReadToken(claimToken, out var claimedExpiry))
            return Task.CompletedTask;

        return WithContextAsync(context => context.Set<IdempotencyEntity>()
            .Where(e => e.Key == key && e.ExpiresAt == claimedExpiry && !e.Completed)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(e => e.Completed, true).SetProperty(e => e.Result, result),
                cancellationToken));
    }

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public Task ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken)
    {
        // Only while the row is still the caller's claim: an unknown, expired-and-purged, or taken-over key is left alone.
        if (!TryReadToken(claimToken, out var claimedExpiry))
            return Task.CompletedTask;

        return WithContextAsync(context => context.Set<IdempotencyEntity>()
            .Where(e => e.Key == key && e.ExpiresAt == claimedExpiry && !e.Completed)
            .ExecuteDeleteAsync(cancellationToken));
    }

    private static string TokenFor(DateTime expiresAt) => expiresAt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryReadToken(string? token, out DateTime expiresAt)
    {
        expiresAt = default;
        if (!long.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return false;

        expiresAt = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;
}
