using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
///     Factory for creating IRequestContext instances.
///     Users can provide their own implementations to produce custom contexts globally.
///     Ensure that you register a transient service implementing this interface AFTER the main CQRS registration.
/// </summary>
public interface IRequestContextFactory
{
    /// <summary>
    /// Creates an instance of <see cref="IRequestContext"/> based on the provided <see cref="IRequest"/>.
    /// This method is intended to initialize a contextual environment for the associated request,
    /// by providing details such as a unique request identifier and the user associated with the request.
    /// </summary>
    /// <param name="request">The request for which the context is being created. This parameter
    /// provides information that may influence the creation of the request context.</param>
    /// <returns>Returns an instance of <see cref="IRequestContext"/> containing contextual details
    /// for the specified request.</returns>
    IRequestContext CreateContext(IRequest request);
}