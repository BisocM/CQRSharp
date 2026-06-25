using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Behaviors.Exceptions;

/// <summary>
///     Executes request-level exception hooks (actions + handlers) for streaming requests.
/// </summary>
public sealed class StreamExceptionHandlingBehavior<TRequest, TItem>(
    IServiceProvider services,
    IRequestExceptionHookRegistry registry,
    ILogger<StreamExceptionHandlingBehavior<TRequest, TItem>> logger)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => int.MinValue;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        Func<CancellationToken, IAsyncEnumerable<TItem>> next,
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

            await using (var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
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

            logger.LogDebug(
                "Exception {ExceptionType} handled for streaming request {RequestType}.",
                failure!.GetType().Name,
                typeof(TRequest).Name);

            if (outcome.Response is not IAsyncEnumerable<TItem> responseStream)
                throw new InvalidOperationException(
                    $"Exception handler for request '{typeof(TRequest).FullName}' returned an invalid response type '{outcome.Response?.GetType().FullName ?? "<null>"}'.");

            await foreach (var item in responseStream.WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}
