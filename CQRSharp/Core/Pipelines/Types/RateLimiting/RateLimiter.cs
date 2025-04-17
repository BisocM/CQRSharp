using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Pipelines.Types.RateLimiting
{
    /// <summary>
    ///   A highly concurrent rate limiter using a sharded LRU cache and lock-minimized token buckets.
    /// </summary>
    public sealed class RateLimiter : IDisposable
    {
        private readonly ShardedLruCache<object, TokenBucket> _cache;
        private readonly RateLimiterOptions _config;
        private readonly Timer? _cleanupTimer;

        /// <summary>
        /// Implements a rate-limiting mechanism to control the number of requests or commands
        /// that can be executed within a specified configuration limit.
        /// </summary>
        /// <remarks>
        /// Instances of <see cref="RateLimiter"/> use token buckets to manage rate-limiting
        /// based on the configuration provided. This class is intended for use in scenarios
        /// where requests need to be controlled or restricted to conform to defined limits.
        /// </remarks>
        public RateLimiter(IOptions<RateLimiterOptions> config)
        {
            _config = config.Value ?? throw new ArgumentNullException(nameof(config));
            ValidateConfiguration(_config);

            var shardCount = Environment.ProcessorCount * 2;
            _cache = new ShardedLruCache<object, TokenBucket>(_config.MaxEntries, shardCount);

            if (_config.MaxIdleTime > TimeSpan.Zero)
            {
                _cleanupTimer = new Timer(
                    _ => CleanupStaleBuckets(),
                    null,
                    _config.CleanupInterval,
                    _config.CleanupInterval);
            }
        }

        /// <summary>
        /// Determines whether a request is allowed based on rate limiting rules.
        /// </summary>
        /// <param name="userIdentifier">The unique identifier for the user making the request.</param>
        /// <param name="commandName">The name of the command being executed.</param>
        /// <returns><c>true</c> if the request is allowed; otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="userIdentifier"/> or <paramref name="commandName"/> is null or whitespace.</exception>
        public bool AllowRequest(string userIdentifier, string commandName)
        {
            if (string.IsNullOrWhiteSpace(userIdentifier))
                throw new ArgumentNullException(nameof(userIdentifier));
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentNullException(nameof(commandName));

            object key = _config.Scope == RateLimitScope.PerCommand
                ? new UserCommandKey(userIdentifier, commandName)
                : userIdentifier;

            var bucket = _cache.GetOrAdd(key, _ => new TokenBucket(
                _config.MaxTokens,
                _config.ReplenishRatePerSecond));

            return bucket.TryConsume();
        }

        private void CleanupStaleBuckets()
        {
            var threshold = DateTime.UtcNow - _config.MaxIdleTime;
            _cache.RemoveWhere(pair => pair.Value.LastAccessed < threshold);
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

        /// <inheritdoc />
        public void Dispose() => _cleanupTimer?.Dispose();

        private record UserCommandKey(string UserIdentifier, string CommandName);
    }

    /// <summary>
    ///   A power-of-two sharded LRU cache to distribute lock contention.
    /// </summary>
    internal class ShardedLruCache<TKey, TValue> where TKey : notnull
    {
        private readonly LruCache<TKey, TValue>[] _shards;
        private readonly int _mask;

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

        private int GetShardIndex(TKey key) => key.GetHashCode() & _mask;

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
    ///   Simple LRU cache with lock protection per shard.
    /// </summary>
    internal class LruCache<TKey, TValue>(int capacity)
        where TKey : notnull
    {
        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _map = new(capacity);
        private readonly LinkedList<CacheItem> _list = new();
        private readonly object _sync = new();

        private class CacheItem(TKey key, TValue value)
        {
            public TKey Key { get; } = key;
            public TValue Value { get; } = value;
        }

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
    }

    /// <summary>
    ///   A token bucket with high-precision refill using Stopwatch timestamps.
    /// </summary>
    internal class TokenBucket(int maxTokens, double replenishRatePerSecond)
    {
        private readonly object _sync = new();
        private double _tokens = maxTokens;
        private long _lastRefillTs = Stopwatch.GetTimestamp();
        private static readonly double TickToSeconds = 1.0 / Stopwatch.Frequency;

        public DateTime LastAccessed { get; private set; } = DateTime.UtcNow;

        public bool TryConsume()
        {
            lock (_sync)
            {
                var nowTs = Stopwatch.GetTimestamp();
                var elapsed = (nowTs - _lastRefillTs) * TickToSeconds;
                if (elapsed >= 1.0)
                {
                    _tokens = Math.Min(maxTokens, _tokens + elapsed * replenishRatePerSecond);
                    _lastRefillTs = nowTs;
                }

                LastAccessed = DateTime.UtcNow;
                if (_tokens < 1)
                    return false;

                _tokens -= 1;
                return true;
            }
        }
    }
}