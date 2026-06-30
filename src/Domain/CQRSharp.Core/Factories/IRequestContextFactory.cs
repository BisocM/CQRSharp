using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Request;

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

    /// <summary>
    ///     Asynchronously creates the context for the given request. CQRSharp calls this once per request, before the
    ///     pipeline runs, so a factory can load request-scoped data (for example, the current user aggregate) from async
    ///     sources at a single awaited point instead of blocking or scattering lazy loads through the handler. The
    ///     default wraps the synchronous <see cref="CreateContext" />, so existing synchronous factories need no
    ///     changes; async factories override it — derive from <c>AsyncRequestContextFactory&lt;TContext&gt;</c> for a
    ///     typed override with no synchronous stub.
    /// </summary>
    /// <param name="request">The request that requires a context.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous creation.</param>
    /// <returns>The created <see cref="IRequestContext" /> instance.</returns>
    ValueTask<IRequestContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
        => new(CreateContext(request));
}