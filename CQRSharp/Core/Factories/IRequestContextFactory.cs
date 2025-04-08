using CQRSharp.Shared.Core.Data.Interfaces.Context;
using CQRSharp.Shared.Core.Data.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
///     Factory for creating IRequestContext instances.
///     Users can provide their own implementations to produce custom contexts globally.
///     Ensure that you register a transient service implementing this interface AFTER the main CQRS registration.
/// </summary>
public interface IRequestContextFactory
{
    /// <summary>
    ///     Creates an instance of <see cref="IRequestContext" /> based on the provided <see cref="IRequest" />.
    ///     This method is intended to initialize a contextual environment for the associated request,
    ///     by providing details such as a unique request identifier and the user associated with the request.
    /// </summary>
    /// <param name="request">
    ///     The request for which the context is being created. This parameter
    ///     provides information that may influence the creation of the request context.
    /// </param>
    /// <returns>
    ///     Returns an instance of <see cref="IRequestContext" /> containing contextual details
    ///     for the specified request.
    /// </returns>
    IRequestContext CreateContext(IRequest request);
}

/// <summary>
///     Interface for a factory responsible for creating instances of a generic context type that inherits from
///     <see cref="IRequestContext" />.
///     Provides a mechanism to generate <typeparamref name="TContext" /> objects to be associated with requests.
///     Implementations of this interface should be registered as transient services.
/// </summary>
public interface IRequestContextFactory<out TContext> : IRequestContextFactory where TContext : IRequestContext
{
    /// <summary>
    ///     Creates an instance of <typeparamref name="TContext"/> based on the provided <see cref="IRequest" />.
    ///     This method is responsible for generating a contextual environment for the given request,
    ///     encapsulating details such as the request identifier and user-specific information.
    /// </summary>
    /// <param name="request">
    ///     The request for which the context is being created. This parameter contains the necessary
    ///     information that informs the creation of the contextual environment.
    /// </param>
    /// <returns>
    ///     Returns an instance of <typeparamref name="TContext"/> that contains contextual information
    ///     relevant to the provided request.
    /// </returns>
    new TContext CreateContext(IRequest request);
}