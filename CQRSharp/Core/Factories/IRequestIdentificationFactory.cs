using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
/// An interface to be used by the library consumer for the implementation of a unique
/// request identification builder.
/// </summary>
public interface IRequestIdentificationFactory
{
    /// <summary>
    /// Gets the unique identifier for the specified request. Suggested parameters for 
    /// </summary>
    /// <param name="request">The request object.</param>
    /// <returns>A unique request identifier string.</returns>
    string GetIdentifier(RequestBase request);
}