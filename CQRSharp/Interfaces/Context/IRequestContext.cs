namespace CQRSharp.Data.Context;

/// <summary>
///     A common interface for all request contexts. This interface mandates that every context
///     must have a RequestId and a UserId property, ensuring these fields are consistently available.
/// </summary>
public interface IRequestContext
{
    /// <summary>
    ///     The unique identifier for this request.
    ///     Typically set by the dispatcher upon receiving the request.
    /// </summary>
    string? RequestId { get; }

    /// <summary>
    ///     The identifier for the user making the request, if available.
    ///     Typically set by a pipeline behavior using IUserIdentificationFactory.
    /// </summary>
    string? UserId { get; }
}