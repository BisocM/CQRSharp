using CQRSharp.Persistence;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Idempotency;

/// <summary>
///     A thread-safe in-process idempotency store for development, tests, and single-node demos. It is NOT durable:
///     claims live in process memory and are lost on restart, so it only deduplicates within a single process lifetime
///     — use a database- or Redis-backed store for cross-process at-most-once semantics. Claims are atomic (one
///     concurrent caller wins), a completed request's result is kept for replay, a key reused with a different payload
///     fingerprint is reported as a mismatch, and every entry ages out after the retention window, so the store does
///     not grow without bound.
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

    public Task<IdempotencyClaim> TryClaimAsync(string key, string? fingerprint, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            SweepExpired(now);

            if (_entries.TryGetValue(key, out var existing) && now - existing.ClaimedAt < _retention)
            {
                // The same key for a different request: neither the original's outcome nor a second run is the answer.
                if (fingerprint is not null && existing.Fingerprint is not null &&
                    !string.Equals(fingerprint, existing.Fingerprint, StringComparison.Ordinal))
                    return Task.FromResult(IdempotencyClaim.PayloadMismatch);

                return Task.FromResult(existing.Completed
                    ? IdempotencyClaim.Completed(existing.Result)
                    : IdempotencyClaim.InProgress);
            }

            // Free, or the previous claim aged out (an abandoned claim self-heals this way). A fresh token identifies
            // this claim, so the previous claimant - if it is still running - can neither complete nor release it.
            var token = Guid.NewGuid().ToString("N");
            _entries[key] = new Entry(now, false, null, fingerprint, token);
            return Task.FromResult(IdempotencyClaim.ClaimedWith(token));
        }
    }

    public Task CompleteAsync(string key, string claimToken, byte[]? result, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Keeps ClaimedAt: the retention window runs from the claim, not from completion.
            if (_entries.TryGetValue(key, out var existing) && !existing.Completed && existing.Token == claimToken)
                _entries[key] = existing with { Completed = true, Result = result };
        }

        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Only the caller's own, in-flight claim is released; a completed request stays remembered.
            if (_entries.TryGetValue(key, out var existing) && !existing.Completed && existing.Token == claimToken)
                _entries.Remove(key);
        }

        return Task.CompletedTask;
    }

    /// <summary>How many keys the store holds, expired ones not yet swept included; for in-process inspection in tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
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

    private sealed record Entry(DateTime ClaimedAt, bool Completed, byte[]? Result, string? Fingerprint, string Token);
}
