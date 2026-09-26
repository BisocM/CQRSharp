namespace CQRSharp.Pipelines;

/// <summary>
///     Configuration options for the timeout behavior.
/// </summary>
public sealed class TimeoutOptions
{
    /// <summary>
    ///     How long a request's handler, or a streaming request's whole enumeration, may run before the timeout behavior
    ///     cancels it and throws <see cref="RequestTimeoutException" />. Must be greater than zero.
    /// </summary>
    /// <remarks>
    ///     The default value is <c>30 seconds</c>.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
