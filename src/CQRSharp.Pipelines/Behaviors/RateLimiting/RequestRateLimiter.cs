using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     The limiter behind the rate-limiting behavior: a token bucket per caller (and, in
///     <see cref="RateLimitScope.PerRequestType" /> scope, per request type), configured by
///     <see cref="RateLimitingOptions" />. Thread-safe; register it once (the builder's <c>UseRateLimiting</c> does).
/// </summary>
/// <remarks>
///     <para>
///         <b>Trusted identity.</b> Buckets are keyed by the caller-supplied identifier. It must come from a trusted
///         source (e.g. an authenticated principal): with an attacker-controlled identifier a caller can sidestep their
///         own limit by varying it, and can inflate the number of buckets.
///     </para>
///     <para>
///         <b>Memory.</b> The limiter keeps about <see cref="RateLimitingOptions.MaxEntries" /> buckets (a few more while
///         concurrent requests from new callers race the reclaim). A bucket that has refilled completely carries no
///         state, so dropping it loses nothing: the idle sweep drops only those, and reaching
///         <see cref="RateLimitingOptions.MaxEntries" /> drops those first. Only when nearly every bucket is still in use
///         are the least recently used ones dropped, and a dropped caller starts again with a full bucket.
///     </para>
/// </remarks>
public sealed class RequestRateLimiter : IDisposable
{
    private readonly ConcurrentDictionary<BucketKey, TokenBucket> _buckets = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _maxTokens;
    private readonly double _replenishRatePerSecond;
    private readonly RateLimitScope _scope;
    private readonly int _maxEntries;
    private readonly int _lowWaterMark;
    private readonly TimeSpan _maxIdleTime;
    private readonly TimeSpan _requestSweepCadence;
    private readonly ITimer? _sweepTimer;

    // Maintained by whoever inserts or removes a bucket: ConcurrentDictionary.Count takes every lock.
    private int _count;
    private int _reclaiming;
    private long _lastRequestSweep;

    /// <summary>
    ///     Creates a limiter for <paramref name="options" />.
    /// </summary>
    /// <param name="options">The limiter's options.</param>
    /// <param name="timeProvider">The clock buckets refill against; defaults to <see cref="TimeProvider.System" />.</param>
    /// <exception cref="ArgumentException">Thrown when the options are invalid.</exception>
    public RequestRateLimiter(IOptions<RateLimitingOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value ?? throw new ArgumentException("The options carry no value.", nameof(options));

        var validation = new RateLimitingOptionsValidator().Validate(Options.DefaultName, settings);
        if (validation.Failed)
            throw new ArgumentException(validation.FailureMessage, nameof(options));

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxTokens = settings.MaxTokens;
        _replenishRatePerSecond = settings.ReplenishRatePerSecond;
        _scope = settings.Scope;
        _maxEntries = settings.MaxEntries;
        _lowWaterMark = settings.MaxEntries - Math.Max(1, settings.MaxEntries / 10);
        _maxIdleTime = settings.MaxIdleTime;

        if (_maxIdleTime <= TimeSpan.Zero) return;

        if (settings.CleanupInterval > TimeSpan.Zero)
        {
            _sweepTimer = _timeProvider.CreateTimer(_ => SweepIdle(), null, settings.CleanupInterval, settings.CleanupInterval);
        }
        else
        {
            _requestSweepCadence = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, _maxIdleTime.Ticks / 2));
            _lastRequestSweep = _timeProvider.GetTimestamp();
        }
    }

    /// <summary>
    ///     Takes a token from the bucket of <paramref name="userId" /> (and, in
    ///     <see cref="RateLimitScope.PerRequestType" /> scope, <paramref name="requestType" />), if it holds one.
    /// </summary>
    /// <param name="userId">The caller's identifier (<see cref="IRateLimitedContext.UserId" />).</param>
    /// <param name="requestType">The type of the request being admitted.</param>
    /// <param name="retryAfter">
    ///     When the request is rejected, how long until the bucket holds a token again, in whole milliseconds rounded up,
    ///     so a caller that waits it out with a timer (which counts whole milliseconds) is never back early; otherwise
    ///     <see cref="TimeSpan.Zero" />.
    /// </param>
    /// <returns><see langword="true" /> when the request may proceed; <see langword="false" /> when it is over the limit.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="userId" /> is null, empty or white space.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requestType" /> is <see langword="null" />.</exception>
    public bool TryAcquire(string userId, Type requestType, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(requestType);

        SweepIdleIfDue();

        var key = new BucketKey(userId, _scope == RateLimitScope.PerRequestType ? requestType : null);
        while (true)
        {
            var bucket = GetOrAdd(key);
            switch (bucket.TryConsume(out retryAfter))
            {
                case ConsumeResult.Granted:
                    return true;
                case ConsumeResult.Denied:
                    return false;
                default:
                    // Retired by a reclaim that has not removed it yet: finish the removal, then use its successor.
                    Remove(key, bucket);
                    break;
            }
        }
    }

    /// <summary>Stops the idle-sweep timer.</summary>
    public void Dispose() => _sweepTimer?.Dispose();

    /// <summary>The number of buckets the limiter holds.</summary>
    internal int BucketCount => Volatile.Read(ref _count);

    private TokenBucket GetOrAdd(BucketKey key)
    {
        while (true)
        {
            if (_buckets.TryGetValue(key, out var existing))
                return existing;

            // Reclaim before inserting, so the new bucket (full, and so a candidate for the lossless pass) is never the
            // one reclaimed on its own behalf.
            if (Volatile.Read(ref _count) >= _maxEntries)
                Reclaim();

            var created = new TokenBucket(_maxTokens, _replenishRatePerSecond, _timeProvider);
            if (!_buckets.TryAdd(key, created)) continue;

            Interlocked.Increment(ref _count);
            return created;
        }
    }

    // Brings the bucket count below MaxEntries: first every bucket that has refilled completely (lossless), then, when
    // more than the low-water mark remain, the least recently used ones down to it. Freeing a tenth at a time is what
    // keeps a flood of new callers from paying a full scan per request. One reclaim at a time; a request that finds one
    // running proceeds, briefly overshooting MaxEntries.
    private void Reclaim()
    {
        if (Interlocked.CompareExchange(ref _reclaiming, 1, 0) != 0) return;
        try
        {
            var now = _timeProvider.GetTimestamp();
            foreach (var (key, bucket) in _buckets)
                if (bucket.TryRetireIfFull(now, TimeSpan.Zero))
                    Remove(key, bucket);

            if (Volatile.Read(ref _count) <= _lowWaterMark) return;

            var byLastUse = _buckets.ToArray();
            Array.Sort(byLastUse, static (left, right) => left.Value.LastUsedTimestamp.CompareTo(right.Value.LastUsedTimestamp));
            foreach (var (key, bucket) in byLastUse)
            {
                if (Volatile.Read(ref _count) <= _lowWaterMark) break;
                bucket.Retire();
                Remove(key, bucket);
            }
        }
        finally
        {
            Volatile.Write(ref _reclaiming, 0);
        }
    }

    private void SweepIdleIfDue()
    {
        if (_sweepTimer is not null || _maxIdleTime <= TimeSpan.Zero) return;

        var now = _timeProvider.GetTimestamp();
        var last = Volatile.Read(ref _lastRequestSweep);
        if (_timeProvider.GetElapsedTime(last, now) < _requestSweepCadence) return;
        if (Interlocked.CompareExchange(ref _lastRequestSweep, now, last) != last) return;

        SweepIdle();
    }

    // Drops the buckets idle for MaxIdleTime that have also refilled completely: with a slow refill rate an idle bucket
    // can still be short of tokens, and dropping it would hand its caller a full one.
    private void SweepIdle()
    {
        var now = _timeProvider.GetTimestamp();
        foreach (var (key, bucket) in _buckets)
            if (bucket.TryRetireIfFull(now, _maxIdleTime))
                Remove(key, bucket);
    }

    // A retired bucket is removed by whoever gets there first; only that caller adjusts the count.
    private void Remove(BucketKey key, TokenBucket bucket)
    {
        if (_buckets.TryRemove(new KeyValuePair<BucketKey, TokenBucket>(key, bucket)))
            Interlocked.Decrement(ref _count);
    }

    /// <summary>The bucket identity: the caller, and the request type in per-request-type scope.</summary>
    private readonly record struct BucketKey(string UserId, Type? RequestType);
}

