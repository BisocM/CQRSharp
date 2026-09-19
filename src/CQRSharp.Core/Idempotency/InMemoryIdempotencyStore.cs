using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Core.Options;
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

    public InMemoryIdempotencyStore(TimeProvider timeProvider, IOptions<InMemoryIdempotencyStoreOptions> options)
    {
        _timeProvider = timeProvider;
        _retention = options.Value.Retention;
    }

    public Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

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

    public Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        _claims.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
