using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
///     Provides a default implementation of the <see cref="IRequestContextFactory" /> interface.
///     Used to create instances of <see cref="RequestContextBase" /> with default values for request and user IDs
///     when no custom factories are registered.
/// </summary>
public class DefaultRequestContextFactory : IRequestContextFactory
{
    /// <inheritdoc />
    public IRequestContext CreateContext(IRequest request)
    {
        //Default values.
        const string requestId = "Request ID factory not registered.";
        const string userId = "User ID factory not registered.";

        return new RequestContextBase(requestId, userId);
    }
}