using System.Collections.Concurrent;
using CQRSharp.Pipelines;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Idempotency;

/// <summary>
///     A thread-safe in-process idempotency store for development, tests, and single-node demos. It is NOT durable:
///     claims live in process memory and are lost on restart, so it only deduplicates within a single process lifetime
///     — use a database- or Redis-backed store for cross-process at-most-once semantics. A claim is atomic (a single
///     concurrent caller wins via a compare-and-swap on the key), and claims that were never released age out after a
///     retention window so the store does not grow without bound.
/// </summary>
internal sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    // Maps a claimed key to the UTC time it was claimed; an entry older than the retention window is treated as free.
    private readonly ConcurrentDictionary<string, DateTime> _claims = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;

    // Expired claims are otherwise only replaced when the SAME key returns, so with a unique key per request (the normal
    // case) the dictionary would grow forever. Sweep them at most once per retention window, from the claim path.
    private long _nextSweepTicks;

    public InMemoryIdempotencyStore(TimeProvider timeProvider, IOptions<InMemoryIdempotencyStoreOptions> options)
    {
        _timeProvider = timeProvider;
        _retention = options.Value.Retention;
    }

    public Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SweepExpired(now);

        while (true)
        {
            // The common path: the key is unclaimed, so add it atomically and win the claim.
            if (_claims.TryAdd(key, now))
                return Task.FromResult(true);

            if (!_claims.TryGetValue(key, out var claimedAt))
                continue; // released between the add and the read; retry to claim it

            if (now - claimedAt < _retention)
                return Task.FromResult(false); // a live claim exists -> this is a duplicate

            // The existing claim has aged past the retention window; atomically take it over.
            if (_claims.TryUpdate(key, now, claimedAt))
                return Task.FromResult(true);

            // Lost the race to another caller; retry from the top.
        }
    }

    private void SweepExpired(DateTime now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.Ticks < due) return;

        // One sweeper per window; a caller that loses the swap skips (the winner is already sweeping).
        if (Interlocked.CompareExchange(ref _nextSweepTicks, (now + _retention).Ticks, due) != due) return;

        foreach (var claim in _claims)
            if (now - claim.Value >= _retention)
                // Remove only the exact expired pair, so a claim that was just taken over (a fresh timestamp) survives.
                _claims.TryRemove(claim);
    }

    public Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        _claims.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
