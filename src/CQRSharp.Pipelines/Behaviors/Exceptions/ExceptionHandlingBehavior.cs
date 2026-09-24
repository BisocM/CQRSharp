using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     Runs the exception hooks declared for the request type when the rest of the pipeline throws: every matching
///     <see cref="IRequestExceptionAction{TRequest,TException}" />, then the
///     <see cref="IRequestExceptionHandler{TRequest,TResponse,TException}" />s from the most derived exception type up,
///     until one handles the exception and supplies the result. An unhandled exception, and an
///     <see cref="OperationCanceledException" /> raised after the caller's own token was cancelled, reaches the caller
///     unchanged; any other cancellation is a failure the hooks see.
/// </summary>
/// <remarks>
///     Registered by <c>AddCqrsGenerated</c> (both forms) unless <c>UseExceptionHandling(false)</c> turns it off. It
///     runs outermost, so its handlers also see what the other built-in behaviors throw.
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="services">The scope the hooks are resolved from.</param>
/// <param name="registry">The generated registry of the hooks declared for each request type.</param>
/// <param name="logger">Logs a handled exception.</param>
public sealed class ExceptionHandlingBehavior<TRequest, TResult>(
    IServiceProvider services,
    IRequestExceptionHookRegistry registry,
    ILogger<ExceptionHandlingBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior, ICqrsExceptionHandlingBehaviorMarker
    where TRequest : IRequest
{
    /// <inheritdoc />
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
        // Only the caller's own cancellation bypasses the hooks: a cancellation nobody asked for (an HttpClient timeout,
        // a handler's own linked token) is a failure like any other, and every hook sees every failure.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

            ExceptionHandlingLog.Handled(logger, typeof(TRequest).Name, ex.GetType().Name);

            return (TResult)outcome.Response!;
        }
    }

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.ExceptionHandling;
}
