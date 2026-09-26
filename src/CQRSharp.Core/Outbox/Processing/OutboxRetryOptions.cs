namespace CQRSharp;

/// <summary>
///     How the outbox processor spaces out the delivery attempts of a failing message: exponential back-off from
///     <see cref="BaseDelay" /> by <see cref="BackoffMultiplier" />, capped at <see cref="MaxDelay" />, with a random
///     <see cref="JitterFactor" /> so a burst of failures does not come back as one burst of retries. The names match
///     the resilience behavior's <c>ResilienceOptions</c>.
/// </summary>
public sealed class OutboxRetryOptions
{
    /// <summary>The delay before the second attempt. Defaults to 2 seconds; must not be negative.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     The factor each further attempt's delay grows by. Defaults to 2 (2 s, 4 s, 8 s, …); 1 makes every delay the
    ///     same. Must be a finite number of at least 1.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>The longest delay between two attempts. Defaults to 5 minutes; must be at least <see cref="BaseDelay" />.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The fraction of the computed delay that is randomised in either direction: 0.2 turns a 10 s delay into
    ///     8–12 s. Defaults to 0.2; 0 disables jitter (deterministic delays, e.g. in tests). Must be below 1.
    /// </summary>
    public double JitterFactor { get; set; } = 0.2;
}
