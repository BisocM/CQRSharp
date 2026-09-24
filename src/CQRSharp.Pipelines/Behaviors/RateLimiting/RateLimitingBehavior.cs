using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     Rejects a request whose caller is over its limit with <see cref="RateLimitExceededException" /> before any other
///     work is done, using the shared <see cref="RequestRateLimiter" />. Applies only to requests whose context implements
///     <see cref="IRateLimitedContext" />; any other request passes straight through.
/// </summary>
/// <typeparam name="TRequest">The type of request being processed.</typeparam>
/// <typeparam name="TResult">The type of result returned after request processing.</typeparam>
public sealed class RateLimitingBehavior<TRequest, TResult>(
    ILogger<RateLimitingBehavior<TRequest, TResult>> logger,
    RequestRateLimiter rateLimiter)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Inside the async method, so a rejection surfaces as a faulted task like every other pipeline failure.
        RateLimitEnforcement.Enforce(request, rateLimiter, logger);
        return await next(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.RateLimiting;
}