/// <summary>The outcome of taking a token from a <see cref="TokenBucket" />.</summary>
internal enum ConsumeResult
{
    Granted,
    Denied,

    /// <summary>The bucket was dropped from the limiter; its caller's state lives in a successor.</summary>
    Retired
}

/// <summary>
///     A token bucket refilled continuously against <see cref="TimeProvider" /> timestamps. Once retired it takes no more
///     tokens, which is what lets the limiter drop a bucket without a concurrent request consuming from the dropped copy.
/// </summary>
internal sealed class TokenBucket(int maxTokens, double replenishRatePerSecond, TimeProvider timeProvider)
{
    private readonly object _sync = new();
    private long _lastRefillTimestamp = timeProvider.GetTimestamp();
    private double _tokens = maxTokens;
    private bool _retired;

    // Read without the lock by the least-recently-used ordering, so kept in a 64-bit field accessed atomically.
    private long _lastUsedTimestamp = timeProvider.GetTimestamp();

    public long LastUsedTimestamp => Interlocked.Read(ref _lastUsedTimestamp);

    public ConsumeResult TryConsume(out TimeSpan retryAfter)
    {
        lock (_sync)
        {
            retryAfter = TimeSpan.Zero;
            if (_retired) return ConsumeResult.Retired;

            var now = timeProvider.GetTimestamp();
            Refill(now);
            Interlocked.Exchange(ref _lastUsedTimestamp, now);

            if (_tokens >= 1)
            {
                _tokens -= 1;
                return ConsumeResult.Granted;
            }

            retryAfter = RetryAfter((1 - _tokens) / replenishRatePerSecond);
            return ConsumeResult.Denied;
        }
    }

