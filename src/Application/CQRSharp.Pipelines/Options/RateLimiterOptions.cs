using CQRSharp.Pipelines.Behaviors.RateLimiting;

namespace CQRSharp.Pipelines.Options;

/// <summary>
///     Options for configuring the built-in <see cref="RateLimiter" />.
/// </summary>
public sealed class RateLimiterOptions
{
    /// <summary>
    ///     The maximum tokens that each user has before they hit the rate limit.
    /// </summary>
    public int MaxTokens { get; set; } = 3;

    /// <summary>
    ///     How many tokens are to be replenished for each user per second.
    /// </summary>
    public double ReplenishRatePerSecond { get; set; } = 1;

    /// <summary>
    ///     The scope of the rate limiter.
    ///     If the rate limiter is set to `Global`, that means that it would limit the user for *all* commands.
    ///     If the rate limiter is set to `PerCommand`, that means that it would limit the user only for the specific command
    ///     that triggered the rate limit.
    /// </summary>
    public RateLimitScope Scope { get; set; } = RateLimitScope.Global;


    /// <summary>
    ///     The maximum duration of idle time after which a token bucket is automatically cleaned up.
    /// </summary>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     The interval at which the system will clean up unused or expired tokens
    ///     to free up memory and maintain optimal performance.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The maximum number of entries the rate limiter cache can hold before older entries are evicted.
    /// </summary>
    /// <remarks>
    ///     Default value is 10000.
    /// </remarks>
    public int MaxEntries { get; set; } = 10000;
}