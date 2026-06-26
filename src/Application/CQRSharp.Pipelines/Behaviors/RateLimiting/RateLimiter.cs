using CQRSharp.Pipelines.Options;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Behaviors.RateLimiting;

/// <summary>
///     A highly concurrent rate limiter using a sharded LRU cache and lock-minimized token buckets.
///     In addition to count-based LRU eviction, stale buckets are removed based on idle time
///     on each request and via a periodic cleanup timer.
/// </summary>
/// <remarks>
///     <para>
///         <b>Trusted identity.</b> Buckets are keyed by the caller-supplied user identifier (and, in
///         <see cref="RateLimitScope.PerCommand" /> scope, the request type). The identifier must come from a trusted
///         source (e.g. an authenticated principal). With unauthenticated or attacker-controlled identifiers a caller
///         can sidestep their own limit by varying the identifier and can inflate bucket cardinality.
///     </para>
///     <para>
///         <b>Cardinality / memory.</b> The number of distinct keys retained is bounded by
///         <see cref="RateLimiterOptions.MaxEntries" /> via LRU eviction; size it for your expected active-key
///         cardinality. Hashing identifiers does not reduce cardinality — only <see cref="RateLimiterOptions.MaxEntries" />
///         and idle eviction bound memory.
///     </para>
/// </remarks>
public sealed class RateLimiter : IDisposable
{
    private readonly ShardedLruCache<object, TokenBucket> _cache;
    private readonly TimeSpan _cleanupCadence;
    private readonly ITimer? _cleanupTimer;
    private readonly RateLimiterOptions _config;
    private readonly TimeProvider _timeProvider;
    private readonly bool _usesTimer;
    private long _lastCleanupCheckpoint;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RateLimiter" /> class.
    /// </summary>
    /// <param name="config">Options for rate limiting behavior.</param>
    public RateLimiter(IOptions<RateLimiterOptions> config, TimeProvider? timeProvider = null)
    {
        _config = config.Value ?? throw new ArgumentNullException(nameof(config));
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateConfiguration(_config);

        //Initialize sharded LRU cache for concurrency
        var shardCount = Environment.ProcessorCount * 2;
        _cache = new ShardedLruCache<object, TokenBucket>(_config.MaxEntries, shardCount);
        _usesTimer = _config.CleanupInterval > TimeSpan.Zero;

        if (!_usesTimer && _config.MaxIdleTime > TimeSpan.Zero)
        {
            _cleanupCadence = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, _config.MaxIdleTime.Ticks / 2));
            _lastCleanupCheckpoint = _timeProvider.GetTimestamp();
        }

        //Schedule periodic cleanup if configured
        if (_usesTimer)
            _cleanupTimer = _timeProvider.CreateTimer(
                _ => CleanupStaleBuckets(),
                null,
                _config.CleanupInterval,
                _config.CleanupInterval);
    }

    /// <summary>
    ///     Disposes the cleanup timer.
    /// </summary>
    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }

    /// <summary>
    ///     Determines whether a request is allowed based on rate limiting rules.
    ///     Also cleans up stale buckets before evaluating the token bucket.
    /// </summary>
    /// <param name="userIdentifier">The unique identifier for the user making the request.</param>
    /// <param name="commandName">The name of the command being executed.</param>
    /// <returns><c>true</c> if the request is allowed; otherwise, <c>false</c>.</returns>
    public bool AllowRequest(string userIdentifier, string commandName)
    {
        if (string.IsNullOrWhiteSpace(userIdentifier))
            throw new ArgumentNullException(nameof(userIdentifier));
        if (string.IsNullOrWhiteSpace(commandName))
            throw new ArgumentNullException(nameof(commandName));

        CleanupStaleBucketsIfNeeded();

        //Determine key based on scope
        object key = _config.Scope == RateLimitScope.PerCommand
            ? new UserCommandKey(userIdentifier, commandName)
            : userIdentifier;

        var bucket = _cache.GetOrAdd(key, _ => new TokenBucket(
            _config.MaxTokens,
            _config.ReplenishRatePerSecond,
            _timeProvider));

        return bucket.TryConsume();
    }

    /// <summary>
    ///     Determines whether a request is allowed based on rate limiting rules.
    ///     This overload keys the per-command bucket by <see cref="Type" /> to avoid collisions between
    ///     different requests that share the same simple name.
    /// </summary>
    /// <param name="userIdentifier">The unique identifier for the user making the request.</param>
    /// <param name="requestType">The request type being executed.</param>
    /// <returns><c>true</c> if the request is allowed; otherwise, <c>false</c>.</returns>
    public bool AllowRequest(string userIdentifier, Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        if (string.IsNullOrWhiteSpace(userIdentifier))
            throw new ArgumentNullException(nameof(userIdentifier));

        CleanupStaleBucketsIfNeeded();

        object key = _config.Scope == RateLimitScope.PerCommand
            ? new UserCommandKey(userIdentifier, requestType)
            : userIdentifier;

        var bucket = _cache.GetOrAdd(key, _ => new TokenBucket(
            _config.MaxTokens,
            _config.ReplenishRatePerSecond,
            _timeProvider));

        return bucket.TryConsume();
    }

    /// <summary>
    ///     Removes token buckets that have been idle longer than the configured maximum idle time.
    /// </summary>
    private void CleanupStaleBuckets()
    {
        if (_config.MaxIdleTime <= TimeSpan.Zero) return;
        var threshold = _timeProvider.GetUtcNow().UtcDateTime - _config.MaxIdleTime;
        CleanupStaleBuckets(threshold);
    }

    private static void ValidateConfiguration(RateLimiterOptions config)
    {
        if (config.MaxTokens <= 0)
            throw new ArgumentException("MaxTokens must be > 0", nameof(config.MaxTokens));
        if (config.ReplenishRatePerSecond <= 0)
            throw new ArgumentException("ReplenishRatePerSecond must be > 0", nameof(config.ReplenishRatePerSecond));
        if (config.MaxEntries <= 0)
            throw new ArgumentException("MaxEntries must be > 0", nameof(config.MaxEntries));
    }

    private void CleanupStaleBucketsIfNeeded()
    {
        if (_usesTimer || _config.MaxIdleTime <= TimeSpan.Zero) return;

        var now = _timeProvider.GetTimestamp();
        var last = Volatile.Read(ref _lastCleanupCheckpoint);
        if (_timeProvider.GetElapsedTime(last, now) < _cleanupCadence) return;
        if (Interlocked.CompareExchange(ref _lastCleanupCheckpoint, now, last) != last) return;

        var threshold = _timeProvider.GetUtcNow().UtcDateTime - _config.MaxIdleTime;
        CleanupStaleBuckets(threshold);
    }

    private void CleanupStaleBuckets(DateTime threshold)
    {
        _cache.RemoveWhere(pair => pair.Value.LastAccessed < threshold);
    }

    /// <summary>
    ///     Composite key for per-command rate limiting.
    /// </summary>
    private sealed record UserCommandKey(string UserIdentifier, object CommandKey);
}

