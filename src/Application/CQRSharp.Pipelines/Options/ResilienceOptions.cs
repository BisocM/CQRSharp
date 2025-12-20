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
}
