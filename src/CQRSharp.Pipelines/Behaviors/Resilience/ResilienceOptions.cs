namespace CQRSharp.Pipelines;

/// <summary>
///     Configuration options for the resilience behavior, which retries requests implementing
///     <see cref="IRetryableRequest" />. The delay before retry <c>n</c> is
///     <see cref="BaseDelay" /> × <see cref="BackoffMultiplier" /><sup>n-1</sup>, capped at <see cref="MaxDelay" />.
///     Invalid values fail at host start.
/// </summary>
public sealed class ResilienceOptions
{
    /// <summary>
    ///     How many times a failed <see cref="IRetryableRequest" /> is retried after its first attempt: 3 (the default)
    ///     means up to four attempts in all. Must not be negative; 0 turns retries off.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    ///     The delay before the first retry. Defaults to one second; <see cref="TimeSpan.Zero" /> retries immediately.
    ///     Must not be negative.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     The factor each further retry's delay grows by: 1 (the default) keeps every delay at <see cref="BaseDelay" />,
    ///     2 doubles it per retry. Must be a finite number of at least 1.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 1.0;

    /// <summary>
    ///     The longest delay between two attempts. Defaults to 30 seconds; must be at least <see cref="BaseDelay" />.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The delay before retry <paramref name="retryAttempt" /> (1-based).</summary>
    internal TimeSpan ComputeRetryDelay(int retryAttempt)
    {
        if (retryAttempt <= 0 || BaseDelay <= TimeSpan.Zero) return TimeSpan.Zero;

        // The options are validated at start (a finite multiplier of at least 1, MaxDelay between BaseDelay and what a
        // timer can wait), so an exponential that overflows to infinity still lands on MaxDelay.
        var delayMs = Math.Min(BaseDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, retryAttempt - 1), MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delayMs);
    }
}
