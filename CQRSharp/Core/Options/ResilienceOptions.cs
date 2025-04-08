namespace CQRSharp.Core.Options;

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
}