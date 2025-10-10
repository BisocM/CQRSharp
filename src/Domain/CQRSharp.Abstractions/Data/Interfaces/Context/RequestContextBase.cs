namespace CQRSharp.Abstractions.Data.Interfaces.Context;

/// <summary>
///     Default implementation of IRequestContext. Can be used as a fallback or baseline.
///     Users can extend this class or implement IRequestContext directly.
/// </summary>
public class RequestContextBase : IRequestContext
{
    /// <summary>
    ///     The time at which the request context was created.
    /// </summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}