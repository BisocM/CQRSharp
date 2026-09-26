namespace CQRSharp.Pipelines;

/// <summary>
///     The continuation a pipeline behavior invokes to run the next behavior (or, at the end of the chain, the handler).
///     The cancellation token is defaulted, so a behavior can simply <c>await next()</c> to flow the ambient token, or
///     pass its own token to override it. Named (rather than a bare <see cref="Func{T, TResult}" />) so it self-documents
///     on hover and the parameterless call compiles.
/// </summary>
/// <typeparam name="TResult">The type of the result returned by the request.</typeparam>
public delegate Task<TResult> RequestHandlerDelegate<TResult>(CancellationToken cancellationToken = default);

/// <summary>
///     A behavior that wraps the handling of commands and queries sent with <c>Send</c>: it runs code before and after
///     the rest of the pipeline and the handler, and can replace the result. Register it open-generic
///     (<c>typeof(IPipelineBehavior&lt;,&gt;)</c>) to wrap every request; a non-generic class closed over one request
///     type is registered by the source generator. Behaviors
///     are ordered by <see cref="IPrioritizedPipelineBehavior" />; a request opts out of one with
///     <see cref="PipelineExemptionAttribute" />.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResult">The request's result type.</typeparam>
public interface IPipelineBehavior<in TRequest, TResult> where TRequest : IRequest
{
    /// <summary>
    ///     Handles the request, invoking the next behavior in the pipeline or, at its end, the handler.
    /// </summary>
    /// <param name="request">The request being handled.</param>
    /// <param name="next">The continuation; call <c>await next()</c> to run the rest of the pipeline.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, containing the result.</returns>
    Task<TResult> Handle(TRequest request,
        RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken);
}