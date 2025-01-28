using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipelines.Types.RateLimiting;

/// <summary>
///     Provides functionality to limit the rate of requests per user or globally.
/// </summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<object, TokenBucket> _buckets = new();
    private readonly RateLimiterOptions _config;
    private readonly ILogger<RateLimiter> _logger;
    private readonly TimeSpan _replenishInterval;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RateLimiter" /> class.
    /// </summary>
    /// <param name="config">The rate limiter configuration options.</param>
    /// <param name="logger">The logger instance.</param>
    public RateLimiter(RateLimiterOptions config, ILogger<RateLimiter> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ValidateConfiguration(_config);

        //Convert replenish rate per second to a TimeSpan interval.
        _replenishInterval = TimeSpan.FromSeconds(1.0 / _config.ReplenishRatePerSecond);

        //Optionally schedule a recurring cleanup
        if (_config.MaxIdleTime > TimeSpan.Zero)
        {
            var timer = new Timer(
                state => CleanupStaleBuckets(), //Callback to cleanup method
                null, //No state object
                _config.CleanupInterval, //Initial delay
                _config.CleanupInterval //Periodic interval
            );
        }
    }

    /// <summary>
    ///     Determines whether a request is allowed based on the rate limiting rules.
    /// </summary>
    /// <param name="userIdentifier">The identifier of the user making the request.</param>
    /// <param name="commandName">The name of the command being executed.</param>
    /// <returns><c>true</c> if the request is allowed; otherwise, <c>false</c>.</returns>
    public bool AllowRequest(string userIdentifier, string commandName)
    {
        if (string.IsNullOrWhiteSpace(userIdentifier))
            throw new ArgumentNullException(nameof(userIdentifier));

        if (string.IsNullOrWhiteSpace(commandName))
            throw new ArgumentNullException(nameof(commandName));

        object key = _config.Scope == RateLimitScope.PerCommand
            ? new UserCommandKey(userIdentifier, commandName)
            : userIdentifier;

        _logger.LogDebug("Checking rate limit with key: {Key} (Hash: {HashCode})", key, key.GetHashCode());

        var bucket = _buckets.GetOrAdd(key, k =>
        {
            _logger.LogDebug("Creating a new bucket for key: {Key} with hash {HashCode}", k, k.GetHashCode());
            return new TokenBucket(_config.MaxTokens, _replenishInterval, _logger);
        });

        var canProceed = bucket.TryConsume();
        _logger.LogDebug("Rate limit check for key {Key} {Result}. Current tokens: {TokenCount}",
            key, canProceed ? "passed" : "was blocked by", bucket.CurrentTokenCount);

        return canProceed;
    }

    private void CleanupStaleBuckets()
    {
        //TODO: This is a potential hotspot in very very high concurrency scenarios. Might want to consider other options.
        var now = DateTime.UtcNow;
        var idleLimit = _config.MaxIdleTime;
        var removedCount =
            (from kvp in _buckets let bucket = kvp.Value where now - bucket.LastAccessed > idleLimit select kvp).Count(
                kvp => _buckets.TryRemove(kvp.Key, out _));

        if (removedCount > 0)
            _logger.LogInformation("RateLimiter: Removed {Count} stale buckets.", removedCount);
    }

    private static void ValidateConfiguration(RateLimiterOptions config)
    {
        if (config.MaxTokens <= 0)
            throw new ArgumentException("MaxTokens must be greater than zero.", nameof(config.MaxTokens));

        if (config.ReplenishRatePerSecond <= 0)
            throw new ArgumentException("ReplenishRatePerSecond must be greater than zero.",
                nameof(config.ReplenishRatePerSecond));
    }

    private record UserCommandKey(string UserIdentifier, string CommandName);

    /// <summary>
    ///     Represents a token bucket for rate limiting.
    /// </summary>
    private class TokenBucket
    {
        private readonly ILogger _logger;
        private readonly int _maxTokens;
        private readonly TimeSpan _replenishInterval;
        private readonly object _syncLock = new();
        private DateTime _lastRefillTimestamp;

        private double _tokens;

        /// <summary>
        ///     Initializes a new instance of the <see cref="TokenBucket" /> class.
        /// </summary>
        /// <param name="maxTokens">The maximum number of tokens in the bucket.</param>
        /// <param name="replenishInterval">The interval at which tokens are replenished.</param>
        /// <param name="logger">The logger instance.</param>
        public TokenBucket(int maxTokens, TimeSpan replenishInterval, ILogger logger)
        {
            _maxTokens = maxTokens;
            _replenishInterval = replenishInterval;
            _logger = logger;
            _tokens = maxTokens;
            _lastRefillTimestamp = DateTime.UtcNow;
        }

        //This is updated on every TryConsume call.
        public DateTime LastAccessed { get; private set; }

        /// <summary>
        ///     Gets the current token count.
        /// </summary>
        public int CurrentTokenCount
        {
            get
            {
                lock (_syncLock)
                {
                    return (int)_tokens;
                }
            }
        }

        /// <summary>
        ///     Attempts to consume a token from the bucket.
        /// </summary>
        /// <returns><c>true</c> if a token was consumed; otherwise, <c>false</c>.</returns>
        public bool TryConsume()
        {
            lock (_syncLock)
            {
                //Update the LastAccessed timestamp.
                LastAccessed = DateTime.UtcNow;
                RefillTokens();

                if (_tokens < 1)
                {
                    _logger.LogWarning("Request blocked - no tokens available.");
                    return false;
                }

                _tokens -= 1;
                _logger.LogDebug("Token consumed. New token count: {TokenCount}", _tokens);
                return true;
            }
        }

        /// <summary>
        ///     Refills tokens based on the time elapsed since the last refill.
        /// </summary>
        private void RefillTokens()
        {
            var now = DateTime.UtcNow;
            var timeElapsed = now - _lastRefillTimestamp;

            var tokensToAdd = timeElapsed.TotalSeconds * (_maxTokens / _replenishInterval.TotalSeconds);

            if (!(tokensToAdd >= 1)) return;

            _tokens = Math.Min(_maxTokens, _tokens + tokensToAdd);
            _lastRefillTimestamp = now;
            _logger.LogDebug("Refilled tokens. Current token count: {TokenCount}", _tokens);
        }
    }
}