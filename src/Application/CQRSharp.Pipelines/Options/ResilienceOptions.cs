namespace CQRSharp.Pipelines.Options;

/// <summary>
///     Configuration options for the resilience behavior.
/// </summary>
public sealed class ResilienceOptions
{
    /// <summary>
    ///     The maximum number of retries for a command execution.
    /// </summary>
    /// <remarks>
    ///     The default value is <c>3</c>.
    /// </remarks>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    ///     The base delay applied between retry attempts.
    ///     Set to <see cref="TimeSpan.Zero" /> to disable delays.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Multiplier applied per retry attempt (e.g., 1.0 = fixed delay, 2.0 = exponential backoff).
    /// </summary>
    public double BackoffMultiplier { get; set; } = 1.0;

    /// <summary>
    ///     Maximum delay cap when using backoff.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Computes the delay before a given retry attempt from <see cref="BaseDelay" />,
    ///     <see cref="BackoffMultiplier" />, and the <see cref="MaxDelay" /> cap. Returns <see cref="TimeSpan.Zero" />
    ///     when delays are disabled (non-positive base delay).
    /// </summary>
    /// <param name="retryAttempt">The 1-based retry attempt number.</param>
    public TimeSpan ComputeRetryDelay(int retryAttempt)
    {
        if (retryAttempt <= 0) return TimeSpan.Zero;

        var baseDelay = BaseDelay;
        if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;

        var backoffMultiplier = BackoffMultiplier;
        if (double.IsNaN(backoffMultiplier) || double.IsInfinity(backoffMultiplier) || backoffMultiplier < 1.0)
            backoffMultiplier = 1.0;

        var delayMs = baseDelay.TotalMilliseconds * Math.Pow(backoffMultiplier, retryAttempt - 1);

        var maxDelay = MaxDelay;
        if (maxDelay > TimeSpan.Zero)
            delayMs = Math.Min(delayMs, maxDelay.TotalMilliseconds);

        if (double.IsNaN(delayMs) || double.IsInfinity(delayMs) || delayMs <= 0)
            return baseDelay;

        return TimeSpan.FromMilliseconds(delayMs);
    }
}
