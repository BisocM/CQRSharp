using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Behaviors.Exceptions;

/// <summary>
///     Executes request-level exception hooks (actions + handlers) in an AOT-safe manner.
/// </summary>
public sealed class ExceptionHandlingBehavior<TRequest, TResult>(
    IServiceProvider services,
    IRequestExceptionHookRegistry registry,
    ILogger<ExceptionHandlingBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public async Task<TResult> Handle(
        TRequest request,
        RequestHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!registry.TryGetInvoker(typeof(TRequest), out var invoker))
                throw;

            var outcome = await invoker(services, request, ex, cancellationToken).ConfigureAwait(false);
            if (!outcome.Handled)
                throw;

            logger.LogDebug(
                "Exception {ExceptionType} handled for request {RequestType}.",
                ex.GetType().Name,
                typeof(TRequest).Name);

            return (TResult)outcome.Response!;
        }
    }

    public int PipelineExecutionPriority => int.MinValue;
}