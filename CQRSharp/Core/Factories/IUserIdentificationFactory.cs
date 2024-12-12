using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
///     An interface to be used by the library consumer for the implementation of a unique
///     user identification builder.
/// </summary>
public interface IUserIdentificationFactory
{
    /// <summary>
    ///     Gets the unique identifier for the specified request, used for rate limiting.
    /// </summary>
    /// <param name="request">The request object to identify the user for.</param>
    /// <returns>A unique user identifier string.</returns>
    string GetIdentifier(IRequest? request);
}