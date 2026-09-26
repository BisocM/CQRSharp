using CQRSharp.Core.Pipelines;
using System.Runtime.ExceptionServices;
using CQRSharp.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     The streaming counterpart of <see cref="ExceptionHandlingBehavior{TRequest,TResult}" />: when the stream throws
///     while it is enumerated, runs the exception hooks declared for the request type. A handler that handles the
///     exception supplies the rest of the stream, which is yielded after the items already produced. An unhandled
///     exception, and an <see cref="OperationCanceledException" /> raised after the consumer's own token was cancelled,
///     reaches the consumer unchanged.
/// </summary>
/// <typeparam name="TRequest">The streaming request type.</typeparam>
/// <typeparam name="TItem">The streamed item type.</typeparam>
/// <param name="services">The scope the hooks are resolved from.</param>
/// <param name="registry">The generated registry of the hooks declared for each request type.</param>
/// <param name="logger">Logs a handled exception.</param>
public sealed class StreamExceptionHandlingBehavior<TRequest, TItem>(
    IServiceProvider services,
    IRequestExceptionHookRegistry registry,
    ILogger<StreamExceptionHandlingBehavior<TRequest, TItem>> logger)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior, ICqrsExceptionHandlingBehaviorMarker
    where TRequest : IRequest
{
    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.ExceptionHandling;

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        if (!registry.TryGetInvoker(typeof(TRequest), out var invoker))
            return next(cancellationToken);

        return ExecuteAsync(invoker);

        async IAsyncEnumerable<TItem> ExecuteAsync(RequestExceptionHookInvoker hookInvoker)
        {
            Exception? failure = null;

            var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    // Only the caller's own cancellation bypasses the hooks: a cancellation nobody asked for (an HttpClient timeout,
                    // a handler's own linked token) is a failure like any other, and every hook sees every failure.
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }

                    if (!moved) yield break;
                    yield return enumerator.Current;
                }
            }

            var outcome = await hookInvoker(services, request, failure!, cancellationToken).ConfigureAwait(false);
            if (!outcome.Handled)
            {
                ExceptionDispatchInfo.Capture(failure!).Throw();
                yield break;
            }

            ExceptionHandlingLog.StreamHandled(logger, typeof(TRequest).Name, failure!.GetType().Name);

            if (outcome.Response is not IAsyncEnumerable<TItem> responseStream)
                throw new InvalidOperationException(
                    $"Exception handler for request '{typeof(TRequest).FullName}' returned an invalid response type '{outcome.Response?.GetType().FullName ?? "<null>"}'.");

            await foreach (var item in responseStream.WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}
