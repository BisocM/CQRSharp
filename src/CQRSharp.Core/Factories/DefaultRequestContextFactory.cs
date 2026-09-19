using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
///     Provides a default implementation of the <see cref="IRequestContextFactory" /> interface.
///     Used to create instances of <see cref="RequestContextBase" /> with default values for request and user IDs
///     when no custom factories are registered.
/// </summary>
public class DefaultRequestContextFactory : IRequestContextFactory
{
    /// <inheritdoc />
    public RequestContextBase CreateContext(IRequest request)
    {
        return new RequestContextBase();
    }
}