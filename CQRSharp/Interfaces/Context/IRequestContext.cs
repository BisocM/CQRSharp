namespace CQRSharp.Interfaces.Context;

/// <summary>
///     A common interface for all request contexts. This interface mandates that every context
///     must have a RequestId and a UserId property, ensuring these fields are consistently available.
/// </summary>
public interface IRequestContext
{
    /// <summary>
    ///     The timestamp indicating when the context or request was created.
    ///     Value is equal to the current UTC time upon initialization.
    /// </summary>
    DateTime CreatedAt { get; }
}