    // The time until one whole token has accrued, in whole milliseconds rounded up: timers count whole milliseconds and
    // Task.Delay drops the fraction, so an exact 999.93 ms would bring a caller that waits it out back 0.93 ms early, to
    // be rejected again. A rate of a token every few hundred years overflows TimeSpan.
    private static TimeSpan RetryAfter(double seconds)
    {
        var milliseconds = Math.Ceiling(seconds * 1000);
        return milliseconds >= TimeSpan.MaxValue.TotalMilliseconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks((long)milliseconds * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>
    ///     Retires the bucket when it has been unused for at least <paramref name="minimumIdle" /> and has refilled
    ///     completely, so dropping it loses nothing.
    /// </summary>
    public bool TryRetireIfFull(long now, TimeSpan minimumIdle)
    {
        lock (_sync)
        {
            if (_retired) return false;
            if (timeProvider.GetElapsedTime(_lastUsedTimestamp, now) < minimumIdle) return false;

            Refill(now);
            if (_tokens < maxTokens) return false;

            _retired = true;
            return true;
        }
    }

    /// <summary>Retires the bucket whatever it holds.</summary>
    public void Retire()
    {
        lock (_sync) _retired = true;
    }

    private void Refill(long now)
    {
        if (now <= _lastRefillTimestamp) return;
        _tokens = Math.Min(maxTokens, _tokens + timeProvider.GetElapsedTime(_lastRefillTimestamp, now).TotalSeconds * replenishRatePerSecond);
        _lastRefillTimestamp = now;
    }
}
