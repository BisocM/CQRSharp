using CQRSharp.RateLimiting.Enums;
using System.Collections.Concurrent;
using CQRSharp.RateLimiting.Options;
using Microsoft.Extensions.Logging;

namespace CQRSharp.RateLimiting.Handlers
{
    //FIXME: I have no flying idea on how to fix this on local-running asynchronous commands (if somebody ever needs this).
    //The issue is that if the command execution is async and back-to-back in nanoseconds of succession, then the rate limiter will not work as expected
    //and will create new buckets for each command, simply because the ConcurrentDictionary has not even yet had the time to register the values. (I might be wrong)
    public sealed class RateLimiter(
        RateLimiterOptions config,
        ILogger<RateLimiter> logger)
    {
        private readonly ConcurrentDictionary<object, TokenBucket> _buckets = new();
        private readonly long _replenishIntervalTicks = TimeSpan.TicksPerSecond * 20; // Explicitly set to 20 seconds

        public bool AllowRequest(string userIdentifier, string commandName)
        {
            object key = config.Scope == RateLimitScope.PerCommand
                ? new UserCommandKey(userIdentifier, commandName)
                : userIdentifier;

            logger.LogInformation($"Checking rate limit with key: {key} (Hash: {key.GetHashCode()})");

            var bucket = _buckets.GetOrAdd(key, k =>
            {
                logger.LogInformation($"Creating a new bucket for key: {k} with hash {k.GetHashCode()}.");
                return new TokenBucket(config.MaxTokens, _replenishIntervalTicks, logger);
            });

            var canProceed = bucket.TryConsume(config.MaxTokens, 1);
            logger.LogInformation($"Rate limit check for key {key} {(canProceed ? "passed" : "was blocked by")} rate limiting. Current tokens: {bucket.CurrentTokenCount}");

            return canProceed;
        }

        private record UserCommandKey(string UserIdentifier, string CommandName);

        private struct TokenBucket(int maxTokens, long replenishIntervalTicks, ILogger logger)
        {
            private long _tokens = maxTokens;
            private long _lastRefillTimestamp = DateTime.UtcNow.Ticks;
            private readonly object _lock = new();

            public int CurrentTokenCount => (int)Interlocked.Read(ref _tokens);

            public bool TryConsume(int maxTokens, int tokensPerInterval)
            {
                lock (_lock)
                {
                    logger.LogDebug($"[TryConsume] Initial token count: {_tokens}");
                    RefillTokensIfNecessary(maxTokens, tokensPerInterval);

                    if (_tokens <= 0)
                    {
                        logger.LogWarning("[TryConsume] Request blocked - no tokens available.");
                        return false;
                    }

                    Interlocked.Decrement(ref _tokens);
                    logger.LogDebug($"[TryConsume] Token consumed. New token count: {_tokens}");
                    return true;
                }
            }

            private void RefillTokensIfNecessary(int maxTokens, int tokensPerInterval)
            {
                long now = DateTime.UtcNow.Ticks;
                long lastRefill = Interlocked.Read(ref _lastRefillTimestamp);

                long intervalsPassed = (now - lastRefill) / replenishIntervalTicks;
                logger.LogDebug($"[RefillTokensIfNecessary] Now: {now}, LastRefill: {lastRefill}, IntervalsPassed: {intervalsPassed}");

                if (intervalsPassed > 0)
                {
                    lock (_lock)
                    {
                        long currentTokensBeforeRefill = _tokens;

                        long refillTokens = intervalsPassed * tokensPerInterval;
                        long newTokens = Math.Min(maxTokens, _tokens + refillTokens);
                        _tokens = newTokens;

                        logger.LogDebug($"[RefillTokensIfNecessary] Refilled tokens from {currentTokensBeforeRefill} to {_tokens}. Intervals passed: {intervalsPassed}");

                        long newTimestamp = lastRefill + intervalsPassed * replenishIntervalTicks;
                        Interlocked.CompareExchange(
                            ref _lastRefillTimestamp,
                            newTimestamp,
                            lastRefill
                        );

                        logger.LogDebug($"[RefillTokensIfNecessary] Updated last refill timestamp to: {_lastRefillTimestamp}");
                    }
                }
                else
                {
                    logger.LogDebug("[RefillTokensIfNecessary] No refill needed - intervals passed is 0.");
                }
            }
        }
    }
}