/// <summary>
///     A power-of-two sharded LRU cache to distribute lock contention.
/// </summary>
internal class ShardedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _mask;
    private readonly LruCache<TKey, TValue>[] _shards;

    public ShardedLruCache(int capacity, int shardCount)
    {
        var count = 1;
        while (count < shardCount) count <<= 1;
        _mask = count - 1;

        var perShard = capacity / count + 1;
        _shards = new LruCache<TKey, TValue>[count];
        for (var i = 0; i < count; i++)
            _shards[i] = new LruCache<TKey, TValue>(perShard);
    }

    private int GetShardIndex(TKey key)
    {
        return key.GetHashCode() & _mask;
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        return _shards[GetShardIndex(key)].GetOrAdd(key, factory);
    }

    public void RemoveWhere(Func<KeyValuePair<TKey, TValue>, bool> predicate)
    {
        foreach (var shard in _shards)
            shard.RemoveWhere(predicate);
    }
}

/// <summary>
///     Simple LRU cache with lock protection per shard.
/// </summary>
internal class LruCache<TKey, TValue>(int capacity)
    where TKey : notnull
{
    private readonly LinkedList<CacheItem> _list = new();
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _map = new(capacity);
    private readonly object _sync = new();

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                return node.Value.Value;
            }

            var value = factory(key);
            var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, value));
            _list.AddFirst(newNode);
            _map[key] = newNode;

            if (_map.Count <= capacity) return value;
            var lru = _list.Last!;
            _list.RemoveLast();
            _map.Remove(lru.Value.Key);

            return value;
        }
    }

    public void RemoveWhere(Func<KeyValuePair<TKey, TValue>, bool> predicate)
    {
        lock (_sync)
        {
            var node = _list.First;
            while (node != null)
            {
                var next = node.Next;
                var key = node.Value.Key;
                var value = node.Value.Value;
                if (predicate(new KeyValuePair<TKey, TValue>(key, value)))
                {
                    _map.Remove(key);
                    _list.Remove(node);
                }

                node = next;
            }
        }
    }

    private class CacheItem(TKey key, TValue value)
    {
        public TKey Key { get; } = key;
        public TValue Value { get; } = value;
    }
}

/// <summary>
///     A token bucket with high-precision refill using Stopwatch timestamps.
/// </summary>
internal class TokenBucket(int maxTokens, double replenishRatePerSecond, TimeProvider timeProvider)
{
    private readonly object _sync = new();

    // Backed by a 64-bit field accessed atomically: the cleanup sweep reads LastAccessed WITHOUT holding _sync, so a
    // plain DateTime field could tear on 32-bit runtimes.
    private long _lastAccessedTicks = timeProvider.GetUtcNow().UtcDateTime.Ticks;
    private long _lastRefillTs = timeProvider.GetTimestamp();
    private double _tokens = maxTokens;

    public DateTime LastAccessed => new(Interlocked.Read(ref _lastAccessedTicks), DateTimeKind.Utc);

    public bool TryConsume()
    {
        lock (_sync)
        {
            var nowTs = timeProvider.GetTimestamp();
            var elapsed = timeProvider.GetElapsedTime(_lastRefillTs, nowTs).TotalSeconds;
            if (elapsed > 0)
            {
                // Refill continuously rather than only in whole-second chunks, so tokens accrue smoothly.
                _tokens = Math.Min(maxTokens, _tokens + elapsed * replenishRatePerSecond);
                _lastRefillTs = nowTs;
            }

            Interlocked.Exchange(ref _lastAccessedTicks, timeProvider.GetUtcNow().UtcDateTime.Ticks);
            if (_tokens < 1)
                return false;

            _tokens -= 1;
            return true;
        }
    }
}