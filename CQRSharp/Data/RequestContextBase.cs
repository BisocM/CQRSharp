namespace CQRSharp.Data;

/// <summary>
/// Extendable property bag or methods for additional consumer-defined data.
/// Users can subclass this to add more fields, or rely on composition.
/// </summary>
// TODO: Make this extensible so that the user can extend the context to include their own lazy-loaded or similar properties.
public class RequestContextBase
{
    public RequestContextBase(string? requestId, string? userId)
    {
        RequestId = requestId;
        UserId = userId;
    }

    /// <summary>
    /// The unique identifier for this request.
    /// Typically set by the dispatcher upon receiving the request.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>
    /// The identifier for the user making the request, if available.
    /// Typically set by a pipeline behavior using IUserIdentificationFactory.
    /// </summary>
    public string? UserId { get; }
}