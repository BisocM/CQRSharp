using System.Collections.Concurrent;
using CQRSharp.RateLimiting.Enums;
using CQRSharp.RateLimiting.Options;
using Microsoft.Extensions.Logging;

namespace CQRSharp.RateLimiting.Handlers
{
    /// <summary>
    /// Provides functionality to limit the rate of requests per user or globally.
    /// </summary>
    public sealed class RateLimiter
    {
        private readonly RateLimiterOptions _config;
        private readonly ILogger<RateLimiter> _logger;
        private readonly ConcurrentDictionary<object, TokenBucket> _buckets = new();
        private readonly TimeSpan _replenishInterval;

        /// <summary>
        /// Initializes a new instance of the <see cref="RateLimiter"/> class.
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
        }

        /// <summary>
        /// Determines whether a request is allowed based on the rate limiting rules.
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

            bool canProceed = bucket.TryConsume();
            _logger.LogDebug("Rate limit check for key {Key} {Result}. Current tokens: {TokenCount}",
                key, canProceed ? "passed" : "was blocked by", bucket.CurrentTokenCount);

            return canProceed;
        }

        private static void ValidateConfiguration(RateLimiterOptions config)
        {
            if (config.MaxTokens <= 0)
                throw new ArgumentException("MaxTokens must be greater than zero.", nameof(config.MaxTokens));

            if (config.ReplenishRatePerSecond <= 0)
                throw new ArgumentException("ReplenishRatePerSecond must be greater than zero.", nameof(config.ReplenishRatePerSecond));
        }

        private record UserCommandKey(string UserIdentifier, string CommandName);

        /// <summary>
        /// Represents a token bucket for rate limiting.
        /// </summary>
        private class TokenBucket
        {
            private readonly int _maxTokens;
            private readonly TimeSpan _replenishInterval;
            private readonly ILogger _logger;
            private readonly object _syncLock = new();

            private double _tokens;
            private DateTime _lastRefillTimestamp;

            /// <summary>
            /// Gets the current token count.
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
            /// Initializes a new instance of the <see cref="TokenBucket"/> class.
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

            /// <summary>
            /// Attempts to consume a token from the bucket.
            /// </summary>
            /// <returns><c>true</c> if a token was consumed; otherwise, <c>false</c>.</returns>
            public bool TryConsume()
            {
                lock (_syncLock)
                {
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
            /// Refills tokens based on the time elapsed since the last refill.
            /// </summary>
            private void RefillTokens()
            {
                DateTime now = DateTime.UtcNow;
                TimeSpan timeElapsed = now - _lastRefillTimestamp;

                double tokensToAdd = timeElapsed.TotalSeconds * (_maxTokens / _replenishInterval.TotalSeconds);

                if (tokensToAdd >= 1)
                {
                    _tokens = Math.Min(_maxTokens, _tokens + tokensToAdd);
                    _lastRefillTimestamp = now;
                    _logger.LogDebug("Refilled tokens. Current token count: {TokenCount}", _tokens);
                }
            }
        }
    }
}