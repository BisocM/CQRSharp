namespace CQRSharp.Data.Context;

/// <summary>
///     Default implementation of IRequestContext. Can be used as a fallback or baseline.
///     Users can extend this class or implement IRequestContext directly.
/// </summary>
public class RequestContextBase(string? requestId, string? userId) : IRequestContext
{
    /// <inheritdoc />
    public string? RequestId { get; } = requestId;

    /// <inheritdoc />
    public string? UserId { get; } = userId;
}