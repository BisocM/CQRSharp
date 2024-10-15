using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.RateLimiting.Handlers
{
    public interface IUserIdentifierFactory
    {
        /// <summary>
        /// Gets the unique identifier for the specified request, used for rate limiting.
        /// </summary>
        /// <param name="request">The request object to identify the user for.</param>
        /// <returns>A unique user identifier string.</returns>
        string GetIdentifier(RequestBase request);
    }
}