using CQRSharp.Pipelines;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Idempotency;

/// <summary>
///     A thread-safe in-process idempotency store for development, tests, and single-node demos. It is NOT durable:
///     claims live in process memory and are lost on restart, so it only deduplicates within a single process lifetime
///     — use a database- or Redis-backed store for cross-process at-most-once semantics. Claims are atomic (one
///     concurrent caller wins), a completed request's result is kept for replay, and every entry ages out after the
///     retention window, so the store does not grow without bound.
/// </summary>
internal sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    // One lock rather than lock-free structures: every operation is a read-check-write on a key's entry, and this store
    // is for development and tests, where being obviously correct matters more than contention.
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;
    private DateTime _nextSweep;

    public InMemoryIdempotencyStore(TimeProvider timeProvider, IOptions<InMemoryIdempotencyStoreOptions> options)
    {
        _timeProvider = timeProvider;
        _retention = options.Value.Retention;
    }

    public Task<IdempotencyClaim> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            SweepExpired(now);

            if (_entries.TryGetValue(key, out var existing) && now - existing.ClaimedAt < _retention)
                return Task.FromResult(existing.Completed
                    ? IdempotencyClaim.Completed(existing.Result)
                    : IdempotencyClaim.InProgress);

            // Free, or the previous claim aged out (a crashed claimant's key self-heals this way).
            _entries[key] = new Entry(now, false, null);
            return Task.FromResult(IdempotencyClaim.Claimed);
        }
    }

    public Task CompleteAsync(string key, byte[]? result, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Keeps ClaimedAt: the retention window runs from the claim, not from completion.
            if (_entries.TryGetValue(key, out var existing) && !existing.Completed)
                _entries[key] = existing with { Completed = true, Result = result };
        }

        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Only an in-flight claim can be released; a completed request stays remembered.
            if (_entries.TryGetValue(key, out var existing) && !existing.Completed)
                _entries.Remove(key);
        }

        return Task.CompletedTask;
    }

    // Expired entries are otherwise only replaced when the SAME key returns, so with a unique key per request (the normal
    // case) the dictionary would grow forever. Swept at most once per retention window, from the claim path.
    private void SweepExpired(DateTime now)
    {
        if (now < _nextSweep) return;
        _nextSweep = now + _retention;

        List<string>? expired = null;
        foreach (var entry in _entries)
            if (now - entry.Value.ClaimedAt >= _retention)
                (expired ??= []).Add(entry.Key);

        if (expired is null) return;
        foreach (var key in expired) _entries.Remove(key);
    }

    private sealed record Entry(DateTime ClaimedAt, bool Completed, byte[]? Result);
}
