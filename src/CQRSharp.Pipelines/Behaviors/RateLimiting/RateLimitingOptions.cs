namespace CQRSharp.Pipelines;

/// <summary>
///     Options for the rate-limiting behavior's <see cref="RequestRateLimiter" />: a token bucket per caller that holds up
///     to <see cref="MaxTokens" /> tokens and refills continuously at <see cref="ReplenishRatePerSecond" />; each request
///     takes one token, and a request that finds none is rejected with <see cref="RateLimitExceededException" />.
/// </summary>
public sealed class RateLimitingOptions
{
    /// <summary>
    ///     The most tokens a bucket holds: the burst a caller can make after being idle. Defaults to 3; must be greater
    ///     than zero.
    /// </summary>
    public int MaxTokens { get; set; } = 3;

    /// <summary>
    ///     How many tokens a bucket regains per second, accrued continuously (0.5 is one token every two seconds).
    ///     Defaults to 1; must be a finite number greater than zero.
    /// </summary>
    public double ReplenishRatePerSecond { get; set; } = 1;

    /// <summary>
    ///     Whether a caller has one bucket for every request type (<see cref="RateLimitScope.Global" />, the default) or
    ///     one per request type (<see cref="RateLimitScope.PerRequestType" />).
    /// </summary>
    public RateLimitScope Scope { get; set; } = RateLimitScope.Global;

    /// <summary>
    ///     How long a bucket must go unused before the idle sweep may drop it. The sweep only drops a bucket that has
    ///     refilled completely, so a caller never regains tokens early through it. Defaults to 10 minutes;
    ///     <see cref="TimeSpan.Zero" /> turns the idle sweep off (buckets are then only reclaimed at
    ///     <see cref="MaxEntries" />).
    /// </summary>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     How often a timer runs the idle sweep. Defaults to 5 minutes; <see cref="TimeSpan.Zero" /> runs the sweep
    ///     from requests instead, at most once every half <see cref="MaxIdleTime" /> (and at least a second apart).
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The number of buckets (distinct callers, or caller and request type pairs) the limiter keeps. Nothing is dropped
    ///     until this many exist. When a new one would exceed it, the limiter first drops every bucket that has refilled
    ///     completely, which loses nothing; only if more than nine tenths of <see cref="MaxEntries" /> are still in use
    ///     does it also drop the least recently used ones, down to nine tenths. Concurrent first requests from new callers can
    ///     briefly take the count past it, by at most the number of such requests in flight. A dropped caller starts
    ///     again with a full bucket, so size this comfortably above the number of callers active within one refill period
    ///     (<see cref="MaxTokens" /> / <see cref="ReplenishRatePerSecond" /> seconds). Defaults to 10,000; must be greater
    ///     than zero.
    /// </summary>
    public int MaxEntries { get; set; } = 10000;
}
