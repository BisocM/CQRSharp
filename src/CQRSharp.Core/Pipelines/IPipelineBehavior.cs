using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     The continuation a pipeline behavior invokes to run the next behavior (or, at the end of the chain, the handler).
///     The cancellation token is defaulted, so a behavior can simply <c>await next()</c> to flow the ambient token, or
///     pass its own token to override it. Named (rather than a bare <see cref="Func{T, TResult}" />) so it self-documents
///     on hover and the parameterless call compiles.
/// </summary>
/// <typeparam name="TResult">The type of the result returned by the request.</typeparam>
public delegate Task<TResult> RequestHandlerDelegate<TResult>(CancellationToken cancellationToken = default);

/// <summary>
///     Defines an interface for pipeline behaviors that can be applied globally to all commands.
/// </summary>
/// <typeparam name="TRequest">The type of the command.</typeparam>
/// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
public interface IPipelineBehavior<in TRequest, TResult> where TRequest : IRequest
{
    /// <summary>
    ///     Handles the command by invoking the next behavior in the pipeline or the command handler.
    /// </summary>
    /// <param name="request">The command being handled.</param>
    /// <param name="next">The continuation; call <c>await next()</c> to run the rest of the pipeline.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, containing the result.</returns>
    Task<TResult> Handle(TRequest request,
        RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken);
}