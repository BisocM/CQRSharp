using CQRSharp.Shared.Data.Interfaces.Context;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
///     Factory for creating IRequestContext instances.
///     Ensure that you register a transient service implementing this interface AFTER the main CQRS registration.
/// </summary>
/// <remarks>
///     This is a default implementation that is only used for generating context instances for
///     <see cref="RequestContextBase" />.
/// </remarks>
public interface IRequestContextFactory : IRequestContextFactory<RequestContextBase>;

/// <summary>
///     Interface for a factory responsible for creating instances of a generic context type that inherits from
///     <see cref="IRequestContext" />.
///     Provides a mechanism to generate <typeparamref name="TContext" /> objects to be associated with requests.
///     Implementations of this interface should be registered as transient services.
/// </summary>
public interface IRequestContextFactory<out TContext> : IInternalRequestContextFactory where TContext : IRequestContext
{
    IRequestContext IInternalRequestContextFactory.CreateContext(IRequest request)
    {
        return CreateContext(request);
    }

    /// <summary>
    ///     Creates an instance of <typeparamref name="TContext" /> based on the provided <see cref="IRequest" />.
    ///     This method is responsible for generating a contextual environment for the given request,
    ///     encapsulating details such as the request identifier and user-specific information.
    /// </summary>
    /// <param name="request">
    ///     The request for which the context is being created. This parameter contains the necessary
    ///     information that informs the creation of the contextual environment.
    /// </param>
    /// <returns>
    ///     Returns an instance of <typeparamref name="TContext" /> that contains contextual information
    ///     relevant to the provided request.
    /// </returns>
    new TContext CreateContext(IRequest request);
}

/// <summary>
///     An internal request context factory.
/// </summary>
public interface IInternalRequestContextFactory
{
    /// <summary>
    ///     Creates an instance of IRequestContext for the given request.
    /// </summary>
    /// <param name="request">The request that requires a context.</param>
    /// <returns>The created IRequestContext instance.</returns>
    IRequestContext CreateContext(IRequest request);
}